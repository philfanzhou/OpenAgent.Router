"""Linux CI smoke test for the production image; requires Docker and a running Redis."""
import http.server
import os
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid


class Downstream(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200 if self.path == "/ready" else 404)
        self.end_headers()

    def log_message(self, *args):
        pass


image = sys.argv[1] if len(sys.argv) > 1 else "openagent-router:ci"
uid = subprocess.check_output(["docker", "run", "--rm", "--entrypoint", "id", image, "-u"], text=True).strip()
assert uid != "0", "Router image must run as non-root"
downstream = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Downstream)
threading.Thread(target=downstream.serve_forever, daemon=True).start()
with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    port = listener.getsockname()[1]
name = "router-smoke-" + uuid.uuid4().hex[:12]
environment = {
    "ASPNETCORE_ENVIRONMENT": "Production",
    "ASPNETCORE_URLS": f"http://127.0.0.1:{port}",
    "ConnectionStrings__Redis": os.getenv("ROUTER_TEST_REDIS", "127.0.0.1:6379"),
    "Authentication__Authority": "https://identity.invalid",
    "Authentication__Audience": "smoke",
    "Authentication__ClientId": "smoke",
    "RouterSettings__IntentRecognition__Enabled": "false",
    "RouterSettings__AgentProviders__Providers__0__Settings__BaseUrl": f"http://127.0.0.1:{downstream.server_port}",
}
command = ["docker", "run", "--detach", "--name", name, "--network", "host"]
for key, value in environment.items():
    command.extend(["--env", f"{key}={value}"])
try:
    subprocess.run(command + [image], check=True, stdout=subprocess.DEVNULL)
    for path in ("/health", "/ready"):
        deadline = time.monotonic() + 45
        while True:
            try:
                with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=2) as response:
                    assert response.status == 200
                break
            except (urllib.error.URLError, TimeoutError):
                if time.monotonic() >= deadline:
                    raise
                time.sleep(0.25)
    print(f"Production image passed: non-root UID {uid}, liveness and Redis/Provider readiness")
except Exception:
    subprocess.run(["docker", "logs", name], check=False)
    raise
finally:
    subprocess.run(["docker", "rm", "--force", name], check=False, stdout=subprocess.DEVNULL)
    downstream.shutdown()

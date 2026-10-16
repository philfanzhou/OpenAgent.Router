#!/usr/bin/env bash
# 比对本仓库 Contracts 与主仓库 OpenAgent 的快照，不一致即失败。
# 用法: tools/check-contracts-drift.sh <主仓库 OpenAgent.Contracts 目录>
set -euo pipefail
upstream="${1:?usage: $0 <upstream OpenAgent.Contracts dir>}"
local_dir="$(cd "$(dirname "$0")/.." && pwd)/src/OpenAgent.Contracts"
[ -d "$upstream" ] || { echo "upstream dir not found: $upstream" >&2; exit 2; }
if diff -rq -x bin -x obj "$upstream" "$local_dir"; then
  echo "Contracts match upstream."
else
  echo "Contracts drifted from upstream; sync the snapshot or update upstream." >&2
  exit 1
fi

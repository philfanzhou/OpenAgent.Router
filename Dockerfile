FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore OpenAgent.Router.sln
RUN dotnet publish src/OpenAgent.Router/OpenAgent.Router.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish/ .

ENV ASPNETCORE_URLS=http://+:5001
EXPOSE 5001
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=3s --start-period=15s --retries=3 \
    CMD curl --fail --silent http://127.0.0.1:5001/health || exit 1
ENTRYPOINT ["dotnet", "OpenAgent.Router.dll"]

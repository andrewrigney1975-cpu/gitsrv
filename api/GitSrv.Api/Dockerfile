FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY api/GitSrv.Api/GitSrv.Api.csproj api/GitSrv.Api/
RUN dotnet restore api/GitSrv.Api/GitSrv.Api.csproj
COPY api/GitSrv.Api/. api/GitSrv.Api/
RUN dotnet publish api/GitSrv.Api/GitSrv.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# curl: used by the compose healthcheck. git: needed from Phase 2 for pack operations, installed
# now so the runtime image is stable across the next few phases.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl git \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
# The bare-repo volume is shared with the ssh container; both write to it. The aspnet image's
# built-in "app" user is uid 1654 — the ssh container creates its "git" user with the same uid so
# on-disk ownership lines up. Pre-owning the mount point means the named volume inherits it.
RUN mkdir -p /var/lib/gitsrv/repositories && chown -R app:app /var/lib/gitsrv
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "GitSrv.Api.dll"]

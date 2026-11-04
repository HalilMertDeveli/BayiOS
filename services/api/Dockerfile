# syntax=docker/dockerfile:1
#
# BayiOS API container image.
# Build context: services/api   ->   docker build -t bayios-api services/api
#
# - Multi-stage: SDK (build) -> chiseled ASP.NET runtime (no shell, no package manager, non-root UID 1654).
# - "-extra" chiseled variant ships ICU + tzdata: needed for tr-TR culture rules and Europe/Istanbul time zone.
# - Base images are pinned by multi-arch index digest; bump tag+digest together (Dependabot "docker" ecosystem).
# - Listens on 8080 (ASPNETCORE_HTTP_PORTS from the base image). TLS is terminated by the host/proxy.
# - No HEALTHCHECK: the runtime image has no shell/curl. Liveness is probed externally on GET /health
#   (fly.preview.toml http_service.checks, CI smoke step). See docs/operations/deploy-and-rollback.md.
# - No configuration or secrets are baked in. ASPNETCORE_ENVIRONMENT defaults to Production;
#   the host sets Preview/Production and injects secrets as env vars at runtime.

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:9b755ea799ae649141a073e30d8b62d8095a5465af2b7694d17866e8c05131d8

FROM ${SDK_IMAGE} AS build
WORKDIR /src

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

# Restore layer: only inputs that affect package resolution, so source edits do not invalidate it.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/BayiOS.Api/BayiOS.Api.csproj src/BayiOS.Api/
COPY src/BayiOS.SharedKernel/BayiOS.SharedKernel.csproj src/BayiOS.SharedKernel/
COPY src/Modules/Identity/BayiOS.Modules.Identity/BayiOS.Modules.Identity.csproj src/Modules/Identity/BayiOS.Modules.Identity/
COPY src/Modules/Catalog/BayiOS.Modules.Catalog/BayiOS.Modules.Catalog.csproj src/Modules/Catalog/BayiOS.Modules.Catalog/
RUN dotnet restore src/BayiOS.Api/BayiOS.Api.csproj

COPY src/ src/
RUN dotnet publish src/BayiOS.Api/BayiOS.Api.csproj \
      --configuration Release \
      --no-restore \
      --output /app \
      -p:UseAppHost=false \
 && rm -f /app/appsettings.Development.json

FROM ${RUNTIME_IMAGE} AS runtime
WORKDIR /app

COPY --from=build --chown=0:0 /app ./

# Non-root (app, UID 1654) is already the default user of chiseled images; stated explicitly for scanners/reviewers.
USER $APP_UID
EXPOSE 8080

ENTRYPOINT ["dotnet", "BayiOS.Api.dll"]

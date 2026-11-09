# Test/CI build environment for infra/linux/scripts/build-release.sh: .NET 10 SDK (Ubuntu 24.04) + Node 24.
# Same SDK digest as services/api/Dockerfile. The release is built on linux-x64 because the web's node_modules
# contain platform-specific native binaries (Next's SWC, etc.).
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b
ARG NODE_IMAGE=node:24-bookworm-slim

FROM ${NODE_IMAGE} AS node

FROM ${SDK_IMAGE}
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
 && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx \
 && node --version && npm --version && dotnet --version

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NEXT_TELEMETRY_DISABLED=1 \
    npm_config_update_notifier=false

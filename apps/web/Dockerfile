# syntax=docker/dockerfile:1
#
# BayiOS web (Next.js) container image — used by the Kubernetes chart (infra/k8s) and the kind CI smoke test.
# Build context: apps/web   ->   docker build -t bayios-web apps/web
#
# - Multi-stage: deps (npm ci) -> build (next build, output "standalone") -> runtime (node + server.js only).
# - Runs as the unprivileged `node` user (UID 1000), listens on 3000, no shell needed at runtime.
# - NEXT_PUBLIC_* values are PUBLIC (they are inlined into the browser bundle at build time) and are passed as
#   build args. Never pass a secret here. The defaults are local placeholders, not real endpoints or keys.
# - Base image pinned by multi-arch index digest; bump tag+digest together.

ARG NODE_IMAGE=node:24-alpine@sha256:a34ab6d7f6f4813f4d0c5469d74685604b962fbdbf5793a7bc7b4998ecb867c8

FROM ${NODE_IMAGE} AS deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

FROM ${NODE_IMAGE} AS build
WORKDIR /app
ARG NEXT_PUBLIC_API_BASE_URL=http://localhost:8080
ARG NEXT_PUBLIC_SUPABASE_URL=http://localhost:54321
ARG NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY=sb_publishable_placeholder-not-a-key
ARG NEXT_PUBLIC_TURNSTILE_SITE_KEY=
ENV NEXT_TELEMETRY_DISABLED=1 \
    NEXT_OUTPUT=standalone \
    NEXT_PUBLIC_API_BASE_URL=${NEXT_PUBLIC_API_BASE_URL} \
    NEXT_PUBLIC_SUPABASE_URL=${NEXT_PUBLIC_SUPABASE_URL} \
    NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY=${NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY} \
    NEXT_PUBLIC_TURNSTILE_SITE_KEY=${NEXT_PUBLIC_TURNSTILE_SITE_KEY}
COPY --from=deps /app/node_modules ./node_modules
COPY . .
RUN npm run build

FROM ${NODE_IMAGE} AS runtime
WORKDIR /app
ENV NODE_ENV=production \
    NEXT_TELEMETRY_DISABLED=1 \
    PORT=3000 \
    HOSTNAME=0.0.0.0
COPY --from=build --chown=0:0 /app/.next/standalone ./
COPY --from=build --chown=0:0 /app/.next/static ./.next/static
USER node
EXPOSE 3000
CMD ["node", "server.js"]

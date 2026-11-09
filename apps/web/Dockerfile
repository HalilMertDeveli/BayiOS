# syntax=docker/dockerfile:1
#
# BayiOS web container image (Next.js standalone output).
# Build context: apps/web   ->   docker build -t bayios-web apps/web --build-arg NEXT_PUBLIC_API_BASE_URL=...
#
# - Multi-stage: deps (npm ci) -> build (next build, output: standalone) -> runtime (server.js + traced node_modules).
# - NEXT_PUBLIC_* values are inlined into the browser bundle AT BUILD TIME (Next.js rule), so they are build args,
#   not runtime env. They are public by definition (API origin, Supabase URL, publishable key) — never a secret.
#   Production web hosting is Vercel (docs/operations/environments.md); this image is used by the kind/Kubernetes
#   rehearsal (docs/operations/kubernetes.md).
# - Runs as the unprivileged `node` user (UID 1000), listens on 3000, works with a read-only root filesystem
#   (mount a writable volume on /app/.next/cache).
# - Base image pinned by multi-arch index digest; bump tag+digest together.

ARG NODE_IMAGE=node:24-bookworm-slim@sha256:01d566e8d94d8c619591d78652babfccd3c6992a3dffa9ed5ab39e11cf6c70e9

FROM ${NODE_IMAGE} AS deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

FROM ${NODE_IMAGE} AS build
WORKDIR /app
ARG NEXT_PUBLIC_API_BASE_URL
ARG NEXT_PUBLIC_SUPABASE_URL
ARG NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY
ARG NEXT_PUBLIC_TURNSTILE_SITE_KEY=
ENV NEXT_TELEMETRY_DISABLED=1 \
    BAYIOS_NEXT_OUTPUT=standalone \
    NEXT_PUBLIC_API_BASE_URL=${NEXT_PUBLIC_API_BASE_URL} \
    NEXT_PUBLIC_SUPABASE_URL=${NEXT_PUBLIC_SUPABASE_URL} \
    NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY=${NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY} \
    NEXT_PUBLIC_TURNSTILE_SITE_KEY=${NEXT_PUBLIC_TURNSTILE_SITE_KEY}
COPY --from=deps /app/node_modules ./node_modules
COPY . .
RUN test -n "$NEXT_PUBLIC_API_BASE_URL" -a -n "$NEXT_PUBLIC_SUPABASE_URL" -a -n "$NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY" \
      || { echo "NEXT_PUBLIC_API_BASE_URL, NEXT_PUBLIC_SUPABASE_URL and NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY build args are required" >&2; exit 1; } \
 && npm run build

FROM ${NODE_IMAGE} AS runtime
WORKDIR /app
ENV NODE_ENV=production \
    NEXT_TELEMETRY_DISABLED=1 \
    PORT=3000 \
    HOSTNAME=0.0.0.0

# Root-owned, read-only for the runtime user. server.js + the traced node_modules subset, then the static assets
# (standalone output does not copy .next/static; there is no public/ directory in this app).
COPY --from=build --chown=0:0 /app/.next/standalone ./
COPY --from=build --chown=0:0 /app/.next/static ./.next/static

USER node
EXPOSE 3000

CMD ["node", "server.js"]

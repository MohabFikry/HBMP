# Mersal web SPA — multi-stage build. Context is the REPO ROOT (like the service Dockerfiles) because the app
# depends on the pnpm workspace packages @mersal/design-system (apps/design-system) and @mersal/contracts
# (libs/contracts). Stage 1 installs + builds; stage 2 serves the static bundle from nginx with SPA fallback.
#
#   docker build -f apps/web/Dockerfile -t mersal/web \
#     --build-arg VITE_API_BASE=/api --build-arg VITE_LIVE=1 .
#
# The VITE_* values are baked at build time (Vite inlines them), so they are build args, not runtime env. Defaults
# match apps/web/.env.example; override per environment. Keep secrets out — only public config belongs in VITE_*.
FROM node:20-bookworm-slim AS build
WORKDIR /repo
RUN npm i -g pnpm@9.15.9

# Manifests first for a cached install layer.
COPY pnpm-workspace.yaml package.json pnpm-lock.yaml ./
COPY apps/web/package.json apps/web/
COPY apps/design-system/package.json apps/design-system/
COPY libs/contracts/package.json libs/contracts/
RUN pnpm install --frozen-lockfile

# Then the sources the web bundle actually needs.
COPY apps/web/ apps/web/
COPY apps/design-system/ apps/design-system/
COPY libs/contracts/ libs/contracts/

# Vite inlines these at BUILD time, so they must arrive as --build-arg (compose: `build.args`). Passing them
# as runtime `environment:` hands them to nginx, which has no use for them, and the bundle keeps whatever was
# baked here instead. The defaults are blank on purpose — src/config.ts treats blank as absent and falls back
# — so an unparameterised `docker build` still produces a bundle that works against a default local stack.
ARG VITE_API_BASE=
ARG VITE_LIVE=1
ARG VITE_OIDC_AUTHORITY=
ARG VITE_OIDC_CLIENT_ID=
ARG VITE_OIDC_REDIRECT=
ENV VITE_API_BASE=$VITE_API_BASE \
    VITE_LIVE=$VITE_LIVE \
    VITE_OIDC_AUTHORITY=$VITE_OIDC_AUTHORITY \
    VITE_OIDC_CLIENT_ID=$VITE_OIDC_CLIENT_ID \
    VITE_OIDC_REDIRECT=$VITE_OIDC_REDIRECT
RUN pnpm --filter @mersal/web build

FROM nginx:1.27-alpine AS runtime
COPY apps/web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /repo/apps/web/dist /usr/share/nginx/html
EXPOSE 8080
# nginx:alpine already runs worker processes unprivileged; the image's default entrypoint is kept.

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

FROM nginxinc/nginx-unprivileged:1.27-alpine AS runtime
# 28.2 — a TEMPLATE, not a finished conf. The nginx image runs envsubst over /etc/nginx/templates/*.template
# at startup, which is how the gateway upstream and the CSP become deployment configuration instead of
# something baked into an image.
#
# NGINX_ENVSUBST_FILTER is not optional: without it envsubst substitutes every ${VAR} it finds a match for,
# and nginx's own $host / $remote_addr / $uri would be replaced by whatever the container happens to have in
# its environment. Restricting it to our two names means an unrelated env var can never rewrite the proxy.
COPY apps/web/nginx.conf.template /etc/nginx/templates/default.conf.template

# The Content-Security-Policy for the app's own origin (ADR-0036 §8.1).
#
# `script-src 'self'` is the directive that matters, and it is why this ships BEFORE the SPA login: after that
# change a password is typed into this origin, so an injected script could keylog it rather than having to
# forge a convincing login form first.
#
# `style-src` keeps 'unsafe-inline', a deliberate and much narrower concession: Radix's dialog pulls in
# react-remove-scroll, which injects a <style> element at runtime to lock the body, and without it every modal
# in the application loses its scroll lock. Inline STYLE is not the vector above — that one is script-src, and
# script-src is given no such exception.
#
# The two fonts.* origins are here because index.html loads Inter and Cairo from Google. That deserves
# revisiting on its own merits for an on-prem-first platform serving refugees — every page load reports the
# client's IP to a third party, and an air-gapped deployment renders with no fonts at all — but self-hosting
# them is a separate change and is NOT smuggled in here.
#
# `blob:` on img-src and frame-src is load-bearing: documents and member photos are fetched as authorized,
# audited streams and rendered from object URLs rather than from bearer links. A deployment whose blob store
# hands the browser a signed URL directly must add that origin here, or the preview fails — and it would fail
# as "document unavailable", which is a lie about the document rather than about the policy.
ENV NGINX_ENVSUBST_FILTER="^(KONG_UPSTREAM|CSP_POLICY)" \
    KONG_UPSTREAM="http://kong:8000" \
    CSP_POLICY="default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; script-src 'self'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' data: blob:; frame-src 'self' blob:; worker-src 'self' blob:; connect-src 'self'"
COPY --from=build /repo/apps/web/dist /usr/share/nginx/html
EXPOSE 8080

# Runs as an unprivileged user, not root (Trivy DS-0002).
#
# The previous base was `nginx:1.27-alpine`, and the note here said "nginx:alpine already runs worker
# processes unprivileged". That was true and beside the point: the MASTER process still ran as root, which is
# the process an nginx compromise gets you. The stock image needs root because it binds :80 and writes its
# pid and caches to root-owned paths — none of which this container does. It already listens on 8080
# (nginx.conf.template, EXPOSE above, and the compose port map), so nothing had to be re-plumbed.
#
# `nginxinc/nginx-unprivileged` is the same nginx from the same maintainers, laid out so every path the
# server writes at runtime — /var/cache/nginx, /tmp, /etc/nginx/conf.d for the rendered template — is owned
# by uid 101. The envsubst templating above is unchanged; it is the same entrypoint.
#
# The USER line is redundant with the base image, which already sets it, and is here anyway: Trivy reads the
# Dockerfile rather than the built image, so an inherited USER is invisible to it. Stating it keeps the gate
# honest instead of teaching the next person to suppress the rule.
USER 101

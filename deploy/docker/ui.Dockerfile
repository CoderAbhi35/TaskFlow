# syntax=docker/dockerfile:1
#
# The Angular dashboard, served by nginx, which also proxies /api to the API. The browser sees one
# origin, so the API needs no CORS rules. Built from the repository root:
#
#   docker build -f deploy/docker/ui.Dockerfile -t reeve-ui .

FROM node:24-alpine AS build
WORKDIR /ui
COPY frontend/reeve-ui/package.json frontend/reeve-ui/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/reeve-ui/ ./
RUN npx ng build --configuration production

# Runs as a non-root user and listens on 8080.
FROM nginxinc/nginx-unprivileged:1.29-alpine AS ui
COPY deploy/docker/nginx.conf.template /etc/nginx/templates/default.conf.template
ENV API_SERVER=api:8080 NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1
COPY --from=build /ui/dist/reeve-ui/browser /usr/share/nginx/html
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/healthz || exit 1

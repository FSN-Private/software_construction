# syntax=docker/dockerfile:1
# Node 24.20.0 Alpine multi-platform manifest, resolved 2026-09-04.
FROM node:24.20.0-alpine@sha256:e67514e5d0f6c46656005e1b693b2ec9d52e80b641307de684d4a015ba7a4eaf AS build
WORKDIR /src/apps/web

COPY apps/web/package.json apps/web/package-lock.json ./
RUN npm ci --ignore-scripts
COPY apps/web/ ./
RUN npm run build

# Nginx 1.27 Alpine multi-platform manifest, resolved 2026-09-04.
FROM nginx:1.27-alpine@sha256:65645c7bb6a0661892a8b03b89d0743208a18dd2f3f17a54ef4b76fb8e2f2a10 AS runtime
COPY --from=build /src/apps/web/dist/ /usr/share/nginx/html/
EXPOSE 80

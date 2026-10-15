# syntax=docker/dockerfile:1
# Node 24.20.0 Alpine multi-platform manifest, resolved 2026-09-04.
FROM node:24.20.0-alpine@sha256:e67514e5d0f6c46656005e1b693b2ec9d52e80b641307de684d4a015ba7a4eaf
WORKDIR /src/packages/infrastructure

COPY packages/infrastructure/package.json packages/infrastructure/package-lock.json ./
COPY packages/infrastructure/vendor/ ./vendor/
RUN npm ci --ignore-scripts
COPY packages/infrastructure/ ./
COPY apps/api/Database/Migrations/ /src/apps/api/Database/Migrations/
RUN npm run build

ENTRYPOINT ["npm", "run", "synth", "--"]

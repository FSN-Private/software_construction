# syntax=docker/dockerfile:1
# .NET 10 SDK manifest pinned to the digest already used by the other workers.
FROM mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src

COPY global.json ./
COPY packages/contracts/Platform.Contracts.csproj packages/contracts/
COPY packages/storage/Platform.Storage.csproj packages/storage/
COPY packages/storage/packages.lock.json packages/storage/
COPY packages/recovery/Platform.Recovery.csproj packages/recovery/
COPY packages/recovery/packages.lock.json packages/recovery/
COPY apps/recovery-worker/Platform.Recovery.Worker.csproj apps/recovery-worker/
COPY apps/recovery-worker/packages.lock.json apps/recovery-worker/
RUN dotnet restore apps/recovery-worker/Platform.Recovery.Worker.csproj --locked-mode

COPY packages/storage/ packages/storage/
COPY packages/contracts/ packages/contracts/
COPY packages/recovery/ packages/recovery/
COPY apps/recovery-worker/ apps/recovery-worker/
COPY apps/api/Database/Migrations/ apps/api/Database/Migrations/
RUN dotnet publish apps/recovery-worker/Platform.Recovery.Worker.csproj \
    --configuration Release --no-restore --output /out /p:UseAppHost=false

# PostgreSQL 18.4 official image, multi-platform manifest pinned by digest.
FROM postgres:18.4-bookworm@sha256:882236b897e39051d2368c5ccc6cda944904723506b2dfc97f2a8f5bc9afa382 AS runtime
# .NET 10 ASP.NET runtime, pinned to the reviewed digest used by the API.
COPY --from=mcr.microsoft.com/dotnet/aspnet@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 /usr/share/dotnet /usr/share/dotnet
WORKDIR /app
COPY --from=build /out/ ./
COPY --from=build /src/apps/api/Database/Migrations/ /app/migrations/
ENV DOTNET_ROOT=/usr/share/dotnet \
    PATH=/usr/lib/postgresql/18/bin:/usr/share/dotnet:${PATH} \
    Recovery__MigrationDirectory=/app/migrations
USER 65532:65532
ENTRYPOINT ["/usr/share/dotnet/dotnet", "/app/Platform.Recovery.Worker.dll"]

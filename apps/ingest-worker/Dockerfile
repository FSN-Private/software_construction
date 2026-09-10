# syntax=docker/dockerfile:1
# .NET SDK 10.0 multi-platform manifest, resolved 2026-09-04.
FROM mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src

COPY global.json ./
COPY packages/storage/Platform.Storage.csproj packages/storage/
COPY packages/storage/packages.lock.json packages/storage/
COPY apps/ingest-worker/Platform.Ingest.Worker.csproj apps/ingest-worker/
COPY apps/ingest-worker/packages.lock.json apps/ingest-worker/
RUN dotnet restore apps/ingest-worker/Platform.Ingest.Worker.csproj --locked-mode

COPY packages/storage/ packages/storage/
COPY apps/ingest-worker/ apps/ingest-worker/
COPY apps/api/Database/Migrations/ apps/api/Database/Migrations/
RUN dotnet publish apps/ingest-worker/Platform.Ingest.Worker.csproj --configuration Release --no-restore --output /out /p:UseAppHost=false

# ASP.NET runtime includes the .NET runtime and uses the same reviewed base as the API.
FROM mcr.microsoft.com/dotnet/aspnet@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 AS runtime
WORKDIR /app
USER $APP_UID
COPY --from=build /out/ ./
ENTRYPOINT ["dotnet", "Platform.Ingest.Worker.dll"]

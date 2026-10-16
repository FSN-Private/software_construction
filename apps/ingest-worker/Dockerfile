# syntax=docker/dockerfile:1
# .NET SDK10.0.402 Noble multi-platform security-update manifest, resolved2026-10-16.
FROM mcr.microsoft.com/dotnet/sdk@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
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
FROM mcr.microsoft.com/dotnet/aspnet@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS runtime
WORKDIR /app
USER $APP_UID
COPY --from=build /out/ ./
ENTRYPOINT ["dotnet", "Platform.Ingest.Worker.dll"]

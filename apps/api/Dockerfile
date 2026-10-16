# syntax=docker/dockerfile:1
# .NET SDK10.0.402 Noble multi-platform security-update manifest, resolved2026-10-16.
FROM mcr.microsoft.com/dotnet/sdk@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src

COPY global.json ./
COPY packages/contracts/Platform.Contracts.csproj packages/contracts/
COPY packages/storage/Platform.Storage.csproj packages/storage/
COPY packages/storage/packages.lock.json packages/storage/
COPY packages/recovery/Platform.Recovery.csproj packages/recovery/
COPY packages/recovery/packages.lock.json packages/recovery/
COPY apps/api/Platform.Api.csproj apps/api/
COPY apps/api/packages.lock.json apps/api/
RUN dotnet restore apps/api/Platform.Api.csproj --locked-mode

COPY apps/api/ apps/api/
COPY packages/contracts/ packages/contracts/
COPY packages/storage/ packages/storage/
COPY packages/recovery/ packages/recovery/
RUN dotnet publish apps/api/Platform.Api.csproj --configuration Release --no-restore --output /out /p:UseAppHost=false

# ASP.NET10.0.13 Noble multi-platform security-update manifest, resolved2026-10-16.
FROM mcr.microsoft.com/dotnet/aspnet@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
COPY --from=build /out/ ./
ENTRYPOINT ["dotnet", "Platform.Api.dll"]

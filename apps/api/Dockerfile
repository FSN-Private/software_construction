# syntax=docker/dockerfile:1
# .NET SDK 10.0 multi-platform manifest, resolved 2026-09-04.
FROM mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
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

# ASP.NET runtime 10.0 multi-platform manifest, resolved 2026-09-04.
FROM mcr.microsoft.com/dotnet/aspnet@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
COPY --from=build /out/ ./
ENTRYPOINT ["dotnet", "Platform.Api.dll"]

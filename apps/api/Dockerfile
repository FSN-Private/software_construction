# syntax=docker/dockerfile:1
# .NET SDK 10.0 multi-platform manifest, resolved 2026-09-04.
FROM mcr.microsoft.com/dotnet/sdk@sha256:74e93524c3de17e642cc18651b89fe3232af088708bc5bd5ca2df46ca87cfa62 AS build
WORKDIR /src

COPY global.json ./
COPY apps/api/Platform.Api.csproj apps/api/
COPY apps/api/packages.lock.json apps/api/
RUN dotnet restore apps/api/Platform.Api.csproj --locked-mode

COPY apps/api/ apps/api/
RUN dotnet publish apps/api/Platform.Api.csproj --configuration Release --no-restore --output /out /p:UseAppHost=false

# ASP.NET runtime 10.0 multi-platform manifest, resolved 2026-09-04.
FROM mcr.microsoft.com/dotnet/aspnet@sha256:ce4ee10bb8925d12de4ff1aaafc8acbb2e65f76435f2bfb231c14ac6f0b46493 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
COPY --from=build --chown=$APP_UID:$APP_GID /out/ ./
ENTRYPOINT ["dotnet", "Platform.Api.dll"]

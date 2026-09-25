FROM mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS worker-build
WORKDIR /src
COPY global.json ./
COPY packages/contracts/Platform.Contracts.csproj packages/contracts/
COPY apps/render-worker/Platform.Render.Worker.csproj apps/render-worker/
COPY apps/render-worker/packages.lock.json apps/render-worker/
RUN dotnet restore apps/render-worker/Platform.Render.Worker.csproj --locked-mode
COPY packages/contracts/ packages/contracts/
COPY apps/render-worker/ apps/render-worker/
RUN dotnet publish apps/render-worker/Platform.Render.Worker.csproj --configuration Release --no-restore --output /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 AS dotnet-runtime

FROM ubuntu@sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3 AS build

ARG DARKTABLE_VERSION=5.6.1
ARG DARKTABLE_SOURCE_SHA256=e8b84ac98b0b689a244e4036c4b56394c1d58ce2d9abc05e0a060ef9f756dc36
ARG DARKTABLE_BUILD_JOBS=2

# Build only from darktable's release archive; Ubuntu 24.04 packages 4.6.1, not the approved release.
RUN sed -i 's/^Types: deb$/Types: deb deb-src/' /etc/apt/sources.list.d/ubuntu.sources \
    && apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get build-dep --yes darktable \
    && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends ca-certificates cmake curl libpotrace-dev ninja-build xz-utils \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /build
RUN curl --fail --location --proto '=https' --tlsv1.2 --output darktable.tar.xz "https://github.com/darktable-org/darktable/releases/download/release-${DARKTABLE_VERSION}/darktable-${DARKTABLE_VERSION}.tar.xz" \
    && echo "${DARKTABLE_SOURCE_SHA256}  darktable.tar.xz" | sha256sum --check \
    && tar --extract --file darktable.tar.xz --xz
RUN cmake -S "darktable-${DARKTABLE_VERSION}" -B output -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=/opt/darktable -DBUILD_GUI=OFF -DUSE_GMIC=OFF -DUSE_OPENCL=OFF -DUSE_OPENEXR=OFF \
    && cmake --build output --parallel "${DARKTABLE_BUILD_JOBS}" \
    && cmake --install output

FROM ubuntu@sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3 AS runtime

# Ubuntu 24.04's darktable package supplies the runtime shared-library closure,
# but its 4.6.1 executable is never used. Keep its dependencies and replace the
# package payload with the verified source-built 5.6.1 installation. OpenEXR is
# disabled because the configured renderer only produces JPEG and must not ship
# its known-vulnerable parser.
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends ca-certificates darktable libpotrace0 \
    && apt-get purge --yes darktable openexr libopenexr-3-1-30 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /opt/darktable /opt/darktable
COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet
COPY --from=worker-build /out/ /app/

RUN install --directory --owner=65532 --group=65532 /var/lib/darktable /tmp/render
ENV PATH=/opt/darktable/bin:$PATH \
    DOTNET_ROOT=/usr/share/dotnet \
    HOME=/var/lib/darktable \
    XDG_CACHE_HOME=/var/lib/darktable/cache \
    XDG_CONFIG_HOME=/var/lib/darktable/config
USER 65532:65532
ENTRYPOINT ["/usr/share/dotnet/dotnet", "/app/Platform.Render.Worker.dll"]

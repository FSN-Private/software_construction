FROM ubuntu@sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3 AS build

ARG DARKTABLE_VERSION=5.6.1
ARG DARKTABLE_SOURCE_SHA256=e8b84ac98b0b689a244e4036c4b56394c1d58ce2d9abc05e0a060ef9f756dc36

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
RUN cmake -S "darktable-${DARKTABLE_VERSION}" -B output -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=/opt/darktable -DBUILD_GUI=OFF -DUSE_GMIC=OFF -DUSE_OPENCL=OFF \
    && cmake --build output --parallel \
    && cmake --install output

FROM ubuntu@sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3

COPY --from=build /opt/darktable /opt/darktable
ENV PATH=/opt/darktable/bin:$PATH
USER 65532:65532
ENTRYPOINT ["darktable-cli"]

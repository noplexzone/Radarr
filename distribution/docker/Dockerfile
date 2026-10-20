# syntax=docker/dockerfile:1

# Radarr with independent movie editions, packaged like lscr.io/linuxserver/radarr so it can
# replace that container with the same /config volume, PUID/PGID, UMASK and TZ settings.
# The service files in root/ follow linuxserver/docker-radarr (GPL-3.0).
#
# Build context: this folder, with the linux-musl-x64 package from build.sh copied to ./radarr
# (the GitHub workflow does this, see .github/workflows/editions-image.yml).

FROM ghcr.io/linuxserver/baseimage-alpine:3.24

ARG BUILD_DATE
ARG VERSION
LABEL build_version="noplexzone/radarr editions version:- ${VERSION} Build-date:- ${BUILD_DATE}"
LABEL org.opencontainers.image.source="https://github.com/noplexzone/radarr"
LABEL org.opencontainers.image.description="Radarr with independent movie editions"
LABEL org.opencontainers.image.licenses="GPL-3.0"

ENV XDG_CONFIG_HOME="/config/xdg" \
  COMPlus_EnableDiagnostics=0 \
  TMPDIR=/run/radarr-temp

RUN \
  apk add -U --upgrade --no-cache \
    icu-libs \
    sqlite-libs \
    xmlstarlet

COPY radarr/ /app/radarr/bin/

# Updates come from new images, never from inside the container
RUN \
  rm -rf /app/radarr/bin/Radarr.Update && \
  chmod 755 /app/radarr/bin/Radarr /app/radarr/bin/ffprobe && \
  printf "UpdateMethod=docker\nBranch=editions\nPackageVersion=%s\nPackageAuthor=[noplexzone](https://github.com/noplexzone/radarr)\n" "${VERSION}" > /app/radarr/package_info && \
  printf "noplexzone/radarr editions version: %s\nBuild-date: %s\n" "${VERSION}" "${BUILD_DATE}" > /build_version

COPY root/ /

EXPOSE 7878

VOLUME /config

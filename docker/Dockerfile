# Headless "Sledding Game" dedicated server under Steam Proton (GE-Proton) on Linux.
#
# The game is a 64-bit Windows IL2CPP Unity build loaded by MelonLoader (version.dll proxy).
# We run it with `-batchmode -nographics` through Proton, which provides the Wine + DXVK
# environment Valve ships for Steam Play. GE-Proton is used because it runs standalone
# outside the Steam client far more reliably than stock Valve Proton.
FROM debian:bookworm-slim

# GE-Proton release to bundle. Override at build time:
#   docker build --build-arg PROTON_VERSION=GE-Proton9-27 .
ARG PROTON_VERSION=GE-Proton9-20

ENV DEBIAN_FRONTEND=noninteractive \
    HOME=/home/steam \
    STEAM_COMPAT_CLIENT_INSTALL_PATH=/home/steam/.steam/steam

# i386 is required: parts of Wine/Proton are 32-bit.
RUN dpkg --add-architecture i386 && \
    apt-get update && \
    apt-get install -y --no-install-recommends \
        ca-certificates curl wget tar xz-utils zstd gzip \
        python3 procps gosu locales xvfb xauth \
        lib32gcc-s1 lib32stdc++6 libc6:i386 \
        libfreetype6 libfreetype6:i386 \
        libgnutls30 libgnutls30:i386 \
        libgl1 libgl1:i386 \
        libvulkan1 libvulkan1:i386 \
        libxml2 libnss3 \
        libgssapi-krb5-2 libgssapi-krb5-2:i386 \
        fontconfig && \
    rm -rf /var/lib/apt/lists/*

# Non-root runtime user (uid 1000 by default to match a typical host user).
RUN useradd -m -u 1000 -s /bin/bash steam

# GE-Proton, extracted to /opt/proton/current.
RUN mkdir -p /opt/proton && \
    curl -fsSL "https://github.com/GloriousEggroll/proton-ge-custom/releases/download/${PROTON_VERSION}/${PROTON_VERSION}.tar.gz" \
        | tar -xz -C /opt/proton && \
    ln -s "/opt/proton/${PROTON_VERSION}" /opt/proton/current

COPY entrypoint.sh /opt/entrypoint.sh
RUN chmod +x /opt/entrypoint.sh

# Proton state (Wine prefix) lives here; mount a volume so EOS identity + prefix persist.
ENV GAME_DIR=/game \
    PROTON=/opt/proton/current/proton \
    STEAM_COMPAT_DATA_PATH=/compatdata

VOLUME ["/compatdata"]
WORKDIR /game
ENTRYPOINT ["/opt/entrypoint.sh"]

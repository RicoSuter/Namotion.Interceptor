#!/usr/bin/env bash
# Rootless TLA+ toolchain bootstrap: no Docker, no sudo.
# Downloads a pinned tla2tools.jar and, only when no system Java is present
# (for example CI provides its own via actions/setup-java), a pinned portable
# Temurin JRE, into tools/tla/.cache (gitignored). Idempotent; verifies SHA-256.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cache="$here/.cache"
mkdir -p "$cache"

JAR_URL="https://github.com/tlaplus/tlaplus/releases/download/v1.8.0/tla2tools.jar"
JAR_SHA256="33de7da9ce1b7fffb9d1c184021178dbb051747be48504e65c584c423721a32e"
JRE_URL="https://github.com/adoptium/temurin17-binaries/releases/download/jdk-17.0.19%2B10/OpenJDK17U-jre_x64_linux_hotspot_17.0.19_10.tar.gz"
JRE_SHA256="adb5a2364baa51de1ef91bb9911f5a61d24b045fe1d6647cb8050272a3a8ee75"

verify() { echo "${2}  ${1}" | sha256sum -c - >/dev/null 2>&1; }

# tla2tools.jar is always required.
if [ -f "$cache/tla2tools.jar" ] && verify "$cache/tla2tools.jar" "$JAR_SHA256"; then
  echo "tla2tools.jar: cached"
else
  echo "tla2tools.jar: downloading"
  curl -sSL --max-time 180 -o "$cache/tla2tools.jar" "$JAR_URL"
  verify "$cache/tla2tools.jar" "$JAR_SHA256" || { echo "checksum mismatch: tla2tools.jar" >&2; exit 1; }
fi

CM_URL="https://github.com/tlaplus/CommunityModules/releases/download/202607091326/CommunityModules-deps.jar"
CM_SHA256="c99dcc2bef705f29c9c63db7e90429e6a0a0ec1d8bfc2a2d89f9de23945bcb4c"
if [ -f "$cache/CommunityModules-deps.jar" ] && verify "$cache/CommunityModules-deps.jar" "$CM_SHA256"; then
  echo "CommunityModules: cached"
else
  echo "CommunityModules: downloading"
  curl -sSL --max-time 180 -o "$cache/CommunityModules-deps.jar" "$CM_URL"
  verify "$cache/CommunityModules-deps.jar" "$CM_SHA256" || { echo "checksum mismatch: CommunityModules" >&2; exit 1; }
fi

# Portable JRE only when no system Java is available.
if { [ -n "${JAVA_HOME:-}" ] && [ -x "$JAVA_HOME/bin/java" ]; }; then
  echo "JRE: using JAVA_HOME"
elif command -v java >/dev/null 2>&1; then
  echo "JRE: using system java ($(command -v java))"
elif [ -x "$cache/jre/bin/java" ]; then
  echo "JRE: cached portable"
else
  echo "JRE: downloading portable Temurin 17"
  curl -sSL --max-time 300 -o "$cache/jre.tar.gz" "$JRE_URL"
  verify "$cache/jre.tar.gz" "$JRE_SHA256" || { echo "checksum mismatch: JRE" >&2; exit 1; }
  rm -rf "$cache/jre" && mkdir -p "$cache/jre"
  tar -xzf "$cache/jre.tar.gz" -C "$cache/jre" --strip-components=1
  rm -f "$cache/jre.tar.gz"
fi

echo "Bootstrap complete. Run: $here/tlc <Module.tla>"

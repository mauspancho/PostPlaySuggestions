#!/usr/bin/env bash
# Publica una nueva versión del plugin: compila, empaqueta, actualiza el manifest,
# hace commit/push y crea el release en GitHub con el ZIP adjunto.
#
# Uso:
#   ./release.sh <version> ["cambios de esta versión"]
#
# Ejemplo:
#   ./release.sh 0.4.1.0 "Corrige el disparo en episodios dobles."
set -euo pipefail

cd "$(dirname "$0")"

VERSION="${1:-}"
CHANGELOG="${2:-Mejoras y correcciones.}"
TARGET_ABI="${PPS_TARGET_ABI:-12.4.0.0}"
REPO_SLUG="${PPS_REPO_SLUG:-mauspancho/PostPlaySuggestions}"
ASSET="PostPlaySuggestions_${VERSION}.zip"

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Uso: ./release.sh <version> [\"cambios\"]" >&2
    echo "Ejemplo: ./release.sh 0.4.1.0 \"Corrige el disparo en episodios dobles.\"" >&2
    exit 1
fi

if ! git diff --quiet || ! git diff --cached --quiet; then
    echo "Hay cambios sin commitear. Confírmalos o descártalos antes de publicar." >&2
    exit 1
fi

if git ls-remote --tags origin "v${VERSION}" | grep -q .; then
    echo "La etiqueta v${VERSION} ya existe en el repositorio." >&2
    exit 1
fi

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
if ! command -v "$DOTNET" >/dev/null 2>&1; then
    DOTNET="dotnet"
fi

echo "== Versión ${VERSION}"
sed -i -E "s#<Version>[^<]+</Version>#<Version>${VERSION}</Version>#" Jellyfin.Plugin.PostPlaySuggestions.csproj
sed -i -E "s#<AssemblyVersion>[^<]+</AssemblyVersion>#<AssemblyVersion>${VERSION}</AssemblyVersion>#" Jellyfin.Plugin.PostPlaySuggestions.csproj
sed -i -E "s#<FileVersion>[^<]+</FileVersion>#<FileVersion>${VERSION}</FileVersion>#" Jellyfin.Plugin.PostPlaySuggestions.csproj
sed -i -E "s#PluginVersion = \"[^\"]+\"#PluginVersion = \"${VERSION}\"#" Plugin.cs
sed -i -E "s#var VERSION = '[^']*'#var VERSION = '${VERSION}'#" Web/postplay-suggestions.js

echo "== Compilando"
"$DOTNET" build -c Release --nologo | tail -4

echo "== Empaquetando"
mkdir -p artifacts
rm -f "artifacts/${ASSET}"
if command -v zip >/dev/null 2>&1; then
    zip -j -q "artifacts/${ASSET}" bin/Release/net10.0/Jellyfin.Plugin.PostPlaySuggestions.dll
else
    python3 - "artifacts/${ASSET}" <<'PY'
import sys, zipfile
with zipfile.ZipFile(sys.argv[1], "w", zipfile.ZIP_DEFLATED) as archive:
    archive.write("bin/Release/net10.0/Jellyfin.Plugin.PostPlaySuggestions.dll",
                  "Jellyfin.Plugin.PostPlaySuggestions.dll")
PY
fi

CHECKSUM="$(md5sum "artifacts/${ASSET}" | awk '{print $1}')"
TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
URL="https://github.com/${REPO_SLUG}/releases/download/v${VERSION}/${ASSET}"

echo "== Manifest"
python3 tools/update-manifest.py manifest.json "$VERSION" "$CHANGELOG" "$CHECKSUM" "$TIMESTAMP" "$URL" "$TARGET_ABI"

if [[ "${DRY_RUN:-0}" == "1" ]]; then
    echo
    echo "[dry-run] Versión ${VERSION} preparada (MD5 ${CHECKSUM})."
    echo "[dry-run] Commit, push y release omitidos."
    git checkout -- Jellyfin.Plugin.PostPlaySuggestions.csproj Plugin.cs Web/postplay-suggestions.js manifest.json
    rm -f "artifacts/${ASSET}"
    exit 0
fi

echo "== Commit y push"
git add -A
git commit -q -m "Post Play Suggestions ${VERSION}"
git push -q origin main

echo "== Release en GitHub"
gh release create "v${VERSION}" "artifacts/${ASSET}" \
    --title "Post Play Suggestions ${VERSION}" \
    --notes "$CHANGELOG" \
    --repo "$REPO_SLUG"

echo "== Verificando"
curl -fsS -o /dev/null "https://raw.githubusercontent.com/${REPO_SLUG}/main/manifest.json"
curl -fsSL -o /tmp/pps-release-check.zip "https://github.com/${REPO_SLUG}/releases/download/v${VERSION}/${ASSET}"
REMOTE_SUM="$(md5sum /tmp/pps-release-check.zip | awk '{print $1}')"
if [[ "$REMOTE_SUM" != "$CHECKSUM" ]]; then
    echo "El checksum remoto (${REMOTE_SUM}) no coincide con el local (${CHECKSUM})." >&2
    exit 1
fi
rm -f /tmp/pps-release-check.zip

echo
echo "Listo. Versión ${VERSION} publicada."
echo "Repositorio para Jellyfin:"
echo "  https://raw.githubusercontent.com/${REPO_SLUG}/main/manifest.json"

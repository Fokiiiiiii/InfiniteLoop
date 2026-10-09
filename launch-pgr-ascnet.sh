#!/usr/bin/env bash
set -euo pipefail

PGR_DIR="${PGR_ASCNET_DIR:-/Volumes/Lucia/Steam Games/SteamLibrary/steamapps/common/Punishing Gray Raven}"
CX_ROOT="/Applications/CrossOver.app/Contents/SharedSupport/CrossOver"
ASCNET_PREFIX="/Users/reiserfs/Applications/Sikarugir/Steam-AscNet.app/Contents/SharedSupport/prefix"

export CX_APPLEGPTK_LIBD3DSHARED_PATH="$CX_ROOT/lib64/apple_gptk/external/libd3dshared.dylib"
export CX_GRAPHICS_BACKEND="d3dmetal"
export CX_ROOT
export CX_WINELOADER="$CX_ROOT/bin/wineloader"
export DYLD_FALLBACK_LIBRARY_PATH="$CX_ROOT/lib64:$CX_ROOT/lib${DYLD_FALLBACK_LIBRARY_PATH:+:$DYLD_FALLBACK_LIBRARY_PATH}"
export GST_PLUGIN_PATH_1_0="$CX_ROOT/lib64/gstreamer-1.0"
export GST_PLUGIN_SYSTEM_PATH="$CX_ROOT/lib64/gstreamer-1.0"
export GST_PLUGIN_SYSTEM_PATH_1_0="$CX_ROOT/lib64/gstreamer-1.0"
export GST_REGISTRY="/Volumes/Lucia/PGR-native-research/logs/starter/gstreamer/crossover-gstreamer-registry.x86_64.bin"
export MTL_HUD_ENABLED="1"
export WINED3DMETAL="1"
export WINEDEBUG="-all"
export ASCNET_PATCH_ORIGIN="${ASCNET_PATCH_ORIGIN:-http://127.0.0.1:8080}"
export WINEDLLOVERRIDES="d3d11,dxgi=n,b;version=n,b"
export WINEDLLPATH="$CX_ROOT/lib/dxmt:$CX_ROOT/lib/wine"
export WINEDLLPATH_PREPEND="$CX_ROOT/lib/dxmt"
export WINEESYNC="0"
export WINEFSYNC="0"
export WINELOADER="$CX_ROOT/bin/wineloader"
export WINEMSYNC="0"
export WINEPREFIX="$ASCNET_PREFIX"
export WINESERVER="$CX_ROOT/bin/wineserver"

cd "$PGR_DIR"
exec "$CX_ROOT/bin/wineloader" PGR.exe

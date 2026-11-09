#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
"$ROOT/test.sh"
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
CSC="C:/Program Files/dotnet/sdk/9.0.314/Roslyn/bincore/csc.dll"
MANAGED="D:/Program Files/steam/steamapps/common/Probably Stolen/Probably Stolen_Data/Managed"
OUT_DIR="D:/code/game_mods/CompactSort/dist"
OUT_DLL="$OUT_DIR/CompactInventorySort.dll"
SRC="D:/code/game_mods/CompactSort/src"

rm -rf "dist" && mkdir -p "dist"

"$DOTNET" "$CSC" /nologo /target:library /langversion:7.3 /optimize+ /nostdlib+ \
  /out:"$OUT_DLL" \
  /reference:"$MANAGED/mscorlib.dll" \
  /reference:"$MANAGED/System.dll" \
  /reference:"$MANAGED/System.Core.dll" \
  /reference:"$MANAGED/netstandard.dll" \
  /reference:"$MANAGED/Assembly-CSharp.dll" \
  /reference:"$MANAGED/0Harmony.dll" \
  /reference:"$MANAGED/UnityEngine.dll" \
  /reference:"$MANAGED/UnityEngine.CoreModule.dll" \
  /reference:"$MANAGED/UnityEngine.InputLegacyModule.dll" \
  "$SRC/Packing/PackModels.cs" \
  "$SRC/Packing/GridPacker.cs" \
  "$SRC/Packing/BeamPacker.cs" \
  "$SRC/Packing/AdvancedPacker.cs" \
  "$SRC/Packing/VisualPacker.cs" \
  "$SRC/ModLog.cs" \
  "$SRC/SortUndo.cs" \
  "$SRC/SortUndoPatch.cs" \
  "$SRC/SortFeatures.cs" \
  "$SRC/CompactSorter.cs" \
  "$SRC/SelfTest.cs" \
  "$SRC/SnapshotDumper.cs" \
  "$SRC/SnapshotPatch.cs" \
  "$SRC/DragFitPatch.cs" \
  "$SRC/SortMenuPatch.cs" \
  "$SRC/SettingsPatch.cs" \
  "$SRC/SortLockPatch.cs" \
  "$SRC/FullSortPatch.cs" \
  "$SRC/CompactSortMod.cs"

cp manifest.xml dist/
mkdir -p dist/CompactInventorySort
cp manifest.xml dist/CompactInventorySort/
cp dist/CompactInventorySort.dll dist/CompactInventorySort/

echo "Built dist/CompactInventorySort/"
ls -la dist/CompactInventorySort/

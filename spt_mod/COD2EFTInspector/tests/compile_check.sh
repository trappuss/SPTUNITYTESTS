#!/bin/sh
# Cloud / Linux compile check of the plugin WITHOUT game files: mono mcs against public reference DLLs
# (NuGet UnityEngine.Modules 2021.3.33 + BepInEx 5.4.23.2 from GitHub). The real build (BUILD_SPT_INSPECTOR.bat)
# uses the PC's SPT 4.1.6 DLLs (Unity 2022.3); an API that differs between the two would only show there.
# mcs is older than Roslyn: no C# 7 type patterns ("x is T t"), so the plugin code avoids them.
set -e
cd "$(dirname "$0")/.."
R="${TMPDIR:-/tmp}/cod2eft_inspector_refs"
if [ ! -d "$R/um" ]; then
  mkdir -p "$R"
  curl -sSL -o "$R/um.nupkg" https://api.nuget.org/v3-flatcontainer/unityengine.modules/2021.3.33/unityengine.modules.2021.3.33.nupkg
  unzip -q -o "$R/um.nupkg" -d "$R/um"
  curl -sSL -o "$R/bep.zip" https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip
  unzip -q -o "$R/bep.zip" -d "$R/bep"
fi
U="$R/um/lib/net35"
REFS=""
for m in UnityEngine UnityEngine.CoreModule UnityEngine.IMGUIModule UnityEngine.TextRenderingModule UnityEngine.InputLegacyModule \
         UnityEngine.ImageConversionModule UnityEngine.ScreenCaptureModule UnityEngine.UIModule UnityEngine.AssetBundleModule UnityEngine.SharedInternalsModule; do
  [ -f "$U/$m.dll" ] && REFS="$REFS -r:$U/$m.dll"
done
mcs -target:library -out:"${TMPDIR:-/tmp}/COD2EFTInspector.dll" -r:"$R/bep/BepInEx/core/BepInEx.dll" -r:"$R/bep/BepInEx/core/0Harmony.dll" $REFS -r:System.Core.dll *.cs
echo "compile OK"

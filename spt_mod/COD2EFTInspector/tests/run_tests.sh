#!/bin/sh
# Cloud / Linux check of the Unity-free parts of the plugin (needs mono: apt-get install mono-mcs mono-runtime).
set -e
cd "$(dirname "$0")/.."
OUT="${TMPDIR:-/tmp}/cod2eft_inspector_tests.exe"
mcs -out:"$OUT" -r:System.Core.dll Json.cs Catalog.cs ../../unity/EFTAutoPrefabber/EFTModBuilderCore.cs ../../unity/EFTAutoPrefabber/EFTAutoPrefabCore.cs ../../unity/EFTAutoPrefabber/EFTLzma.cs ../../unity/EFTAutoPrefabber/EFTSkeletonData.cs -nowarn:219 tests/CatalogTest.cs
mono "$OUT"

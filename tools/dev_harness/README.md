# Old Blender dev harness (COD2EFT 2.4.x, cloud)

Moved here on 2026-10-01 from the PC folder `COD2EFT\_dev\` (PROJECT_CONTEXT listed it as missing from the repo).
These scripts are **historical**: they were written for one cloud container and have hard-coded paths
(`/home/claude/cod2eft`, `/home/claude/fitdev`, `/mnt/user-data/uploads/...`). Adjust the paths before using one.

| File | What it did | Today use instead |
|---|---|---|
| `runall2.py`, `runsel.py` | batch-convert every / selected character with `cod2eft_batch.py` | `tools/regress.py` |
| `cmpall.py` | compare vertex positions of two runs (Blender 5.0 vs 4.4) | `tools/regress.py` (fit numbers, mesh counts, PNG hashes) |
| `uitest4.py` | installs the add-on zip and drives the panel operators (FP hands, separate by material) | nothing yet: the only UI test |
| `codhist.py` + `codhist.json` | per-material texel histograms of the 2.4-era test set | `tools/cod_survey.py` |
| `skindet.py` | first skin-colour probe for `_skin` slots (Kovac 2003 rule), **not validated** | starting point for the `_skin` work (BACKLOG / spec) |

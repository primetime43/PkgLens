# Features

| Area | Tools |
| --- | --- |
| Packages | Inspect metadata, browse/filter files, extract, replace, compare, build, repack, and verify |
| Editing | Edit PARAM.SFO, preview pending replacements, review/revert changes, and save a verified copy |
| Protected files | Decrypt/rebuild EDAT/SDAT and SELF/EBOOT/SPRX; manage keys and licenses |
| Exports | PS1 BIN/CUE, PS2 ISO, PSP PBP/ISO/CSO, and structured Vita content |
| Archives | Browse, edit, rebuild, and verify zlib/LZMA PSARC archives |
| Libraries | Match base/update/DLC packages, find duplicates, organize files, and resume batch jobs |

## Package workflows

- Open PS3, PSP/PSX, and Vita packages with automatic standard-key selection.
- Inspect title, region, firmware, content IDs, images, text, binary files, and trophy sets.
- [Decrypt package contents](decrypt-package-contents.md), including pending edits, with per-file reports.
- Build packages from folders, compare packages, and create changed-file overlays.
- Convert supported PS3 packages for RPCS3, CEX CFW, or HEN.
- [Finalize debug PS3 packages](finalize-package.md) while preserving their contents.
- Batch audit, classify, verify, extract, export, or convert package folders.

## Executables and keys

- [Build, rebuild, or process folders of SELF files](self-building.md), including executable preparation while packing.
- [Verify existing SELF files](self-verification.md) and show [CEX/DEX format hints](cex-dex-detection.md).
- Analyze firmware requirements and apply supported firmware or byte patches.
- [Decrypt, encrypt, or rebuild EDAT/SDAT](edat-tools.md).
- [Check package key coverage](key-coverage.md) or [find an EDAT key in an executable](devklic-discovery.md).
- [Manage RAP and klicensee libraries](keys.md) with automatic lookup.

## Platform tools

PS1/PS2 Classics export disc images; PS2 images are verified during decryption.
PSP tools split PBP containers, extract manual pages, and decrypt supported NPUMDIMG images.
Vita export arranges package files; it does not remove inner PFS/SELF protection.

Long operations provide progress and cancellation. Rebuild workflows verify their output;
individual guides explain what each check covers. See the [project scope](../README.md#scope)
for signing and compatibility limits.

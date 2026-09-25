# PkgLens features

PkgLens provides the following workflows through the desktop app and, where applicable, the CLI.
The GUI's **Suggested** page automatically shows the actions that match the opened package.

| Area | Main capabilities |
|---|---|
| Packages | Inspect, search, edit, extract, replace, build, compare, convert, and verify |
| Protected content | EDAT/SDAT and SELF/EBOOT inspection, decryption, integrity checks, and fake-signing |
| Platforms | PS1, PS2, PSP, and PS Vita classification and export workflows |
| Archives | Browse, extract, replace, rebuild, and verify zlib or LZMA PSARC files |
| Libraries | RAP management, audits, matching, duplicate detection, organization, and batch jobs |
| Safety | Eligibility rules, preflight checks, progress, cancellation, reports, and output verification |

## Package inspection and editing

- Open retail or debug PS3, PSP, and PS Vita packages with automatic package-key detection.
- Browse, search, and filter the package file tree; reopen packages from recent history.
- View package headers, metadata, content IDs, regions, title IDs, versions, and file details.
- Read and edit `PARAM.SFO` values.
- Preview supported images, text files, and binary data.
- Preview unsaved replacement files with updated sizes and modified indicators; SFO details and package artwork refresh immediately, and reverting restores the originals.
- Browse `TROPHY.TRP` sets with game artwork, trophy icons, names, descriptions, grades, hidden flags, and medal counts.
- Extract one file, selected files, or the complete package directory tree.
- Decrypt package contents in one operation, including pending edits: extract the tree, decrypt supported PS3 SELF/EBOOT/SPRX, EDAT/SDAT, and fixed-key PSP EDAT/PGD files, and retain locked or failed files with per-file text/JSON reports.
- Replace package files and save a separate rebuilt package copy.
- Review pending file changes with original and replacement sizes, and revert individual files.
- Prompt to save, discard, or cancel before closing or opening another package with unsaved edits.
- Continue editing the verified saved copy after saving; failed or cancelled saves preserve pending edits.
- Inspect extracted game folders and executable metadata.

## Decryption, executables, and firmware

- Decrypt and integrity-check EDAT/SDAT files, including compressed EDAT content.
- Inspect SELF, EBOOT, and SPRX headers without decrypting the executable.
- Decrypt supported SELF/EBOOT files to plaintext ELF output.
- Fake-sign ELF files as compressed CFW-ready SELF files.
- Analyze every executable in a package or folder for its highest firmware requirement.
- Lower verified firmware requirements when the executable supports safe patching.
- Apply explicit hex find/replace and offset-based byte patches.
- Audit required package keys, SELF revisions, content IDs, license types, and encryption support.
- Manage RAP licenses and automatically resolve or discover matching RAPs by content ID.

## Package building and conversion

- Build streaming retail-encrypted or debug packages from content folders.
- Fake-sign embedded executables while building a package.
- Convert PS3 packages for RPCS3, CEX CFW, or HEN using target compatibility profiles.
- Compare two packages, including file hashes and `PARAM.SFO` changes.
- Create compact PS3 overlay/update packages containing changed and added files.
- Run operation preflight checks for format, keys, licenses, output paths, disk space, and support.
- Automatically verify rebuilt packages and converted output before reporting success.

## Platform exports

- Export PS1 Classics metadata and reconstruct supported single- or multi-disc BIN/CUE images.
- Verify and decrypt PS2 Classics `ISO.BIN.ENC` images to ISO.
- Convert supported PS2 Classics packages to a CFW/HEN package copy.
- Export PSP packages directly to `EBOOT.PBP`, decrypted ISO, or compressed CSO.
- Split PSP PBP files, decrypt supported PSP manuals, and decrypt NPUMDIMG `DATA.PSAR` images.
- Classify Vita apps, updates, DLC, and themes and export the correct directory structure.

## Archives and libraries

- Browse, search, preview, extract, replace, rebuild, and verify zlib or LZMA PSARC archives.
- Scan package libraries and group base games, updates, and DLC by title, region, and version.
- Warn about missing base packages, likely region mismatches, duplicates, and superseded updates.
- Organize PS3, PSP, and Vita libraries into platform, title, and content-role folders.
- Batch audit, classify, verify, extract, export, or CFW-convert folders of packages.
- Resume batch manifests, retry failures, and preserve selected target profiles.
- Save supported reports as JSON or CSV.

## Safety and compatibility

- Verify package structure, bounds, header authentication, CMAC, and public-key signatures.
- Perform deep EDAT block integrity verification and distinguish key failures from corruption.
- Verify exported ISO/CSO structures, rebuilt PSARCs, and fake-signed SELF round trips.
- Preflight and eligibility rules prevent incompatible workflows from being started accidentally.
- Cancellable GUI operations provide progress, modal errors, and safe temporary-output cleanup.
- Check GitHub's latest published release from the Help menu and open its download page.

PkgLens does not create genuine Sony signatures or use per-console secrets. See the
[README scope section](../README.md#scope) for output compatibility and limitations.

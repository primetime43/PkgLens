# Decrypt package contents

Open a package, then **Decrypt data → Decrypt package contents**, or
**File → Extract and repack → Decrypt package contents**. Choose a parent folder;
PkgLens creates a new `<package>-decrypted` folder with a numeric suffix if needed.

The export includes pending file/SFO edits and leaves the source session unchanged.

- `files/`: original folder structure. Successful decryptions contain plaintext under the original
  filename; SELF/EBOOT/SPRX contain ELF data. Locked, failed, or unsupported files retain their input bytes.
- `decryption-report.txt` and `.json`: per-file results, content IDs, failure reasons, and pending-edit status.
  No key bytes are included. The results window can filter files needing attention.

Supported: PS3 SELF/EBOOT/SPRX, EDAT/SDAT, and fixed-key PSP EDAT/PGD, using [automatic key lookup](keys.md).
SELF/PSP buffered inputs over 128 MiB stay unchanged; PS3 EDAT/SDAT streams.
Cancellation discards unfinished export output, after the current buffered operation returns.

This is an inspection folder, not an installable game. Archives, PBP, and disc images are not unpacked
recursively; use the platform exporters. Vita inner PFS/SELF protection is unsupported.

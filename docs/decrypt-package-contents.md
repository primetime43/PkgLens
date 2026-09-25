# Decrypt package contents

Open a package, then choose **Decrypt data → Decrypt package contents…**, or
**File → Extract and repack → Decrypt package contents…**. Choose an output parent folder.
PkgLens creates a new `<package>-decrypted` folder, adding a numeric suffix if necessary.

The export uses the current editing version, including unsaved file replacements and SFO edits.
It does not modify the source package or clear pending changes.

The output contains:

- `files/`: the package directory tree. Successfully decrypted files contain plaintext under
  their original names; EBOOT/SELF/SPRX outputs contain ELF data. Files that need a license,
  fail decryption, or use unsupported encryption retain their original contents.
- `decryption-report.txt` and `decryption-report.json`: a result for every file, including
  relevant content IDs, reasons for failures, and whether a pending edit was used. Reports
  contain no raw keys or RAP bytes.

The results window initially shows files that need attention when any exist. Uncheck the
filter to see every file. Import missing licenses under **Keys & licenses**, then run the
export again to a new folder.

Supported inner formats are PS3 SELF/EBOOT/SPRX revisions covered by the existing decryptor,
PS3 EDAT/SDAT, and fixed-key PSP EDAT/PGD. The operation resolves file-specific local klicensees,
the bundled catalog, and matching RAPs from the configured library. Free/debug content uses
the existing built-in decryption paths. Key availability does not guarantee that a file is valid;
integrity failures are reported and the encrypted input is retained.

SELF and PSP EDAT/PGD inputs above 128 MiB remain unchanged because their existing decryptors
buffer entire files. PS3 EDAT/SDAT decryption streams its output. Cancellation discards the
unfinished export; buffered decryption may finish the current file before cancellation takes effect.

This produces a folder for inspection, not an installable game. It does not recursively unpack
archives, PBP containers, or disc images. Use the dedicated platform exporters for PS1/PS2 Classics
and PSP disc images. Vita packages can have their outer package layer extracted, but their files
are reported as unsupported for inner decryption; the package AES keys do not remove PFS/SELF protection.

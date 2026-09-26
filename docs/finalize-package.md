# Finalize PKG

Open **File → Build and convert → Finalize PKG**, or **Create package → Finalize existing PKG**.
Choose a debug PS3 package, then **Finalize copy** and an output path. Save pending edits first:
this tool reads the file on disk.

Finalization converts the container to retail encryption while preserving its decrypted data,
metadata, filenames, offsets, embedded signatures, and license information. No SELF/RAP keys are needed.
Only debug PS3 packages with no footer or a standard 128-byte footer are accepted.

The output's structure, header/footer checksums, and decrypted contents are verified before saving.
Processing streams data; failure or cancellation preserves the source and any existing destination.
The result targets compatible CFW/HEN, has no Sony PKG signature, and retains the executables' original
license requirements. Hardware installation has not been validated.

References: [PSL1GHT finalizer](https://github.com/ps3dev/PSL1GHT/blob/master/tools/geohot/package_finalize.c)
and [RPCS3 package reader](https://github.com/RPCS3/rpcs3/blob/master/rpcs3/Crypto/unpkg.cpp).

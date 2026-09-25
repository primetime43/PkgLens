# Finalize PKG

Open **File → Build and convert → Finalize PKG**, or select **Finalize existing PKG** on the
**Create package** page. Choose a non-finalized PS3 package, then **Finalize copy** and an output
filename. The default suffix is `-finalized.pkg`.

The tool reads the selected package on disk. Save any pending edits first if they should be included.
It accepts debug PS3 packages with no footer (including PkgLens-created packages) or the standard
128-byte footer. Already retail-encrypted packages, PSP/Vita packages, split/truncated files,
invalid bounds, corrupt checksums and unsupported trailer layouts are rejected before publication.

Finalization changes the container encryption to retail AES-CTR using the bundled standard PS3
package key. It preserves the plaintext data region byte for byte, including entry records, names,
file offsets, file flags, embedded executable signatures, license data and PARAM.SFO. Metadata and
padding before the data region remain at their original offsets. No SELF/RAP keys are needed and
there is no extraction/repacking step.

The finalizer regenerates the header AES-CMAC and SHA-1 digest and writes a fresh 128-byte footer
with a whole-package SHA-1 checksum. It verifies package structure, header integrity, the footer
checksum and a SHA-256 comparison of the complete decrypted data region before atomically
publishing the output. Processing streams through a fixed-size buffer; cancellation is checked
between chunks. The input stays intact, and failure or cancellation preserves an existing destination.

The output targets compatible CFW/HEN and has no Sony PKG ECDSA signature. Finalizing does not
change the executables' existing compatibility or license requirements. Hardware installation
has not been validated in this pass.

The container conversion and footer layout were checked against the public
[PSL1GHT package_finalize implementation](https://github.com/ps3dev/PSL1GHT/blob/master/tools/geohot/package_finalize.c).
The debug and retail keystream definitions also match
[RPCS3's package reader](https://github.com/RPCS3/rpcs3/blob/master/rpcs3/Crypto/unpkg.cpp).
Automated checks cover content preservation, licensed signed SELF retention, non-aligned data,
multiple streaming chunks, both accepted footer layouts, malformed inputs, altered outputs and
cancellation during encryption and verification. A separate Python/cryptography check also compared
the complete plaintext and metadata and verified header/footer checksums on a generated fixture.

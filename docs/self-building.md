# Encrypted SELF building

Open **SELF / EBOOT → Build / rebuild SELF**. Choose an output profile, then select **Choose executable
and build…**. The input may be a decrypted PPU ELF or an existing EBOOT.BIN, SELF, or SPRX. Choose a
different output path; the original stays intact.

- **Encrypt output** uses the bundled APP or NPDRM root keys, fresh random metadata/segment keys,
  AES-CBC metadata wrapping, AES-CTR payload encryption, section HMAC-SHA1, and ELF digest fields.
  Turn it off to create a debug fSELF instead.
- **Compress segments** uses zlib where it reduces size. Section headers remain intact.
- **NPDRM output** adds license metadata and authenticates the content ID and final filename.
  Leave it off for NON-DRM APP output. An ELF needs a content ID for NPDRM; an existing NPDRM SELF
  can supply its original ID. Keep the output filename after building, or rebuild for the new name.
- Encrypted NPDRM defaults to **FREE**. **LOCAL** and **NETWORK** require a matching klicensee or RAP.
  The output license is selected independently of the source license; changing it does not create
  an entitlement or a retail signature.
- **Keys and encryption settings** selects the hexadecimal key revision (default `0A`) and independent
  source/output raw klicensees or RAPs. A raw key takes precedence over a selected RAP, followed by the
  local/bundled klicensee catalog, free SELF key, or RAP library. Selecting a RAP does not install it.
- Blank **Advanced metadata** fields preserve the source SELF's auth ID, vendor ID, app version,
  firmware field, and control flags. Plain ELF inputs use the standard defaults. NPDRM app type is
  preserved from an NPDRM source or detected as EXEC/SPRX from an ELF. Changing a firmware field
  does not patch executable SDK checks or instructions.

The builder decrypts its output and compares the entire recovered ELF before publishing the file
atomically. Unsupported key revisions, missing licenses, malformed ELF layouts, and layouts that
would lose data fail without replacing the destination. Input files are limited to 128 MiB, with a
256 MiB limit on combined segment payloads. Only 64-bit big-endian PPU APP and NPDRM output is supported.

## Compatibility and verification

Encryption and authentication hashes are implemented; Sony ECDSA signatures are not. Signature
fields are deliberately left zero. These outputs require a compatible loader with patched signature
checks, and do not establish full TrueAncestor signing parity, DEX/OFW support, or stock retail bootability.
Hardware boot compatibility has not been tested.

A subsequent [legacy-signing and RPCS3 experiment](self-signing-validation.md) confirmed that a
real executable rebuilt by PkgLens reaches game boot code in RPCS3. External legacy signing keys
also produced a mathematically valid signature; the current PkgLens builder still leaves signature
fields empty. RPCS3 accepted both variants, so boot acceptance does not establish signature validity.

The implementation was checked against the public [scetool source](https://github.com/naehrwert/scetool).
An independently compiled scetool decrypted eight generated APP/NPDRM cases (FREE, LOCAL, NETWORK;
compressed and uncompressed) into byte-identical ELFs. Automated tests additionally cover every bundled
key revision, multi-segment SPRX, section-header retention, malformed layouts, cancellation, source
preservation, and separate input/output license keys. The reference tool is not bundled with PkgLens.

# Encrypted SELF building

Open **SELF / EBOOT → Build / rebuild SELF**. Choose an output profile, then select **Choose executable
and build…**. The input may be a decrypted PPU ELF or an existing EBOOT.BIN, SELF, or SPRX. Choose a
different output path; the original stays intact.

- **Encrypt output** uses the bundled APP or NPDRM root keys, fresh random metadata/segment keys,
  AES-CBC metadata wrapping, AES-CTR payload encryption, section HMAC-SHA1, and ELF digest fields.
  Turn it off to create a debug fSELF instead.
- **Sign with a verified legacy key** adds an ECDSA header signature using the selected legacy CEX
  profile. The profile controls both the signing key and encryption revision, so the manual revision
  field is hidden while signing is enabled. APP supports keys `00`, `01`, `04`, `07`, `0A`; NPDRM
  supports `01`, `04`, `07`, `0A`. The default profile is `0A`. Profiles select cryptographic keys;
  they do not patch the ELF or reproduce TrueAncestor's STD/ALT firmware presets.
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
atomically. When signing is enabled, it also decrypts the final header and verifies its signature
against the profile's public key. It never falls back to unsigned output after a signing failure.
Unsupported key revisions, missing licenses, malformed ELF layouts, and layouts that
would lose data fail without replacing the destination. Input files are limited to 128 MiB, with a
256 MiB limit on combined segment payloads. Only 64-bit big-endian PPU APP and NPDRM output is supported.

## Compatibility and verification

Encryption, authentication hashes, and selected legacy ECDSA header signatures are implemented.
With signing off, signature fields remain zero and the output requires patched signature checks.
With signing on, the header signature verifies against a published legacy public key; this does not
mean Sony issued the rebuilt executable or that current firmware accepts it. NPDRM footer signing,
PKG signing, modern signing keys, DEX/OFW modes, and exact STD/ALT profiles are not included.
Hardware boot compatibility has not been tested; full TrueAncestor parity is not claimed.

A subsequent [legacy-signing and RPCS3 experiment](self-signing-validation.md) confirmed that a
real executable rebuilt by PkgLens reaches game boot code in RPCS3. Legacy-signed and unsigned
encrypted outputs were both accepted, so boot acceptance does not establish signature validity.

The implementation was checked against the public [scetool source](https://github.com/naehrwert/scetool).
An independently compiled scetool decrypted eight generated APP/NPDRM cases (FREE, LOCAL, NETWORK;
compressed and uncompressed) into byte-identical ELFs. Automated tests additionally cover every bundled
key revision, multi-segment SPRX, section-header retention, malformed layouts, cancellation, source
preservation, and separate input/output license keys. The reference tool is not bundled with PkgLens.

The integrated signer was additionally checked with 21 outputs: all nine legacy profiles with and
without compression, LOCAL/NETWORK NPDRM cases, and a real executable. scetool recovered identical
ELFs, and a separate Python elliptic-curve verifier accepted every signature using the reference key
and curve data. The real APP `0A` output also reached the game's boot code in RPCS3; missing game
assets prevented a full gameplay test. Signing uses the managed Bouncy Castle library to avoid
relying on platform-specific support for the PS3's custom elliptic curves.

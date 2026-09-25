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

## Folder processing

Open **SELF / EBOOT → Process a folder of executables**, or **Tools → Library and batch →
Process executables in a folder**. Choose source and output folders, choose an operation, then
**Scan folder** to preview individual files and output names before starting.

- Scan includes `EBOOT.BIN`, `.self`, `.sprx`, and `.elf`, with optional subfolder traversal.
  Source and output folders must be separate, with neither inside the other. Linked files and
  subfolders are excluded; linked source/output roots are rejected.
- **Decrypt to ELF** appends `.elf` to the original filename to avoid collisions between SELF and
  SPRX names. Existing plaintext ELFs are skipped. Recovered ELFs undergo structural/round-trip
  validation; this does not authenticate the original SELF's signature or every input section hash.
- **Rebuild encrypted SELF**, **Build fake-signed SELF**, and **Legacy CEX signing** preserve the
  source's APP/NPDRM type, content ID, license type and executable metadata. Plain ELF inputs become
  APP outputs named `<original>.elf.self`. SELF filenames and relative subfolders are retained.
  Compression and the output key revision are selectable. Each rebuilt output is decrypted back to
  the original ELF for an exact comparison; legacy signing additionally verifies the header signature.
- Saved keys and RAPs are resolved independently for each file. **Keys and licenses** allows a RAP
  folder override or a raw klicensee override that applies to every file. No RAP is installed implicitly.
- Originals and existing output files are never replaced. Outputs are published atomically only
  after verification. Naming collisions, missing keys, malformed files and unsupported profiles appear
  as individual errors; other eligible files can still complete. Select a row for the full result.
- **Cancel** stops at the next cancellation checkpoint, preserving completed outputs. **Start / resume**
  processes unfinished files. After supplying missing keys, **Retry failed** retries failed files only.
  The job list lasts for the current dialog; scanning again refreshes it and skips existing outputs.

## Rebuilding executables while packing or repacking

On **Create package**, select an **Executables** mode: keep unchanged, encrypted rebuild,
fake-sign all, or legacy-sign all. The original **Fake-sign EBOOT only (legacy)** option remains
available separately. **Save package as** for a PS3 package offers the same all-file modes and
defaults to keeping executables unchanged.

For an all-file mode, select the key revision and compression setting, then **Check and prepare
executables**. Matching saved keys and RAPs are resolved per executable; the dialog also accepts a
RAP folder or a raw klicensee override. Every EBOOT.BIN, SELF, SPRX and ELF is rebuilt into temporary
storage and verified before **Continue to save** becomes available. APP/NPDRM type, content ID,
license, executable metadata and package filenames are preserved. Plain ELF inputs become APP SELF
contents at their existing package path.

Missing keys, unsupported profiles and malformed files appear as individual blocking results.
Fix them and run the check again; the new all-file modes never silently include an unprocessed
executable. The pre-existing EBOOT-only legacy mode retains its older fallback behavior.

Repacking reads pending executable replacements and includes pending SFO and other file edits.
Preparation does not alter the editing session or source folder. Executable hashes and the candidate
list are checked again before packing, so changing an executable requires another check. The
prepared files are streamed into the PKG, then extracted and hashed to confirm that the embedded
bytes exactly match the verified outputs. Package verification also runs before atomic publication.
Cancellation or a failed check leaves an existing destination intact; temporary executable copies
are cleaned up when the operation ends. Successful repacking opens the saved copy as usual.

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

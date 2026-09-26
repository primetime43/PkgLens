# Verify SELF / EBOOT

Open **SELF / EBOOT → Verify SELF / EBOOT**, or **Tools → Inspect and verify → Verify SELF / EBOOT**. Choose an existing EBOOT.BIN, SELF or SPRX file. No open package is required.

The tool reads the selected file on disk and runs two independent checks:

- **Header signature:** Valid, Invalid, Unsigned, or Unsupported. Missing keys and errors that prevent verification are reported separately from an invalid signature.
- **Decryption:** Succeeded when the existing decryptor recovers an ELF accepted by the builder's structural checks; otherwise Missing key, Key error, Failed, or Unsupported, with a reason.

The same report includes CEX/DEX format, program type, key revision, firmware field, NPDRM license type and content ID. A valid header can accompany a damaged payload, and an unsigned file can decrypt successfully, so there is no blanket “file verified” result.

Saved filename-specific keys, known keys, the configured RAP library, and the standard SELF free-license key are resolved using the same lookup as SELF building. Expand **Keys and licenses** to choose a RAP or enter a raw klicensee. The raw key takes precedence. Changing either field clears the old report; **Verify again** reads the file and checks it with the new selection. Overrides are not saved.

Verification is read-only: it recovers the ELF in memory, creates no output or backup, and never patches or resigns the source. Files up to 128 MiB are supported. Cancellation is checked while reading and between verification/decryption stages; the current cryptographic stage may finish before cancellation completes.

Header signature checks use the existing legacy profiles: APP revisions 00/01/04/07/0A and NPDRM 01/04/07/0A. Other revisions can still pass decryption when a decryption profile exists. Debug/fSELF markers 8000 and C000 are reported as Unsigned. Decryption checks support 64-bit big-endian PS3 PPU executables; plain ELF inputs are not signed SELF files.

This does not authenticate payload hashes, verify NPDRM footer signatures or license entitlement, establish Sony provenance, or prove hardware/RPCS3 compatibility. Successful decryption alone cannot detect every form of payload corruption. The verification window states these limits alongside the results.

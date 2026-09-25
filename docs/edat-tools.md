# EDAT / SDAT tools

Open **Tools → EDAT / SDAT tools**, or use the button on **Decrypt data**.

| Operation | Result |
| --- | --- |
| Decrypt only | Plaintext from an existing EDAT or SDAT. |
| Encrypt plaintext | New EDAT or SDAT with your chosen filename, version, content ID, license, and block size. |
| Quick rebuild | Re-encrypt the original or replacement plaintext, preserving the original NPD identity, filename, license, version, and block size. |
| Custom rebuild | Decrypt the source, optionally replace its plaintext, and encrypt with new output settings or format. |

Choose a source, configure the operation, and select **Build and verify** (or **Decrypt and verify**).
You can then **Preview plaintext** or **Save copy**. Changing any setting invalidates the previous result.
Original files and replacement plaintext are never overwritten. Cancellation removes incomplete scratch output.

For package editing, right-click an EDAT/SDAT in **Browse files** and choose **Rebuild EDAT / SDAT…**.
The dialog reads the current package contents, including pending replacements. After verification,
**Stage in package** adds the rebuilt encrypted file to pending changes. Review or undo it before saving
a separate package copy. Staging requires the same filename and is limited to 128 MiB per file.
Plaintext previews are limited to 32 MiB; larger files can be decrypted and saved for external inspection.
Standalone processing streams file data through temporary files.

## Keys and settings

- SDAT derives its key from the header and needs no RAP.
- Free EDAT uses a developer klicensee. The standard key is the fallback; enter a raw key for content that uses a different one.
- Licensed EDAT needs its content key: select a RAP, enter a 32-character hexadecimal key, or let PkgLens check its key catalogs and configured RAP directory.
- For licensed output, the optional advanced developer header key is separate from the RAP-derived content key. Blank uses the built-in developer key.
- Raw keys take precedence over selected RAPs and automatic lookup. Entering a key here does not add it to the key database.
- EDAT supports versions 1–4; SDAT supports versions 2–4. Output block sizes are 1, 2, 4, 8, 16, or 32 KiB.
- The EDAT title hash includes the filename. Keep the chosen filename when saving; use custom rebuild to rename an existing EDAT.

## Verification and limits

Rebuilds verify the original before accepting replacement plaintext. Newly encrypted output is decrypted
again and its length and SHA-256 are compared with the intended plaintext before saving or staging is enabled.
This checks content integrity, not whether a particular console or title will accept the result.

All rebuilt output is uncompressed, including when the original used compression. Quick rebuild preserves
the NPD identity and validity fields but normalizes the block layout. Sony ECDSA signatures are not generated;
output is intended for compatible CFW/HEN or emulator workflows.

This pass covers the four main operations. Unlock-EDAT generation and DevKlic search/extraction are not included.
The tools are implemented in managed .NET and do not require Java or the external reference executable.

Format references: [PS3 EDAT files](https://www.psdevwiki.com/ps3/EDAT_files),
[NPD headers](https://www.psdevwiki.com/ps3/NPD), and
[Hykem's make_npdata](https://github.com/ErikPshat/make_npdata-hykem/blob/master/Linux/make_npdata.c).
During development, that independent reference tool decrypted 22 generated samples byte for byte:
empty and multi-block files across all supported versions, with free EDAT, licensed EDAT, and SDAT.

# EDAT / SDAT tools

Open **Tools → EDAT / SDAT tools**, or the button on **Decrypt data**.

| Operation | Result |
| --- | --- |
| Decrypt only | Verified plaintext |
| Encrypt plaintext | New EDAT/SDAT with selected output settings |
| Quick rebuild | Original or replacement plaintext, preserving NPD identity and settings |
| Custom rebuild | Replacement plaintext and/or new format, filename, license, or settings |

Choose the source and operation, then **Build and verify** or **Decrypt and verify**.
Use **Preview plaintext** or **Save copy** after success. Originals are preserved;
changing settings clears the previous result.

To edit a package, right-click an EDAT/SDAT in **Browse files → Rebuild EDAT / SDAT**.
It reads pending replacements. **Stage in package** adds a verified result to pending edits;
staging requires the original filename and a file no larger than 128 MiB. Preview limit: 32 MiB.

## Keys and settings

- **Check key and file** validates the header, metadata, and content without saving plaintext.
  It distinguishes missing keys, unconfirmed header matches, and damaged content.
- Raw key overrides RAP selection and [automatic lookup](keys.md). Overrides are not saved.
- SDAT derives its key from the header. Free EDAT uses a developer key; licensed EDAT needs
  the matching content key/RAP. The advanced developer header key is separate from that content key.
- EDAT supports versions 1–4; SDAT supports 2–4. Block sizes: 1–32 KiB in powers of two.
- EDAT authenticates its filename. Keep the chosen name when saving, or use custom rebuild to rename.

## Limits

Rebuilds validate the source, then decrypt the output and compare plaintext length and SHA-256.
Output is uncompressed and intended for compatible CFW/HEN or emulators; no ECDSA signatures
are generated. Debug files cannot prove a key match. Unlock-EDAT generation is not included.
Cancellation removes incomplete scratch output.

See [key discovery](devklic-discovery.md). Format references:
[EDAT](https://www.psdevwiki.com/ps3/EDAT_files), [NPD](https://www.psdevwiki.com/ps3/NPD),
[make_npdata](https://github.com/ErikPshat/make_npdata-hykem/blob/master/Linux/make_npdata.c).
The independent reference tool recovered identical plaintext from 22 generated EDAT/SDAT cases.

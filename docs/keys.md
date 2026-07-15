# Keys

PkgLens bundles the public PS3 decryption keys it needs, so packages open with no setup. The only
thing you supply yourself is a **RAP** for licensed EDAT/EBOOT content — that's tied to your
account, so it can't be bundled.

## What's built in

These are universal, symmetric decryption keys. All are public and have been for over a decade,
embedded by every PS3 package tool (RPCS3, scetool, make_npdata, and others). None is a
private or signing key.

| Key | Used for | Source in code |
|---|---|---|
| NPDRM PKG PS3 AES key | Decrypt retail `.pkg` data + header CMAC | `BundledKeys.Ps3GpkgAesKey` |
| NPDRM PKG PS3 IDU AES key | Decrypt IDU/kiosk packages automatically | `BundledKeys.Ps3IduAesKey` |
| PSP / PSX PKG AES key | Decrypt PSP and PSX package data | `BundledKeys.PspPkgAesKey` |
| PSVita PKG AES keys 2–4 | Decrypt Vita package revisions selected by `key_type` | `BundledKeys.VitaPkgAesKey2`–`4` |
| appldr / NPDRM SELF keysets | Decrypt SELF/EBOOT.BIN → ELF | `Self.SelfKeyset` |
| EDAT/SDAT keys + free klicensee | Decrypt NPDRM data files | `Npd.NpdKeys` |

Debug (non-finalized) packages need no key at all — their keystream is derived from the header's
QA digest.

## What you still supply

- **RAP files** — a licensed (non-free) EDAT or EBOOT is encrypted with a klicensee derived from the
  RAP that came with your purchase. Import it once with `pkglens raps import`; CLI and GUI operations
  then resolve it automatically by content ID. Explicit `--klic` and `--rap` options take precedence.
  PkgLens derives the klicensee itself and never stores RAP material in the repository.
- **Per-console secrets (IDPS / EID)** are out of scope and never needed by any PkgLens feature. A
  workflow that would require them isn't something PkgLens does.

## RAP library

```
pkglens raps import CONTENT-ID.rap
pkglens raps import license.rap --content-id CONTENT-ID
pkglens raps list
pkglens raps status [CONTENT-ID]
pkglens raps remove CONTENT-ID
```

The default directory is `%USERPROFILE%\.pkglens\raps` on Windows or `~/.pkglens/raps` elsewhere.
Set `PKGLENS_RAPS` or pass `--rap-dir DIR` to select another library. Files are indexed as
`<content-id>.rap`; listings and status never print the 16-byte RAP contents. Import rejects unsafe
content IDs and will not replace an installed RAP unless `--force` is supplied.

In the GUI, open **Tools → RAP library** or use **Keys → Manage RAP library**. The manager provides
the CLI operations visually, including a selectable persisted library folder. All GUI workflows that
decrypt licensed content use this same library automatically.

## Adding a custom PKG key

You rarely need this. Standard PS3, IDU/kiosk, PSP/PSX, and Vita key revisions 2–4 are selected
automatically. A custom key file joins the candidate ring for a non-standard PS3 package; it no
longer masks packages covered by a bundled key:

```
pkglens keys import <32-hex-key>      # install an override (the key, or a path to a key file)
pkglens keys status                    # show the bundled key + any override
```

PkgLens looks for a custom key file, in order, in:

1. the `--keys DIR` option,
2. the `PKGLENS_KEYS` environment variable,
3. the platform default: `%USERPROFILE%\.pkglens` (Windows) / `~/.pkglens` (Linux/macOS).

The first present of these filenames is used: `ps3_gpkg_aes.key`, `ps3_gpkg.key`, `gpkg_key`. The
file may contain **either** 16 raw bytes **or** 32 hex characters (an optional `0x` prefix and
whitespace are tolerated).

## When decryption applies

| Package kind | Header flag (0x04) | Decryption |
|---|---|---|
| **Debug** (non-finalized) | `0x0000` | Self-contained SHA-1 keystream from the header QA digest. |
| **Retail** (finalized) | `0x8000` | AES-128-CTR with an automatically selected standard, IDU, PSP, or Vita package key, seeded by `data_riv`. |

`pkglens info` always works (header, content id, and metadata are unencrypted); `list`, `sfo`, and
`extract` decrypt the item table, which now succeeds for supported PS3, PSP/PSX, and Vita packages
out of the box. PS3 selection prefers an authenticated header-CMAC match and uses strict item-table
validation for unsigned or stale-header packages.

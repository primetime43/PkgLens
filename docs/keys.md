# Keys

PkgLens bundles the public PS3 decryption keys it needs, so packages open with no setup. It also
ships a read-only catalog of publicly documented title-specific SELF klicensees. You still supply
account/content-specific **RAPs** and keys not present unambiguously in that catalog. Runtime
decryption never downloads keys from a third-party service.

## What's built in

The decryption keys below are publicly documented symmetric keys used by PS3 tools such as RPCS3,
scetool, and make_npdata. A separate, limited legacy SELF signing catalog is described below.

| Key | Used for | Source in code |
|---|---|---|
| NPDRM PKG PS3 AES key | Decrypt retail `.pkg` data + header CMAC | `BundledKeys.Ps3GpkgAesKey` |
| NPDRM PKG PS3 IDU AES key | Decrypt IDU/kiosk packages automatically | `BundledKeys.Ps3IduAesKey` |
| PSP / PSX PKG AES key | Decrypt PSP and PSX package data | `BundledKeys.PspPkgAesKey` |
| PSVita PKG AES keys 2–4 | Decrypt Vita package revisions selected by `key_type` | `BundledKeys.VitaPkgAesKey2`–`4` |
| appldr / NPDRM SELF keysets | Decrypt SELF/EBOOT.BIN → ELF | `Self.SelfKeyset` |
| EDAT/SDAT keys + free klicensee | Decrypt NPDRM data files | `Npd.NpdKeys` |
| Known title/content klicensees | Resolve unusual free-license SELFs automatically | `Npd.KnownKlicenseeStore` |

Debug (non-finalized) packages need no key at all — their keystream is derived from the header's
QA digest.

### Legacy SELF signing

`Self.LegacySelfSigning` bundles nine published legacy CEX header signing profiles: APP revisions
`00`, `01`, `04`, `07`, `0A`, and NPDRM revisions `01`, `04`, `07`, `0A`. Unlike the symmetric keys,
these include private ECDSA scalars and their public-key points. Key values were cross-checked against
[RPCS3's key vault](https://github.com/RPCS3/rpcs3/blob/master/rpcs3/Crypto/key_vault.cpp); curve data
uses the loader-curve format documented by [scetool](https://github.com/naehrwert/scetool).
The catalog validates curve points, group membership, private/public agreement, and matching
encryption keys before use. Signing uses Bouncy Castle with RFC 6979 nonces; no external tools or
downloads are needed at runtime.

These profiles do not supply modern keys, DEX signing, PKG signing, or NPDRM footer signing.
Each generated SELF header signature is verified after encryption, and the recovered ELF is compared
with the source before saving. See [SELF building](self-building.md).

## What you still supply

- **RAP files** — a licensed (non-free) EDAT or EBOOT is encrypted with a klicensee derived from the
  RAP that came with your purchase. Import it once with `pkglens raps import`; CLI and GUI operations
  then resolve it automatically by content ID. Explicit `--klic` and `--rap` options take precedence.
  PkgLens derives the klicensee itself and never stores RAP material in the repository.
- **Uncatalogued SELF klicensees** — some SELFs marked as free still use a title/file-specific key.
  PkgLens checks the bundled catalog first. If no unambiguous match exists, enter the
  32-hex-character key once; after successful decryption, PkgLens saves the verified local mapping.
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

## Klicensee library

The local database is `%USERPROFILE%\.pkglens\klicensees.json` on Windows or
`~/.pkglens/klicensees.json` elsewhere. Set `PKGLENS_KLICENSEES` to select another JSON file. Raw
keys are present in that local file, so protect it like other account/content license material; GUI
listings display only a one-way fingerprint.

For SELF decrypts, resolution order is: an entered raw key, a selected RAP, the narrowest
unambiguous saved klicensee mapping, an unambiguous bundled-catalog mapping, the RAP library, then
the standard free-license behavior. Exact content IDs rank ahead of title-wide catalog entries.
Mappings can include content ID, title ID, SELF filename, and license type. A broad title match with
competing keys is rejected instead of guessing.

In the GUI, use **Keys → Manage klicensee library** to import or remove mappings. The importer
supports annotated text lines and legacy `[klicensee]` INI blocks, for example:

```text
00112233445566778899AABBCCDDEEFF BLUS12345 optional description
```

Unannotated pools containing only raw keys are skipped because there is no safe way to know which
title or SELF each key belongs to. PkgLens does not ship or automatically fetch a GitHub key list.

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

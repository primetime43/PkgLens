# Keys

PkgLens bundles the **public** PS3 decryption keys it needs, so packages open with no setup. The
only material you ever supply yourself is per-purchase license data (a **RAP** for a licensed
EDAT/EBOOT) — that is tied to your account, so it can't be bundled.

## What's built in

These are universal, symmetric **decryption** keys — public for well over a decade and embedded by
every PS3 package tool (RPCS3, scetool, make_npdata, …). None is a private/signing key.

| Key | Used for | Source in code |
|---|---|---|
| NPDRM PKG PS3 AES key | Decrypt retail `.pkg` data + header CMAC | `BundledKeys.Ps3GpkgAesKey` |
| NPDRM PKG PS3 IDU AES key | IDU/kiosk packages (override only) | `BundledKeys.Ps3IduAesKey` |
| appldr / NPDRM SELF keysets | Decrypt SELF/EBOOT.BIN → ELF | `Self.SelfKeyset` |
| EDAT/SDAT keys + free klicensee | Decrypt NPDRM data files | `Npd.NpdKeys` |

Debug (non-finalized) packages need no key at all — their keystream is derived from the header's
QA digest.

## What you still supply

- **RAP files** — a licensed (non-free) EDAT or EBOOT is encrypted with a klicensee derived from the
  RAP that came with *your* purchase. Pass it per operation: `--rap FILE` on the CLI
  (`decrypt`, `unself`, `pack --resign`), or the RAP picker in the GUI. PkgLens converts the RAP to
  the klicensee itself; it is never stored in the repo.
- **Per-console secrets (IDPS / EID)** are **out of scope** and are never needed by any PkgLens
  feature. If a workflow would require them, it isn't something PkgLens does.

## Overriding the bundled PKG key

You rarely need this — only for a package the standard key doesn't cover (e.g. an IDU/kiosk key).
An override key file, if present, takes precedence over the bundled key:

```
pkglens keys import <32-hex-key>      # install an override (the key, or a path to a key file)
pkglens keys status                    # show the bundled key + any override
```

PkgLens looks for an override file, in order, in:

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
| **Retail** (finalized) | `0x8000` | AES-128-CTR with the bundled NPDRM PKG PS3 AES key, seeded by `data_riv`. |

`pkglens info` always works (header, content id, and metadata are unencrypted); `list`, `sfo`, and
`extract` decrypt the item table, which now succeeds for both kinds out of the box.

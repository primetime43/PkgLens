# Keys and licenses

Standard PS3, IDU/kiosk, PSP/PSX, and Vita package keys are bundled and selected automatically.
SELF and EDAT/SDAT tools also include public keysets and a known-klicensee catalog.
Licensed content can still require a matching RAP or title/file-specific klicensee.
Runtime lookup never downloads keys.

## RAP library

Open **Keys & licenses → Manage RAP library** to import licenses and select a library folder.
The default is `~/.pkglens/raps` (`%USERPROFILE%\.pkglens\raps` on Windows).
Use `PKGLENS_RAPS` or CLI `--rap-dir DIR` to override it.

```sh
pkglens raps import CONTENT-ID.rap
pkglens raps import license.rap --content-id CONTENT-ID
pkglens raps list
pkglens raps status CONTENT-ID
pkglens raps remove CONTENT-ID
```

RAPs must contain 16 bytes and are indexed by content ID. CLI replacement requires `--force`.
Listings and reports do not print key bytes.

## Klicensees

Open **Keys & licenses → Manage klicensee library** to import or remove annotated mappings.
Exact content-ID and filename matches take priority; ambiguous mappings are rejected.
Unannotated pools of keys are not imported. [Executable key discovery](devklic-discovery.md)
can save a mapping after verifying it against the target EDAT.

The local database is `~/.pkglens/klicensees.json`; override it with `PKGLENS_KLICENSEES`.
It contains raw keys, so treat it as private license data.

Where offered, an entered raw key overrides a selected RAP, followed by automatic lookup.
SELF, EDAT, and SDAT use different free/header-derived keys; their tools choose the appropriate one.

## Custom package keys

```sh
pkglens keys import <32-hex-key-or-key-file>
pkglens keys status
```

Lookup directory: `--keys DIR`, then `PKGLENS_KEYS`, then `~/.pkglens`.
Accepted filenames: `ps3_gpkg_aes.key`, `ps3_gpkg.key`, or `gpkg_key`.
Files contain 16 raw bytes or 32 hex characters. An override joins the standard key candidates;
it does not disable bundled-key detection. Debug packages need no external key.

## Legacy signing and limits

Bundled SELF header-signing profiles cover APP `00/01/04/07/0A` and NPDRM `01/04/07/0A`.
See [SELF building](self-building.md) for use and [validation results](self-signing-validation.md).
Modern signing keys, PKG/NPDRM-footer signing, and per-console IDPS/EID operations are not provided.
Vita package keys decrypt the outer container, not inner PFS/SELF protection.

Implementation references: [RPCS3 key vault](https://github.com/RPCS3/rpcs3/blob/master/rpcs3/Crypto/key_vault.cpp),
[scetool](https://github.com/naehrwert/scetool), and the key classes under
[`PkgLens.Core`](../src/PkgLens.Core). User key files and RAPs are not repository fixtures.

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PkgLens is a cross-platform, testable rewrite of a PS3 `.pkg` inspector (it also reads/extracts
PSP/PSVita packages, which share the container). It parses/inspects packages and performs keyless
operations (fake-signing, magic patching, EDAT/SDAT decryption). It
**never forges Sony signatures** — repacked retail packages and fake-signed SELFs target *patched
loaders (CFW)* only, never stock retail consoles. `README.md` covers user-facing features and the
feature-coverage matrix vs TrueAncestor.

## Build, test, run

```
dotnet build                 # whole solution (PkgLens.slnx)
dotnet test                  # all xUnit tests (Core.Tests only)
dotnet test --filter "FullyQualifiedName~SelfDecryptor"   # one test class
dotnet run --project src/PkgLens.Cli -- info <pkg>        # run the CLI (exe is named `pkglens`)
dotnet run --project src/PkgLens.Gui                      # run the GUI
```

GUI convenience scripts (Windows): `run-gui.cmd` builds+launches; `publish-gui.cmd` makes a
standalone `dist\pkglens.gui.exe` + Desktop shortcut. Target framework is **net8.0** throughout.

## Architecture

Three projects layered over one dependency-free core:

- **`src/PkgLens.Core`** — pure parsing, models, and crypto. **No UI, no I/O policy, no bundled
  keys, no NuGet dependencies.** Everything else consumes it. Operates on seekable `Stream`s;
  streaming and memory-light — it reads only headers/tables/small regions, never bulk file data
  (extraction decrypts entry ranges lazily). Multi-byte PKG/SELF fields are **big-endian**; PSP
  structures (PBP, NPUMDIMG, PGD) are **little-endian**. **Organized platform-first** into three
  top-level areas so it's clear what's shared vs PS3 vs PSP:
    - **`Shared/`** (`PkgLens.Core.Shared[.Models/.Sfo/.Keys/.Crypto/.Formats]`) — the PKG container is
      identical across PS3/PSP/PSVita, so everything shared lives here: `Formats/PkgContainerReader`
      (the one container reader), `Models/`, `Sfo/`, the `Keys/` decryption pipeline, `Crypto/` primitives
      (AES-CTR/CMAC/SHA-1 keystream + the LZRC/`EdatLz` range coder used by both platforms), and the
      public facades (`PkgReader`, `PkgWriter`, `PkgBuilder`, `PkgVerifier`, `PackageScanner`, folder tools).
    - **`Ps3/`** (`PkgLens.Core.Ps3[.Self/.Npd]`) — PS3-only: `Self/` (SELF/EBOOT decrypt/resign/patch),
      `Npd/` (EDAT/SDAT, RAP→klicensee), `NpdrmSignature` (PKG ECDSA verify).
    - **`Psp/`** (`PkgLens.Core.Psp`) — PSP-only: `Edat/` (PSP EDAT/PGD, DOCUMENT.DAT), `Iso/` (NPUMDIMG→ISO),
      `Crypto/` (KIRK/AMCTRL engine + PSP-content keys), `Pbp/` (EBOOT.PBP).
    - Root `PkgLens.Core` holds only `PkgFormatException`/`PkgKeyException` (so they resolve everywhere via
      the enclosing namespace).
- **`src/PkgLens.Cli`** — thin CLI (`pkglens`), hand-rolled arg parser (deliberately no
  System.CommandLine). Entry: `Program.cs` dispatches subcommands; each big subcommand
  (`pack`, `resign`, `unself`, `patch`, `folderinfo`, `scan`, `keys`) lives in its own `*Command.cs`.
  `scan <dir>` catalogs a folder of `.pkg` files (one row each, `--json`/`--csv`), isolating per-file
  parse failures into error rows. `unpbp <EBOOT.PBP>` splits a PSP PBP container (`Psp/Pbp/PbpArchive`,
  keyless) into its parts (PARAM.SFO, icons, DATA.PSP, DATA.PSAR). `undoc <DOCUMENT.DAT>` decrypts a
  PSP/minis manual to PNG pages (`Psp/Edat/PspDocument`; per-doc DES key from the sibling DOCINFO.EDAT).
  `psar decrypt <DATA.PSAR>` decrypts a PSP minis NPUMDIMG image to a plain `.iso` — **keyless**
  (`Psp/Iso/NpumdImg`; version key recovered from the header's own BBMac via AMCTRL `bbmac_getkey`, no
  RAP; blocks LZRC-decompressed via `EdatLz`). GUI: *Extract PSP ISO…* on an EBOOT.PBP.
- **`src/PkgLens.Gui`** — Avalonia 11 MVVM (`CommunityToolkit.Mvvm`), compiled bindings on.
  `ViewModels/` hold logic (`PackageViewModel` is the workhorse); `Views/*.axaml` are dialogs. Has full
  CLI parity: `ManualViewerDialog` (undoc), PBP unpack + `ScanDialog` (scan) all reuse the Core surface
  (`PackageScanner` is shared by the CLI `scan` and the GUI dialog).
- **`tests/PkgLens.Core.Tests`** — xUnit. `TestData/Synthetic*Builder.cs` build PKG/SELF/SFO/ELF
  fixtures in-memory (**no copyrighted data, no keys in the repo**). `Core` exposes internals to
  the tests via `InternalsVisibleTo`.

### Key seams in Core (understand these before extending)

- **`IPackageReader`** (`Shared/Formats/`) — per-format reader. `PkgContainerReader` handles the shared
  PS3 / PSP / PSVita container (same header, metadata TLV, item table, PARAM.SFO — only the data key
  differs); PS4 (`CNT`, a different format) is still meant to slot in as its own reader. `PkgReader`
  (static, `Shared/`) is the convenience facade the CLI and GUI actually call
  (`Open`/`Read`/`ExtractEntry`/`ExtractAll`).
- **`IKeyProvider` → `DecryptionContext` → `PkgDecryptorSet` / `IPkgDecryptor`** (`Shared/Keys/`, `Shared/Crypto/`) —
  this is the decryption pipeline. A key provider resolves a `DecryptionContext` from a `PkgHeader`;
  that context builds a `PkgDecryptorSet` (the primary `IPkgDecryptor` plus, for PSP/PSX, a secondary
  one). Debug (non-finalized) packages resolve with **no key** (keystream from the QA digest,
  `DebugSha1Decryptor`); finalized packages use AES-128-CTR (`RetailAesCtrDecryptor`) with a key chosen
  by platform: **PS3** uses the bundled NPDRM PKG AES key (`BundledKeys.Ps3GpkgAesKey`); **PSP/PSVita**
  (platform 0x0002) select by `header.PspKeyType` (= `header[0xE7] & 7`) — 1 = PSP
  (`BundledKeys.PspPkgAesKey` used directly), 2/3/4 = PSVita (key derived via AES-ECB of the data_riv
  with `VitaPkgAesKey{2,3,4}`). **PSP/PSX packages also key per entry**: the item table uses the PSP
  key, but each entry's name/data use the PSP key when the record's type high-byte is `0x90`, else the
  PS3 gpkg key (matching pkg2zip) — that's what `PkgDecryptorSet.For(entry)` encapsulates, so extraction
  and per-entry name decryption must go through it, never a single decryptor. Header/metadata/content-id
  parsing never needs a key — only listing/extracting/SFO does. `FileKeyProvider` (used by
  CLI/GUI/`PkgReader`) returns a user *override* key file if one exists, else falls back to the bundled
  key (PS3 only; PSP/PSVita always use bundled keys). `InMemoryKeyProvider` is the low-level explicit-key
  building block (no bundled fallback, PS3-only) — tests rely on `InMemoryKeyProvider(null)` *failing* to
  resolve retail packages, so don't add a fallback there.
- **`Ps3/Self/`** — SELF/EBOOT subsystem: `SelfReader` (keyless header inspect), `SelfDecryptor`
  (SELF→ELF, uses public keysets in `SelfKeyset`), `SelfBuilder`/`EbootResigner` (ELF→fSELF
  fake-sign), `EbootPatcher` (magic patches, e.g. SDK-version downgrade).
- **`Ps3/Npd/`** — NPDRM data files: `EdatFile` (PS3 EDAT/SDAT decrypt; routes PSP EDAT/PGD to `Psp/Edat/`),
  `NpdKeys` (`RapToKlicensee`), `RapStore`. (The LZRC decompressor is shared — `Shared/Crypto/EdatLz`.)
- **`Psp/Edat/`** — **PSP EDAT / PGD** decrypt (`PspEdatFile` is the entry: handles "\0PSPEDAT" and bare
  "\0PGD"; `PspDocument` decrypts DOCUMENT.DAT manuals). Uses the KIRK/AMCTRL engine in `Psp/Crypto/`
  (`PspAmctrl` = BBMac + BBCipher over AES-CBC, `PspKirkKeys` public fixed keys). **PGD fixed-key
  (drm_type 1) only**; fuse-bound (drm_type 2) is rejected. `Psp/Iso/NpumdImg` decrypts a minis UMD image
  to `.iso` **keyless** (version key recovered from the header's BBMac via `bbmac_getkey`).
- **Write paths** (`Shared/`): `PkgBuilder`/`PkgWriter` (pack a folder → `.pkg`), `SfoWriter` (repack
  PARAM.SFO), `PkgVerifier` (SHA-1 digest + AES-CMAC integrity + read-only ECDSA header-signature
  verification via `Ps3/NpdrmSignature` — Sony's public NPDRM key, never forges/re-signs).

## Conventions & guardrails

- **Bundled keys are public *decryption* keys only** — the universal NPDRM PKG AES key plus the
  PSP/PSVita package keys (`Shared.Keys.BundledKeys`), SELF keysets (`Ps3.Self.SelfKeyset`), and EDAT/SDAT
  keys (`Ps3.Npd.NpdKeys`), all public for a decade+ (the PSP/Vita keys transcribed from pkg2zip; its
  `pkg_ps3_key` matches our `Ps3GpkgAesKey`). **Never** bundle a private/signing key, a per-console
  secret (IDPS/EID), or
  per-user license data (RAP) — RAP stays a per-operation `--rap` input. `.gitignore` blocks
  `*.key`, `keys/`, `*.pkg`, `gpkg_key` so no user-specific material or copyrighted package is
  committed. `KeyStore.KnownGpkgKeySha256` fingerprints the standard key to tell a user whether an
  override they supply matches it.
- **Signature forgery is permanently out of scope** — no Finalize-for-retail, no DEX/OFW resign,
  no private-key/per-console (IDPS/EID) operations. Keyless fSELF/repack paths target CFW only.
- Core stays dependency-free and I/O-policy-free — push file opening and UI concerns to CLI/GUI.
- CLI exit codes are meaningful: `0` ok · `1` usage · `2` parse error · `3` key/decryption error ·
  `4` integrity failure (see `ExitCode.cs`). Errors map from `PkgKeyException`/`PkgFormatException`.
- New byte-format work should come with a synthetic fixture in `TestData/`; several formats are
  additionally verified byte-for-byte against real dumps (documented in `README.md`, not committed).

## Working conventions

- **The maintainer runs all git commits/staging.** Make and verify changes (build + tests), then stop —
  don't `git commit`/`git add` or offer to; leave the working tree for review.
- Format/algorithm **sources of truth** (psdevwiki 403s, so code is verified against implementations):
  PS3 container/SELF ← RPCS3 `unpkg`/`unself`; the shared LZRC range coder ← RPCS3 `lz.cpp`; PSP/Vita
  package keys ← pkg2zip; PSP EDAT/PGD KIRK/AMCTRL ← tpunix `kirk_engine`; NPUMDIMG layout ← hykem
  `sign_np`, and the keyless version-key recovery (`bbmac_getkey`) ← qwikrazor87 `npdrm_free`; PSP
  DOCUMENT.DAT ← seiya-dev `PSP-DOCUMENT.DAT`; PKG ECDSA curve/key ← windsurfer1122 `PSN_get_pkg_info`.
- **Design deviations** (deliberate, not oversights): named **PkgLens** (not the spec's PkgView);
  hand-rolled a zero-dependency CLI arg parser instead of `System.CommandLine`.
- **Out of scope by maintainer decision:** PSP SELF/PRX *executable* decryption (EBOOT.BIN/`.prx` inside a
  decrypted minis ISO → plaintext ELF, via KIRK CMD1 / a PrxDecrypter port) — the extracted ISO already
  runs in PPSSPP/CFW, which decrypt the executables at boot. Don't re-propose it unless asked.
- **Minor open backlog** (UX polish): a shared GUI error dialog (failures currently go to the status
  bar), `IsBusy`/progress guards on the long operations, and `--json` on the remaining CLI subcommands.

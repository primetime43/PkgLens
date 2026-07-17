# PkgLens

PkgLens opens a PS3 `.pkg` and shows what's inside — content id, metadata, the file tree, and
`PARAM.SFO`. Beyond inspecting, it extracts files, decrypts EDAT/SDAT and SELF/EBOOT data, repacks
and builds packages, and fake-signs EBOOTs for custom firmware. It reads PSP and PSVita packages
too, since they share the PS3 container.

It runs on Windows, Linux, and macOS through a command-line tool (`pkglens`) and an Avalonia desktop
app, both built on one dependency-free parsing core.

> **PkgLens never forges Sony signatures.** Repacked and fake-signed files target jailbroken (CFW)
> consoles, never stock retail. See [Scope](#scope).

## Contents

- [Install & run](#install--run)
- [What it does](#what-it-does)
  - [Inspect](#inspect)
  - [Extract & decrypt](#extract--decrypt)
  - [PSP / minis](#psp--minis)
  - [Pack & repack](#pack--repack)
  - [Patch & verify](#patch--verify)
- [CLI](#cli)
- [Feature coverage vs TrueAncestor](#feature-coverage-vs-trueancestor)
- [Keys](#keys)
- [Scope](#scope)
- [Project layout](#project-layout)
- [Build & test](#build--test)
- [License](#license)

## Install & run

Build the solution, then run either front end:

```
dotnet build
dotnet run --project src/PkgLens.Cli -- info <pkg>   # CLI
dotnet run --project src/PkgLens.Gui                 # GUI
```

On Windows you can skip the command line: double-click `run-gui.cmd` to build and launch, or run
`publish-gui.cmd` once for a standalone `dist\pkglens.gui.exe` plus a Desktop shortcut.

In the GUI, open a package with **File → Open** or drag a `.pkg` onto the window. Retail and debug
packages both open with no setup — the standard decryption key is built in. The left pane is a
folder tree; the right pane lists the selected folder's files. **Tools → Package info…** shows the
header, metadata, and `PARAM.SFO`. **File → Convert package → CFW…** performs the complete EBOOT
conversion and streaming rebuild workflow in one operation. **File → Export PSP package…** turns a
PSP package directly into `EBOOT.PBP`, a decrypted ISO, or a compressed CSO.

## What it does

### Inspect

- Parse the header, content id (region / title id / variant / name), and metadata block.
- List the item table with names, sizes, and flags.
- Read `PARAM.SFO`, edit its values, then Save As to repack with the changes.
- View files inline: images (ICON0/PIC1), text, or a hex dump.
- Read a SELF / EBOOT.BIN / SPRX header — program type, key revision, segments, control blocks,
  firmware version, NPDRM content id. No keys needed.

### Extract & decrypt

- Extract one file, a selection, or everything to a folder (the tree is rebuilt on disk).
- Decrypt EDAT/SDAT data files. SDAT needs no key; a licensed EDAT resolves its RAP automatically
  from the local library. Compressed EDATs are supported.
- Decrypt an encrypted EBOOT.BIN / `.self` back to a plaintext ELF (`unself`). Fake-signed and debug
  SELFs need no key; a licensed one resolves its RAP automatically when installed.

### PSP / minis

- Decrypt PSP EDAT and bare PGD files (fixed-key content only; fuse-bound content is out of scope).
- Decrypt a PSP/minis manual (`DOCUMENT.DAT`) into its PNG pages (`undoc`).
- Split a PSP `EBOOT.PBP` into its parts (`unpbp`): `PARAM.SFO`, icons, `DATA.PSP`, `DATA.PSAR`.
- Decrypt a minis `DATA.PSAR` (NPUMDIMG) to a mountable `.iso` (`psar decrypt`) — keyless, no RAP.
- Export a PSP package directly to `EBOOT.PBP`, decrypted `.iso`, or PSP-compatible compressed `.cso`
  (`pspexport`), without manually extracting the package and PBP first.

### Pack & repack

- Replace a file and save a new `.pkg`.
- Build a `.pkg` from a content folder (`pack`). *Fast Pack* infers the ids from `PARAM.SFO`; pass
  options for *Custom Pack*. The default is retail-encrypted (for CFW); `--debug` builds a
  non-finalized package for RPCS3 or dev consoles.
- Fake-sign a plaintext ELF into a SELF that boots on CFW (`resign`), with optional custom sign
  fields.
- Fake-sign a folder's `EBOOT.BIN` while packing (`pack --resign`).
- Convert a PS3 package for CFW in one GUI workflow: find every `EBOOT.BIN`, resolve licensed SELFs
  from the RAP library, decrypt and fake-sign them, optionally lower their firmware requirement,
  stream the rebuilt `.pkg`, and write a per-executable transformation report beside it.

### Patch & verify

- Magic-patch an EBOOT so a game boots on lower firmware — lower the required SDK version, or apply
  raw find/replace and at-offset edits (`patch`).
- Verify package integrity: structural bounds, the header SHA-1 digest, the header AES-CMAC, and a
  read-only ECDSA check against Sony's public NPDRM key. A genuine retail package passes; a repacked,
  altered, or truncated one is flagged.
- Catalog a folder of packages, one row each (`scan`, with `--json` / `--csv`).
- Report on an extracted content folder (`folderinfo`).

Every format is covered by synthetic in-test fixtures, and several are additionally checked
byte-for-byte against real retail dumps.

## CLI

```
pkglens --version                                         # print the embedded release version
pkglens info   <pkg> [--keys DIR] [--json]              # header + content-id + SFO summary
pkglens list   <pkg> [--keys DIR] [--json]              # entry table
pkglens sfo    <pkg> [--keys DIR] [--json]              # dump PARAM.SFO key/values
pkglens extract <pkg> [--out DIR] [--filter GLOB] [--json]   # extract entries
pkglens verify <pkg> [--json]                           # integrity check (SHA-1 + CMAC + ECDSA)
pkglens decrypt <edat> [--rap FILE] [--json]            # decrypt; stored RAPs resolve automatically
pkglens self   <eboot> [--json]                         # inspect a SELF/EBOOT.BIN header (no keys)
pkglens unself <eboot> [--rap FILE | --klic HEX] [--out FILE]   # decrypt a SELF → plaintext ELF
pkglens resign <elf> [--out FILE] [--npdrm]             # ELF → fake-signed SELF (fSELF) for CFW
pkglens patch  <eboot> [--sdk-version 4.00] [--find HEX --replace HEX] [--at OFF=HEX]
pkglens pack   <folder> [--out FILE] [--debug] [--resign [--rap FILE]]   # build a .pkg
pkglens folderinfo <folder> [--json]                    # report on an extracted content folder
pkglens scan   <dir> [--recursive] [--json | --csv]     # catalog a folder of .pkg files
pkglens unpbp  <EBOOT.PBP> [--out DIR] [--list]         # split a PSP PBP into its parts
pkglens undoc  <DOCUMENT.DAT> [--docinfo FILE] [--out DIR]   # decrypt a PSP manual to PNG pages
pkglens psar   decrypt <DATA.PSAR> [--out FILE]         # decrypt a PSP NPUMDIMG to .iso (keyless)
pkglens pspexport <pkg> [--format pbp|iso|cso] [--out FILE] [--json]   # one-step PSP export
pkglens keys   import|status|where                      # manage an optional override key
pkglens raps   import|list|status|remove                # manage the local RAP library
```

Licensed EDATs and EBOOTs are matched to stored RAPs by content ID. Explicit `--klic` and `--rap`
options override the library. Use `--rap-dir DIR` or `PKGLENS_RAPS` for a non-default library.

```
pkglens raps import MY-CONTENT-ID.rap                    # content ID inferred from the filename
pkglens raps import license.rap --content-id CONTENT-ID # specify it explicitly
pkglens raps list
pkglens raps status [CONTENT-ID]
pkglens raps remove CONTENT-ID
```

The GUI exposes the same library from **Tools → RAP library** and the **Keys** page. It supports
import, list/status, folder selection, replacement, and removal. EDAT viewing/decryption, Unself,
Patch, and Pack-with-resign all resolve stored RAPs automatically; choosing a RAP override caches it
under the detected content ID for later operations.

Every command accepts `--json`. Successful JSON mode writes exactly one JSON value to stdout;
diagnostics remain on stderr and exit codes remain unchanged.

Exit codes: `0` ok · `1` usage · `2` parse error · `3` key/decryption error · `4` integrity failure.

### Pack examples

```
pkglens pack ./MyGameFolder                              # Fast Pack, retail-encrypted (for CFW)
pkglens pack ./MyGameFolder --content-id UP0001-NPUB30910_00-EXAMPLE000000001 \
        --install-dir NPUB30910 --content-type GameExec  # Custom Pack (explicit ids)
pkglens pack ./MyGameFolder --debug                      # non-finalized (RPCS3 / dev)
```

A retail-encrypted package carries a valid CMAC but no ECDSA signature. That's fine for a jailbroken
(CFW) console, where the kernel patches skip the install-time signature check, but a stock retail
console will reject it. Use `--debug` for a self-contained non-finalized package that needs no key.

> **Running a repacked game:** the package installs on CFW, but a retail NPDRM `EBOOT.BIN` still
> needs its license (`act.dat` / `.rif`) to boot — or the EBOOT fake-signed to an fSELF, which
> `pack --resign` does for you.

## Feature coverage vs TrueAncestor

How PkgLens maps to the two TrueAncestor tools (PKG Repacker + SELF Resigner): **19 covered ·
1 partial · 3 out of scope by design · 1 not built.** The out-of-scope items all need Sony's private
signing keys, which PkgLens never uses. A themed version of this table is in
[`docs/coverage.html`](docs/coverage.html).

Legend: ✅ covered · 🟡 partial · ⛔ out of scope (needs signing keys) · ⬜ not yet built

### PKG Repacker

| # | Feature | Status | In PkgLens |
|---|---------|:------:|-----------|
| 1 | Fast Pack Pkg | ✅ | `pkglens pack` (infers IDs from PARAM.SFO) |
| 2 | Custom Pack Pkg | ✅ | `pack` with `--content-id`, `--install-dir`, … |
| 3 | Unpack Pkg | ✅ | `pkglens extract` · right-click · extract-all |
| 4 | Repack Pkg | ✅ | Replace a file → Save As (unsigned rebuild) |
| 5 | Finalize Pkg | ⛔ | Forges the retail ECDSA signature |
| 6 | Show Game Folder Info | ✅ | `pkglens folderinfo` · Pack page button |
| 7 | Edit PARAM.SFO | ✅ | SFO editor → Save As |
| 8 | Show Pkg Info | ✅ | `pkglens info` · Package info dialog |
| P | Patch PARAM.SFO *(switch)* | ✅ | Same SFO editor path |
| R | Resign EBOOT.BIN *(switch)* | ✅ | `pack --resign` — fake-signs while packing |

### SELF Resigner

| # | Feature | Status | In PkgLens |
|---|---------|:------:|-----------|
| 1 | Decrypt EBOOT.BIN Only | ✅ | `pkglens unself` (byte-exact vs real EBOOT) |
| 2 | Resign to NON-DRM EBOOT | ✅ | `unself` → `resign` (GUI chains it) |
| 3 | Resign to NPDRM EBOOT | ✅ | `resign --npdrm` |
| 4 | Decrypt SELF / SPRX Only | ✅ | `unself` (same SCE format) |
| 5 | Fast Resign NON-DRM SELF/SPRX | ✅ | Decrypt → fake-sign chain |
| 6 | Fast Resign NPDRM SELF/SPRX | ✅ | Same, `--npdrm` |
| 7 | Custom Sign → NON-DRM | ✅ | `resign --auth-id --vendor-id --app-version --type` |
| 8 | Custom Sign → NPDRM | ✅ | Custom fields + `--content-id` |
| 9 | Magic Patch EBOOT/SELF/SPRX | ✅ | `pkglens patch --sdk-version` + find/replace/offset |
| 10 | Decrypt DEX EBOOT (fSELF) | ✅ | `unself` handles fake-signed / debug SELFs |
| 11 | Resign to NON-DRM EBOOT — DEX/OFW | ⛔ | OFW signature check needs debug signing keys |
| 12 | Resign to NPDRM EBOOT — DEX/OFW | ⛔ | Same — signing keys, out of scope |
| O | Output Method *(switch)* | 🟡 | fSELF profile with settable fw-version + control flags; cap flags need signing |
| D | Compress Data *(switch)* | ⬜ | fSELF segments are stored uncompressed |

Beyond TrueAncestor, PkgLens adds a full inspector GUI, integrity `verify`, EDAT/SDAT decryption,
SELF-header inspect, a batch `scan` catalog, `--json` output, and cross-platform support.

## Keys

The standard and IDU/kiosk PS3 keys plus PSP/PSX and Vita package keys are bundled and selected
automatically, so supported retail and debug packages open with no setup. The only thing you supply
yourself is a **RAP** for licensed EDAT/EBOOT content (`--rap FILE`) — it's tied to your purchase,
so it can't be bundled.

You rarely need to add a custom package key. When installed, it joins the automatic candidate ring
without masking the bundled keys:

```
pkglens keys status                # show the bundled key + any override
pkglens keys import <32-hex-key>   # install an override key
```

See [`docs/keys.md`](docs/keys.md) for the full list of bundled keys and where overrides are read
from.

## Scope

PkgLens parses and inspects package structure and extracts content you own. It bundles only public
**decryption** keys — the NPDRM PKG AES key, appldr/NPDRM SELF keysets, and EDAT/SDAT keys, all
public for over a decade and embedded by every PS3 package tool. It bundles no private/signing keys
and no per-console (IDPS/EID) secrets, and `.gitignore` blocks `*.key`, `keys/`, and `*.pkg` so no
user-specific material or copyrighted package is committed.

Permanently out of scope:

- **Signature forgery.** PkgLens never recomputes the header CMAC or forges the ECDSA signature. A
  repacked retail package is unsigned and won't install on a stock console.
- **Finalize-for-retail and DEX/OFW resigning.** Making a package or SELF pass a stock,
  non-jailbroken console's signature check needs Sony's private keys.
- **Any private-key or per-console (IDPS/EID) operation.**

Fake-signed EBOOTs and repacked packages target patched loaders (CFW) only.

## Project layout

```
src/PkgLens.Core          # pure parsing + models + crypto (net8.0, no dependencies)
src/PkgLens.Cli           # thin CLI → builds the `pkglens` executable
src/PkgLens.Gui           # Avalonia MVVM desktop inspector
tests/PkgLens.Core.Tests  # synthetic fixtures + xUnit tests (no copyrighted data, no keys)
docs/keys.md              # which keys are bundled, and what you supply (RAP)
docs/coverage.html        # feature coverage vs TrueAncestor (open in a browser)
```

## Build & test

```
dotnet build   # whole solution
dotnet test    # xUnit tests
```

## License

Permissive — ISC or MIT (the original PkgView is ISC).

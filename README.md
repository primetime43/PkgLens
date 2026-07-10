# PkgLens

A modern, cross-platform, testable rewrite of the classic PS3 `.pkg` inspector. PkgLens opens a
package and shows what's inside — content id, metadata, the file tree, and `PARAM.SFO` — from a
clean, dependency-free parsing core shared by the CLI (and, later, a GUI).

See [`pkgview-ng.md`](pkgview-ng.md) for the full design spec.

## Status

Core, CLI, and a first GUI are implemented and tested (spec §11 steps 1–5, plus v0.2 items):

- **Header** parsing (big-endian), with friendly errors on bad magic / truncation.
- **Content id** decode → region / title-id / variant / name.
- **Metadata** block → typed model, unknown ids preserved as raw hex.
- **Decryptors** behind `IKeyProvider`: retail AES-128-CTR and debug SHA-1 keystream.
- **Item table** listing (names, sizes, dir/file, encrypted flag).
- **PARAM.SFO** parser → title / title-id / version / category, plus an **SFO editor** (edit
  values, then Save As repacks with the patched PARAM.SFO).
- **Extraction** (streamed) — single file, right-click, or **extract-all** to a folder (rebuilds the tree).
- **File viewing** — render images (ICON0/PIC1), show text, or a hex dump.
- **EDAT / SDAT decryption** — decrypt NPDRM data files: SDAT with no key, EDAT with its RAP
  (or the free key). Viewer decrypts EDATs automatically; `pkglens decrypt` on the CLI.
- **Modify / repack** — replace a file and save a new `.pkg` (re-encrypted). Signatures are **not**
  forged, so repacked *retail* packages are unsigned (won't install on a real console); *debug*
  packages repack cleanly. The original file is never modified in place.
- **Pack** — build a `.pkg` from a content folder (`pkglens pack <folder>`). *Fast Pack* infers the
  content id / install dir / content type from `PARAM.SFO`; pass options for *Custom Pack*. Defaults to
  retail-encrypted — the format a jailbroken (CFW) PS3 installs; the package is unsigned, which CFW
  doesn't care about (its patches skip the signature check), but stock retail consoles won't take it.
  Use `--debug` for a self-contained non-finalized package (RPCS3 / dev consoles, no key). PkgLens
  never forges signatures.
- **Integrity `verify`** — structural bounds, the header **SHA-1** digest, and the header **CMAC**
  (`AES-CMAC(gpkg_key, header[0x00:0x80])`, algorithm confirmed against real packages). Detects
  truncation, corruption, and modified/repacked headers. ECDSA signature check is not yet included.
- **SELF inspector** — read an `EBOOT.BIN` / `.self` / `.sprx` header (`pkglens self <eboot>`): program
  type, key revision, the embedded ELF header, and NPDRM content id — no keys needed.
- **Resign (ELF → fSELF)** — fake-sign a plaintext ELF into a SELF (`pkglens resign <elf>`) that boots on
  a jailbroken (CFW) PS3. No keys; the ELF is stored unencrypted with key revision `0x8000`, so it runs
  where signature checks are patched (CFW) — never on stock retail.
- **Unself (SELF → ELF)** — decrypt an encrypted `EBOOT.BIN` / `.self` back to its plaintext ELF
  (`pkglens unself <eboot>`), so you can inspect or fake-sign one you only have encrypted. Uses public
  decryption keysets (appldr / NPDRM, by key revision); free-license and debug SELFs need no key, a
  licensed NPDRM SELF needs its RAP (`--rap`). Verified **byte-for-byte** against a real retail
  `EBOOT.BIN → EBOOT.ELF` pair. No signing keys are involved — the output is a plaintext ELF, not a
  resigned file.
- **CLI**: `pkglens info | list | sfo | verify | extract | decrypt | self | unself | resign | pack | keys`, with `--json`, `--keys`, `--out`, `--filter`, `--rap`.
- **GUI** (Avalonia): classic menu/toolbar, folder tree + Name/Size list, viewer, Package-info dialog,
  drag-and-drop, light/dark theme, replace + Save-As (repack), and **Pack folder → .pkg** (File menu /
  toolbar) to build a package from a content folder.

Verified against synthetic in-test fixtures **and** a real retail package.

## Layout

```
src/PkgLens.Core   # no UI, no I/O policy — pure parsing + models (net8.0, no dependencies)
src/PkgLens.Cli    # thin CLI over the core  ->  builds the `pkglens` executable
src/PkgLens.Gui    # Avalonia MVVM desktop inspector
tests/PkgLens.Core.Tests  # synthetic PKG/SFO builders + xUnit tests (no copyrighted data, no keys)
docs/keys.md       # how to supply the runtime key for retail packages
```

## GUI

Launch it any of these ways:

- **Double-click `run-gui.cmd`** (repo root) — builds and launches; nothing to type.
- **`publish-gui.cmd`** — run once to get a standalone `dist\pkglens.gui.exe` plus a **Desktop
  shortcut** you can pin to Start/taskbar.
- **`dotnet run --project src/PkgLens.Gui`** — the manual/CLI way.
- **IDE** — open `PkgLens.slnx`, set `PkgLens.Gui` as startup, press F5.

Use **File → Open .pkg…** (or drag a `.pkg` onto the window). The left pane is a folder tree; the
right pane lists the selected folder's files with sizes; **Tools → Package info…** shows the
header, metadata, and PARAM.SFO. Debug packages open immediately. For retail packages, use
**Tools → Set retail key…** and paste the NPDRM PKG PS3 AES key once (saved to `~/.pkglens`), or
**Tools → Keys folder…** to point at an existing key file. Select a file and **File → Extract
selected…**. To build a package, use **File → Pack folder → .pkg…** (or the **Pack** toolbar button):
pick a content folder, confirm the pre-filled fields, and choose where to save. See
[`docs/keys.md`](docs/keys.md) for where to obtain the key.

## Build & test

```
dotnet build
dotnet test
```

## Usage

```
pkglens info  <pkg> [--keys DIR] [--json]   # header + content-id + SFO summary
pkglens list  <pkg> [--keys DIR] [--json]   # entry table
pkglens sfo   <pkg> [--keys DIR] [--json]   # dump PARAM.SFO key/values
pkglens self  <eboot>                       # inspect a SELF/EBOOT.BIN header (no keys)
pkglens unself <eboot> [--rap FILE] [--out FILE]  # decrypt a SELF → plaintext ELF
pkglens resign <elf> [--out FILE] [--npdrm] # ELF → fake-signed SELF (fSELF) for CFW
pkglens pack  <folder> [--out FILE]         # build a .pkg from a content folder
pkglens keys  import|status|where           # manage the runtime retail key
```

**Pack** turns a content folder back into a package. With no options it's *Fast Pack* — the content
id, install directory and content type are inferred from the folder's `PARAM.SFO`:

```
pkglens pack ./MyGameFolder                              # Fast Pack, retail-encrypted (for CFW)
pkglens pack ./MyGameFolder --content-id UP0001-NPUB30910_00-EXAMPLE000000001 \
        --install-dir NPUB30910 --content-type GameExec  # Custom Pack (explicit)
pkglens pack ./MyGameFolder --debug                      # non-finalized (RPCS3 / dev)
```

The default output is **retail-encrypted** (needs the runtime key). It carries a valid CMAC but no
ECDSA signature, so it's *unsigned* — which is fine for a **jailbroken (CFW) PS3**, where the kernel
patches skip the signature check on install, but a stock retail console will reject it. Use `--debug`
for a self-contained non-finalized package that needs no key and reads back in PkgLens / RPCS3.
PkgLens never forges signatures.

> **Note on running repacked games:** a package installs on CFW, but if it contains a retail NPDRM
> `EBOOT.BIN`, the game still needs its license (`act.dat`/`.rif`) to boot — or the EBOOT fake-signed
> to an fSELF. EBOOT resigning is planned (see the Resign tool in the GUI).

Debug (non-finalized) packages decrypt with no key at all. Retail (finalized) packages need the
NPDRM PKG PS3 AES key supplied **at runtime**. Install it once and every later run just works:

```
pkglens keys import <32-hex-key>   # writes ~/.pkglens/ps3_gpkg_aes.key, confirms it's the right key
pkglens keys status                # show the search path + whether a key is present
```

See [`docs/keys.md`](docs/keys.md) for where to obtain the key (it is never bundled). Exit codes:
`0` ok · `1` usage · `2` parse error · `3` key/decryption error · `4` integrity failure.

## Legal & scope

PkgLens **parses and inspects** package structure and extracts content the user is entitled to
(their own dumps / debug packages). Format parsing and metadata display are not circumvention.

- **No keys are bundled.** The repository ships derivation *logic* only; users supply their own
  key files at runtime. `.gitignore` blocks `*.key`, `keys/`, and `*.pkg` from ever being committed.
- **Repacking is unsigned.** You can rebuild a package you own with modified files, but PkgLens
  never recomputes the header CMAC or forges the ECDSA signature — a repacked retail package is
  unsigned and will not install on a retail console.
- **Out of scope, permanently:** signature forgery / re-signing, and any private-key or per-console
  (IDPS/EID) operation.

Licensing: keep it permissive (the original is ISC; MIT/ISC pairs well).

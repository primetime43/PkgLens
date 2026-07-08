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
- **PARAM.SFO** parser → title / title-id / version / category.
- **Entry extraction** (streamed) for content you own.
- **File viewing** — render images (ICON0/PIC1), show text, or a hex dump.
- **Modify / repack** — replace a file and save a new `.pkg` (re-encrypted). Signatures are **not**
  forged, so repacked *retail* packages are unsigned (won't install on a real console); *debug*
  packages repack cleanly. The original file is never modified in place.
- **Integrity `verify`** — structural bounds, the header **SHA-1** digest, and the header **CMAC**
  (`AES-CMAC(gpkg_key, header[0x00:0x80])`, algorithm confirmed against real packages). Detects
  truncation, corruption, and modified/repacked headers. ECDSA signature check is not yet included.
- **CLI**: `pkglens info | list | sfo | verify | keys`, with `--json` and `--keys`.
- **GUI** (Avalonia): classic menu/toolbar, folder tree + Name/Size list, viewer, Package-info dialog,
  drag-and-drop, light/dark theme, replace + Save-As (repack).

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
selected…**. See [`docs/keys.md`](docs/keys.md) for where to obtain the key.

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
pkglens keys  import|status|where           # manage the runtime retail key
```

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

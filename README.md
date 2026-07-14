# PkgLens

A modern, cross-platform, testable rewrite of the classic PS3 `.pkg` inspector. PkgLens opens a
package and shows what's inside — content id, metadata, the file tree, and `PARAM.SFO` — from a
clean, dependency-free parsing core shared by the CLI (and, later, a GUI).

See [`pkgview-ng.md`](pkgview-ng.md) for the full design spec.

## Status

Core, CLI, and a first GUI are implemented and tested (spec §11 steps 1–5, plus v0.2 items):

- **Header** parsing (big-endian), with friendly errors on bad magic / truncation.
- **PSP / PSVita packages** (platform 0x0002) read and extract alongside PS3. These share the PS3
  container; only the data key differs — resolved from the header key_type (`header[0xE7] & 7`): PSP/PSX
  use the bundled PSP key, PSVita derives its key from the data_riv (bundled Vita keys 2/3/4). PSP/PSX
  packages additionally select a **per-entry** key — content marked type `0x90` uses the PSP key, other
  entries (PARAM.SFO, ICON0.PNG, …) use the PS3 gpkg key — matching pkg2zip. Verified end-to-end against
  a real PSP minis package (`info` / `list` / `sfo` / `extract` all work; extracted PNG/SFO/EDAT files
  carry correct magic). PSP-keyed entries are flagged in listings (`[PSP]` in `list`, a `psp` field in
  `--json`, a badge in the GUI file list) so an embedded PSP entry in a PS3 package isn't mistaken for
  corruption when the PS3 key can't decode its name.
- **Content id** decode → region / title-id / variant / name.
- **Metadata** block → typed model, unknown ids preserved as raw hex.
- **Decryptors** behind `IKeyProvider`: retail AES-128-CTR and debug SHA-1 keystream.
- **Item table** listing (names, sizes, dir/file, encrypted flag).
- **PARAM.SFO** parser → title / title-id / version / category, plus an **SFO editor** (edit
  values, then Save As repacks with the patched PARAM.SFO).
- **Extraction** (streamed) — single file, right-click, or **extract-all** to a folder (rebuilds the tree).
- **File viewing** — render images (ICON0/PIC1), show text, or a hex dump.
- **PSP EDAT / PGD decryption** — decrypt PSP `\0PSPEDAT` files and bare `\0PGD` files (e.g.
  DOCINFO.EDAT, DOCUMENT.DAT) via the PSP AMCTRL/KIRK/PGD pipeline. **Fixed-key content only** (no RAP
  needed); fuse-bound (per-console) content is out of scope. The PGD's built-in BBMac checks make a
  successful decrypt self-verifying — validated against a real PSP minis package. A different format
  from PS3 NPDRM EDAT, auto-detected by magic.
- **EDAT / SDAT decryption** — decrypt NPDRM data files: SDAT with no key, EDAT with its RAP
  (or the free key). Viewer decrypts EDATs automatically; `pkglens decrypt` on the CLI. **Compressed**
  EDATs are supported too (LZ decompressor ported from RPCS3); the decrypt/metadata pipeline is verified
  byte-for-byte against real EDATs, and the compressed path's plumbing is tested end-to-end, though the LZ
  core awaits a check against a real compressed sample.
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
  type, key revision, the embedded ELF header, the **segment table** (offset / size / compressed /
  encrypted per segment), the **control-info blocks** (control flags, file digest, NPDRM), the
  **firmware version**, and NPDRM content id — all no keys needed.
- **Resign (ELF → fSELF)** — fake-sign a plaintext ELF into a SELF (`pkglens resign <elf>`) that boots on
  a jailbroken (CFW) PS3. No keys; the ELF is stored unencrypted with key revision `0x8000`, so it runs
  where signature checks are patched (CFW) — never on stock retail. **Custom Sign** lets you override the
  `app_info` fields — auth id, vendor id, app version, program type — plus the **firmware version** (in the
  type-2 digest control block) and the 32-byte **control flags** (a type-1 control-info block), and the
  NPDRM content id, plus the NPDRM **license type** and **app type** (`--auth-id`, `--vendor-id`,
  `--app-version`, `--type`, `--fw-version M.NN`, `--control-flags 64-HEX`, `--content-id`,
  `--np-license-type FREE|LOCAL|NETWORK`, `--np-app-type SPRX|EXEC|USPRX|UEXEC`, or the Pack/Resign page's
  *Custom sign fields* panel); unset fields use the fSELF defaults. (Capability flags and NPDRM
  real-filename aren't settable: they live in the encrypted-metadata / real-signature path a keyless
  fSELF doesn't have.)
- **Resign-on-pack** — pack a content folder with `--resign` (or the Pack page's *Resign EBOOT.BIN as it
  packs* checkbox) and PkgLens fake-signs the folder's `EBOOT.BIN` to an fSELF as it builds, so the packaged
  game boots on CFW without its license. A licensed EBOOT needs its RAP (`--rap` / the RAP picker) to decrypt
  first; free-license, debug, plain-ELF and already-fSELF EBOOTs need nothing.
- **Game folder info** — a read-only report on an extracted content folder (`pkglens folderinfo <folder>`,
  or the Tools → *Folder info…* menu / Pack page's *Show folder info…* button): content id / title from
  `PARAM.SFO`, file counts and size, the EBOOT's sign state (encrypted / fake-signed / plain ELF), and any
  EDAT/SDAT files with their license.
- **Batch scan / catalog** — walk a folder of packages and emit one row each
  (`pkglens scan <dir> [--recursive] [--json | --csv]`): content id, title, title id, version, size,
  platform (PS3 / PSP / PSVita), retail-or-debug, and whether it decrypted. A package that won't parse
  becomes an error row rather than aborting the scan — the whole thing is read-only and streams headers
  only. Use `--csv` to import a library into a spreadsheet, `--json` to pipe it into other tooling.
- **Magic Patch** — apply static byte edits to an EBOOT/ELF so a game boots on CFW (`pkglens patch <eboot>`,
  or the Resign page's *Magic patch* section). The headline patch **lowers the firmware/SDK version** the
  executable demands (`--sdk-version 4.00`) — stored in the ELF's `sys_process_param` block — so a title built
  against a newer SDK runs on an older CFW. Generic `--find/--replace` and `--at OFFSET=HEX` patches cover
  other known edits. Accepts an encrypted EBOOT (decrypted first, then re-fake-signed). Verified end-to-end
  against the real golden EBOOT (4.40 → 4.00, byte-checked after a decrypt/patch/resign/re-decrypt round-trip).
- **Unself (SELF → ELF)** — decrypt an encrypted `EBOOT.BIN` / `.self` back to its plaintext ELF
  (`pkglens unself <eboot>`), so you can inspect or fake-sign one you only have encrypted. Uses public
  decryption keysets (appldr / NPDRM, by key revision); free-license and debug SELFs need no key, a
  licensed NPDRM SELF needs its RAP (`--rap`) or its raw klicensee (`--klic`). Also decrypts **fake-signed / debug (fSELF) EBOOTs**
  (key version 0x80 / 0xC0) — the ELF is stored in the clear, so no keys are needed. Verified
  **byte-for-byte** against a real retail `EBOOT.BIN → EBOOT.ELF` pair *and* on the fSELF round-trip.
  No signing keys are involved — the output is a plaintext ELF, not a resigned file.
- **CLI**: `pkglens info | list | sfo | verify | extract | decrypt | self | unself | resign | patch | pack | folderinfo | scan | keys`, with `--json`, `--csv`, `--keys`, `--out`, `--filter`, `--rap`, `--klic`.
- **GUI** (Avalonia): classic menu/toolbar, folder tree + Name/Size list, viewer, Package-info dialog,
  drag-and-drop, light/dark theme, replace + Save-As (repack), and **Pack folder → .pkg** (File menu /
  toolbar) to build a package from a content folder.

Verified against synthetic in-test fixtures **and** a real retail package.

## Feature coverage vs TrueAncestor

How PkgLens maps to the two TrueAncestor tools (PKG Repacker + SELF Resigner). A rich, themed version of
this table is in [`docs/coverage.html`](docs/coverage.html) — open it in a browser.

**19 covered · 1 partial · 3 out of scope by design · 1 not built.** The out-of-scope items all require
Sony's private signing keys (making content pass a *stock, non-jailbroken* console's signature check),
which PkgLens never does.

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

**Beyond TrueAncestor**, PkgLens also has a full package-inspector GUI (tree + list + image/text/hex viewer),
integrity `verify` (SHA-1 + AES-CMAC), EDAT/SDAT decryption (incl. compressed), SELF-header inspect,
batch **`scan`** to catalog a whole library (`--json` / `--csv`), `--json` output, and runs cross-platform.

## Layout

```
src/PkgLens.Core   # no UI, no I/O policy — pure parsing + models (net8.0, no dependencies)
src/PkgLens.Cli    # thin CLI over the core  ->  builds the `pkglens` executable
src/PkgLens.Gui    # Avalonia MVVM desktop inspector
tests/PkgLens.Core.Tests  # synthetic PKG/SFO builders + xUnit tests (no copyrighted data, no keys)
docs/keys.md       # which keys are bundled, and what you still supply (RAP)
docs/coverage.html # feature coverage vs TrueAncestor (themed, open in a browser)
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
header, metadata, and PARAM.SFO. Debug and retail packages both open immediately — the standard
retail key is built in. (You only need **Tools → Import override key…** for a non-standard package,
e.g. an IDU/kiosk key.) Select a file and **File → Extract
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
pkglens unself <eboot> [--rap FILE | --klic HEX] [--out FILE]  # decrypt a SELF → plaintext ELF
pkglens resign <elf> [--out FILE] [--npdrm] # ELF → fake-signed SELF (fSELF) for CFW
pkglens patch <eboot> [--sdk-version 4.00] [--find HEX --replace HEX] [--at OFF=HEX]  # magic-patch an EBOOT
pkglens folderinfo <folder> [--json]        # report on an extracted content folder
pkglens scan  <dir> [--recursive] [--json | --csv]   # catalog a folder of .pkg files, one row each
pkglens pack  <folder> [--out FILE] [--resign [--rap FILE]]   # build a .pkg (optionally resign EBOOT)
pkglens keys  import|status|where           # manage an optional override retail key
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

Both debug (non-finalized) and retail (finalized) packages decrypt out of the box — the standard
NPDRM PKG PS3 AES key is a public decryption key and is **bundled**. The `keys` command only manages
an optional *override* key file for a non-standard package (e.g. an IDU/kiosk key):

```
pkglens keys status                # show the bundled key + any override
pkglens keys import <32-hex-key>   # install an override (writes ~/.pkglens/ps3_gpkg_aes.key)
```

The only material you supply yourself is a **RAP** for a *licensed* EDAT/EBOOT (`--rap FILE`) — it's
tied to your purchase, so it can't be bundled. See [`docs/keys.md`](docs/keys.md). Exit codes:
`0` ok · `1` usage · `2` parse error · `3` key/decryption error · `4` integrity failure.

## Legal & scope

PkgLens **parses and inspects** package structure and extracts content the user is entitled to
(their own dumps / debug packages). Format parsing and metadata display are not circumvention.

- **Only public decryption keys are bundled.** PkgLens ships the well-known, universal PS3
  *decryption* keys (the NPDRM PKG AES key, appldr/NPDRM SELF keysets, EDAT/SDAT keys) so packages
  you own open with no setup — the same keys every PS3 package tool has embedded for over a decade.
  It bundles **no** private/signing keys and **no** per-console (IDPS/EID) secrets, and users still
  supply their own per-purchase **RAP** license for licensed content. `.gitignore` blocks `*.key`,
  `keys/`, and `*.pkg` so no user-specific key material or copyrighted package is ever committed.
- **Repacking is unsigned.** You can rebuild a package you own with modified files, but PkgLens
  never recomputes the header CMAC or forges the ECDSA signature — a repacked retail package is
  unsigned and will not install on a retail console.
- **Out of scope, permanently:** signature forgery / re-signing, and any private-key or per-console
  (IDPS/EID) operation. This includes **Finalize-for-retail** and **DEX/OFW resigning**: making a package
  or SELF pass a *stock, non-jailbroken* console's signature check needs Sony's private signing keys,
  which PkgLens never uses. The keyless fSELF path targets *patched* loaders (CFW) only.

Licensing: keep it permissive (the original is ISC; MIT/ISC pairs well).

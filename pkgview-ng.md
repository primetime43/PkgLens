# PkgView-NG — Design & Project Spec

A modern, cross-platform rewrite of [Sorvigolova/PkgView](https://github.com/Sorvigolova/PkgView) (forked from [ifcaro/PkgView](https://github.com/ifcaro/PkgView)), the classic PS3 `.pkg` inspector.

The original was VB.NET / WinForms, Windows-only, written 2010–2012 ("at the time I was not very good at programming," per the author). This project keeps the same *purpose* — open a PKG, show what's inside — but rebuilds it on a maintainable, cross-platform, testable foundation.

> **Scope note:** This is a **read/inspect + extract-what-you-own** tool for package *analysis* and preservation. Parsing package metadata and listing entries is format work, not circumvention. Decryption features apply to retail-key-derivable content and debug packages; anything requiring per-console or private signing keys is explicitly out of scope. See [Legal & Scope](#legal--scope).

---

## 1. Goals

- **Cross-platform**: Windows, Linux, macOS from one codebase.
- **Fast & memory-light**: stream large PKGs (multi-GB) without loading them fully into RAM.
- **Correct**: a well-tested parser with a spec-referenced implementation and fixtures.
- **Two front doors**: a library core + a thin CLI + a GUI, so the same parser powers all of them.
- **Extensible**: PS3 first, with the format layer designed so PSP/PSVita (and later PS4) can slot in.

### Non-goals
- Re-signing, forging, or installing packages.
- Bundling or distributing any console keys.
- Anything that needs private ECDSA keys or per-console (IDPS/EID) secrets.

---

## 2. Recommended stack

The original is .NET, and you've got a .NET background, so the lowest-friction modern path is:

**Primary recommendation — C# / .NET 8+**
- **Core**: `PkgView.Core` — a dependency-free class library (`netstandard2.1`/`net8.0`) doing all parsing.
- **CLI**: `PkgView.Cli` — `System.CommandLine`.
- **GUI**: **Avalonia UI** (MVVM, truly cross-platform, WinForms/WPF-like mental model). This is the natural upgrade path from WinForms.
- **Tests**: xUnit + small binary fixtures.
- Crypto: built-in `System.Security.Cryptography` (AES-CTR built from AES-ECB, SHA-1, CMAC via `AesCmac` or a tiny impl).

**Alternatives** (pick based on taste, not necessity):

| Stack | GUI | Pros | Cons |
|---|---|---|---|
| **Rust** | Tauri or egui | Tiny binaries, great byte-wrangling, fearless concurrency | More upfront work; you'd be porting from scratch |
| **Go** | Wails or Fyne | Simple, fast builds, easy cross-compile | Generics/crypto ergonomics weaker than Rust |
| **TypeScript** | Electron/Tauri + web UI | Best UI ergonomics, easy to make it pretty | Heavy runtime; binary parsing is clunkier |

The rest of this doc is written stack-agnostically; examples lean C#.

---

## 3. What a PS3 PKG actually is

A PKG is the container Sony uses for PSN downloads, DLC, patches, themes, and homebrew. Structure at a glance:

```
┌────────────────────────────────────────┐
│ Main header (0x80 bytes, big-endian)   │  magic, type, offsets, content-id, RIV, digests
├────────────────────────────────────────┤
│ (Extended header / signature area)     │  hashes + ECDSA signature over the header
├────────────────────────────────────────┤
│ Metadata block (typed TLV entries)     │  DRM type, content type, install dir, flags, ...
├────────────────────────────────────────┤
│ Item/file entry table (encrypted)      │  per-file: name offset+size, data offset+size, flags
├────────────────────────────────────────┤
│ File data (encrypted)                  │  the actual PARAM.SFO, ICON0.PNG, EBOOT, etc.
└────────────────────────────────────────┘
```

### 3.1 Main header (the fields that matter)

All multi-byte values are **big-endian**.

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0x00 | 4 | `magic` | `0x7F504B47` = `\x7F` `P` `K` `G` |
| 0x04 | 2 | `revision` | `0x8000` finalized/retail, `0x0000` non-finalized/debug |
| 0x06 | 2 | `type` | `0x0001` = PS3, `0x0002` = PSP/PSVita |
| 0x08 | 4 | `metadata_offset` | start of the typed metadata block |
| 0x0C | 4 | `metadata_count` | number of metadata entries |
| 0x10 | 4 | `metadata_size` | size of metadata block |
| 0x14 | 4 | `item_count` | number of files + directories |
| 0x18 | 8 | `total_size` | full PKG size |
| 0x20 | 8 | `data_offset` | start of the (encrypted) item table + data |
| 0x28 | 8 | `data_size` | encrypted region size |
| 0x30 | 0x30 | `content_id` | ASCII, null-padded, e.g. `UP0001-NPUB30910_00-EXAMPLE000000001` |
| 0x60 | 0x10 | `digest` | QA / integrity digest |
| 0x70 | 0x10 | `data_riv` | AES-CTR IV used to derive/seed the keystream |
| 0x80 | 0x40 | `header_cmac + signature` | CMAC (0x10) + ECDSA sig (0x28) over the header |

> **Source of truth:** verify every offset against the psdevwiki *PKG files* page before you trust this table — treat this as an orientation map, not the spec. Write fixtures that assert against real files.

### 3.2 Content ID

`content_id` decomposes into useful display fields:

```
UP0001 - NPUB30910 _00 - EXAMPLE000000001
└─┬──┘   └───┬────┘ └┬┘   └──────┬───────┘
 region    title-id  variant     internal name
```

### 3.3 Metadata block

A sequence of typed entries (each: `u32 id`, `u32 size`, then `size` bytes). Common IDs carry: DRM type, content type (e.g. GameData, GameExec, Theme, License), package flags, the "index table" params, the install directory / title-id, and QA/PARAM info. Build these as an enum + a dictionary in the model; unknown IDs are surfaced as raw hex rather than dropped.

### 3.4 Encryption & key derivation

- **Retail (finalized) PS3 packages** decrypt with **AES-128-CTR**, using the well-known PS3 gpkg AES key together with the header's `data_riv` as the counter seed. The item table and each file share the same keystream space, offset by their position in the data region.
- **PSP / PSVita packages** use their own AES keys (separate keysets).
- **Debug (non-finalized) packages** don't use AES-CTR: the keystream is an incrementing **SHA-1 hash** of a block derived from the header digest area, XORed against the data.

The parser should expose a `KeyProvider` abstraction:

```csharp
public interface IKeyProvider {
    // Returns the AES key + initial counter for a finalized package,
    // or the SHA-1 seed for a debug package. Never bundled in the repo.
    bool TryResolve(PkgHeader header, out DecryptionContext ctx);
}
```

Keys are **loaded at runtime** from a user-supplied file/dir (mirroring how PS3 tooling reads `~/.ps3` / `%USERPROFILE%\ps3keys`). The repo ships *zero* key material — only the derivation logic and a documented file layout.

### 3.5 PARAM.SFO

Almost every PKG contains a `PARAM.SFO` — a small key/value blob with the human-readable metadata users actually want: `TITLE`, `TITLE_ID`, `CATEGORY`, `APP_VER`, `VERSION`, `PARENTAL_LEVEL`, etc. A dedicated `SfoParser` turns this into the fields the GUI shows up top. `ICON0.PNG` / `PIC1.PNG` give you thumbnails for a nice UI.

---

## 4. Architecture

```
pkgview-ng/
├─ src/
│  ├─ PkgView.Core/            # no UI, no I/O policy — pure parsing + models
│  │  ├─ Formats/
│  │  │  ├─ Ps3/               # header, metadata, item table, decryptors
│  │  │  ├─ Psp/               # (later) shares interfaces with Ps3
│  │  │  └─ IPackageReader.cs  # common surface for all formats
│  │  ├─ Crypto/               # AES-CTR-from-ECB, SHA1 keystream, CMAC verify
│  │  ├─ Sfo/                  # PARAM.SFO parser
│  │  ├─ Keys/                 # IKeyProvider + file-based loader
│  │  └─ Models/               # PkgInfo, PkgEntry, ContentId, SfoTable
│  ├─ PkgView.Cli/            # thin: info / list / extract / verify
│  └─ PkgView.Gui/            # Avalonia MVVM
├─ tests/
│  └─ PkgView.Core.Tests/     # fixtures: tiny synthetic PKGs + real-file assertions
├─ docs/
│  ├─ pkg-format.md           # your own notes, cross-referenced to psdevwiki
│  └─ keys.md                 # how to supply keys, expected file layout
└─ README.md
```

**Key design rule:** `PkgView.Core` never opens a file dialog, never touches a UI, never hardcodes a key. It takes a `Stream` and an `IKeyProvider`, and returns models. Everything else is a consumer.

### Reader flow

```
open(Stream) → read main header → validate magic/type
             → (optional) verify header CMAC / signature → report tampering
             → parse metadata block → PkgInfo
             → derive DecryptionContext via IKeyProvider
             → decrypt item table (streamed) → IEnumerable<PkgEntry>
             → on demand: decrypt a single entry's data range → Stream
             → locate PARAM.SFO / ICON0.PNG → enrich display model
```

Streaming matters: never materialize a 4 GB package. Decrypt entry ranges lazily when the user clicks a file or requests extraction.

---

## 5. Feature roadmap

### MVP (v0.1)
- [ ] Parse & validate PS3 main header; friendly errors on bad magic/truncation.
- [ ] Decode `content_id` into region / title-id / name.
- [ ] Parse metadata block → typed model (+ raw hex for unknowns).
- [ ] Retail AES-CTR + debug SHA-1 decryptors behind `IKeyProvider`.
- [ ] List item table (names, sizes, dir/file, encrypted flag).
- [ ] Parse `PARAM.SFO`; show title / title-id / version / category.
- [ ] CLI: `pkgview info <file>` and `pkgview list <file>`.

### v0.2 — extraction & UI
- [ ] Extract selected entries / extract-all (streamed) for content you own.
- [ ] Render `ICON0.PNG` / `PIC1.PNG` in the GUI header.
- [ ] Avalonia GUI: header pane + metadata pane + file tree + SFO pane.
- [ ] `pkgview extract <file> [--out dir] [--filter glob]`.

### v0.3 — integrity & breadth
- [ ] Verify header CMAC + data digests; flag mismatches clearly.
- [ ] PSP/PSVita package support via the shared format interface.
- [ ] Drag-and-drop, recent files, dark mode.
- [ ] `pkgview verify <file>` for CI-style checks.

### Later / maybe
- [ ] PS4 `.pkg` (`CNT`) — different format, own module.
- [ ] Batch mode: scan a folder, emit a CSV/JSON catalog (great for a media/preservation library).
- [ ] JSON output (`--json`) so it composes with other tooling.

---

## 6. CLI sketch

```
pkgview info    <pkg>                 # header + content-id + SFO summary
pkgview list    <pkg> [--json]        # entry table
pkgview extract <pkg> [--out DIR] [--filter GLOB]
pkgview verify  <pkg>                 # magic, CMAC, digests
pkgview sfo     <pkg>                 # dump PARAM.SFO key/values

# keys resolved from --keys DIR, $PKGVIEW_KEYS, or the platform default dir
```

Exit codes: `0` ok, `2` parse error, `3` key/decryption error, `4` integrity failure. Machine-friendly `--json` on every subcommand.

---

## 7. GUI layout (Avalonia)

```
┌───────────────────────────────────────────────┐
│ [ICON0]  TITLE (TITLE_ID)      v1.02  Retail   │  ← header strip from SFO + header
├──────────────┬────────────────────────────────┤
│ File tree    │  Details tab | Metadata | SFO   │
│  EBOOT.BIN   │                                 │
│  PARAM.SFO   │  name / offset / size / flags   │
│  USRDIR/...  │  [Extract]  [Extract all]       │
└──────────────┴────────────────────────────────┘
 status bar: path · package size · #entries · integrity ✓/✗
```

MVVM: `PackageViewModel` wraps `PkgInfo`; the tree binds to `Entries`; extraction runs off-thread with progress. Keep the core synchronous/streaming and let the GUI own the async/progress concerns.

---

## 8. Testing strategy

- **Synthetic fixtures**: generate tiny valid PKGs in-test (a couple of fake entries, a minimal SFO) so parser tests need no copyrighted content and run in CI.
- **Golden files**: for real packages you own, commit only *expected-output JSON*, not the PKGs. Assert the parser reproduces them.
- **Property tests**: round-trip content-id encode/decode; keystream determinism; truncated-input fuzzing (parser must fail gracefully, never crash/hang).
- **Endianness & bounds**: explicit tests — this is where the original-era tools most often broke.

---

## 9. Porting notes from the VB.NET original

- The old code is WinForms + synchronous file reads; treat it as a **behavioral reference**, not a template. Read it to learn *which fields it displays and how it interprets them*, then re-derive against the spec.
- Watch for **big-endian** reads: VB code often did manual byte swaps; use explicit `BinaryPrimitives.ReadUInt32BigEndian` etc. rather than reimplementing swaps.
- The original likely conflated "header parse" and "UI populate." Split them — that's the single biggest maintainability win.
- Anything in the old source that hardcodes key bytes: **do not port**. Move it behind `IKeyProvider` and a runtime key file.

---

## 10. Legal & Scope

- This tool **parses and inspects** package structure and **extracts content the user is entitled to** (their own dumps / debug packages). Format parsing and metadata display are not circumvention.
- **No keys are bundled.** The repo contains derivation *logic* only; users supply their own key files at runtime, matching established PS3-tooling conventions.
- **Out of scope, permanently:** re-signing, signature forgery, package creation for install, private-key or per-console (IDPS/EID) operations, or anything whose purpose is defeating protections on content you don't own.
- Include a clear README disclaimer and keep the license permissive (the original is ISC; MIT/ISC pairs well).

---

## 11. First steps

1. `dotnet new classlib -n PkgView.Core` + `dotnet new console -n PkgView.Cli` + xUnit test project; wire a solution.
2. Implement `PkgHeader` parsing against a synthetic fixture — get `magic`, `type`, offsets, `content_id` green.
3. Add the SFO parser (self-contained, no crypto) — instant visible payoff (`pkgview sfo`).
4. Add `IKeyProvider` + retail AES-CTR; decrypt the item table; ship `pkgview list`.
5. Only then start the Avalonia GUI on top of the now-proven core.

Build the core until `info`/`list`/`sfo` are solid on the CLI; the GUI is the easy part once the parser is trustworthy.

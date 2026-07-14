# PkgLens — Design & Project Spec

A cross-platform rewrite of [Sorvigolova/PkgView](https://github.com/Sorvigolova/PkgView) (forked from [ifcaro/PkgView](https://github.com/ifcaro/PkgView)), the classic PS3 `.pkg` inspector.

The original was VB.NET / WinForms, Windows-only, written 2010–2012 ("at the time I was not very good at programming," per the author). This project keeps the same *purpose* — open a PKG, show what's inside — but rebuilds it on a maintainable, cross-platform, testable foundation.

> **This spec predates the implementation.** The project shipped as **PkgLens** (not the spec's PkgView), and a few design calls changed along the way. Where this document and the code disagree, the code wins. Deliberate deviations are called out inline; the biggest are: the name, a hand-rolled CLI arg parser instead of `System.CommandLine`, and public decryption keys bundled for convenience (never private or signing keys). See [`README.md`](README.md) for the shipped feature set and [`CLAUDE.md`](CLAUDE.md) for the current architecture.

> **Scope note:** This is a read/inspect, extract-what-you-own, and repack-for-CFW tool for package analysis and preservation. Parsing metadata and listing entries is format work, not circumvention. Decryption uses public keys (bundled) or a user's own per-purchase license (RAP). Anything needing per-console or private *signing* keys — making content pass a stock retail console's checks — is out of scope. See [Legal & Scope](#legal--scope).

---

## 1. Goals

- **Cross-platform**: Windows, Linux, macOS from one codebase.
- **Fast & memory-light**: stream large PKGs (multi-GB) without loading them fully into RAM.
- **Correct**: a well-tested parser with a spec-referenced implementation and fixtures.
- **Two front doors**: a library core + a thin CLI + a GUI, so the same parser powers all of them.
- **Extensible**: PS3 first, with the format layer designed so PSP/PSVita (and later PS4) can slot in.

### Non-goals
- Forging Sony signatures, or making any package/SELF pass a stock (non-jailbroken) console's checks. Fake-signing (fSELF) and repacking are supported, but the output is unsigned and targets custom firmware (CFW) only.
- Bundling private or signing keys, or per-console (IDPS/EID) secrets. Public *decryption* keys, embedded by every PS3 tool for a decade, are bundled for convenience.
- Anything that needs private ECDSA keys or per-console (IDPS/EID) secrets.

---

## 2. Recommended stack

The original is .NET, and the maintainer has a .NET background, so the chosen path is C# / .NET 8:

**Shipped stack — C# / .NET 8**
- **Core**: `PkgLens.Core` — a dependency-free class library (`net8.0`) doing all parsing and crypto.
- **CLI**: `PkgLens.Cli` — builds the `pkglens` executable. *Deviation:* a hand-rolled zero-dependency arg parser, not `System.CommandLine`.
- **GUI**: `PkgLens.Gui` — **Avalonia UI 11** (MVVM via `CommunityToolkit.Mvvm`, compiled bindings on). The natural upgrade path from WinForms.
- **Tests**: xUnit + in-memory synthetic fixtures.
- Crypto: built-in `System.Security.Cryptography` (AES-CTR built from AES-ECB, SHA-1, CMAC), plus a ported LZRC/EDAT range coder and a PSP KIRK/AMCTRL engine.

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

The parser exposes an `IKeyProvider` abstraction that resolves a `DecryptionContext` from a header:

```csharp
public interface IKeyProvider {
    // Returns the AES key + initial counter for a finalized package,
    // or the SHA-1 seed for a debug package.
    bool TryResolve(PkgHeader header, out DecryptionContext ctx);
}
```

The standard NPDRM PKG AES key is a **public decryption key**, so it's bundled (`BundledKeys`) and packages open with no setup — matching every other PS3 tool. A user may still drop in an *override* key file for a non-standard package (e.g. an IDU/kiosk key); `FileKeyProvider` checks `--keys DIR`, `$PKGLENS_KEYS`, then the platform default (`~/.pkglens`) before falling back to the bundled key. What is **never** bundled: any private/signing key, per-console (IDPS/EID) secret, or per-purchase RAP — a licensed EDAT/EBOOT still needs its own RAP passed at runtime. See [`docs/keys.md`](docs/keys.md).

### 3.5 PARAM.SFO

Almost every PKG contains a `PARAM.SFO` — a small key/value blob with the human-readable metadata users actually want: `TITLE`, `TITLE_ID`, `CATEGORY`, `APP_VER`, `VERSION`, `PARENTAL_LEVEL`, etc. A dedicated `SfoParser` turns this into the fields the GUI shows up top. `ICON0.PNG` / `PIC1.PNG` give you thumbnails for a nice UI.

---

## 4. Architecture

The core is organized **platform-first** — `Shared/` for what all platforms use (the PKG container is identical across PS3/PSP/PSVita), then `Ps3/` and `Psp/` for the parts that differ:

```
PkgLens/
├─ src/
│  ├─ PkgLens.Core/            # no UI, no I/O policy, no NuGet deps — pure parsing + models + crypto
│  │  ├─ Shared/               # PkgLens.Core.Shared.*
│  │  │  ├─ Formats/           #   PkgContainerReader (the one container reader), IPackageReader
│  │  │  ├─ Models/            #   PkgHeader, PkgEntry, ContentId, metadata model
│  │  │  ├─ Sfo/               #   PARAM.SFO parser + writer
│  │  │  ├─ Keys/              #   IKeyProvider, DecryptionContext, BundledKeys
│  │  │  ├─ Crypto/            #   AES-CTR-from-ECB, SHA-1 keystream, CMAC, shared LZRC/EdatLz
│  │  │  └─ (facades)          #   PkgReader, PkgWriter, PkgBuilder, PkgVerifier, PackageScanner
│  │  ├─ Ps3/                  # PkgLens.Core.Ps3.* — Self/ (SELF resign/patch), Npd/ (EDAT/SDAT, RAP)
│  │  └─ Psp/                  # PkgLens.Core.Psp.* — Edat/, Iso/ (NPUMDIMG), Crypto/ (KIRK), Pbp/
│  ├─ PkgLens.Cli/            # thin CLI → the `pkglens` executable (subcommand-per-file)
│  └─ PkgLens.Gui/            # Avalonia MVVM
├─ tests/
│  └─ PkgLens.Core.Tests/     # synthetic PKG/SELF/SFO builders + xUnit (no copyrighted data, no keys)
├─ docs/
│  ├─ keys.md                 # which keys are bundled, and what you supply (RAP)
│  └─ coverage.html           # feature coverage vs TrueAncestor
└─ README.md
```

**Key design rule:** `PkgLens.Core` never opens a file dialog, never touches a UI, never applies I/O policy. It takes a `Stream` and an `IKeyProvider`, and returns models. Everything else is a consumer.

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

The original MVP → v0.3 plan all shipped, and the project grew well beyond an inspector into a full
repack / resign toolkit. Current status:

### Inspect & extract — done
- [x] Parse & validate the PS3 main header; friendly errors on bad magic/truncation.
- [x] Decode `content_id` into region / title-id / name.
- [x] Parse the metadata block → typed model (raw hex for unknown ids).
- [x] Retail AES-CTR + debug SHA-1 decryptors behind `IKeyProvider`.
- [x] List the item table (names, sizes, dir/file, encrypted flag).
- [x] Parse `PARAM.SFO`; show + edit title / title-id / version / category.
- [x] Extract selected entries / extract-all (streamed).
- [x] Render `ICON0.PNG` / `PIC1.PNG`, text, and hex in the GUI viewer.
- [x] Avalonia GUI with drag-and-drop and light/dark theme.

### Integrity & breadth — done
- [x] `verify`: structural bounds, header SHA-1 digest, header AES-CMAC, and a read-only ECDSA check against Sony's public NPDRM key.
- [x] PSP / PSVita packages via the shared container reader (per-entry keying for PSP/PSX).
- [x] Batch `scan` a folder → one row each, `--json` / `--csv`.
- [x] `--json` on the inspection subcommands.

### Repack, pack & resign — done (beyond the original spec)
- [x] Replace a file and Save As a rebuilt (unsigned) `.pkg`.
- [x] `pack` a content folder → `.pkg` (Fast/Custom Pack; retail-for-CFW or `--debug`).
- [x] SELF tools: `self` (inspect), `unself` (decrypt → ELF), `resign` (ELF → fSELF), `patch` (magic/byte patch).
- [x] `pack --resign` fake-signs a folder's EBOOT while packing.

### PSP / minis — done
- [x] PSP EDAT / PGD decryption (fixed-key content).
- [x] `undoc` (DOCUMENT.DAT → PNG pages), `unpbp` (split EBOOT.PBP), `psar decrypt` (NPUMDIMG → keyless `.iso`).

### Later / maybe — not built
- [ ] PS4 `.pkg` (`CNT`) — a different format; slots in as its own `IPackageReader`.
- [ ] `--json` on the remaining action subcommands (pack/resign/patch).

---

## 6. CLI sketch

The shipped CLI grew past this sketch; the inspection core is below. See [`README.md`](README.md) for the full list including `pack`, `resign`, `unself`, `patch`, `unpbp`, `undoc`, and `psar`.

```
pkglens info    <pkg> [--json]        # header + content-id + SFO summary
pkglens list    <pkg> [--json]        # entry table
pkglens extract <pkg> [--out DIR] [--filter GLOB]
pkglens verify  <pkg>                 # bounds, CMAC, SHA-1, ECDSA (read-only)
pkglens sfo     <pkg> [--json]        # dump PARAM.SFO key/values

# override keys resolved from --keys DIR, $PKGLENS_KEYS, or ~/.pkglens (bundled key used otherwise)
```

Exit codes: `0` ok, `1` usage, `2` parse error, `3` key/decryption error, `4` integrity failure.

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

MVVM: `PackageViewModel` wraps the package; the tree binds to its entries; extraction runs off-thread. Keep the core synchronous/streaming and let the GUI own the async/progress concerns.

The shipped GUI wraps this inspector in a left-nav shell of **tool pages** — Home, Package (the inspector above), Pack, Resign, Decrypt, and Keys — so the repack/resign/PSP tools each get a focused page rather than crowding the inspector.

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
- Anything in the old source that hardcodes key bytes: don't port it verbatim. Public decryption keys live in `BundledKeys` / the keysets; everything else (private keys, per-console secrets) stays out, and licensed content is keyed by a runtime RAP.

---

## 10. Legal & Scope

- This tool **parses and inspects** package structure and **extracts content the user is entitled to** (their own dumps / debug packages). Format parsing and metadata display are not circumvention.
- **Only public decryption keys are bundled.** The NPDRM PKG AES key, SELF keysets, and EDAT/SDAT keys have been public for over a decade and are embedded by every PS3 tool. The repo bundles **no** private/signing key and **no** per-console (IDPS/EID) secret, and licensed content still needs the user's own per-purchase RAP at runtime.
- **Repacking is unsigned.** A rebuilt or fake-signed file carries a valid CMAC but no forged ECDSA signature, so it targets patched loaders (CFW) — never a stock retail console.
- **Out of scope, permanently:** signature forgery / re-signing for retail, Finalize-for-retail, DEX/OFW resigning, and any private-key or per-console (IDPS/EID) operation.
- Keep the license permissive (the original is ISC; MIT/ISC pairs well).

---

## 11. First steps

The original bootstrap order, which the project followed:

1. `PkgLens.Core` classlib + `PkgLens.Cli` console + an xUnit test project, wired into `PkgLens.slnx`.
2. `PkgHeader` parsing against a synthetic fixture — `magic`, `type`, offsets, `content_id` green.
3. The SFO parser (self-contained, no crypto) — instant visible payoff (`pkglens sfo`).
4. `IKeyProvider` + retail AES-CTR; decrypt the item table; ship `pkglens list`.
5. The Avalonia GUI, last, on top of the now-proven core.

Build the core until `info`/`list`/`sfo` are solid on the CLI; the GUI is the easy part once the parser is trustworthy. (All five steps are done — see the roadmap in §5.)

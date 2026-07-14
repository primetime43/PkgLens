---
name: audit-backlog-2026-07
description: Remaining robustness/feature backlog from the 2026-07-14 codebase audit (Tier 1 done)
metadata:
  type: project
---

A three-part audit (Core parsing, crypto/SELF/EDAT, CLI/GUI) ran 2026-07-14. **Tier 1 (crash/correctness) is DONE** — committed fixes + regression tests:
- EDAT zero/absurd `block_size` guard (`EdatFile.ParseHeader`)
- SFO hostile `data_max_len` clamp + bulk-write; `valueStart` u32+u32 overflow widened (`SfoParser`/`SfoWriter`)
- `SelfDecryptor` bounds-checks all header/metadata/ELF offsets + 256 MiB ELF-size cap (`RequireWithin`)
- `PkgVerifier` ulong subtraction-guards on data-region/metadata checks; item-table now actually validates every entry's offset

**Tier 2 remaining (half-finished paths):**
- `EdatLz` LZ core unverified vs a real compressed EDAT sample (no golden test) — biggest correctness unknown
- `EbootPatcher.SetFirmwareVersion` drops the ones digit of the minor (4.46→4.4x); `SdkVersion.Display` hard-codes M.N0
- SFO `data_fmt 0x0004` (binary) values round-trip lossily through UTF-8 — keep raw bytes on `SfoEntry`
- `SelfKeyset` NPDRM/APP revision gaps — re-check vs RPCS3 `key_vault.cpp`
- `SfoWriter` key-offset u16 truncation (M2), `SfoTable.TryGet` null-annotation lie (L6)

**Tier 3 remaining (UX/error handling):**
- CLI: unhandled `IOException`/`UnauthorizedAccessException` escape as stack traces on every subcommand (only `PkgKeyException`/`PkgFormatException` caught) → wrap dispatch in one top-level handler; `unself` maps `PkgFormatException` to exit 3 vs everyone else's 2
- CLI: `--json` no-ops on `self`/`decrypt`, absent from pack/resign/unself/patch; `Console.OutputEncoding` unset → mojibake
- GUI: failures swallowed to status bar (no modal error dialog); `IsBusy` guards only load; Extract All ignores the progress callback

**Net-new features proposed:** `pkglens scan <dir> --json|--csv` batch catalog (spec's "later" item, highest value); `FlagPsp` awareness (parsed but unused → silent mojibake); ECDSA signature *verification* (read-only, in scope). See [[pkglens-scope-guardrails]] for what stays out of scope.

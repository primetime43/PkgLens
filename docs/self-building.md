# Build and rebuild SELF

Open **SELF / EBOOT → Build / rebuild SELF**. Select settings, choose an ELF, EBOOT.BIN,
SELF, or SPRX, then save to a different path.

| Setting | Behavior |
| --- | --- |
| Encrypt output | Encrypted APP/NPDRM SELF; disable for debug fSELF |
| Sign with a verified legacy key | Header signing for APP `00/01/04/07/0A` or NPDRM `01/04/07/0A`; default `0A` |
| Compress segments | Uses zlib where it reduces size |
| NPDRM output | Adds content ID, filename, and license metadata; otherwise produces APP |
| Keys and encryption settings | Independent input/output keys or RAPs; raw overrides take precedence |
| Advanced metadata | Blank fields preserve source metadata; plain ELF uses defaults |

NPDRM defaults to FREE; LOCAL/NETWORK output needs a matching key or RAP.
Keep the output filename, or rebuild for its new name. Metadata edits do not patch executable
instructions or SDK checks. Signing profiles select keys, not TrueAncestor STD/ALT presets.

Before saving, the builder compares the recovered ELF with the source and verifies any requested
legacy header signature. Signing failure never falls back to unsigned output. Input limit: 128 MiB;
combined segment-payload limit: 256 MiB. Output supports 64-bit big-endian PPU APP/NPDRM.

## Folder processing

Open **SELF / EBOOT → Process a folder of executables**. Choose separate source/output folders,
select an operation, and **Scan folder** before starting.

- Supports `EBOOT.BIN`, `.self`, `.sprx`, and `.elf`, optionally including subfolders.
- Decrypt appends `.elf`; existing plaintext ELFs are skipped. Rebuild/fake-sign/legacy-sign
  preserve APP/NPDRM type, license, content ID, metadata, and relative paths.
- Keys/RAPs resolve per file. A raw override applies to every file.
- Existing outputs are skipped. Linked paths, overlapping folders, and naming collisions are rejected.
- **Start / resume** handles unfinished files; **Retry failed** retries failures after fixes.
  Jobs last for the current dialog. Cancellation keeps completed outputs.

## While packing or repacking

**Create package** and **Save package as** offer keep, encrypted rebuild, fake-sign all, and
legacy-sign all modes. **Check and prepare executables** verifies every candidate before saving;
missing keys or failed files block the all-file modes. The older EBOOT-only mode keeps its existing fallback.

Repacking includes pending edits. Source changes require another preparation check. Prepared bytes
are checked after embedding, and the final package is verified before publication. Failure or
cancellation preserves the existing destination and removes temporary output.

## Compatibility

Unsigned output needs patched signature checks. A valid legacy header signature does not establish
Sony provenance or stock/DEX compatibility. PKG/NPDRM-footer signing, modern signing keys, and genuine
DEX/OFW modes are unavailable. Hardware booting is untested.

See [reference validation](self-signing-validation.md), [existing-file verification](self-verification.md),
[CEX/DEX labels](cex-dex-detection.md), and [keys](keys.md).

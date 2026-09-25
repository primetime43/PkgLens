# Find an EDAT key in an executable

Open **Tools → Find key in executable**, or choose **Find key in executable…** in the
EDAT workbench's input key section. From the workbench, the current EDAT is selected automatically,
including pending package replacements.

1. Select the EDAT you want to decrypt.
2. Select an EBOOT.BIN, SELF, SPRX, or decrypted PS3 ELF from the same game.
3. Choose **Find and verify key**. The tool decrypts supported SELF files with the existing
   public keysets, local/bundled klicensee mappings, and configured RAP library. If needed,
   provide the executable's own RAP or raw klicensee in the expandable section.
4. A confirmed match shows its fingerprint, representation, and offset in the **decrypted ELF**.
   Choose **Save confirmed key** to add an exact content-ID, EDAT-filename, and license mapping
   to the local klicensee database. A confirmed mapping replaces an existing mapping with the same scope.

The executable is read as data, never run. Search and verification do not save decrypted files
or keys. Only the Save button writes a key, after rechecking the target EDAT. When opened from
the workbench, saving clears manual input-key overrides and checks the newly saved mapping.
You can also manage the saved mapping through Keys & licenses.

The search covers contiguous raw 16-byte values at every byte offset and contiguous 32-character
ASCII hexadecimal strings (upper or lower case). Hex strings are checked first, followed by
aligned binary values and then all remaining offsets. Duplicate candidates are skipped with a
bounded cache. The first candidate that authenticates the EDAT header and passes full content
verification is returned. Raw key bytes are never shown in the result or progress messages.

## Limits

- Executable input and decrypted ELF size are limited to 64 MiB. Large searches can take minutes.
- Progress and cancellation are available. A cancellation during SELF decryption takes effect
  after the existing SELF decryptor returns; candidate scanning and EDAT verification check it repeatedly.
- A key built at runtime, split into pieces, encoded differently, or stored in another module
  will not necessarily be found. Try other executables from the same title if there is no match.
- Licensed EDAT needs its content klicensee, which can differ from its developer header key.
  A developer-header-only match is not accepted because it cannot prove the file will decrypt.
- SDAT needs no external key. Debug EDAT skips authentication and cannot prove a key match.
  Both are rejected as discovery targets.
- A matching header with damaged content is reported as a failed verification and cannot be saved.
  Finding no match does not prove the key is absent; damage to the target header also prevents matching.

The key check reuses PkgLens's EDAT authentication and decryption implementation. The format's
key and authentication behavior can also be inspected in
[RPCS3's EDAT implementation](https://github.com/RPCS3/rpcs3/blob/master/rpcs3/Crypto/unedat.cpp).

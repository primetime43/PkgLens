# Find an EDAT key

Open **Tools → Find key in executable**, or the same action in the EDAT workbench.
The workbench supplies its current EDAT, including pending replacements.

1. Select the EDAT and an EBOOT.BIN, SELF, SPRX, or decrypted PS3 ELF from the same game.
2. Choose **Find and verify key**. Supply the executable's own RAP/klicensee if automatic lookup fails.
3. A match shows a fingerprint and offset in the decrypted ELF. **Save confirmed key** rechecks the
   EDAT and saves an exact content-ID, filename, and license mapping in the [local database](keys.md).
   An existing mapping with the same scope is replaced.

Executables are read as data, never run. Search does not save plaintext or keys; only **Save confirmed key**
writes a mapping. From the workbench, saving clears manual overrides and checks the new mapping.

Search covers raw 16-byte values and 32-character ASCII hex keys. A match must authenticate the header
and pass full content verification. Runtime-generated, split, or differently encoded keys may be missed.

Input/decrypted ELF limit: 64 MiB. Large searches can take minutes; cancellation during SELF decryption
waits for that stage. SDAT and debug EDAT cannot be discovery targets. Damaged content cannot produce a
savable match, and a failed search does not prove the key is absent.

Reference: [RPCS3 EDAT implementation](https://github.com/RPCS3/rpcs3/blob/master/rpcs3/Crypto/unedat.cpp).

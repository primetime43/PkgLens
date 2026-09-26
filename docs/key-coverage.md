# Package key coverage

Open a readable package, then **Decrypt data → Check key coverage**, or **Tools → Package key coverage**.
The read-only scan includes pending replacements and uses your current [keys and RAPs](keys.md).

Select a row for content ID, key source/fingerprint, and details. Search or filter files needing attention.
**Save report** exports all rows as JSON/text; **Scan again** uses current keys and edits. No raw keys are exported.

| Result | Meaning |
| --- | --- |
| Verified | EDAT/SDAT header, metadata, content, and size passed validation |
| Decrypts | SELF/PSP decryption succeeded; signatures and full payload integrity were not checked |
| No key needed | Plain ELF detected; integrity not checked |
| Needs key | No unambiguous key/RAP; import one or try [EDAT key discovery](devklic-discovery.md) |
| Key not confirmed | Invalid candidate or header authentication failure; key error and header damage may look alike |
| Damaged | Structure/content check failed; details say whether the EDAT header authenticated |
| Unsupported | Format, device-specific protection, or revision is unsupported |
| Not checked | Over the 128 MiB buffering limit, or debug EDAT skips authentication |
| Error | File/key read or processing error |

Archives, nested packages, and disc containers are not scanned recursively. Vita inner protection and
fuse-bound PSP files are unsupported. Cancellation discards an unfinished report; buffered decryption
may finish its current file first. Unrecognized data does not prove a file is unencrypted.

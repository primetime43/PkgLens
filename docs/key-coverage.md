# Package key coverage

Open a readable package, then choose **Tools → Package key coverage** or
**Decrypt data → Check key coverage**. The scan checks the current editing version of each file,
including unsaved replacements. It reads supported protected files and tests your existing keys
without exporting plaintext, installing keys, or changing the package.

The report lists protected files and recognized plaintext ELF executables. Ordinary files are
counted separately. Select a row for its content ID, key source/fingerprint, verification details,
and suggested next steps. Search by filename, content ID, or key source, or filter to files that
need attention. **Save report** exports all results as JSON or text, regardless of the current filter.
**Scan again** reruns verification with the keys available at that time.

| Result | Meaning / next step |
| --- | --- |
| Verified | EDAT/SDAT header, metadata, content integrity, and plaintext size passed validation. |
| Decrypts | SELF/EBOOT or supported PSP content decrypted successfully. This does not claim complete executable integrity or valid Sony signatures. |
| No key needed | A plaintext ELF was detected; executable integrity was not checked. |
| Needs key | No unambiguous applicable klicensee or RAP was found. Import the matching license, or use executable key discovery for an EDAT, then scan again. |
| Key not confirmed | A candidate key or RAP is invalid, or header authentication failed. An incorrect key and damaged header cannot always be distinguished. |
| Damaged | Structure or content verification failed. For EDAT payload failures, the details indicate whether the key authenticated the header first. |
| Unsupported | The format, device-specific protection, or SELF key revision is unsupported. Details identify the relevant platform exporter when possible. |
| Not checked | The file exceeds the scan's 128 MiB per-file buffering limit, or debug EDAT skips authentication. No key match is claimed. |
| Error | A file or key source could not be read, or the check could not finish. Other files are still checked. |

Automatic resolution includes local/discovered klicensees, bundled mappings, installed RAPs,
the standard free EDAT developer key, SDAT header-derived keys, and available SELF/PSP keys.
Key bytes are excluded from the report. Custom free EDAT keys are also used by package content export.

This is a snapshot of the open package and current keys. It does not discover new keys or scan
inside archives, nested packages, or disc containers. Vita inner protection and fuse-bound PSP
content require other tooling/material. Unrecognized ordinary headers do not prove that a file
contains no proprietary encryption. Package decryption must work before its contents can be scanned.
Cancellation discards the incomplete report. SELF and PSP decryption use existing buffered routines,
so cancellation during those routines takes effect after the current file's decryption returns.

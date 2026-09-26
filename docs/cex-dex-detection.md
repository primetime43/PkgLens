# CEX / DEX labels

Shown in SELF inspection, verification, selected package-file details, and folder-processing results.
Labels update when a package replacement is staged or reverted.

| Label | Evidence |
| --- | --- |
| CEX / retail | Known retail APP/NPDRM key revision |
| DEX / debug (fSELF) | SELF revision `8000` or `C000`; also usable by compatible CEX CFW |
| Unknown | Plain ELF, unsupported or incomplete headers, or unreadable files |

These are source-format hints, not signature or console-compatibility results. Package encryption
is not used to classify embedded files. Package selection reads up to 4 KiB; unavailable metadata
returns Unknown. Ordinary data files show no label.

References: [RPCS3 key vault](https://github.com/RPCS3/rpcs3/blob/master/rpcs3/Crypto/key_vault.cpp)
and [SELF loader](https://github.com/RPCS3/rpcs3/blob/master/rpcs3/Crypto/unself.cpp).

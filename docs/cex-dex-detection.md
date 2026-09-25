# CEX / DEX format indicators

SELF inspection, the selected-file details in Browse files, and the source column in folder SELF processing show a header-based format hint. Unsaved replacements and reverting a replacement update the selected-file indicator immediately.

- **CEX / retail:** PS3 APP or NPDRM SELF with a recognized retail key revision.
- **DEX / debug (fSELF):** PS3 SELF with revision `8000` or `C000`. These formats are also used by CFW tools, so this does not mean DEX-only.
- **Unknown:** plain ELF, unsupported revisions/program types, incomplete headers, or unreadable executable files.

The labels describe the source file's format, not the selected output profile, signature validity, firmware requirements, or guaranteed console compatibility. PKG retail/debug encryption is not used to classify its embedded files. Ordinary data files do not show a target label. Package selection inspects at most 4 KiB; if application information is outside that prefix, it reports Unknown.

Detection uses the existing retail APP/NPDRM key mappings from [RPCS3's key vault](https://github.com/RPCS3/rpcs3/blob/master/rpcs3/Crypto/key_vault.cpp) and the two debug markers recognized by [RPCS3's SELF loader](https://github.com/RPCS3/rpcs3/blob/master/rpcs3/Crypto/unself.cpp) (`IsDebugSelf` / `CheckDebugSelf`). System SELF files are left Unknown unless they carry a debug marker.

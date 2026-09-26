# Repository notes

PkgLens is a .NET 8 package and executable toolkit. See [README](README.md) for setup
and [features](docs/features.md) for the current scope.

## Build and layout

```sh
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~SelfDecryptor"
dotnet run --project src/PkgLens.Gui
dotnet run --project src/PkgLens.Cli -- info <pkg>
```

- `src/PkgLens.Core`: parsers, cryptography, and shared workflows, organized under
  `Shared`, `Ps3`, `Psp`, `Ps1`, `Ps2`, and `Vita`. Uses SharpCompress and Bouncy Castle.
- `src/PkgLens.Gui`: Avalonia MVVM; views call services and Core. Capture UI settings
  before `Task.Run`; never access controls or a window's `DataContext` from a worker.
- `src/PkgLens.Cli`: command handlers using Core and a hand-written argument parser.
- `tests/PkgLens.Core.Tests`: synthetic format fixtures, CLI contracts, and headless GUI tests.

## Conventions

- The maintainer handles staging and commits. Leave verified changes for review.
- Keep UI concerns out of Core. Prefer streaming; enforce bounds on buffered formats.
- PKG/SELF fields are generally big-endian; PSP PBP/PGD structures are little-endian.
- Route package extraction through `PkgDecryptorSet.For(entry)`: PSP/PSX entries can use
  different keys within one package. Keep `InMemoryKeyProvider` explicit for tests.
- Preserve source files, pending edits, and existing destinations on failure. Use the
  existing atomic-output helpers and operation progress/cancellation patterns.
- Test format changes with synthetic fixtures. Retain third-party license notices.
- Public decryption keys and the documented legacy SELF signing profiles are bundled.
  User RAPs, private license data, per-console secrets, and game dumps stay out of Git.
- Firmware keysets, genuine DEX/OFW signing, and PSP PRX executable decryption are outside
  the current scope. Do not re-propose PSP PRX work unless the maintainer asks.

## Reference implementations

RPCS3: PKG, SELF, EDAT, and LZRC; scetool: SELF signing; pkg2zip: PSP/Vita packages;
tpunix kirk_engine: PSP EDAT/PGD; sign_np and npdrm_free: NPUMDIMG;
PSP-DOCUMENT.DAT: manual extraction; PSN_get_pkg_info: PKG signature verification.

Keep local reference tools, samples, logs, and experiments under ignored `TestResults/`.
Current user documentation belongs in `docs/`; avoid duplicating it here.

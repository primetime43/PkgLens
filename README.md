# PkgLens

A desktop app and CLI for PS3, PSP, and PS Vita packages: inspect, extract, edit,
build, decrypt, convert, and verify. Includes SELF/EBOOT, EDAT/SDAT, PSARC, and disc-export tools.

![PkgLens desktop app](docs/images/pkglens-home.png)

Download the Windows GUI archive from [Releases](https://github.com/primetime43/PkgLens/releases),
extract it, and run the `.exe`. Open or drag in a `.pkg` or `.psarc` to begin.

## Guides

- [Features and workflows](docs/features.md)
- [Keys and RAP licenses](docs/keys.md)
- [EDAT / SDAT tools](docs/edat-tools.md)
- [SELF building](docs/self-building.md) and [verification](docs/self-verification.md)
- [Decrypt package contents](docs/decrypt-package-contents.md)

For CLI commands, run `pkglens --help` or `pkglens <command> --help`.

## Scope

Standard package keys are bundled; licensed content may need a RAP or content-specific key.
Rebuilt packages and unsigned/fake-signed executables target compatible patched loaders or emulators.
Legacy SELF header signing supports selected older revisions; it does not sign PKGs or NPDRM footers,
prove Sony provenance, or guarantee stock retail/DEX bootability. No per-console IDPS/EID secrets are used.
Use content you own.

## Development

```sh
dotnet build
dotnet test
```

Projects target .NET 8; use an SDK supporting `.slnx` solutions (.NET 10 is used in CI).
CI runs manually from **Actions → CI** and checks tests, dependency vulnerabilities, and coverage.
Optional reports are retained for three days.

Set the version in `Directory.Build.props`, then push a matching tag such as `v1.0.0`
to publish Windows x64, Linux x64, and macOS Intel/Apple Silicon GUI and CLI archives.
See [CLAUDE.md](CLAUDE.md) for repository conventions.

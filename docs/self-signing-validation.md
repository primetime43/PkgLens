# Legacy SELF signing and RPCS3 validation

Tested on 2026-09-25 with the user's existing TrueAncestor key files, an independently compiled
scetool 0.2.9, and RPCS3 `0.0.42-8a8a19de` (local build), using firmware 4.93 in an isolated copy.
No original executable, emulator configuration, or license library was changed.

## Results

| Input | ECDSA signature | RPCS3 result |
|---|---|---|
| Original `default_mp.elf` | Not applicable: plain ELF | Reached game boot code |
| Same ELF signed with the legacy APP revision `0A` key using scetool | Valid against that revision's public key | Reached the same game boot code |
| Same ELF rebuilt by PkgLens as encrypted APP revision `0A` | Empty signature; fails ECDSA validation as expected | Reached the same game boot code |

RPCS3's separate `--decrypt` operation recovered byte-identical ELFs from both SELF files.
Each 15-second headless boot probe created the game's boot thread and printed `HDD Boot Game`.
All three also encountered missing `/app_home/zone/english/` content and
`CELL_GAMEDATA_ERROR_PARAM` in the isolated test directory. The probes were then stopped.
This establishes successful executable loading and execution, not successful gameplay or hardware booting.

## Signature verification

The external TrueAncestor key file contains nonzero private keys for several older revisions.
PkgLens's bundled `SelfKeyset` still contains only encryption root keys and IVs.

For the tested APP revision `0A` key, an independent Python elliptic-curve calculation verified that:

1. The curve generator and public key lie on the curve, and the generator has the stated order.
2. Multiplying the generator by the private scalar yields the supplied public key.
3. The public key also appears in the installed RPCS3 source's key vault.
4. The scetool-produced ECDSA signature verifies over the SHA-1 hash of the decrypted SELF header.
5. The current PkgLens output's zero signature fails that same check.

This is a cryptographically valid signature for a legacy SELF key. It does not mean Sony issued
the rebuilt executable, that all firmware accepts it, or that modern signing keys are available.
RPCS3 accepted both signed and unsigned encrypted output in this experiment, so emulator acceptance
alone cannot distinguish a valid Sony-key signature from an unsigned rebuild.

The current PkgLens builder was not changed to sign executables during this experiment. Adding a
signing mode would require supported legacy signing-key profiles, curve handling, signature generation,
and independent verification, while retaining explicit limitations for other revisions.

## Local evidence

The ignored `TestResults/rpcs3-signing/` folder contains the test project, signature extraction and
verification scripts, `signature-verification.json`, original/comparison outputs, `rpc-decrypt.log`,
and the three `*-boot.log` files. External keys, firmware, game executables, and emulator binaries
remain local test artifacts and are not distributed with PkgLens.

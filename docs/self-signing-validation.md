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
PkgLens's `SelfKeyset` contains only encryption root keys and IVs. At the time of this initial
experiment, no signing catalog was bundled; the subsequent integration is described below.

For the tested APP revision `0A` key, an independent Python elliptic-curve calculation verified that:

1. The curve generator and public key lie on the curve, and the generator has the stated order.
2. Multiplying the generator by the private scalar yields the supplied public key.
3. The public key also appears in the installed RPCS3 source's key vault.
4. The scetool-produced ECDSA signature verifies over the SHA-1 hash of the decrypted SELF header.
5. The original unsigned PkgLens comparison's zero signature fails that same check.

This is a cryptographically valid signature for a legacy SELF key. It does not mean Sony issued
the rebuilt executable, that all firmware accepts it, or that modern signing keys are available.
RPCS3 accepted both signed and unsigned encrypted output in this experiment, so emulator acceptance
alone cannot distinguish a valid Sony-key signature from an unsigned rebuild.

The PkgLens builder was not changed during that initial experiment.

## Integrated signer follow-up

PkgLens now bundles nine published legacy signing profiles separately in `LegacySelfSigning`:
APP `00/01/04/07/0A` and NPDRM `01/04/07/0A`. It validates each private/public pair and its curve,
generates a header signature, and verifies the serialized encrypted output before saving.

All 21 independent reference cases passed: every profile compressed and uncompressed, two additional
LOCAL/NETWORK NPDRM cases, and the real ELF from the initial experiment. scetool recovered the exact
source ELF in each case. A separate Python verifier checked every signature using reference key and
curve data rather than the production signing library. The real PkgLens-signed APP `0A` output
reached `HDD Boot Game` in RPCS3 and encountered the same missing game-data error as the baseline.
This remains a boot-code test, not a full gameplay or console-hardware validation.

The GUI exposes **Sign with a verified legacy key** and a profile selector. Unsupported revisions
are rejected without silently emitting unsigned output. NPDRM footer signing remains unavailable.

## Local evidence

The ignored `TestResults/rpcs3-signing/` folder contains the test project, signature extraction and
verification scripts, `signature-verification.json`, original/comparison outputs, `rpc-decrypt.log`,
and the three `*-boot.log` files. External keys, firmware, game executables, and emulator binaries
remain local test artifacts and are not distributed with PkgLens. Only the selected publicly
documented legacy key/curve profiles are bundled with the application. Follow-up evidence is in
`legacy-verification.json`, `verify-legacy.py`, and `pkglens-legacy-real-boot.log` in that folder.

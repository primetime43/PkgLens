# SELF reference validation

Tested 2026-09-25 with independently compiled scetool 0.2.9 and RPCS3
`0.0.42-8a8a19de`, firmware 4.93, in an isolated local setup.

| Check | Result |
| --- | --- |
| Eight encrypted APP/NPDRM cases, with/without compression | scetool recovered identical ELFs |
| 21 signed cases: nine profiles with/without compression, LOCAL/NETWORK NPDRM, and a real executable | scetool recovered identical ELFs; an independent Python verifier accepted every header signature |
| Real APP `0A`: original ELF, scetool-signed, PkgLens unsigned, and PkgLens legacy-signed | All reached game boot code in RPCS3 |

The boot probes encountered missing game assets and were stopped. This validates executable loading,
not gameplay or hardware compatibility. RPCS3 accepted unsigned output too, so booting alone does not
prove signature validity. Independent checks verified curve membership, private/public agreement,
and signatures against reference key/curve data rather than the production signing library.

Automated tests cover bundled revisions, compressed segments, SPRX, section headers, malformed layouts,
separate license keys, cancellation, and source preservation. See [SELF building](self-building.md).

## Local evidence

Ignored `TestResults/rpcs3-signing/` retains the experiment scripts, reference outputs, boot/decrypt logs,
`signature-verification.json`, `legacy-verification.json`, and `verify-legacy.py`.
Other reference experiments live in `TestResults/self-reference/`.
Game samples, firmware, external keys, and emulator binaries are not distributed with PkgLens.

References: [scetool](https://github.com/naehrwert/scetool),
[RPCS3 key vault](https://github.com/RPCS3/rpcs3/blob/master/rpcs3/Crypto/key_vault.cpp).

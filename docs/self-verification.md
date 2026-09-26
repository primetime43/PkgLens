# Verify SELF / EBOOT

Open **SELF / EBOOT → Verify SELF / EBOOT**, or **Tools → Inspect and verify → Verify SELF / EBOOT**.
Choose an existing EBOOT.BIN, SELF, or SPRX; no open package is needed.

- **Header signature:** Valid, Invalid, Unsigned, or Unsupported; missing keys and check failures
  are reported separately. Uses the [legacy signing profiles](self-building.md).
- **Decryption:** Succeeded if a supported ELF is recovered and passes structural checks;
  otherwise shows the missing key, failure, or unsupported format.
- Details include CEX/DEX format, program type, key revision, firmware field, license, and content ID.

[Keys and RAPs](keys.md) resolve automatically. Expand **Keys and licenses** for a RAP or raw-key
override; raw takes precedence. Changes clear the old result. **Verify again** rereads the file.
Overrides are not saved.

Read-only, in-memory checks support files up to 128 MiB and 64-bit big-endian PS3 PPU executables.
Cancellation takes effect between processing stages. Debug fSELF is reported as Unsigned.
Payload hashes, NPDRM footer signatures, license entitlement, and console compatibility are not checked.
A valid header can accompany damaged content; successful decryption does not prove payload integrity.

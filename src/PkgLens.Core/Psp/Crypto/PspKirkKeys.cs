namespace PkgLens.Core.Psp;

/// <summary>
/// PSP KIRK / AMCTRL decryption constants for the PSP EDAT → PGD decryptor (from tpunix's
/// <c>kirk_engine</c> / <c>amctrl</c>). Only the subset needed to decrypt a fixed-key PGD is here:
/// KIRK keyseeds 0x38/0x39/0x63, the DNAS fixed keys, and the AMCTRL scramble constants — no
/// signing keys and no per-console fuse secrets.
/// </summary>
internal static class PspKirkKeys
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    // KIRK CMD4/CMD7 AES-128-CBC keys, by keyseed.
    private static readonly byte[] Kirk7Key38 = Hex("12468D7E1C42209BBA5426835EB03303");
    private static readonly byte[] Kirk7Key39 = Hex("C43BB6D653EE67493EA95FBC0CED6F8A");
    private static readonly byte[] Kirk7Key63 = Hex("9C9B1372F8C640CF1C62F5D592DDB582");

    /// <summary>Returns the KIRK AES key for a keyseed, or throws for an unsupported one.</summary>
    public static byte[] Kirk7(int keyseed) => keyseed switch
    {
        0x38 => Kirk7Key38,
        0x39 => Kirk7Key39,
        0x63 => Kirk7Key63,
        _ => throw new PkgFormatException($"PSP EDAT uses an unsupported KIRK keyseed 0x{keyseed:X}."),
    };

    /// <summary>DNAS fixed key for the PGD header MAC (pgd_flag 2). From dnas.c <c>dnas_key1A90</c>.</summary>
    public static readonly byte[] DnasKey1A90 = Hex("EDE25D2DBBF812E53C5C5932FAE3E243");

    /// <summary>DNAS fixed key for the PGD header MAC (pgd_flag 1). From dnas.c <c>dnas_key1AA0</c>.</summary>
    public static readonly byte[] DnasKey1AA0 = Hex("2774FBEBA4A001D702569E338C195783");

    // AMCTRL scramble constants (amctrl.c loc_1CD4 / loc_1CE4 / loc_1CF4).
    public static readonly byte[] Amctrl1CD4 = Hex("E350ED1D910A1FD029BB1C3EF34077FB");
    public static readonly byte[] Amctrl1CE4 = Hex("135FA47CAB395BA476B8CCA98F3A0445");
    public static readonly byte[] Amctrl1CF4 = Hex("678D7FA32A9CA0D1508AD8385E4B017E");
}

namespace PkgLens.Cli;

/// <summary>Process exit codes, as documented in the spec's CLI sketch (§6).</summary>
internal static class ExitCode
{
    public const int Ok = 0;
    public const int Usage = 1;
    public const int ParseError = 2;
    public const int KeyOrDecryptError = 3;
    public const int IntegrityFailure = 4;
}

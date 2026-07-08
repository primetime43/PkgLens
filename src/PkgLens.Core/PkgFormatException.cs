namespace PkgLens.Core;

/// <summary>
/// Thrown when a package cannot be parsed: bad magic, truncation, inconsistent offsets, or
/// out-of-bounds structures. The parser is expected to fail with this exception rather than
/// crash with a raw <see cref="IndexOutOfRangeException"/> or similar.
/// </summary>
public sealed class PkgFormatException : Exception
{
    public PkgFormatException(string message) : base(message) { }
    public PkgFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when a package's decryption key material could not be resolved (e.g. a retail
/// package with no runtime key file available).
/// </summary>
public sealed class PkgKeyException : Exception
{
    public PkgKeyException(string message) : base(message) { }
    public PkgKeyException(string message, Exception inner) : base(message, inner) { }
}

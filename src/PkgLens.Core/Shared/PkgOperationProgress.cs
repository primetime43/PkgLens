namespace PkgLens.Core.Shared;

/// <summary>Byte-based progress reported by long-running package operations.</summary>
public readonly record struct PkgOperationProgress(long Completed, long Total, string? Item = null)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Completed * 100d / Total, 0, 100);
}

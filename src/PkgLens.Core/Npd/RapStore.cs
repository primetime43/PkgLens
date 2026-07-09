namespace PkgLens.Core.Npd;

/// <summary>
/// Resolves RAP license files by content id from <c>~/.pkglens/raps/&lt;content-id&gt;.rap</c>, so
/// licensed EDATs can be decrypted without picking a file every time.
/// </summary>
public static class RapStore
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pkglens", "raps");

    /// <summary>Returns the 16-byte RAP for <paramref name="contentId"/>, or null if none is installed.</summary>
    public static byte[]? Find(string contentId)
    {
        try
        {
            string path = Path.Combine(DefaultDirectory, contentId + ".rap");
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length == 16) return bytes;
            }
        }
        catch { /* ignore and fall through */ }
        return null;
    }

    /// <summary>Copies a RAP into the store under the content id, returning the destination path.</summary>
    public static string Install(string contentId, byte[] rap)
    {
        if (rap.Length != 16) throw new ArgumentException("A RAP must be 16 bytes.", nameof(rap));
        Directory.CreateDirectory(DefaultDirectory);
        string path = Path.Combine(DefaultDirectory, contentId + ".rap");
        File.WriteAllBytes(path, rap);
        return path;
    }
}

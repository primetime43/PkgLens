using PkgLens.Core;

namespace PkgLens.Cli;

/// <summary>
/// <c>pkglens folderinfo &lt;folder&gt;</c> — a read-only report on an extracted PS3 content folder
/// (TrueAncestor's "Show Game Folder Info"): content id / title from PARAM.SFO, file counts and size,
/// the EBOOT's sign state, and any NPDRM data files with their license state. Writes nothing.
/// </summary>
internal static class FolderInfoCommand
{
    public static int Run(ReadOnlySpan<string> args)
    {
        string? folder = null;
        bool json = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--json") { json = true; continue; }
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"error: unknown option '{a}'.");
                return ExitCode.Usage;
            }
            if (folder is not null) { Console.Error.WriteLine($"error: unexpected extra argument '{a}'."); return ExitCode.Usage; }
            folder = a;
        }

        if (folder is null)
        {
            Console.Error.WriteLine("error: a <folder> to inspect is required.");
            Console.Error.WriteLine("usage: pkglens folderinfo <folder> [--json]");
            return ExitCode.Usage;
        }
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"error: folder not found: {folder}");
            return ExitCode.Usage;
        }

        try
        {
            var report = GameFolderInfo.Describe(folder);
            Render.FolderInfo(report, json);
            return ExitCode.Ok;
        }
        catch (PkgFormatException ex)
        {
            Console.Error.WriteLine($"folderinfo error: {ex.Message}");
            return ExitCode.ParseError;
        }
    }
}

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Gui.ViewModels;

/// <summary>A node in the file tree, built by splitting entry paths on '/'.</summary>
public sealed class EntryNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public ulong Size { get; init; }

    /// <summary>True when the file's data uses the PSP AES key (embedded Mini / PSP Remaster content).</summary>
    public bool IsPsp { get; init; }

    /// <summary>The underlying package entry for files (null for synthesized directory nodes).</summary>
    public PkgEntry? Entry { get; init; }

    public ObservableCollection<EntryNode> Children { get; } = new();

    /// <summary>Bound to the tree container's expansion state. Only the root defaults to open.</summary>
    public bool IsExpanded { get; set; }

    /// <summary>Sub-folders only — used by the left navigation tree.</summary>
    public IEnumerable<EntryNode> DirectoryChildren =>
        Children.Where(c => c.IsDirectory);

    public bool HasSubfolders => Children.Any(c => c.IsDirectory);

    public string Icon => IsDirectory ? "📁" : "📄";
    public string SizeDisplay => IsDirectory ? "" : FormatSize(Size);

    /// <summary>Short badge shown after the name in the file list; empty unless the entry is PSP-encrypted.</summary>
    public string Badge => IsPsp ? "PSP" : "";
    public bool HasBadge => IsPsp;

    public static string FormatSize(ulong bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.##} {units[u]}";
    }

    /// <summary>Builds a tree from a flat entry list. Intermediate directories are synthesized as needed.</summary>
    public static ObservableCollection<EntryNode> BuildTree(IEnumerable<PkgEntry> entries)
    {
        var roots = new ObservableCollection<EntryNode>();
        var dirIndex = new Dictionary<string, EntryNode>(System.StringComparer.OrdinalIgnoreCase);

        EntryNode GetOrAddDirectory(string path, string name, ObservableCollection<EntryNode> parent)
        {
            if (dirIndex.TryGetValue(path, out var existing))
                return existing;
            var node = new EntryNode { Name = name, FullPath = path, IsDirectory = true };
            dirIndex[path] = node;
            InsertSorted(parent, node);
            return node;
        }

        foreach (var entry in entries)
        {
            var segments = entry.Name.Split('/', System.StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) continue;

            var currentChildren = roots;
            string accumulated = "";

            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                accumulated = accumulated.Length == 0 ? segment : accumulated + "/" + segment;
                bool isLast = i == segments.Length - 1;

                if (isLast && !entry.IsDirectory)
                {
                    var fileNode = new EntryNode
                    {
                        Name = segment,
                        FullPath = accumulated,
                        IsDirectory = false,
                        Size = entry.FileSize,
                        IsPsp = entry.IsPsp,
                        Entry = entry,
                    };
                    InsertSorted(currentChildren, fileNode);
                }
                else
                {
                    var dir = GetOrAddDirectory(accumulated, segment, currentChildren);
                    currentChildren = dir.Children;
                }
            }
        }

        return roots;
    }

    // Directories first, then files, each alphabetical.
    private static void InsertSorted(ObservableCollection<EntryNode> list, EntryNode node)
    {
        int i = 0;
        while (i < list.Count && Compare(list[i], node) < 0) i++;
        list.Insert(i, node);
    }

    private static int Compare(EntryNode a, EntryNode b)
    {
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
        return string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase);
    }
}

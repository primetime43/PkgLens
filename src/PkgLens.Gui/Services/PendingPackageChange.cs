using System;
using PkgLens.Core.Shared.Models;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Services;

public sealed class PackageEntryChangedEventArgs(PkgEntry entry) : EventArgs
{
    public PkgEntry Entry { get; } = entry;
}

public sealed record PendingPackageChange(PkgEntry Entry, ulong ReplacementSize)
{
    public string Path => Entry.Name;
    public string OriginalSize => EntryNode.FormatSize(Entry.FileSize);
    public string NewSize => EntryNode.FormatSize(ReplacementSize);
}

using System.Linq;
using PkgLens.Core.Sfo;
using PkgLens.Core.Tests.TestData;
using Xunit;

namespace PkgLens.Core.Tests;

public class SfoWriterTests
{
    [Fact]
    public void Write_RoundTripsUnchangedTable()
    {
        byte[] blob = new SfoBuilder()
            .AddString("TITLE", "Original Title")
            .AddString("TITLE_ID", "NPUB30910")
            .AddInt("PARENTAL_LEVEL", 5)
            .Build();

        var table = SfoParser.Parse(blob);
        byte[] rewritten = SfoWriter.Write(table.Entries);
        var reparsed = SfoParser.Parse(rewritten);

        Assert.Equal("Original Title", reparsed.Title);
        Assert.Equal("NPUB30910", reparsed.TitleId);
        Assert.Equal(5u, reparsed.ParentalLevel);
    }

    [Fact]
    public void Write_AppliesEditedStringAndInt()
    {
        byte[] blob = new SfoBuilder()
            .AddString("TITLE", "Old")
            .AddInt("PARENTAL_LEVEL", 5)
            .Build();
        var table = SfoParser.Parse(blob);

        var edited = table.Entries
            .Select(e => e.Key switch
            {
                "TITLE" => e.WithValue("A Much Longer New Title"),
                "PARENTAL_LEVEL" => e.WithInt(11),
                _ => e,
            })
            .ToList();

        var reparsed = SfoParser.Parse(SfoWriter.Write(edited));
        Assert.Equal("A Much Longer New Title", reparsed.Title);
        Assert.Equal(11u, reparsed.ParentalLevel);
    }

    [Fact]
    public void Write_PreservesEntryOrderAndFormats()
    {
        byte[] blob = new SfoBuilder().AddString("A", "x").AddInt("B", 1).AddString("C", "y").Build();
        var table = SfoParser.Parse(blob);

        var reparsed = SfoParser.Parse(SfoWriter.Write(table.Entries));
        Assert.Equal(new[] { "A", "B", "C" }, reparsed.Entries.Select(e => e.Key));
        Assert.Equal(SfoFormat.Int32, reparsed.Entries[1].Format);
    }
}

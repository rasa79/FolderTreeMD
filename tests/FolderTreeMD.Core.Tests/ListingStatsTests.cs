using System.Text;
using FolderTreeMD.Core;

namespace FolderTreeMD.Core.Tests;

/// <summary>
/// Tests for <see cref="ListingStats"/> (the §3.4 preview header counts) and <see cref="ListingFile"/>
/// (the §5 rule 12 encoding of a saved listing). Both exist so the UI's header and Save claims are
/// machine-verified rather than asserted on trust (D19).
/// </summary>
public class ListingStatsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "FolderTreeMD.ListingFileTests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Creates the temp directory the save tests write into. <see cref="ListingFile.Save"/> does not
    /// create directories, because the Save-As dialog always hands it an existing one.
    /// </summary>
    public ListingStatsTests()
    {
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Counts the root, folders, files, link lines and access-denied lines of a listing.</summary>
    [Fact]
    public void FromMarkdown_CountsFoldersFilesAndCharacters()
    {
        const string markdown =
            "- [App]\n" +
            "    - [docs]\n" +
            "        - format-v1.md\n" +
            "    - [protected] (access denied)\n" +
            "    - [shared] (link → ..\\shared-lib)\n" +
            "    - main.py\n";

        ListingStats stats = ListingStats.FromMarkdown(markdown);

        // Root + docs + protected + shared are folder-flavoured lines; format-v1.md and main.py are files.
        Assert.Equal(4, stats.Folders);
        Assert.Equal(2, stats.Files);
        Assert.Equal(markdown.Length, stats.Characters);
    }

    /// <summary>Indentation and trailing newlines do not change the classification.</summary>
    [Fact]
    public void FromMarkdown_IgnoresIndentation()
    {
        ListingStats stats = ListingStats.FromMarkdown("- [a]\n        - [a/b]\n            - x.txt\n");

        Assert.Equal(2, stats.Folders);
        Assert.Equal(1, stats.Files);
    }

    /// <summary>Text that is not a list item contributes only to the character count.</summary>
    [Fact]
    public void FromMarkdown_IgnoresNonListLines()
    {
        const string markdown = "Select a folder and press Generate.\n\n- [only]\n";

        ListingStats stats = ListingStats.FromMarkdown(markdown);

        Assert.Equal(1, stats.Folders);
        Assert.Equal(0, stats.Files);
        Assert.Equal(markdown.Length, stats.Characters);
    }

    /// <summary>An empty editor is zero of everything.</summary>
    [Fact]
    public void FromMarkdown_EmptyTextIsAllZero()
    {
        ListingStats stats = ListingStats.FromMarkdown(string.Empty);

        Assert.Equal(0, stats.Folders);
        Assert.Equal(0, stats.Files);
        Assert.Equal(0, stats.Characters);
    }

    /// <summary>
    /// M5 review finding 7: the counts must not depend on the line ending. The engine emits <c>\n</c>,
    /// while a WPF <c>TextBox</c> hands back <c>\r\n</c> once the user has typed, so the same listing
    /// has to report the same numbers either way.
    /// </summary>
    [Fact]
    public void FromMarkdown_CountsIdenticallyForLfAndCrLf()
    {
        const string lf = "- [App]\n    - [docs]\n        - format-v1.md\n    - main.py\n";
        string crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        ListingStats fromLf = ListingStats.FromMarkdown(lf);
        ListingStats fromCrLf = ListingStats.FromMarkdown(crlf);

        Assert.Equal(fromLf, fromCrLf);
        Assert.Equal(2, fromCrLf.Folders);
        Assert.Equal(2, fromCrLf.Files);
        Assert.Equal(lf.Length, fromCrLf.Characters);
    }

    /// <summary>Carriage returns are excluded from the character count, showing as the same number.</summary>
    [Fact]
    public void FromMarkdown_ExcludesCarriageReturnsFromTheCharacterCount()
    {
        const string crlf = "- [a]\r\n    - b.txt\r\n";

        ListingStats stats = ListingStats.FromMarkdown(crlf);

        Assert.Equal(crlf.Length - 2, stats.Characters); // two \r characters
        Assert.Equal(stats.Characters, ListingStats.FromMarkdown(crlf.Replace("\r", string.Empty, StringComparison.Ordinal)).Characters);
    }

    /// <summary>
    /// Only <c>\n</c> and <c>\r\n</c> terminate a line — a bare <c>\r</c> does not, which is what both
    /// the engine and a WPF <c>TextBox</c> produce. Such content is therefore one list item, and its
    /// carriage returns are still excluded from the character count.
    /// </summary>
    [Fact]
    public void FromMarkdown_CarriageReturnOnlyContentIsOneLine()
    {
        const string content = "- [a]\r- b.txt\r";

        ListingStats stats = ListingStats.FromMarkdown(content);

        Assert.Equal(1, stats.Folders);
        Assert.Equal(0, stats.Files);
        Assert.Equal(content.Length - 2, stats.Characters);
    }

    /// <summary>The header text uses the §3.4 wording and separator exactly.</summary>
    [Fact]
    public void ToHeaderText_UsesTheSpecWording()
    {
        var stats = new ListingStats(Folders: 4, Files: 2, Characters: 137);

        Assert.Equal("4 folders · 2 files · 137 chars", stats.ToHeaderText());
    }

    /// <summary>§5 rule 12: a saved listing is UTF-8 with no byte-order mark.</summary>
    [Fact]
    public void Save_WritesUtf8WithoutBom()
    {
        string path = Path.Combine(_directory, "listing.md");
        const string markdown = "- [App]\n    - naïve.txt\n";

        ListingFile.Save(path, markdown);

        byte[] bytes = File.ReadAllBytes(path);
        bool startsWithBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        Assert.False(startsWithBom, "a saved listing must not start with a BOM");
        Assert.Equal(markdown, new UTF8Encoding(false).GetString(bytes));
        Assert.Equal(markdown, File.ReadAllText(path));
    }

    /// <summary>An empty listing is still a valid file (the Save button is disabled for it, but the writer must not lie).</summary>
    [Fact]
    public void Save_EmptyText_WritesAnEmptyFile()
    {
        string path = Path.Combine(_directory, "empty.md");

        ListingFile.Save(path, string.Empty);

        Assert.True(File.Exists(path));
        Assert.Empty(File.ReadAllBytes(path));
    }

    /// <summary>
    /// The M5 DoD evidence, end to end: a listing produced by the real engine for a real fixture tree,
    /// written through the same call the Save button uses, is UTF-8 without a BOM and reads back
    /// byte-identical — including the non-ASCII name in the fixture.
    /// </summary>
    [Fact]
    public void EngineListing_SavedFile_HasNoBomAndRoundTrips()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("docs/naïve-Übersicht.md");
        tree.AddFile("main.py");
        string markdown = MarkdownListing.Generate(tree.Root, new ListingOptions(), null, CancellationToken.None);
        string path = Path.Combine(_directory, "App.md");

        ListingFile.Save(path, markdown);

        byte[] bytes = File.ReadAllBytes(path);
        bool startsWithBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        Assert.False(startsWithBom, "the saved listing must not start with a BOM");
        Assert.Equal(markdown, File.ReadAllText(path));
        Assert.Contains("naïve-Übersicht.md", markdown, StringComparison.Ordinal);

        // The header counts describe the same text the file received.
        ListingStats stats = ListingStats.FromMarkdown(markdown);
        Assert.Equal(2, stats.Folders); // the root and [docs]
        Assert.Equal(2, stats.Files);
        Assert.Equal(markdown.Length, stats.Characters);
    }

    /// <summary>Removes the temp directory created by a test.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

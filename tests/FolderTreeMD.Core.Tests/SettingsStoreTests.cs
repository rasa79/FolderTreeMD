using System.Text;
using System.Text.Json;
using FolderTreeMD.Core;

namespace FolderTreeMD.Core.Tests;

/// <summary>
/// Tests for <see cref="SettingsStore"/>: the §4 schema keys, the defaults contract for missing and
/// corrupt files, value coercion, and a save/load round trip. Every test uses its own temp file, so
/// the user's real <c>%APPDATA%</c> settings are never touched.
/// </summary>
public class SettingsStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "FolderTreeMD.SettingsTests", Guid.NewGuid().ToString("N"));

    private readonly string _path;

    /// <summary>Initializes the test with a temp settings path inside a fresh directory.</summary>
    public SettingsStoreTests()
    {
        _path = Path.Combine(_directory, "settings.json");
    }

    /// <summary>§4: every value survives a save/load round trip unchanged.</summary>
    [Fact]
    public void RoundTrip_PreservesEverySchemaValue()
    {
        var saved = new AppSettings
        {
            Depth = 7,
            IncludeHidden = true,
            FollowSymlinks = true,
            ShowFileSizes = false,
            ShowFolderSizes = true,
            ShowAttributes = false,
            IndentSize = 2,
            LastFolder = @"C:\Users\ana\Projects\App",
        };

        SettingsStore.SaveTo(saved, _path);
        AppSettings loaded = SettingsStore.LoadFrom(_path);

        Assert.Equal(saved.Depth, loaded.Depth);
        Assert.Equal(saved.IncludeHidden, loaded.IncludeHidden);
        Assert.Equal(saved.FollowSymlinks, loaded.FollowSymlinks);
        Assert.Equal(saved.ShowFileSizes, loaded.ShowFileSizes);
        Assert.Equal(saved.ShowFolderSizes, loaded.ShowFolderSizes);
        Assert.Equal(saved.ShowAttributes, loaded.ShowAttributes);
        Assert.Equal(saved.IndentSize, loaded.IndentSize);
        Assert.Equal(saved.LastFolder, loaded.LastFolder);
    }

    /// <summary>
    /// §4 / M4 carry-over 1: the file contains exactly the eight schema keys, with the schema's
    /// spelling, so the on-disk format cannot drift from the documented one.
    /// </summary>
    [Fact]
    public void SavedFile_ContainsExactlyTheSchemaKeys()
    {
        SettingsStore.SaveTo(new AppSettings(), _path);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_path));
        string[] keys = [.. document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

        Assert.Equal(
            new[]
            {
                "depth",
                "followSymlinks",
                "includeHidden",
                "indentSize",
                "lastFolder",
                "showAttributes",
                "showFileSizes",
                "showFolderSizes",
            },
            keys);
    }

    /// <summary>The saved file is UTF-8 without a BOM, like every other file this app writes.</summary>
    [Fact]
    public void SavedFile_HasNoByteOrderMark()
    {
        SettingsStore.SaveTo(new AppSettings(), _path);

        byte[] bytes = File.ReadAllBytes(_path);
        bool startsWithBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        Assert.False(startsWithBom, "settings.json must not start with a BOM");
        Assert.Equal(File.ReadAllText(_path), new UTF8Encoding(false).GetString(bytes));
    }

    /// <summary>§4: a missing file means defaults, and no dialog.</summary>
    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        Assert.False(File.Exists(_path));

        AppSettings settings = SettingsStore.LoadFrom(_path);

        Assert.Equal(-1, settings.Depth);
        Assert.False(settings.IncludeHidden);
        Assert.False(settings.FollowSymlinks);
        Assert.True(settings.ShowFileSizes);
        Assert.False(settings.ShowFolderSizes);
        Assert.True(settings.ShowAttributes);
        Assert.Equal(4, settings.IndentSize);
        Assert.Null(settings.LastFolder);
    }

    /// <summary>§4: corrupt JSON means defaults — the load must not throw.</summary>
    [Fact]
    public void CorruptJson_ReturnsDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_path, "{ this is not json ");

        AppSettings settings = SettingsStore.LoadFrom(_path);

        Assert.Equal(-1, settings.Depth);
        Assert.Equal(4, settings.IndentSize);
        Assert.True(settings.ShowFileSizes);
    }

    /// <summary>§4: an empty file is corrupt too, and still means defaults.</summary>
    [Fact]
    public void EmptyFile_ReturnsDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_path, string.Empty);

        AppSettings settings = SettingsStore.LoadFrom(_path);

        Assert.Equal(-1, settings.Depth);
        Assert.Equal(4, settings.IndentSize);
    }

    /// <summary>§4: <c>indentSize</c> is only 2 or 4; any other value on disk is coerced to the default 4.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(99)]
    public void InvalidIndentSizeOnDisk_IsCoercedToDefault(int onDisk)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_path, $$"""{"indentSize": {{onDisk}}}""");

        AppSettings settings = SettingsStore.LoadFrom(_path);

        Assert.Equal(4, settings.IndentSize);
    }

    /// <summary>§4: a valid non-default indent is kept.</summary>
    [Fact]
    public void ValidIndentSizeOnDisk_IsKept()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_path, """{"indentSize": 2}""");

        Assert.Equal(2, SettingsStore.LoadFrom(_path).IndentSize);
    }

    /// <summary>Defaults in code match the schema's documented defaults (§4).</summary>
    [Fact]
    public void Defaults_MatchTheSchema()
    {
        var settings = new AppSettings();

        Assert.Equal(-1, settings.Depth);
        Assert.False(settings.IncludeHidden);
        Assert.False(settings.FollowSymlinks);
        Assert.True(settings.ShowFileSizes);
        Assert.False(settings.ShowFolderSizes);
        Assert.True(settings.ShowAttributes);
        Assert.Equal(4, settings.IndentSize);
        Assert.Null(settings.LastFolder);
    }

    /// <summary>Saving creates a missing parent directory, so a first run with no %APPDATA% folder works.</summary>
    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        Assert.False(Directory.Exists(_directory));

        SettingsStore.SaveTo(new AppSettings { Depth = 0 }, _path);

        Assert.True(File.Exists(_path));
        Assert.Equal(0, SettingsStore.LoadFrom(_path).Depth);
    }

    /// <summary>
    /// Only the eight schema keys are read: an unknown key in the file is ignored rather than
    /// breaking the load, so a hand-edited file with extra content still works.
    /// </summary>
    [Fact]
    public void UnknownKeys_AreIgnored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_path, """{"depth": 3, "colourScheme": "dark"}""");

        AppSettings settings = SettingsStore.LoadFrom(_path);

        Assert.Equal(3, settings.Depth);
        Assert.Equal(4, settings.IndentSize);
    }

    /// <summary>
    /// D15: the atomic write leaves no temp residue — after a save the directory holds the settings
    /// file and nothing else.
    /// </summary>
    [Fact]
    public void Save_LeavesNoTempFileBehind()
    {
        SettingsStore.SaveTo(new AppSettings { Depth = 3 }, _path);

        string[] files = Directory.GetFiles(_directory);
        Assert.Equal([Path.Combine(_directory, "settings.json")], files);
        Assert.DoesNotContain(files, file => file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// D15: saving repeatedly replaces the file (the overwrite path of the atomic move) and still
    /// leaves exactly one file, with the newest values.
    /// </summary>
    [Fact]
    public void SaveTwice_ReplacesFileAndKeepsOnlyTheLatest()
    {
        SettingsStore.SaveTo(new AppSettings { Depth = 1 }, _path);
        SettingsStore.SaveTo(new AppSettings { Depth = 2, IndentSize = 2 }, _path);

        Assert.Equal([Path.Combine(_directory, "settings.json")], Directory.GetFiles(_directory));

        AppSettings loaded = SettingsStore.LoadFrom(_path);
        Assert.Equal(2, loaded.Depth);
        Assert.Equal(2, loaded.IndentSize);
    }

    /// <summary>Removes the temp settings directory created by a test.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

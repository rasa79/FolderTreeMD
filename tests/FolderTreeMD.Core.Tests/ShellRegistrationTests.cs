using FolderTreeMD.Core;

namespace FolderTreeMD.Core.Tests;

/// <summary>
/// Pins the portable install's registry plan (M8, DECISIONS.md D28, LEARN[35]): the two key paths, the label,
/// the icon reference, the two command lines (quoting and placeholder included) and the idempotence rule.
/// Nothing here touches a registry — the plan is data and the comparison takes a reader.
/// </summary>
public sealed class ShellRegistrationTests
{
    private const string ExePath = @"C:\tools\FolderTreeMD.exe";

    [Fact]
    public void Plan_RegistersBothSurfacesUnderHkcuClasses()
    {
        ShellRegistrationPlan plan = ShellRegistration.Plan(ExePath);

        Assert.Equal(
            new[]
            {
                @"Software\Classes\Directory\shell\FolderTreeMD",
                @"Software\Classes\Directory\Background\shell\FolderTreeMD",
            },
            plan.Keys);
    }

    [Fact]
    public void Plan_WritesLabelIconAndCommandForEachSurface()
    {
        ShellRegistrationPlan plan = ShellRegistration.Plan(ExePath);

        Assert.Equal(6, plan.Values.Count);
        Assert.Equal(
            new ShellRegistryValue[]
            {
                new(ShellRegistration.FolderIconKeyPath, null, "Copy folder content as markdown"),
                new(ShellRegistration.FolderIconKeyPath, "Icon", @"C:\tools\FolderTreeMD.exe,0"),
                new(
                    @"Software\Classes\Directory\shell\FolderTreeMD\command",
                    null,
                    "\"C:\\tools\\FolderTreeMD.exe\" --folder \"%1\""),
                new(ShellRegistration.FolderBackgroundKeyPath, null, "Copy folder content as markdown"),
                new(ShellRegistration.FolderBackgroundKeyPath, "Icon", @"C:\tools\FolderTreeMD.exe,0"),
                new(
                    @"Software\Classes\Directory\Background\shell\FolderTreeMD\command",
                    null,
                    "\"C:\\tools\\FolderTreeMD.exe\" --folder \"%V\""),
            },
            plan.Values);
    }

    [Theory]
    [InlineData(ShellSurface.FolderIcon, "%1")]
    [InlineData(ShellSurface.FolderBackground, "%V")]
    public void Command_QuotesTheExecutableAndThePlaceholder(ShellSurface surface, string placeholder)
    {
        Assert.Equal($"\"{ExePath}\" --folder \"{placeholder}\"", ShellRegistration.Command(ExePath, surface));
    }

    [Fact]
    public void Command_UsesTheFolderOptionTheParserKnows()
    {
        // The registration must never drift from the §7 grammar the app actually parses (LEARN[30]).
        Assert.Contains(CliCommand.FolderOption, ShellRegistration.Command(ExePath, ShellSurface.FolderIcon), StringComparison.Ordinal);
    }

    [Fact]
    public void Command_KeepsSpacesAndNonAsciiPathsIntact()
    {
        const string Path = @"C:\Users\bucko\My Tools\Müller\FolderTreeMD.exe";

        Assert.Equal(
            "\"C:\\Users\\bucko\\My Tools\\Müller\\FolderTreeMD.exe\" --folder \"%1\"",
            ShellRegistration.Command(Path, ShellSurface.FolderIcon));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("FolderTreeMD.exe")]
    [InlineData(@"tools\FolderTreeMD.exe")]
    [InlineData(@"C:\tools\")]
    // `Path.IsPathRooted` accepted both of these (OCR M8 finding 6, L27); `Path.IsPathFullyQualified` refuses
    // them, because the shell would resolve them against its own working directory.
    [InlineData(@"C:tools\FolderTreeMD.exe")]
    [InlineData(@"\tools\FolderTreeMD.exe")]
    public void Plan_RefusesAPathThatIsNotAnAbsoluteFile(string executablePath)
    {
        Assert.Throws<ArgumentException>(() => ShellRegistration.Plan(executablePath));
    }

    [Fact]
    public void IsUpToDate_TrueWhenTheReaderReturnsEveryPlannedValue()
    {
        ShellRegistrationPlan plan = ShellRegistration.Plan(ExePath);

        // The reader answers from the plan itself: the "already registered" case, which must write nothing.
        Assert.True(plan.IsUpToDate((key, name) =>
            plan.Values.SingleOrDefault(value => value.KeyPath == key && value.ValueName == name)?.Value));
    }

    [Fact]
    public void IsUpToDate_FalseWhenAValueIsMissingOrDifferent()
    {
        ShellRegistrationPlan plan = ShellRegistration.Plan(ExePath);

        string? MissingIcon(string key, string? name) =>
            name == "Icon" ? null : plan.Values.SingleOrDefault(value => value.KeyPath == key && value.ValueName == name)?.Value;

        string? StaleCommand(string key, string? name) =>
            name is null && key.EndsWith(@"\command", StringComparison.Ordinal)
                ? "\"D:\\old\\FolderTreeMD.exe\" --folder \"%1\""
                : plan.Values.SingleOrDefault(value => value.KeyPath == key && value.ValueName == name)?.Value;

        Assert.False(plan.IsUpToDate(MissingIcon));
        Assert.False(plan.IsUpToDate(StaleCommand));
        Assert.False(plan.IsUpToDate((_, _) => null));
    }

    [Fact]
    public void IsUpToDate_ComparesOrdinalSoACaseChangeIsARepair()
    {
        ShellRegistrationPlan plan = ShellRegistration.Plan(ExePath);

        Assert.False(plan.IsUpToDate((key, name) =>
            name == "Icon"
                ? @"c:\tools\FolderTreeMD.exe,0"
                : plan.Values.SingleOrDefault(value => value.KeyPath == key && value.ValueName == name)?.Value));
    }

    [Fact]
    public void IsUpToDate_RejectsANullReader()
    {
        Assert.Throws<ArgumentNullException>(() => ShellRegistration.Plan(ExePath).IsUpToDate(null!));
    }

    /// <summary>
    /// The skip decision (M8 fix round 3, LEARN[39], closing L34/L35): only *this* app's package name skips the
    /// portable registration. Every other answer — a foreign package, the family-name shape, an empty string, or
    /// no identity at all — registers, which is the direction the human chose ("detection failure or any other
    /// identity ⇒ register").
    /// </summary>
    /// <param name="packageName">The package name a process reports, or <c>null</c>.</param>
    /// <param name="expectedSkip">Whether the registration must be skipped.</param>
    [Theory]
    [InlineData("FolderTreeMD.Sparse", true)]
    [InlineData("foldertreemd.sparse", true)]
    [InlineData("Microsoft.WindowsTerminal", false)]
    [InlineData("Microsoft.WindowsTerminal_8wekyb3d8bbwe", false)]
    [InlineData("Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", false)]
    [InlineData("FolderTreeMD.Sparse.Extra", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ShouldSkipRegistration_OnlyForThisAppsOwnPackage(string? packageName, bool expectedSkip)
    {
        Assert.Equal(expectedSkip, ShellRegistration.ShouldSkipRegistration(packageName));
    }

    [Fact]
    public void PackagedIdentityName_MatchesTheManifestName()
    {
        // The manifest's Identity/@Name is the same string, and build-package.ps1 asserts the two agree
        // (M8 fix round 3, item 4). This pins the value the app compares against.
        Assert.Equal("FolderTreeMD.Sparse", ShellRegistration.PackagedIdentityName);
    }
}

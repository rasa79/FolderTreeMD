namespace FolderTreeMD.Core.Tests;

/// <summary>
/// The §7 command-line grammar, table-driven: every shape of argument list a caller can produce, and what
/// <see cref="CliCommand.Parse"/> must make of it.
/// </summary>
/// <remarks>
/// This is the coverage that M6's first version lacked (OCR run-6 findings 1, 4 and 5): the scan lived as
/// a private method on the WPF <c>App</c>, unreachable from a suite that targets <c>net8.0</c> and
/// references Core only. The two defects it then contained — an option consuming the next option as its
/// value, and a valueless <c>--to</c> silently falling back to the clipboard — are cases in the table
/// below.
/// </remarks>
public sealed class CliCommandTests
{
    /// <summary>Every argument list the parser is expected to handle, with its required outcome.</summary>
    /// <remarks>
    /// Columns: the raw arguments, the expected <see cref="CliCommandKind"/>, the expected folder, the
    /// expected output path and — for a usage failure — a fragment the message must contain. Every usage
    /// failure must also quote <see cref="CliCommand.Usage"/>, which the test asserts separately.
    /// </remarks>
    public static TheoryData<string?[], CliCommandKind, string?, string?, string?> Cases => new()
    {
        // No CLI option at all: the process shows the window.
        { Array.Empty<string>(), CliCommandKind.NotACliRequest, null, null, null },
        { ["--enable-long-paths"], CliCommandKind.NotACliRequest, null, null, null },
        { ["--unknown", "value"], CliCommandKind.NotACliRequest, null, null, null },

        // Valid requests: both options, either order, unknown arguments ignored.
        { ["--folder", @"C:\data"], CliCommandKind.ListFolder, @"C:\data", null, null },
        { ["--folder", @"C:\data", "--to", @"C:\out.md"], CliCommandKind.ListFolder, @"C:\data", @"C:\out.md", null },
        { ["--to", @"C:\out.md", "--folder", @"C:\data"], CliCommandKind.ListFolder, @"C:\data", @"C:\out.md", null },
        { ["--folder", @"C:\data", "stray", "--to", @"C:\out.md"], CliCommandKind.ListFolder, @"C:\data", @"C:\out.md", null },
        { ["--FOLDER", @"C:\data"], CliCommandKind.ListFolder, @"C:\data", null, null },

        // A repeated option keeps its last value.
        { ["--folder", @"C:\first", "--folder", @"C:\last"], CliCommandKind.ListFolder, @"C:\last", null, null },
        { ["--to", @"C:\first.md", "--folder", @"C:\data", "--to", @"C:\last.md"], CliCommandKind.ListFolder, @"C:\data", @"C:\last.md", null },

        // Usage failures: an option token is never consumed as a value, and a valueless option is an error.
        { ["--folder"], CliCommandKind.UsageError, null, null, "--folder needs a folder path" },
        { ["--folder", "--to", @"C:\out.md"], CliCommandKind.UsageError, null, null, "--folder needs a folder path" },
        { ["--folder", "   "], CliCommandKind.UsageError, null, null, "--folder needs a folder path" },
        { ["--folder", @"C:\data", "--folder"], CliCommandKind.UsageError, null, null, "--folder needs a folder path" },
        { ["--to"], CliCommandKind.UsageError, null, null, "--to needs a file path" },
        { ["--to", ""], CliCommandKind.UsageError, null, null, "--to needs a file path" },
        { ["--to", "--folder", @"C:\data"], CliCommandKind.UsageError, null, null, "--to needs a file path" },
        { ["--folder", @"C:\data", "--to"], CliCommandKind.UsageError, null, null, "--to needs a file path" },
        { ["--folder", @"C:\data", "--to", "  "], CliCommandKind.UsageError, null, null, "--to needs a file path" },

        // A --to without a folder is a CLI request with a missing required option, not a window request.
        { ["--to", @"C:\out.md"], CliCommandKind.UsageError, null, null, "--folder is required" },

        // The single-token --name=value shape is a usage failure, never a silent drop: ignoring it would
        // deliver to the clipboard for a caller who asked for a file, or open the window for a caller who
        // asked for a listing (OCR run-2 finding 3).
        { ["--to=C:\\out.md"], CliCommandKind.UsageError, null, null, "--to does not accept an '=' value" },
        { ["--folder=C:\\data"], CliCommandKind.UsageError, null, null, "--folder does not accept an '=' value" },
        { ["--folder", @"C:\data", @"--to=C:\out.md"], CliCommandKind.UsageError, null, null, "--to does not accept an '=' value" },
        { ["--FOLDER=C:\\data"], CliCommandKind.UsageError, null, null, "--folder does not accept an '=' value" },
        { ["--folder="], CliCommandKind.UsageError, null, null, "--folder does not accept an '=' value" },
        { ["--to="], CliCommandKind.UsageError, null, null, "--to does not accept an '=' value" },

        // …but only an exact, full option name followed by '=' counts; anything else stays an unknown
        // argument, which is what the notification platform's activation argument needs.
        { ["--other=value"], CliCommandKind.NotACliRequest, null, null, null },
        { ["--folderx=C:\\data"], CliCommandKind.NotACliRequest, null, null, null },
        { ["--tox=1"], CliCommandKind.NotACliRequest, null, null, null },

        // A null element is an unknown argument, never an exception: the scan had that tolerance before
        // the '=' rule, and the parser is public API (OCR run-3 finding 2). A null in value position is
        // simply "no usable value", so the option it follows fails the same way an absent value does.
        { [null], CliCommandKind.NotACliRequest, null, null, null },
        { [null, "--folder", @"C:\data"], CliCommandKind.ListFolder, @"C:\data", null, null },
        { ["--folder", null], CliCommandKind.UsageError, null, null, "--folder needs a folder path" },
        { ["--to", null], CliCommandKind.UsageError, null, null, "--to needs a file path" },

        // M8's self-registration: two whole commands, each on its own (DECISIONS.md D28).
        { ["--install"], CliCommandKind.InstallShell, null, null, null },
        { ["--uninstall"], CliCommandKind.UninstallShell, null, null, null },
        { ["--INSTALL"], CliCommandKind.InstallShell, null, null, null },

        // …and a usage failure when they are combined with each other or with a listing request: a silent
        // precedence rule would leave the caller's other instruction unhonoured.
        { ["--install", "--uninstall"], CliCommandKind.UsageError, null, null, "--install and --uninstall cannot be combined" },
        { ["--uninstall", "--install"], CliCommandKind.UsageError, null, null, "--install and --uninstall cannot be combined" },
        { ["--install", "--folder", @"C:\data"], CliCommandKind.UsageError, null, null, "--install cannot be combined with --folder" },
        { ["--uninstall", "--to", @"C:\out.md"], CliCommandKind.UsageError, null, null, "--uninstall cannot be combined with" },
        { ["--install=x"], CliCommandKind.UsageError, null, null, "--install and --uninstall carry no value" },
        { ["--uninstall="], CliCommandKind.UsageError, null, null, "--install and --uninstall carry no value" },
    };

    /// <summary>Parses one argument list and checks every field the table pins.</summary>
    /// <param name="args">Raw arguments, possibly containing a null element.</param>
    /// <param name="expectedKind">Expected kind of request.</param>
    /// <param name="expectedFolder">Expected folder, or <c>null</c>.</param>
    /// <param name="expectedOutputPath">Expected output path, or <c>null</c>.</param>
    /// <param name="expectedErrorFragment">Fragment a usage failure must contain, or <c>null</c>.</param>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Parse_ReturnsTheDocumentedOutcome(
        string?[] args,
        CliCommandKind expectedKind,
        string? expectedFolder,
        string? expectedOutputPath,
        string? expectedErrorFragment)
    {
        CliCommand command = CliCommand.Parse(args);

        Assert.Equal(expectedKind, command.Kind);
        Assert.Equal(expectedFolder, command.Folder);
        Assert.Equal(expectedOutputPath, command.OutputPath);

        if (expectedErrorFragment is null)
        {
            Assert.Null(command.Error);
            return;
        }

        Assert.NotNull(command.Error);
        Assert.Contains(expectedErrorFragment, command.Error);
        Assert.Contains(CliCommand.Usage, command.Error);
    }

    /// <summary>
    /// A <c>--to</c> without a usable value never yields a command, so the clipboard default can never
    /// stand in for a file the caller asked for (the mis-delivery OCR run 6 reported).
    /// </summary>
    [Theory]
    [InlineData("--to")]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_ValuelessOutputOption_NeverYieldsTheClipboardDefault(string value)
    {
        CliCommand command = CliCommand.Parse(["--folder", @"C:\data", "--to", value]);

        Assert.Equal(CliCommandKind.UsageError, command.Kind);
        Assert.Null(command.OutputPath);
    }
}

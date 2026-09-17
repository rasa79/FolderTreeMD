using System.IO;
using System.Text;

namespace FolderTreeMD.Core;

/// <summary>
/// Writes a listing to disk in the encoding <c>UI_SPEC.md</c> §5 rule 12 requires for a saved
/// listing: UTF-8 **without** a byte-order mark.
/// </summary>
// LEARN[21] (D19): saving is one line of framework code, but it lives here so the encoding rule can be
// machine-verified through the same call the UI makes.
// Alternatives considered: (a) call File.WriteAllText in the window's Save handler — rejected: the DoD
//   requires evidence that the saved file has no BOM, and an untestable line cannot supply it; (b) rely
//   on File.WriteAllText's default — rejected: the default is UTF-8 *without* BOM only because that is
//   the framework's choice today, and the specification makes it our rule, so it is stated explicitly;
//   (c) a stream writer with an explicit encoder — rejected as more code for the same result.
// Pros: the §5 rule 12 requirement has one named place and a test that reads the bytes back.
// Cons: a one-line wrapper around the framework call (accepted: it is the seam that makes the rule
//   verifiable, the same trade-off as D14 for the settings path).
// See also: LEARN[20]
public static class ListingFile
{
    /// <summary>Saves a listing as UTF-8 without BOM.</summary>
    /// <param name="path">Destination path.</param>
    /// <param name="markdown">Listing text, including any user edits.</param>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The location is not writable.</exception>
    public static void Save(string path, string markdown) =>
        File.WriteAllText(path, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}

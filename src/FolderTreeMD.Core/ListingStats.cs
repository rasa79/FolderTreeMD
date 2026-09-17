using System.Text;

namespace FolderTreeMD.Core;

/// <summary>
/// The counts shown in the preview header of <c>UI_SPEC.md</c> §3.4:
/// <c>{N} folders · {M} files · {C} chars</c>.
/// </summary>
/// <param name="Folders">Number of folder-flavoured lines (root, folders, link lines, access-denied lines).</param>
/// <param name="Files">Number of file lines.</param>
/// <param name="Characters">Number of characters in the listing text.</param>
// LEARN[20] (D19): these counts live in Core, as a pure function over the listing text, so the header
// value can be machine-verified instead of being a UI claim on trust.
// Alternatives considered: (a) count inside the window's TextChanged handler — rejected: the numbers
//   §3.4 specifies would then be unverifiable without a UI-driver, exactly the weakness the M4 review
//   asked to reduce; (b) have the engine return the counts alongside the markdown — rejected: it would
//   change the §6 public contract for a presentation detail, and the editor is *editable*, so the
//   counts have to follow text the engine never produced; (c) count with string.Split — rejected: it
//   allocates one string per line on every keystroke, while this scan allocates nothing.
// Pros: one tested definition of "folder line" and "file line" (a line whose entry starts with `[` is
//   a folder-flavoured line, otherwise it is a file), and the counts can be recomputed on every edit
//   because the scan is linear and allocation-free.
// Cons: the definition is syntactic — a hand-edited line that does not follow the §5 grammar is
//   classified by the same simple rule, so an editor typo shows up in the counts. That is accepted:
//   the header describes what is in the box.
// See also: LEARN[21]
public readonly record struct ListingStats(int Folders, int Files, int Characters)
{
    /// <summary>Counts the lines of a listing and its length.</summary>
    /// <param name="markdown">Listing text, possibly edited by the user.</param>
    /// <returns>The counts for the §3.4 header.</returns>
    /// <remarks>
    /// Carriage returns are excluded everywhere — from the line classification and from the character
    /// count — so the same listing reports identical numbers whether it arrived with <c>\n</c>
    /// (the engine's own output) or with <c>\r\n</c> (what a WPF <c>TextBox</c> hands back once the user
    /// has typed in it). Without that, <c>{C} chars</c> would jump by one per line the moment someone
    /// put the caret in the editor (M5 review finding 7).
    /// </remarks>
    public static ListingStats FromMarkdown(string markdown)
    {
        int folders = 0;
        int files = 0;
        int carriageReturns = 0;
        int index = 0;

        while (index < markdown.Length)
        {
            int lineEnd = markdown.IndexOf('\n', index);
            if (lineEnd < 0)
            {
                lineEnd = markdown.Length;
            }

            // The content of this line, without the \r of a \r\n ending and without the \n.
            int contentEnd = lineEnd;
            if (contentEnd > index && markdown[contentEnd - 1] == '\r')
            {
                contentEnd--;
            }

            int entry = index;
            while (entry < contentEnd && markdown[entry] == ' ')
            {
                entry++;
            }

            // A list item is "- " followed by the entry text; §5 gives folders, links and
            // access-denied lines a "[…]" entry and files a bare name.
            if (entry + 1 < contentEnd && markdown[entry] == '-' && markdown[entry + 1] == ' ')
            {
                int text = entry + 2;
                if (text < contentEnd)
                {
                    if (markdown[text] == '[')
                    {
                        folders++;
                    }
                    else
                    {
                        files++;
                    }
                }
            }

            index = lineEnd + 1;
        }

        // Every carriage return is excluded, wherever it sits, so the count describes the same text
        // the engine produced rather than the editor's storage detail.
        for (int position = markdown.IndexOf('\r'); position >= 0; position = markdown.IndexOf('\r', position + 1))
        {
            carriageReturns++;
        }

        return new ListingStats(folders, files, markdown.Length - carriageReturns);
    }

    /// <summary>
    /// Formats the counts the way §3.4 shows them, with the middle dot separator.
    /// </summary>
    /// <returns>E.g. <c>3 folders · 2 files · 120 chars</c>.</returns>
    public string ToHeaderText() => $"{Folders} folders · {Files} files · {Characters} chars";
}

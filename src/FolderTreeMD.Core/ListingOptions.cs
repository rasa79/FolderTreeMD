namespace FolderTreeMD.Core;

/// <summary>
/// Options for one markdown listing run. Mirrors the settings schema of
/// <c>UI_SPEC.md</c> §4 one to one, so an <c>AppSettings</c> instance can be mapped onto it
/// without loss once settings persistence exists.
/// </summary>
public class ListingOptions
{
    /// <summary>
    /// How many levels below the root are emitted. <c>-1</c> means unlimited,
    /// <c>0</c> means the root line only (UI_SPEC §5 rule 6).
    /// </summary>
    public int Depth { get; set; } = -1;

    /// <summary>When <c>true</c>, entries with the hidden attribute are listed as well.</summary>
    public bool IncludeHidden { get; set; }

    /// <summary>When <c>true</c>, directory symlinks and junctions are followed with cycle detection.</summary>
    public bool FollowSymlinks { get; set; }

    /// <summary>When <c>true</c>, file lines get a size suffix.</summary>
    public bool ShowFileSizes { get; set; } = true;

    /// <summary>When <c>true</c>, folder lines get a recursive size suffix.</summary>
    public bool ShowFolderSizes { get; set; }

    /// <summary>When <c>true</c>, lines get the <c>[HRSA]</c> attribute flag group.</summary>
    public bool ShowAttributes { get; set; } = true;

    /// <summary>Number of spaces per level. <c>2</c> or <c>4</c> (UI_SPEC §4).</summary>
    public int IndentSize { get; set; } = 4;
}

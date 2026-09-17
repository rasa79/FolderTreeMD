using System.Globalization;
using System.Text;

namespace FolderTreeMD.Core;

/// <summary>
/// Turns a folder into the markdown nested list defined by <c>UI_SPEC.md</c> §5.
/// This is the whole headless engine: it has no WPF dependency and is exercised by
/// <c>FolderTreeMD.Core.Tests</c> against generated fixture trees.
/// </summary>
public static class MarkdownListing
{
    /// <summary>
    /// Directory enumeration options used by the traversal: everything is returned (including
    /// hidden, system and reparse-point entries) and the engine applies the §5 filtering itself.
    /// </summary>
    // LEARN[3] (established in M1, revised in M2 and M3): M1 skipped reparse points entirely to
    // avoid cycles; M2 emitted the §5 not-followed line and never expanded them; M3 keeps both
    // modes — not followed (never expand) and followed (expand, but only if the target's
    // FileIdentity has not been visited, which is what makes a cycle terminate).
    // Alternatives considered: (a) skip reparse points — rejected: the §5 table requires the link
    //   line; (b) classify by `LinkTarget != null` — rejected: reparse tags without a target (volume
    //   mount points, cloud placeholders) have a null LinkTarget yet are still reparse points;
    //   (c) detect cycles by comparing resolved paths — rejected in LEARN[6].
    // Pros of chosen approach: the two §5 link rows fall out of one classification, and the followed
    //   mode reuses the visited-identity set rather than a second traversal mechanism.
    // Cons of chosen approach: a reparse point whose tag carries no target has no §5 line and is
    //   omitted (KNOWN_LIMITATIONS L4); the followed mode needs the per-directory identity read.
    // See also: LEARN[2], LEARN[6]
    // LEARN[4]: hidden filtering is applied by the engine after enumeration, not by
    // EnumerationOptions.AttributesToSkip, and it applies to folder *size* sums too (UI_SPEC §5
    // rule 7: "respecting the hidden-files setting").
    // Alternatives considered: (a) let AttributesToSkip filter — rejected: links must stay visible;
    //   (b) filter only the emitted entries and sum everything — rejected: rule 7 explicitly ties the
    //   sum to the hidden setting, so a hidden file must not inflate a visible folder's size.
    // Pros of chosen approach: one predicate governs what is listed and what is counted, so a folder
    //   line can never disagree with the entries beneath it.
    // Cons of chosen approach: every entry pays one attribute read, and the size walk repeats the
    //   filter while summing.
    // See also: LEARN[3]
    internal static readonly EnumerationOptions DirectoryEnumerationOptions = new()
    {
        AttributesToSkip = FileAttributes.None,

        // LEARN[31] (M6 fix round, closing L10): IgnoreInaccessible is turned off explicitly, because
        // its framework default (`true`) swallows exactly the failures this engine exists to report.
        // What happened with the default: Directory.GetDirectories/GetFiles returned an empty set for a
        //   directory the process may not read instead of throwing, so ReadChildren's catch — and with
        //   it §5's `(access denied)` line and rule 7's `~` partial sum — never ran against the real
        //   filesystem, and ReadRootChildren's deliberate propagation (D5) could not fire for an
        //   existing-but-unreadable root, leaving §7's "2 = enumeration failed" unreachable for it.
        //   The unit tests stayed green because they inject the throw through the internal seams
        //   (LEARN[5]), which bypass this option entirely. Found by M6's CLI verification, recorded as
        //   L10, measured then: `new EnumerationOptions().IgnoreInaccessible` = true;
        //   `Directory.GetDirectories(<denied>, "*", <defaults>)` = 0 entries, no exception; the same
        //   call with the option off = UnauthorizedAccessException.
        // Alternatives considered: (a) keep the default and probe readability with a separate call
        //   before enumerating — rejected: a second syscall per directory to work around an option that
        //   exists for exactly this, and the probe would have to duplicate the enumeration semantics;
        //   (b) keep the default and accept the silent under-report — rejected: it contradicts §5, §6
        //   and D5, and M7's Explorer verb would report success for a folder it could not read;
        //   (c) catch the failure only where a line can be emitted — impossible for the root, whose
        //   failure has no §5 line form (rule 10) and must propagate.
        // Pros: the per-directory contract, the partial sum and the root propagation are all reachable
        //   from the OS again; the missing-root behaviour is unchanged (DirectoryNotFoundException is
        //   thrown with the option either way).
        // Cons: an unreadable directory now costs one exception and one catch instead of being silently
        //   skipped — which is the point — and ACL-protected trees (the common Windows case) behave
        //   differently from every pre-M6 build. LEARN[5]'s note that a deny-ACL fixture is
        //   privilege-dependent is why the fixture verifies that the deny actually blocks this process
        //   and then **fails with an explicit reason** when it does not (or on a platform without
        //   Windows ACLs): xUnit 2.x has no dynamic skip, and a silent pass would be worse than a red
        //   test that states why. (Corrected in the M6 fix round 2 — the first wording said "skip".)
        // See also: LEARN[5], LEARN[10], DECISIONS.md D5
        IgnoreInaccessible = false,
    };

    /// <summary>
    /// Test seam: enumerates the subdirectories of a directory. Defaults to
    /// <see cref="Directory.GetDirectories(string, string, EnumerationOptions)"/>; a test replaces it
    /// to simulate an access-denied directory deterministically.
    /// </summary>
    // LEARN[5]: these seams exist so the access-denied contract, the reparse classification and the
    // partial folder-size sum can be tested deterministically.
    // Alternatives considered: (a) deny-ACL fixtures — rejected: the outcome depends on the
    //   privileges the test host runs with (an elevated or backup-privileged runner reads denied
    //   directories anyway) and cleanup must undo the ACL even when a test fails midway; (b) rely on
    //   code review — rejected: AccessDeniedFolderMarkedAndSiblingsContinue and
    //   Rule7_PartialSumGetsTilde are named tests; (c) a full filesystem abstraction — rejected as
    //   over-engineering for a handful of failure modes.
    // Pros of chosen approach: the production catch path runs exactly as written, on any machine and
    //   any privilege level, with no ACL setup to leak.
    // Cons of chosen approach: three internal mutable seams that tests must reset in a finally block;
    //   a test that forgot would leak the fake into other tests. Collection parallelisation is
    //   disabled for the assembly to keep that safe.
    // Revised in the M6 fix round: a *real* ACL fixture now exists as well (AccessDeniedFixtureTests),
    //   because these seams inject the exception the recovery expects — which is precisely why they
    //   could not detect that EnumerationOptions.IgnoreInaccessible (default true) stopped the real
    //   filesystem call from ever throwing it (L10). Both layers stay: the seams pin the contract
    //   deterministically on any machine, and the ACL tests pin that the framework call still reaches it.
    // See also: LEARN[2], LEARN[31]
    internal static Func<string, string[]> EnumerateDirectories { get; set; } =
        path => Directory.GetDirectories(path, "*", DirectoryEnumerationOptions);

    /// <summary>
    /// Test seam: enumerates the files of a directory. Defaults to
    /// <see cref="Directory.GetFiles(string, string, EnumerationOptions)"/>; replaced together with
    /// <see cref="EnumerateDirectories"/> by tests that need an entry the OS will not let the test
    /// create (an access-denied sibling). See LEARN[5].
    /// </summary>
    internal static Func<string, string[]> EnumerateFiles { get; set; } =
        path => Directory.GetFiles(path, "*", DirectoryEnumerationOptions);

    /// <summary>
    /// Test seam: reads one entry's attributes and, for reparse points, its stored link target.
    /// Defaults to <see cref="InspectEntryOnDisk"/>; tests replace it when the machine cannot create
    /// the entry kind under test (file symlinks need elevation here, and a reparse attribute cannot
    /// be forced onto an ordinary file — see KNOWN_LIMITATIONS L4). See LEARN[5].
    /// </summary>
    internal static Func<string, EntryInfo?> InspectEntry { get; set; } = InspectEntryOnDisk;

    /// <summary>
    /// Test seam: reads a directory's identity for cycle detection. Defaults to
    /// <see cref="FileIdentityReader.TryRead"/>; replaced by a test that needs the "identity could not
    /// be read" branch (a link whose target cannot be opened, e.g. a link to a denied directory).
    /// See LEARN[5].
    /// </summary>
    internal static Func<string, FileIdentity?> ReadFileIdentity { get; set; } = FileIdentityReader.TryRead;

    /// <summary>
    /// Generates the markdown listing for <paramref name="rootPath"/>.
    /// </summary>
    /// <param name="rootPath">Absolute path of the folder that becomes the root line.</param>
    /// <param name="options">Listing options; see <see cref="ListingOptions"/>.</param>
    /// <param name="progress">Optional receiver for the number of entries emitted so far.</param>
    /// <param name="ct">Cancellation token, checked between directories (UI_SPEC §6).</param>
    /// <returns>The markdown listing, ending in a single newline.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="IOException">
    /// The root folder itself could not be read (missing, or an I/O failure). Unlike a nested
    /// directory — which becomes <c>(access denied)</c> and lets the run continue — a root failure
    /// has no §5 line and must reach the caller, because callers need to distinguish "listed an
    /// unreadable subfolder" from "could not list anything at all" (the CLI exit codes of §7 depend
    /// on it). See DECISIONS.md D5.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The root folder denied access. See <see cref="IOException"/>.</exception>
    // LEARN[2]: the whole traversal is driven by one explicit stack instead of recursive calls.
    // Alternatives considered: (a) recursion over Directory.GetDirectories / GetFiles — the obvious
    //   short version; (b) an explicit queue processed breadth first; (c) async enumeration.
    // Pros of chosen approach: UI_SPEC §6 forbids recursion because a 300+ level tree would overflow
    //   the call stack (acceptance criterion 10), and an explicit stack keeps that guarantee in our
    //   hands rather than the runtime's. Each iteration handles one directory: it enumerates and
    //   sorts that directory's children, pushes subdirectories (reverse order) then files, and emits
    //   its own line first. That yields exactly the §5 rule 4 order — a folder line immediately
    //   followed by its children, folders before files — with no re-ordering pass. Cancellation is
    //   checked once per directory and progress is reported per emitted entry, both required by §6.
    // Cons of chosen approach: more code than recursion, and the Frame record carries the state
    //   (level, name, relative path, attributes) recursive locals would have held for free. The stack
    //   holds one frame per pending entry, so memory is proportional to the entry count, not depth.
    // See also: LEARN[1]
    // LEARN[7] (revised in the M3 review round — see LEARN[10]): a folder's size is computed from its
    // own subtree when its line is emitted, rather than by threading a running total through the
    // traversal.
    // Alternatives considered: (a) accumulate each child's size into its parent frame while
    //   traversing — impossible without a second pass: frames are pushed in pre-order and popped
    //   parent-first, so a parent's total is only known after its children have been *emitted*;
    //   (b) cache subtree sums in a dictionary keyed by path — deliberately not done: an optimisation
    //   with no measured need (karpathy-guidelines §2), and the budget in the walk already bounds the
    //   damage on pathological trees.
    // Pros of chosen approach: one definition of "what is in this folder" — the same hidden filter and
    //   the same access-denied handling the listing uses — and rule 7's partial-sum tilde falls out of
    //   the walk.
    // Cons of chosen approach: work is repeated — a folder at depth d is walked once for each of its
    //   ancestors that also displays a size, so the worst case is O(entries × depth). Folder sizes are
    //   opt-in and off by default (§3.3) and the walk is in-memory, so this is accepted; the walk
    //   itself is iterative and never enters reparse points, which LEARN[10] explains.
    // See also: LEARN[2], LEARN[10]
    public static string Generate(string rootPath, ListingOptions options, IProgress<int>? progress, CancellationToken ct)
    {
        var lines = new List<string>();
        var stack = new Stack<Frame>();

        // LEARN[9] (revised in the M3 round-2 review, D13): cycle detection (D10) tracks the identity
        // of the *current expansion path* — the chain of ancestors of the frame being processed — not
        // every directory ever entered. One slot is appended per expanded directory, **including a
        // null slot when the identity cannot be read**, so position always equals tree level.
        // Alternatives considered: (a) one growing set of every expanded directory — rejected: a link
        //   to a directory that merely appears elsewhere in the tree (a sibling listed earlier) would
        //   be misreported as a cycle and silently downgraded to the not-followed form, even though
        //   following it terminates; (b) a set per link, cleared after each — rejected: it would need
        //   its own traversal bookkeeping and would still have to distinguish ancestors from siblings;
        //   (c) appending only readable identities — rejected (this was the bug the review caught):
        //   a failed read would shift every deeper entry one slot down, so `TruncateTo` would leave a
        //   previous branch's entry in place and a link to a mere sibling-branch descendant would be
        //   misreported as a cycle.
        // Pros of chosen approach: "cycle" means exactly what it should — the target is one of this
        //   frame's own ancestors — and the index/level invariant is unconditional, so truncation is
        //   always exact. A null slot never matches a real identity, so an unreadable ancestor simply
        //   cannot be recognised by identity (it is then caught on the next encounter, once the
        //   followed link has recorded its target).
        // Cons of chosen approach: the list is a list of nullable identities, and the traversal must
        //   keep the "one append per expansion" invariant (see TruncateTo and Remember).
        // See also: LEARN[3], LEARN[6]
        var pathIdentities = new List<FileIdentity?>();
        string rootName = new DirectoryInfo(rootPath).Name;
        int entries = 0;

        // The root is line 0 in every case: UI_SPEC §5 table gives it "- [{RootName}]{folder-size?}"
        // — never indented and never attributed (rule 10).
        lines.Add($"- [{rootName}]{FolderSuffix(rootPath, options, attributes: null, ct)}");

        // The root is read before the depth check, so `Depth == 0` still proves the root exists and
        // is readable: a root failure must propagate for every Depth value (rule 10, DECISIONS.md D5),
        // and §5 defines no root line that could report it.
        (string[] rootDirectories, string[] rootFiles) = ReadRootChildren(rootPath);

        if (options.Depth == 0)
        {
            return Flush(lines);
        }

        // The root's identity anchors the ancestor path, so a link back to the root is detected as a
        // cycle. Identity work happens only in followed mode (M3 review item 3): with the default
        // `followSymlinks = false` a link is never entered, so a cycle cannot form and the per-
        // directory handle read would be pure cost.
        if (options.FollowSymlinks)
        {
            TruncateTo(pathIdentities, level: 0);
            Remember(rootPath, pathIdentities);
        }

        PushChildren(stack, Frame.Directory(rootPath, 0, rootName, string.Empty), rootDirectories, rootFiles, options, ref entries, progress);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            Frame frame = stack.Pop();

            if (frame.IsFile)
            {
                // A file that is a reparse point keeps the §5 link form, with its file name; only a
                // plain file gets the size/attribute suffix group (see LinkNotFollowed_HasNoSizeOrAttributeSuffix).
                lines.Add(frame.LinkTarget is null
                    ? $"{Indent(frame.Level, options.IndentSize)}- {frame.Name}{FileSuffix(frame.Path, options)}"
                    : $"{Indent(frame.Level, options.IndentSize)}- {frame.Name} (link → {frame.LinkTarget})");
                progress?.Report(++entries);
                continue;
            }

            // LEARN[8]: a link line — followed or not — carries *only* the (link → target) annotation.
            // Size and attribute suffixes are never appended to it, and a followed link's children are
            // listed beneath it as ordinary annotated entries.
            // Alternatives considered: (a) annotate a link line like a folder or file (size, [HRSA])
            //   — rejected: the §5 table gives the link rows exactly one suffix form; (b) treat a
            //   followed link as a transparent alias and list the target's content only once —
            //   rejected: §6 says the target's children are listed as normal entries, so a target
            //   reachable twice is listed twice; (c) append the `[L]` flag of an older example —
            //   removed from §5 by the human in the M3 correction round.
            // Pros: one unambiguous form per mode; every annotation appears where the spec puts it;
            //   LinkNotFollowed_HasNoSizeOrAttributeSuffix pins "only the annotation" with every
            //   annotation setting switched on.
            // Cons: when a followed link's target is also reachable inside the tree its subtree is
            //   listed twice (once under the link, once under the real path) — required by §6, but
            //   visually redundant.
            if (frame.IsLink)
            {
                string linkTarget = frame.LinkTarget ?? string.Empty;

                if (!options.FollowSymlinks)
                {
                    lines.Add($"{Indent(frame.Level, options.IndentSize)}- [{frame.RelativePath}] (link → {linkTarget})");
                    progress?.Report(++entries);
                    continue;
                }

                // UI_SPEC §5 rule 4 (and M3 review item 1): the depth limit applies to a followed link
                // exactly as it does to a folder — the link's own line is emitted, its children are
                // not. The bare form is used because "→ followed" would claim children that are not
                // there; the identity read is skipped too, since nothing will be entered.
                if (options.Depth >= 0 && frame.Level >= options.Depth)
                {
                    lines.Add($"{Indent(frame.Level, options.IndentSize)}- [{frame.RelativePath}] (link → {linkTarget})");
                    progress?.Report(++entries);
                    continue;
                }

                // UI_SPEC §6 / D10: only a target on the current ancestor path is a cycle. The path is
                // truncated to this frame's level first, which drops any earlier sibling of the same
                // level — a link to a sibling is therefore followed, not mistaken for a cycle.
                TruncateTo(pathIdentities, frame.Level);
                FileIdentity? targetIdentity = ReadFileIdentity(frame.Path);
                if (targetIdentity is null || pathIdentities.Contains(targetIdentity.Value))
                {
                    lines.Add($"{Indent(frame.Level, options.IndentSize)}- [{frame.RelativePath}] (link → {linkTarget})");
                    progress?.Report(++entries);
                    continue;
                }

                // Followed: the resolved directory behaves like any other folder, so the link line is
                // emitted with the "→ followed" marker and the target's children are listed beneath
                // it at the link's level (UI_SPEC §5 table, "Symlink/junction (followed)").
                pathIdentities.Add(targetIdentity.Value);
                lines.Add($"{Indent(frame.Level, options.IndentSize)}- [{frame.RelativePath}] (link → {linkTarget} → followed)");
                progress?.Report(++entries);

                (string[] linkDirectories, string[] linkFiles, bool linkDenied) = ReadChildren(frame);
                if (!linkDenied)
                {
                    PushChildren(stack, frame, linkDirectories, linkFiles, options, ref entries, progress);
                }

                continue;
            }

            bool atDepthLimit = options.Depth >= 0 && frame.Level >= options.Depth;
            (string[] subDirectories, string[] files, bool accessDenied) = atDepthLimit
                ? ([], [], false)
                : ReadChildren(frame);

            // UI_SPEC §5 rule 4: a folder at the depth limit still gets its line, it just does not
            // get children — so the cutoff is applied here, after the line, not when pushing. Its
            // size is still shown (rule 7 has no depth exception).
            lines.Add(accessDenied
                ? $"{Indent(frame.Level, options.IndentSize)}- [{frame.RelativePath}] (access denied)"
                : $"{Indent(frame.Level, options.IndentSize)}- [{frame.RelativePath}]{FolderSuffix(frame.Path, options, frame.Attributes, ct)}");
            progress?.Report(++entries);

            if (!accessDenied)
            {
                if (options.FollowSymlinks)
                {
                    TruncateTo(pathIdentities, frame.Level);
                    Remember(frame.Path, pathIdentities);
                }

                PushChildren(stack, frame, subDirectories, files, options, ref entries, progress);
            }
        }

        return Flush(lines);
    }

    /// <summary>
    /// Trims the ancestor path to <paramref name="level"/> entries, so the entry at that level can be
    /// replaced by the directory now being expanded (LEARN[9]).
    /// </summary>
    /// <param name="pathIdentities">Ancestor identities by tree level; a <c>null</c> slot means the
    /// identity of the directory at that level could not be read.</param>
    /// <param name="level">Level of the directory about to be expanded or checked.</param>
    private static void TruncateTo(List<FileIdentity?> pathIdentities, int level)
    {
        while (pathIdentities.Count > level)
        {
            pathIdentities.RemoveAt(pathIdentities.Count - 1);
        }
    }

    /// <summary>
    /// Builds the suffix group of a folder line in the §5 rule 7/9 order: size first, then
    /// attributes. The root (<paramref name="attributes"/> <c>null</c>) never gets attribute flags.
    /// </summary>
    /// <param name="path">Absolute path of the folder.</param>
    /// <param name="options">Listing options.</param>
    /// <param name="attributes">Folder attributes, or <c>null</c> for the root line.</param>
    /// <param name="ct">Cancellation token, observed inside the size walk (M3 review item 5).</param>
    /// <returns>The suffix text, empty when nothing is enabled or present.</returns>
    private static string FolderSuffix(string path, ListingOptions options, FileAttributes? attributes, CancellationToken ct)
    {
        var suffix = new StringBuilder();

        if (options.ShowFolderSizes && TryMeasureFolder(path, options, ct, out string size))
        {
            suffix.Append(" (").Append(size).Append(')');
        }

        if (attributes is not null)
        {
            suffix.Append(AttributeSuffix(attributes.Value, options.ShowAttributes));
        }

        return suffix.ToString();
    }

    /// <summary>Builds the suffix group of a file line (UI_SPEC §5 rule 2 order).</summary>
    /// <param name="path">Absolute path of the file.</param>
    /// <param name="options">Listing options.</param>
    /// <returns>The suffix text, empty when nothing is enabled or readable.</returns>
    private static string FileSuffix(string path, ListingOptions options)
    {
        var suffix = new StringBuilder();

        if (options.ShowFileSizes)
        {
            try
            {
                suffix.Append(" (").Append(FormatSize(new FileInfo(path).Length)).Append(')');
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // An unreadable file keeps its line, it just gets no size.
            }
        }

        if (options.ShowAttributes && SafeInspectEntry(path) is EntryInfo entry)
        {
            suffix.Append(AttributeSuffix(entry.Attributes, enabled: true));
        }

        return suffix.ToString();
    }

    /// <summary>
    /// Builds the <c>[HRSA]</c> flag group: letters in the fixed order H, R, S, A, only present flags
    /// shown, and the brackets omitted entirely when no flag applies or attributes are off
    /// (UI_SPEC §5 rule 3).
    /// </summary>
    /// <param name="attributes">Attributes of the entry.</param>
    /// <param name="enabled">Whether the attribute setting is on.</param>
    /// <returns>The flag group, or an empty string.</returns>
    private static string AttributeSuffix(FileAttributes attributes, bool enabled)
    {
        if (!enabled)
        {
            return string.Empty;
        }

        var flags = new StringBuilder();
        if (attributes.HasFlag(FileAttributes.Hidden))
        {
            flags.Append('H');
        }

        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            flags.Append('R');
        }

        if (attributes.HasFlag(FileAttributes.System))
        {
            flags.Append('S');
        }

        if (attributes.HasFlag(FileAttributes.Archive))
        {
            flags.Append('A');
        }

        return flags.Length == 0 ? string.Empty : " [" + flags + "]";
    }

    /// <summary>
    /// Formats a byte count as <c>N B</c> / <c>N.N KB</c> / <c>N.N MB</c> — one decimal, trailing
    /// <c>.0</c> trimmed (UI_SPEC §5 rule 2). Units step at 1024, with no unit above MB defined by
    /// the spec, so anything larger keeps growing in MB.
    /// </summary>
    /// <param name="bytes">Size in bytes.</param>
    /// <returns>The formatted size.</returns>
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        double value = bytes / 1024d;
        string unit = "KB";
        if (value >= 1024d)
        {
            value /= 1024d;
            unit = "MB";
        }

        // "0.#" writes one decimal only when it is not zero — exactly "trim a trailing .0".
        return value.ToString("0.#", CultureInfo.InvariantCulture) + " " + unit;
    }

    /// <summary>
    /// Measures a folder for the §5 rule 7 suffix: iterative sum of the contained file sizes,
    /// respecting rules 5 (hidden) and 7 (reparse points are never entered), with the <c>~</c> marker
    /// when the sum is partial.
    /// </summary>
    /// <param name="path">Absolute path of the folder.</param>
    /// <param name="options">Listing options.</param>
    /// <param name="ct">Cancellation token, checked between directories (M3 review item 5).</param>
    /// <param name="size">Receives the formatted size, with the tilde when partial.</param>
    /// <returns>
    /// <c>false</c> only when the folder itself could not be read — a nested access-denied subtree
    /// makes the sum partial, not unmeasurable.
    /// </returns>
    // LEARN[10] (D9): the size walk is an explicit stack, like the listing traversal, and it never
    // enters a reparse point in either link mode (UI_SPEC §5 rule 7).
    // Alternatives considered: (a) the mutually recursive SumEntries/SumFolder pair this replaces —
    //   rejected on two counts: UI_SPEC §6 forbids recursion (a 300+ level tree must not overflow the
    //   call stack, and LEARN[2] already made the listing iterative for exactly that reason), and it
    //   recursed into reparse points when followSymlinks was on, so a junction cycle made the sum walk
    //   unbounded; (b) keeping recursion and only adding cycle detection — rejected: the stack-depth
    //   hazard would remain, and rule 7 now states flatly that reparse points are never entered;
    //   (c) memoising subtree sums — still unavailable: it would not remove the need for an iterative
    //   walk, and there is no measured need for the cache.
    // Pros of chosen approach: same bounded-stack guarantee as the listing; skipping reparse points
    //   makes a cyclic junction impossible to enter, so the walk always terminates; the cancellation
    //   token is checked on every iteration.
    // Cons of chosen approach: a folder whose content is reachable only through a junction sums to
    //   less than the target physically holds (KNOWN_LIMITATIONS L5), and the explicit stack needs the
    //   "first popped directory is the measured one" convention to tell "unmeasurable" from "partial".
    // See also: LEARN[2], LEARN[7]
    private static bool TryMeasureFolder(string path, ListingOptions options, CancellationToken ct, out string size)
    {
        var budget = new Budget();
        var pending = new Stack<string>();
        long total = 0;
        bool partial = false;
        bool isMeasuredFolder = true;

        pending.Push(path);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            if (!budget.TrySpend())
            {
                partial = true;
                break;
            }

            string directory = pending.Pop();
            string[] subDirectories;
            string[] files;
            try
            {
                subDirectories = EnumerateDirectories(directory);
                files = EnumerateFiles(directory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                if (isMeasuredFolder)
                {
                    // The measured folder itself is unreadable: no size at all.
                    size = string.Empty;
                    return false;
                }

                // A nested denied subtree contributes nothing and makes the sum partial (rule 7).
                partial = true;
                continue;
            }

            isMeasuredFolder = false;

            foreach (string file in files)
            {
                if (SafeInspectEntry(file) is not EntryInfo entry)
                {
                    continue;
                }

                if (!options.IncludeHidden && entry.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    continue; // rule 5 applies to sums as well as to lines
                }

                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue; // rule 7: never entered for sums, in either link mode
                }

                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    partial = true;
                }
            }

            foreach (string subDirectory in subDirectories)
            {
                if (SafeInspectEntry(subDirectory) is not EntryInfo entry)
                {
                    continue;
                }

                if (!options.IncludeHidden && entry.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    continue;
                }

                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue; // rule 7: a junction is not followed while summing, even in followed mode
                }

                pending.Push(subDirectory);
            }
        }

        size = (partial ? "~" : string.Empty) + FormatSize(total);
        return true;
    }

    /// <summary>
    /// Enumerates one nested directory's children, translating UI_SPEC §6's per-directory failure
    /// contract into a flag: an unreadable directory returns empty arrays and <c>true</c> instead of
    /// throwing, so the caller can emit the <c>(access denied)</c> line and carry on with siblings.
    /// This recovery applies to *nested* directories only — see <see cref="ReadRootChildren"/>.
    /// </summary>
    /// <param name="frame">Directory to read.</param>
    /// <returns>Subdirectories, files, and whether the directory was unreadable.</returns>
    private static (string[] Directories, string[] Files, bool AccessDenied) ReadChildren(Frame frame)
    {
        try
        {
            return (EnumerateDirectories(frame.Path), EnumerateFiles(frame.Path), false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return ([], [], true);
        }
    }

    /// <summary>
    /// Reads the root's children without the per-directory recovery applied to nested directories.
    /// </summary>
    /// <param name="rootPath">Absolute path of the root folder.</param>
    /// <returns>Subdirectories and files of the root.</returns>
    /// <exception cref="IOException">The root could not be read; callers must handle this.</exception>
    /// <exception cref="UnauthorizedAccessException">The root denied access; callers must handle this.</exception>
    private static (string[] Directories, string[] Files) ReadRootChildren(string rootPath) =>
        (EnumerateDirectories(rootPath), EnumerateFiles(rootPath));

    /// <summary>
    /// Pushes a directory's already-read children onto <paramref name="stack"/> in reverse listing
    /// order — files first so subdirectories end up on top — after applying the hidden filter
    /// (UI_SPEC §5 rules 4 and 5) and classifying reparse points as link frames.
    /// </summary>
    /// <param name="stack">Traversal stack to push onto.</param>
    /// <param name="frame">The directory whose children are being queued.</param>
    /// <param name="subDirectories">Subdirectory paths returned by the reader.</param>
    /// <param name="files">File paths returned by the reader.</param>
    /// <param name="options">Listing options; <see cref="ListingOptions.IncludeHidden"/> is read here.</param>
    /// <param name="entries">Running count of emitted entries, for progress reporting.</param>
    /// <param name="progress">Optional progress receiver.</param>
    private static void PushChildren(
        Stack<Frame> stack,
        Frame frame,
        string[] subDirectories,
        string[] files,
        ListingOptions options,
        ref int entries,
        IProgress<int>? progress)
    {
        Array.Sort(subDirectories, StringComparer.OrdinalIgnoreCase);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        int childLevel = frame.Level + 1;

        for (int i = files.Length - 1; i >= 0; i--)
        {
            string path = files[i];
            string name = Path.GetFileName(path);

            // try/catch per entry (not just per directory): an entry that disappears or becomes
            // unreadable between enumeration and inspection is skipped, and the listing continues.
            if (SafeInspectEntry(path) is not EntryInfo fileEntry)
            {
                continue;
            }

            if (!options.IncludeHidden && fileEntry.Attributes.HasFlag(FileAttributes.Hidden))
            {
                continue; // UI_SPEC §5 rule 5
            }

            if (fileEntry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // A file symlink gets the same §5 link line as a directory link, showing its file
                // name (UI_SPEC §5 rule 1 and the "Symlink/junction (not followed)" row).
                if (fileEntry.LinkTarget is not null)
                {
                    stack.Push(Frame.FileLink(path, childLevel, name, Append(frame.RelativePath, name), fileEntry.LinkTarget));
                }

                continue; // No target → no §5 line → omitted, recorded as L4.
            }

            stack.Push(Frame.File(path, childLevel, name, Append(frame.RelativePath, name)));
        }

        for (int i = subDirectories.Length - 1; i >= 0; i--)
        {
            string path = subDirectories[i];
            string name = Path.GetFileName(path);

            if (SafeInspectEntry(path) is not EntryInfo directoryEntry)
            {
                continue;
            }

            if (!options.IncludeHidden && directoryEntry.Attributes.HasFlag(FileAttributes.Hidden))
            {
                continue; // The same predicate as files, so the rule cannot diverge (LEARN[4]).
            }

            string relativePath = Append(frame.RelativePath, name);

            if (directoryEntry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // Never expanded unless followSymlinks is on; an entry with no target has no §5 line
                // and is omitted (L4).
                if (directoryEntry.LinkTarget is not null)
                {
                    stack.Push(Frame.Link(
                        path,
                        childLevel,
                        name,
                        relativePath,
                        directoryEntry.LinkTarget,
                        directoryEntry.Attributes));
                }

                continue;
            }

            // Identities are recorded when a directory is *expanded*, not here — see Generate.
            stack.Push(Frame.Directory(path, childLevel, name, relativePath, directoryEntry.Attributes));
        }
    }

    /// <summary>
    /// Appends one slot for a directory to the current ancestor path: its identity, or <c>null</c>
    /// when the identity cannot be read. The slot is appended either way so that position always
    /// equals tree level (LEARN[9], DECISIONS.md D13) — skipping the append would shift every deeper
    /// entry down one slot and make <see cref="TruncateTo"/> leave stale entries in place.
    /// </summary>
    /// <param name="path">Absolute path of the directory.</param>
    /// <param name="pathIdentities">Ancestor identities by tree level.</param>
    private static void Remember(string path, List<FileIdentity?> pathIdentities) =>
        pathIdentities.Add(ReadFileIdentity(path));

    /// <summary>
    /// Inspects one entry for the traversal: calls <see cref="InspectEntry"/> and converts a failure
    /// into <c>null</c> so the caller can skip that single entry and keep listing.
    /// </summary>
    /// <param name="path">Absolute path of the entry.</param>
    /// <returns>The entry's identity, or <c>null</c> when it could not be inspected.</returns>
    private static EntryInfo? SafeInspectEntry(string path)
    {
        try
        {
            return InspectEntry(path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads one entry's attributes and, for a reparse point, the target as the OS stores it.
    /// </summary>
    /// <param name="path">Absolute path of the entry.</param>
    /// <returns>
    /// The entry's identity, or <c>null</c> when the entry has no readable identity — the caller
    /// then skips that entry and keeps listing. Exceptions are converted by <see cref="SafeInspectEntry"/>.
    /// </returns>
    private static EntryInfo? InspectEntryOnDisk(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        string? linkTarget = null;

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // Directory or file decides which FileSystemInfo can read the tag.
            linkTarget = attributes.HasFlag(FileAttributes.Directory)
                ? new DirectoryInfo(path).LinkTarget
                : new FileInfo(path).LinkTarget;
        }

        return new EntryInfo(attributes, linkTarget);
    }

    /// <summary>Joins a parent relative path with a child name using <c>/</c> (UI_SPEC §5 rule 1).</summary>
    /// <param name="parent">Parent relative path; empty for the root.</param>
    /// <param name="name">Child entry name.</param>
    /// <returns>The child's root-relative path with forward slashes.</returns>
    private static string Append(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    /// <summary>
    /// Joins the emitted lines into the final string. Every line is followed by <c>\n</c>, so the
    /// listing ends with exactly one trailing newline (UI_SPEC §5 rule 8).
    /// </summary>
    /// <param name="lines">Lines emitted so far.</param>
    /// <returns>The complete markdown listing.</returns>
    private static string Flush(List<string> lines)
    {
        var builder = new StringBuilder();
        foreach (string line in lines)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Builds the indentation prefix for a level: <paramref name="indentSize"/> spaces per level.</summary>
    /// <param name="level">Tree level of the entry; the root is 0.</param>
    /// <param name="indentSize">Spaces per level.</param>
    /// <returns>The indentation string.</returns>
    private static string Indent(int level, int indentSize) => new(' ', level * indentSize);

    /// <summary>What the engine needs to know about one enumerated entry.</summary>
    /// <param name="Attributes">Attributes as reported for the entry.</param>
    /// <param name="LinkTarget">
    /// Target of a reparse point as the OS stores it, or <c>null</c> when the entry is not a reparse
    /// point **or** its reparse tag carries no target (volume mount point, cloud placeholder).
    /// </param>
    internal readonly record struct EntryInfo(FileAttributes Attributes, string? LinkTarget);

    /// <summary>
    /// Cap on how many entries one folder-size sum may visit. A folder sum is opt-in and explicitly
    /// warned about in the UI (§3.3), but the engine must still not be able to run away on a
    /// pathological tree: when the cap is hit the sum is simply marked partial (rule 7's tilde).
    /// </summary>
    private sealed class Budget
    {
        private const int MaxEntries = 5_000_000;

        private int _remaining = MaxEntries;

        /// <summary>Takes one unit of budget.</summary>
        /// <returns><c>false</c> when the budget is exhausted.</returns>
        public bool TrySpend() => _remaining-- > 0;
    }

    /// <summary>One entry waiting to be listed, plus the state needed to emit its line.</summary>
    /// <param name="Path">Absolute path of the entry; used to enumerate directories.</param>
    /// <param name="Level">Depth of the entry: 0 for the root, +1 per level below it.</param>
    /// <param name="Name">File-system name of the entry, used for file lines.</param>
    /// <param name="RelativePath">Root-relative path with <c>/</c> separators (UI_SPEC §5 rule 1).</param>
    /// <param name="IsFile">Whether this is a file (a leaf that is never expanded).</param>
    /// <param name="IsLink">Whether this is a directory reparse point.</param>
    /// <param name="LinkTarget">Stored target of a reparse point; <c>null</c> for plain entries.</param>
    /// <param name="Attributes">Attributes of the entry, used for the <c>[HRSA]</c> flags.</param>
    private readonly record struct Frame(
        string Path,
        int Level,
        string Name,
        string RelativePath,
        bool IsFile,
        bool IsLink,
        string? LinkTarget,
        FileAttributes Attributes)
    {
        /// <summary>Creates a frame for a directory that will be expanded.</summary>
        /// <param name="path">Absolute path.</param>
        /// <param name="level">Tree level.</param>
        /// <param name="name">Entry name.</param>
        /// <param name="relativePath">Root-relative path.</param>
        /// <param name="attributes">Directory attributes.</param>
        /// <returns>The frame.</returns>
        public static Frame Directory(string path, int level, string name, string relativePath, FileAttributes attributes = default) =>
            new(path, level, name, relativePath, IsFile: false, IsLink: false, LinkTarget: null, attributes);

        /// <summary>Creates a frame for a file.</summary>
        /// <param name="path">Absolute path.</param>
        /// <param name="level">Tree level.</param>
        /// <param name="name">Entry name.</param>
        /// <param name="relativePath">Root-relative path.</param>
        /// <returns>The frame.</returns>
        public static Frame File(string path, int level, string name, string relativePath) =>
            new(path, level, name, relativePath, IsFile: true, IsLink: false, LinkTarget: null, Attributes: default);

        /// <summary>Creates a frame for a file that is a reparse point.</summary>
        /// <param name="path">Absolute path.</param>
        /// <param name="level">Tree level.</param>
        /// <param name="name">Entry name.</param>
        /// <param name="relativePath">Root-relative path.</param>
        /// <param name="linkTarget">Stored target of the reparse point.</param>
        /// <returns>The frame.</returns>
        public static Frame FileLink(string path, int level, string name, string relativePath, string linkTarget) =>
            new(path, level, name, relativePath, IsFile: true, IsLink: false, LinkTarget: linkTarget, Attributes: default);

        /// <summary>Creates a frame for a directory reparse point.</summary>
        /// <param name="path">Absolute path.</param>
        /// <param name="level">Tree level.</param>
        /// <param name="name">Entry name.</param>
        /// <param name="relativePath">Root-relative path.</param>
        /// <param name="linkTarget">Stored target of the reparse point.</param>
        /// <param name="attributes">Link attributes, used when the link is followed.</param>
        /// <returns>The frame.</returns>
        public static Frame Link(string path, int level, string name, string relativePath, string linkTarget, FileAttributes attributes) =>
            new(path, level, name, relativePath, IsFile: false, IsLink: true, LinkTarget: linkTarget, attributes);
    }
}

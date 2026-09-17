using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FolderTreeMD.Core;
using Microsoft.Win32;

namespace FolderTreeMD;

/// <summary>
/// The single application window: pick a folder, tune the listing settings, generate the markdown into
/// the editable preview, then save or copy it. Settings persist on every change (UI_SPEC §3.3) and are
/// restored at startup, and every failure is reported in the §3.5 status area.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// How long the §3.5 "Copied to clipboard" state stays before returning to Ready.
    /// </summary>
    private static readonly TimeSpan CopiedFeedbackDuration = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Shortest gap between two status-line progress updates (M5 review finding 5). The engine still
    /// reports once per entry — that contract is unchanged — but only about one report in ten reaches
    /// the UI on a busy tree, so the dispatcher is not flooded.
    /// </summary>
    private const int ProgressThrottleMilliseconds = 100;

    /// <summary>Path of the folder currently shown in <see cref="PathBox"/>, or <c>null</c>.</summary>
    private string? _selectedFolder;

    /// <summary>The persisted settings; every control reads from and writes to this instance.</summary>
    private AppSettings _settings = new();

    /// <summary>
    /// Whether the controls are being populated from <see cref="_settings"/>. Change handlers run
    /// while loading too, and without this guard startup would save what it just read and generate a
    /// listing — which §4 explicitly forbids for the restored <c>lastFolder</c>.
    /// </summary>
    private bool _loading;

    /// <summary>Cancellation source of the running enumeration, or <c>null</c> when idle.</summary>
    private CancellationTokenSource? _enumeration;

    /// <summary>
    /// Set when a settings change arrives while a run is in flight: the run is cancelled and a fresh
    /// one started with the new options, so a listing built from stale options can never survive
    /// (M5 review finding 3). Cleared by the run that acts on it.
    /// </summary>
    private bool _restartAfterCancellation;

    /// <summary>
    /// The write failure that currently owns the status line, with the kind of write that failed, or
    /// <c>null</c> when no write failure is outstanding. Both writers — the settings file and a Save-as
    /// export — record their failures here, so the status line has one owner concept instead of a
    /// per-call-site obligation (D21).
    /// </summary>
    private (SaveKind Kind, string Message)? _pendingFailure;

    /// <summary>Whether the line currently shows the §3.5 copy feedback this timer owns.</summary>
    private bool _copyFeedbackShowing;

    /// <summary>Returns the status line to Ready after the §3.5 copy feedback.</summary>
    private readonly DispatcherTimer _statusResetTimer = new() { Interval = CopiedFeedbackDuration };

    /// <summary>
    /// Initializes the window: restore settings and the last folder, then paint the §3.5 status line
    /// and the §3.4 header for the empty state.
    /// </summary>
    /// <remarks>
    /// The baseline <see cref="StatusKind.Ready"/> is set **before** <see cref="RestoreLastFolder"/>,
    /// because that method may replace it with the D16 notice for a stale <c>lastFolder</c>. Setting it
    /// afterwards silently discarded the notice (M5 review finding 1).
    /// </remarks>
    public MainWindow()
    {
        InitializeComponent();

        _statusResetTimer.Tick += (_, _) => OnCopyFeedbackEnded();

        _settings = SettingsStore.Load();
        ApplySettingsToControls();
        SetStatus(StatusKind.Ready);
        RestoreLastFolder();
        UpdateLongPathsState();
        EditorPlaceholder.Visibility = Editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMeta();
        UpdateActionAvailability();
    }

    /// <summary>Opens the folder picker; choosing a folder generates its listing and is remembered.</summary>
    /// <param name="sender">The Browse button.</param>
    /// <param name="e">Click arguments.</param>
    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a folder to list" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SelectFolder(dialog.FolderName);
        PersistSettings();

        // No repaint here: GenerateAsync paints progress before its first await, and the run's completion
        // repaints a pending failure (M6 fix round 2, item 5), so anything painted at this point would be
        // overwritten in the same dispatcher turn.
        _ = GenerateAsync();
    }

    /// <summary>Starts an enumeration, or cancels the one in progress (§3.2, §3.5).</summary>
    /// <param name="sender">The Generate/Cancel button.</param>
    /// <param name="e">Click arguments.</param>
    private void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_enumeration is not null)
        {
            _enumeration.Cancel();
            return;
        }

        _ = GenerateAsync();
    }

    /// <summary>
    /// Saves the current editor text as markdown, UTF-8 without BOM (§3.5, §5 rule 12). A failure is
    /// recorded as the pending write failure, exactly like a settings failure, so both kinds own the
    /// status line through one mechanism (D21).
    /// </summary>
    /// <param name="sender">The Save button.</param>
    /// <param name="e">Click arguments.</param>
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string defaultName = Path.GetFileName(_selectedFolder ?? string.Empty);
        if (string.IsNullOrEmpty(defaultName))
        {
            defaultName = "listing";
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md",
            FileName = defaultName + ".md",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ListingFile.Save(dialog.FileName, Editor.Text);

            // A later success of the same kind clears its own failure; a failure of the other kind stays.
            if (_pendingFailure?.Kind == SaveKind.Listing)
            {
                _pendingFailure = null;
            }

            RepaintOwnerState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _pendingFailure = (SaveKind.Listing, $"The listing could not be saved: {exception.Message}");
            RepaintOwnerState();
        }
    }

    /// <summary>
    /// Copies the current editor text (including edits) and shows the §3.5 feedback. The feedback is a
    /// borrowed line, not an owned state: the timer only takes back its own text and never paints over a
    /// run that has started since, nor over a pending write failure (D21).
    /// </summary>
    /// <param name="sender">The Copy button.</param>
    /// <param name="e">Click arguments.</param>
    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Editor.Text);
            SetStatus(StatusKind.Copied);
            _statusResetTimer.Stop();
            _statusResetTimer.Start();
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // A clipboard failure is transient, like an enumeration failure: it is not a write failure
            // that a later action of the same kind could clear.
            SetStatus(StatusKind.Error, exception.Message);
        }
    }

    /// <summary>
    /// Commits the typed depth when the box is left. The value is not applied per keystroke —
    /// regenerating a large tree on every digit would make typing "24" walk the disk twice (D17).
    /// </summary>
    /// <param name="sender">The depth box.</param>
    /// <param name="e">Focus arguments.</param>
    private void DepthBox_LostFocus(object sender, RoutedEventArgs e) => CommitDepth();

    /// <summary>Commits the typed depth on Enter, without waiting for focus to leave.</summary>
    /// <param name="sender">The depth box.</param>
    /// <param name="e">Key arguments.</param>
    private void DepthBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitDepth();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Applies the depth box's content: within <c>-1…99</c> it becomes the setting (persisted and
    /// re-rendered only when it actually changed), otherwise the box snaps back to the accepted value.
    /// Either way the displayed text is normalised, so `007` becomes `7`.
    /// </summary>
    // LEARN[18] (D17): the depth is committed on LostFocus or Enter, never per keystroke.
    // Alternatives considered: (a) the first M4 version, wired to TextChanged — rejected by review:
    //   every parseable keystroke wrote settings.json and re-ran the whole engine, so typing "24" cost
    //   two disk writes and two complete directory walks, a visible stall per digit on a large tree;
    //   (b) a short debounce timer — rejected as more moving parts than the requirement needs: the
    //   value only matters once the user has finished typing, which LostFocus/Enter already signal;
    //   (c) a WPF Slider — rejected: §3.3 asks for a numeric box with an explicit `-1 = unlimited`.
    // Pros of chosen approach: one persist and one regenerate per committed value; invalid or
    //   out-of-range input cannot reach the settings at all (the clamp *is* the range check); the
    //   display always ends up normalised, which doubles as the snap-back.
    // Cons of chosen approach: a value typed and then abandoned by closing the window without the box
    //   losing focus is not committed — the trade-off any commit-on-focus UI makes.
    // See also: LEARN[13]
    private void CommitDepth()
    {
        bool accepted =
            int.TryParse(DepthBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int depth)
            && depth is >= -1 and <= 99;

        if (accepted && depth != _settings.Depth)
        {
            _settings.Depth = depth;
            PersistSettings();
            RegenerateIfFolderSelected();
            RepaintIfFailurePending();
        }

        _loading = true;
        DepthBox.Text = _settings.Depth.ToString(CultureInfo.InvariantCulture);
        _loading = false;
    }

    /// <summary>Persists and re-renders when the hidden-files toggle changes.</summary>
    /// <param name="sender">The toggle.</param>
    /// <param name="e">Click arguments.</param>
    private void HiddenToggle_Changed(object sender, RoutedEventArgs e) =>
        ApplyToggle(() => _settings.IncludeHidden = HiddenToggle.IsChecked == true);

    /// <summary>Persists and re-renders when the follow-symlinks toggle changes.</summary>
    /// <param name="sender">The toggle.</param>
    /// <param name="e">Click arguments.</param>
    private void SymlinksToggle_Changed(object sender, RoutedEventArgs e) =>
        ApplyToggle(() => _settings.FollowSymlinks = SymlinksToggle.IsChecked == true);

    /// <summary>Persists and re-renders when the file-sizes toggle changes.</summary>
    /// <param name="sender">The toggle.</param>
    /// <param name="e">Click arguments.</param>
    private void FileSizesToggle_Changed(object sender, RoutedEventArgs e) =>
        ApplyToggle(() => _settings.ShowFileSizes = FileSizesToggle.IsChecked == true);

    /// <summary>Persists and re-renders when the attributes toggle changes.</summary>
    /// <param name="sender">The toggle.</param>
    /// <param name="e">Click arguments.</param>
    private void AttributesToggle_Changed(object sender, RoutedEventArgs e) =>
        ApplyToggle(() => _settings.ShowAttributes = AttributesToggle.IsChecked == true);

    /// <summary>
    /// Persists and re-renders when the folder-sizes toggle changes, and shows or hides the §3.3
    /// warning box with it. The load guard matters here as much as in <see cref="ApplyToggle"/>:
    /// without it, startup with folder sizes enabled would write the settings file back.
    /// </summary>
    /// <param name="sender">The toggle.</param>
    /// <param name="e">Click arguments.</param>
    private void FolderSizesToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.ShowFolderSizes = FolderSizesToggle.IsChecked == true;
        FolderSizeWarning.Visibility = _settings.ShowFolderSizes ? Visibility.Visible : Visibility.Collapsed;
        PersistSettings();
        RegenerateIfFolderSelected();
        RepaintIfFailurePending();
    }

    /// <summary>Persists and re-renders when the indent radio selection changes.</summary>
    /// <param name="sender">The selected radio button.</param>
    /// <param name="e">Click arguments.</param>
    private void IndentRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.IndentSize = Indent2Radio.IsChecked == true ? 2 : 4;
        PersistSettings();
        RegenerateIfFolderSelected();
        RepaintIfFailurePending();
    }

    /// <summary>
    /// Keeps the §3.4 preview honest while the user edits: the empty-state placeholder, the header
    /// counts and the enabled state of Save all follow the editor's content.
    /// </summary>
    /// <param name="sender">The editor.</param>
    /// <param name="e">Text-change arguments.</param>
    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        EditorPlaceholder.Visibility = Editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMeta();
        UpdateActionAvailability();
    }

    /// <summary>
    /// Relaunches the app elevated to write the long-path registry value (one UAC prompt) and then
    /// re-derives the button and note from the resulting registry state. The wait is asynchronous and
    /// the launch is offloaded, so the window keeps painting while the prompt is up, and the button is
    /// disabled throughout so a second prompt cannot be started (M4 review items 1, 3 and 5).
    /// </summary>
    /// <param name="sender">The long-paths button.</param>
    /// <param name="e">Click arguments.</param>
    private async void LongPathsButton_Click(object sender, RoutedEventArgs e)
    {
        LongPathsButton.IsEnabled = false;

        LongPathEnableOutcome outcome = LongPathEnableOutcome.NotAttempted;
        try
        {
            outcome = await LongPathsHelper.RelaunchElevatedAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            // The registry state decides what is shown; the outcome only explains a failure to the user.
            UpdateLongPathsState(outcome);
        }
    }

    /// <summary>Stores the selected folder in the settings, so it can be restored next time.</summary>
    /// <param name="folder">Absolute path of the chosen folder.</param>
    private void SelectFolder(string folder)
    {
        _selectedFolder = folder;
        PathBox.Text = folder;
        _settings.LastFolder = folder;
        UpdateActionAvailability();
    }

    /// <summary>
    /// Puts the restored <c>lastFolder</c> in the path box **without** generating: §4 says the folder
    /// is restored, not re-listed, so the editor stays as the user left it.
    /// </summary>
    /// <remarks>
    /// A folder that no longer exists is not restored at all (M4 review item 2, D16): the window keeps
    /// its empty state, nothing is written back, and — per the M4 approval note — the §3.5 status line
    /// says so instead of leaving the user wondering why the box is empty.
    /// </remarks>
    private void RestoreLastFolder()
    {
        if (string.IsNullOrWhiteSpace(_settings.LastFolder))
        {
            return;
        }

        if (!Directory.Exists(_settings.LastFolder))
        {
            _selectedFolder = null;
            PathBox.Text = "No folder selected";
            SetStatus(StatusKind.Notice, "The remembered folder no longer exists");
            return;
        }

        _selectedFolder = _settings.LastFolder;
        PathBox.Text = _settings.LastFolder;
    }

    /// <summary>Copies the loaded settings into every control, without triggering the change handlers.</summary>
    private void ApplySettingsToControls()
    {
        _loading = true;

        DepthBox.Text = _settings.Depth.ToString(CultureInfo.InvariantCulture);
        HiddenToggle.IsChecked = _settings.IncludeHidden;
        SymlinksToggle.IsChecked = _settings.FollowSymlinks;
        Indent2Radio.IsChecked = _settings.IndentSize == 2;
        Indent4Radio.IsChecked = _settings.IndentSize == 4;
        FileSizesToggle.IsChecked = _settings.ShowFileSizes;
        FolderSizesToggle.IsChecked = _settings.ShowFolderSizes;
        FolderSizeWarning.Visibility = _settings.ShowFolderSizes ? Visibility.Visible : Visibility.Collapsed;
        AttributesToggle.IsChecked = _settings.ShowAttributes;

        _loading = false;
    }

    /// <summary>
    /// Renders the long-paths button from the registry state, with the most recent attempt's outcome
    /// explaining any failure. The mapping itself is the pure, tested
    /// <see cref="LongPathStatus.Describe"/> (D18).
    /// </summary>
    /// <param name="outcome">How the most recent attempt ended; the startup state has none.</param>
    private void UpdateLongPathsState(LongPathEnableOutcome outcome = LongPathEnableOutcome.NotAttempted)
    {
        LongPathStatus status = LongPathStatus.Describe(outcome, LongPathsHelper.IsLongPathsEnabled());
        LongPathsButton.IsEnabled = status.ButtonEnabled;
        LongPathsNote.Text = status.Note;
    }

    /// <summary>Applies one toggle change: update the settings, persist, and re-render if possible.</summary>
    /// <param name="apply">Mutation to run against <see cref="_settings"/>.</param>
    private void ApplyToggle(Action apply)
    {
        if (_loading)
        {
            return;
        }

        apply();
        PersistSettings();
        RegenerateIfFolderSelected();
        RepaintIfFailurePending();
    }

    /// <summary>
    /// Writes the settings to <c>%APPDATA%\FolderTreeMD\settings.json</c>. A failure becomes the pending
    /// write failure (D21); a later success of the *same kind* clears it and repaints, so a resolved
    /// failure never lingers while a different kind of failure stays visible. The D1 placeholder dialog
    /// is gone, per the M4 approval note.
    /// </summary>
    private void PersistSettings()
    {
        try
        {
            SettingsStore.Save(_settings);

            if (_pendingFailure?.Kind == SaveKind.Settings)
            {
                _pendingFailure = null;
                RepaintOwnerState();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _pendingFailure = (SaveKind.Settings, $"Settings could not be saved: {exception.Message}");
        }
    }

    /// <summary>
    /// Paints whoever owns the status line: the pending write failure, or the idle state when nothing is
    /// outstanding. Every path that used to set <see cref="StatusKind.Ready"/> unconditionally calls
    /// this instead, so no writer can erase a failure that is still true, and every path that used to
    /// paint the failure on its own goes through here too, so the mapping lives in exactly one place
    /// (D21, M5 watch item 2).
    /// </summary>
    /// <remarks>
    /// A run in flight owns the line, so this yields to it (M5 watch item 1): a Save-as success or a
    /// settings-recovery repaint can no longer replace <c>Enumerating…</c> mid-run. The failure or the
    /// idle state is not lost by yielding — the run's own completion calls this again once it has
    /// retired itself (see <see cref="GenerateAsync"/>), so the owner state is painted one run later
    /// instead of being dropped.
    /// </remarks>
    // LEARN[29] (M6, the M5 watch items): the yield lives inside the one painter, and the run retires
    // itself before the completion repaint.
    // Alternatives considered: (a) let each caller ask "is a run in flight?" before painting — rejected:
    //   that reintroduces exactly the per-call-site obligation D21 removed, and the two sites OCR found
    //   in M5 (a Save-as success and PersistSettings' recovery) are the proof that such obligations get
    //   missed; (b) drop a mid-run repaint instead of deferring it — rejected: the pending failure would
    //   then be able to disappear, which is the failure mode D21 exists to prevent; (c) have the run
    //   paint the owner state from the `finally` *after* clearing `_enumeration` while leaving the try
    //   block's repaint in place — rejected: two repaint sites, one of which is silently suppressed,
    //   which is the kind of thing that reads as dead code later.
    // Pros of chosen approach: one condition in one method decides who may paint, and the deferral is
    //   self-correcting — the run's own completion always repaints, so a yielded paint is never lost.
    // Cons: after a settings change during a run the pending failure is visible one run later rather
    //   than immediately, and a repaint requested during a run is invisible until that run ends.
    // Revised in the M6 fix round (OCR run-6 findings 3 and 6), which tightened both ends of the rule:
    //   (a) the completing run skips this painter only while *nothing* owns the line — a pending write
    //   failure recorded during the run still outranks the transient enumeration message, so
    //   `GenerateAsync`'s `finally` guards with `!enumerationFailed || _pendingFailure is not null`;
    //   (b) the settings call sites that used to call `ShowPendingFailure` go through
    //   `RepaintIfFailurePending` instead, because with no run to yield to this painter would otherwise
    //   put the idle `Ready` state over the D16 startup notice or live copy feedback. The mapping stays
    //   in one method; what the sites choose is only whether this action has anything to say.
    // Revised again in the M6 fix round 2 (item 5 of the second `REQUEST CHANGES`): (b) is a named
    //   method rather than a guard copied into four handlers — the reviewer's point was that a copy
    //   names nothing, so a sixth settings handler could omit it silently, which is the obligation D21
    //   removed. The `BrowseButton_Click` site has no call at all: `GenerateAsync` paints progress before
    //   its first await, so a repaint there could never be seen (OCR run-2 finding 8).
    // See also: LEARN[23]
    private void RepaintOwnerState()
    {
        if (_enumeration is not null)
        {
            return; // the run owns the line; its completion repaints the owner state
        }

        if (_pendingFailure is { } failure)
        {
            SetStatus(StatusKind.Error, failure.Message);
        }
        else
        {
            SetStatus(StatusKind.Ready);
        }
    }

    /// <summary>
    /// Repaints only when a write failure is outstanding — the one thing a settings change has to say.
    /// With nothing pending the line belongs to whoever else put something there (the D16 startup notice,
    /// live copy feedback, a transient message), and <see cref="RepaintOwnerState"/> would replace it with
    /// the idle state (D21 revision, M6 fix round 2 item 5).
    /// </summary>
    /// <remarks>
    /// Named rather than copied into each handler: the four settings handlers
    /// (<see cref="CommitDepth"/>, <see cref="FolderSizesToggle_Changed"/>, <see cref="IndentRadio_Checked"/>
    /// and <see cref="ApplyToggle"/>) must all do the same thing, and a copy would name nothing for a
    /// sixth handler to notice.
    /// </remarks>
    private void RepaintIfFailurePending()
    {
        if (_pendingFailure is not null)
        {
            RepaintOwnerState();
        }
    }

    /// <summary>
    /// Ends the §3.5 copy feedback. The timer only takes back its own text: if the line has moved on
    /// (a run started, a failure appeared, another state was painted), it does nothing at all, and it
    /// never replaces a running enumeration's progress with Ready (D21).
    /// </summary>
    private void OnCopyFeedbackEnded()
    {
        _statusResetTimer.Stop();

        if (!_copyFeedbackShowing)
        {
            return; // the feedback text is no longer on the line; nothing to take back
        }

        _copyFeedbackShowing = false;

        if (_enumeration is not null)
        {
            return; // a run owns the line now; its own completion repaints the owner state
        }

        RepaintOwnerState();
    }

    /// <summary>
    /// Regenerates the listing for the selected folder, if there is one (§3.3). A change that arrives
    /// while a run is in flight cancels that run and queues a fresh one with the new options, so a
    /// listing built from stale options never reaches the editor (M5 review finding 3).
    /// </summary>
    private void RegenerateIfFolderSelected()
    {
        if (_selectedFolder is null)
        {
            return;
        }

        if (_enumeration is not null)
        {
            _restartAfterCancellation = true;
            _enumeration.Cancel();
            return;
        }

        _ = GenerateAsync();
    }

    /// <summary>
    /// Runs the engine off the UI thread with the current settings and puts the result in the editor.
    /// While it runs the Generate button is Cancel (§3.2) and the status line reports progress (§3.5);
    /// §3.2 says Generate replaces the editor content, so user edits are intentionally overwritten.
    /// </summary>
    /// <returns>A task that completes when the enumeration has finished or been cancelled.</returns>
    // LEARN[22] (M5): enumeration runs on a worker thread, driven by one CancellationTokenSource per
    // run, with progress marshalled back through Progress<int>.
    // Alternatives considered: (a) call MarkdownListing.Generate on the UI thread — rejected: it is a
    //   full directory walk, so the window would freeze for the whole run and §3.5's "Enumerating…"
    //   status could never paint; (b) await Task.Run and pass an IProgress implementation that touches
    //   the UI directly — rejected: Progress<int> captures the synchronization context for us, which is
    //   exactly the marshalling this needs; (c) a background worker with its own cancel flag — rejected:
    //   the engine already takes a CancellationToken (§6), so a CTS is the whole mechanism.
    // Pros: the UI stays responsive, Cancel works through the engine's own contract, and the status
    //   line gets a progress report per entry.
    // Cons: two code paths can now request a run (Generate and RegenerateIfFolderSelected), so both
    //   must respect the "one run at a time" rule — guarded by _enumeration.
    // See also: LEARN[23]
    private async Task GenerateAsync()
    {
        if (_selectedFolder is null || _enumeration is not null)
        {
            return;
        }

        string folder = _selectedFolder;
        using var cancellation = new CancellationTokenSource();
        _enumeration = cancellation;
        SetRunningState(true);
        ReportProgress(cancellation, 0);

        // The engine reports once per entry (LEARN[2]); this forwards at most one report per
        // ProgressThrottleMilliseconds to the status line (M5 review finding 5).
        var progress = new StatusProgress(Dispatcher, entries => ReportProgress(cancellation, entries));

        // Whether the run ended in the transient enumeration failure below, which owns the line
        // afterwards and must not be replaced by the completion repaint.
        bool enumerationFailed = false;

        try
        {
            ListingOptions options = SettingsMapping.ToOptions(_settings);
            string markdown = await Task.Run(
                () => MarkdownListing.Generate(folder, options, progress, cancellation.Token),
                cancellation.Token).ConfigureAwait(true);

            Editor.Text = markdown;
            UpdateMeta();
        }
        catch (OperationCanceledException)
        {
            // §3.5 defines no cancelled state; the line goes back to its owner in the finally below,
            // exactly like a completed run.
        }
        catch (Exception exception)
        {
            // An enumeration failure is transient: unlike a write failure it is not remembered, because
            // no later action of the same kind can clear it (D21). It is painted here and kept while
            // nothing else owns the line; a write failure that arrived during the run still wins below.
            // D5: the engine throws for an unreadable or missing root, which lands here.
            enumerationFailed = true;
            SetStatus(StatusKind.Error, exception.Message);
        }
        finally
        {
            // The run is retired *before* the owner is repainted: RepaintOwnerState yields while a run
            // is in flight (M5 watch item 1), so the run that is finishing has to stop being the
            // current run first. Retiring it is also what drops any throttled report still queued for
            // it, so a late report cannot overwrite the state painted just below.
            _enumeration = null;
            SetRunningState(false);

            // The transient enumeration message keeps the line only while nothing owns it; a pending
            // write failure is an owner state and outranks it (D21, M6 fix round item 4). Without the
            // second half of this condition a failure recorded during the run — a failed Save-as, whose
            // button stays enabled while running — would stay set but invisible.
            if (!enumerationFailed || _pendingFailure is not null)
            {
                RepaintOwnerState();
            }

            // A settings change that arrived mid-run cancelled this one; re-run with the new options.
            if (_restartAfterCancellation)
            {
                _restartAfterCancellation = false;
                _ = GenerateAsync();
            }
        }
    }

    /// <summary>
    /// Paints the running state's progress, but only for the run that is still current. Throttled
    /// progress is allowed to show even while a write failure is pending — the failure is restored when
    /// the run ends, not suppressed throughout (D21) — and a report that was still queued when the run
    /// finished is dropped, so it cannot overwrite the state the completion restored.
    /// </summary>
    /// <param name="run">The run the report belongs to.</param>
    /// <param name="entries">Number of entries enumerated so far.</param>
    private void ReportProgress(CancellationTokenSource run, int entries)
    {
        if (!ReferenceEquals(_enumeration, run))
        {
            return; // a finished or superseded run's late report must not repaint the status line
        }

        SetStatus(StatusKind.Enumerating, entries);
    }

    /// <summary>
    /// Switches between the idle and running forms of the primary button: while a run is in flight the
    /// label is <c>Cancel</c> and it stays enabled, otherwise <c>Generate</c>, enabled only when a
    /// folder is selected (§3.2).
    /// </summary>
    /// <param name="running">Whether an enumeration is in flight.</param>
    private void SetRunningState(bool running)
    {
        GenerateButton.Content = running ? "Cancel" : "Generate";
        GenerateButton.ToolTip = running
            ? "Stop the running enumeration"
            : "Enumerate the selected folder with the current settings";
        UpdateActionAvailability();
    }

    /// <summary>Enables Generate only when it can do something, and Save/Copy only when there is text (§3.5).</summary>
    /// <remarks>
    /// Browse is disabled while a run is in flight, for the same reason the primary button becomes
    /// Cancel: choosing another folder mid-run would otherwise be dropped on the floor and the running
    /// enumeration's listing would land in the editor for a folder the path box no longer shows
    /// (M5 review finding 2).
    /// </remarks>
    private void UpdateActionAvailability()
    {
        bool running = _enumeration is not null;
        GenerateButton.IsEnabled = running || _selectedFolder is not null;
        BrowseButton.IsEnabled = !running;

        bool hasText = Editor.Text.Length > 0;
        SaveButton.IsEnabled = hasText;
        CopyButton.IsEnabled = hasText;
    }

    /// <summary>Recomputes the §3.4 header counts from the editor's current content.</summary>
    private void UpdateMeta() => MetaText.Text = ListingStats.FromMarkdown(Editor.Text).ToHeaderText();

    /// <summary>
    /// Paints the §3.5 status line: the text, plus the dot colour and text colour of the state.
    /// </summary>
    /// <param name="kind">Which §3.5 state to show.</param>
    /// <param name="detail">Progress count for <see cref="StatusKind.Enumerating"/>, or an error message.</param>
    // LEARN[23] (M5): the §3.5 states are a small enum mapped here onto text and brushes, instead of
    // raw strings and colours scattered through the handlers.
    // Alternatives considered: (a) set StatusText.Text at each call site — rejected: the wording of four
    //   states would be duplicated in six places, and a missed dot colour is invisible until someone
    //   looks at the screen; (b) a WPF trigger-based status control — rejected as heavier than one enum
    //   and one switch for a single status line; (c) put the mapping in Core like D18's — rejected: it
    //   would drag Brush objects (or colour strings) and §3.5 wording into the engine library, and the
    //   two pure parts that *are* worth testing (the header counts and the long-path state) already
    //   live there.
    // Pros: one place defines every §3.5 state, so the wording, the dot and the text colour cannot
    //   disagree; the error colour and the accent colour appear exactly once.
    // Cons: the mapping itself is UI-only and therefore only machine-verifiable through the pieces that
    //   are pure — the wording here is assigned to the human checklist (L7).
    // See also: LEARN[22]
    private void SetStatus(StatusKind kind, object? detail = null)
    {
        StatusDot.Visibility = Visibility.Visible;

        switch (kind)
        {
            case StatusKind.Ready:
                StatusText.Text = "Ready — settings saved automatically";
                StatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
                StatusDot.Fill = (Brush)FindResource("SuccessDotBrush");
                break;

            case StatusKind.Enumerating:
                StatusText.Text = $"Enumerating… {detail ?? 0} entries";
                StatusText.Foreground = (Brush)FindResource("AccentBrush");
                StatusDot.Fill = (Brush)FindResource("AccentBrush");
                break;

            case StatusKind.Copied:
                StatusText.Text = "Copied to clipboard";
                StatusText.Foreground = (Brush)FindResource("AccentBrush");
                StatusDot.Fill = (Brush)FindResource("AccentBrush");
                break;

            case StatusKind.Error:
                StatusText.Text = detail as string ?? "Something went wrong";
                StatusText.Foreground = (Brush)FindResource("ErrorTextBrush");
                StatusDot.Fill = (Brush)FindResource("ErrorTextBrush");
                break;

            case StatusKind.Notice:
                StatusText.Text = detail as string ?? string.Empty;
                StatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
                StatusDot.Visibility = Visibility.Collapsed;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown status kind");
        }

        // The copy feedback is borrowed text, not an owner: this flag lets the timer know whether its
        // own words are still on the line before it takes them back (D21).
        _copyFeedbackShowing = kind == StatusKind.Copied;
    }

    /// <summary>Which write failed, so a later success of the same kind can clear that failure (D21).</summary>
    private enum SaveKind
    {
        /// <summary>The settings file (<c>%APPDATA%\FolderTreeMD\settings.json</c>).</summary>
        Settings,

        /// <summary>A Save-as export of the listing.</summary>
        Listing,
    }

    /// <summary>The §3.5 status states, plus the informational state the M4 review asked for (D16/D20).</summary>
    private enum StatusKind
    {
        /// <summary>Idle: <c>Ready — settings saved automatically</c> with the success dot.</summary>
        Ready,

        /// <summary>An enumeration is running: <c>Enumerating… {n} entries</c> in accent.</summary>
        Enumerating,

        /// <summary>Copied feedback, shown for <see cref="CopiedFeedbackDuration"/>.</summary>
        Copied,

        /// <summary>Anything that failed, in the §3.5 error colour.</summary>
        Error,

        /// <summary>
        /// Information that is not a failure — currently the skipped stale <c>lastFolder</c> (D16), and
        /// where a partial (<c>~</c>) folder sum would be explained if it ever needed explaining.
        /// </summary>
        Notice,
    }

    /// <summary>
    /// Forwards the engine's per-entry progress reports to the status line at most once every
    /// <see cref="ProgressThrottleMilliseconds"/>, marshalling each forwarded report onto the UI thread.
    /// </summary>
    /// <remarks>
    /// The engine's contract is unchanged: it still calls <see cref="Report"/> once per entry, from the
    /// worker thread. What is throttled is the dispatcher traffic and the status text, because a large
    /// tree produces tens of thousands of reports and every one of them used to become a UI message
    /// (M5 review finding 5). <see cref="Report"/> is only ever called from the enumeration's worker
    /// thread, so the timestamp field needs no synchronisation.
    /// </remarks>
    private sealed class StatusProgress : IProgress<int>
    {
        private static readonly long MinimumTimestampDelta =
            (long)(Stopwatch.Frequency * (ProgressThrottleMilliseconds / 1000.0));

        private readonly Dispatcher _dispatcher;
        private readonly Action<int> _update;
        private long _lastTimestamp = long.MinValue;

        /// <summary>Initializes the throttle.</summary>
        /// <param name="dispatcher">Dispatcher that owns the status line.</param>
        /// <param name="update">Callback to run on the UI thread with the latest count.</param>
        public StatusProgress(Dispatcher dispatcher, Action<int> update)
        {
            _dispatcher = dispatcher;
            _update = update;
        }

        /// <summary>Records a progress report, forwarding it when the throttle interval has elapsed.</summary>
        /// <param name="value">Number of entries reported by the engine.</param>
        public void Report(int value)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastTimestamp != long.MinValue && now - _lastTimestamp < MinimumTimestampDelta)
            {
                return;
            }

            _lastTimestamp = now;
            _dispatcher.BeginInvoke(() => _update(value));
        }
    }
}

namespace FolderTreeMD.Core;

/// <summary>
/// How an attempt to turn on the machine-wide long-path switch ended.
/// </summary>
public enum LongPathEnableOutcome
{
    /// <summary>No attempt has been made in this session (app startup).</summary>
    NotAttempted,

    /// <summary>The elevated helper reported that the registry value is set.</summary>
    Succeeded,

    /// <summary>The elevated helper ran but reported failure, or could not be started.</summary>
    Failed,

    /// <summary>The elevated helper did not finish in time and was terminated.</summary>
    TimedOut,

    /// <summary>The user dismissed the UAC prompt.</summary>
    Declined,
}

/// <summary>
/// The SYSTEM panel's long-path button state: whether the button is enabled and what its note says.
/// </summary>
/// <param name="ButtonEnabled">Whether the button should be clickable.</param>
/// <param name="Note">The note text shown under the button.</param>
// LEARN[16] (D18): this mapping lives in the Core library, as a pure function of two inputs, even
// though the strings it produces are UI copy.
// Alternatives considered: (a) keep it in the WPF project and test it there — impossible without
//   changing the test project: the suite targets net8.0 and references Core only (PLAN.md's project
//   layout), and a net8.0 project cannot reference a net8.0-windows one; (b) move the tests to
//   net8.0-windows and reference the app — rejected as a structural change to the plan's project
//   layout, far larger than the mapping itself; (c) link the same source file into the test project
//   with a Compile item — rejected: it compiles the file twice and hides which assembly owns it;
//   (d) add registry/process seams so the WPF helper is testable — explicitly out of scope for this
//   round, and recorded as the remainder under KNOWN_LIMITATIONS L7.
// Pros of chosen approach: the one part of the long-path feature with branching logic is a pure
//   function that the existing suite covers completely, with no new project, no seam and no
//   duplicated compilation.
// Cons of chosen approach: the engine library now carries three user-facing strings and an enum about
//   a Windows setting. That is a deliberate, documented layering concession: they are inert data in a
//   pure function, and if a second UI ever consumes them the mapping can move out without touching
//   behaviour.
// See also: LEARN[14]
public readonly record struct LongPathStatus(bool ButtonEnabled, string Note)
{
    /// <summary>Note shown when the machine already allows long paths.</summary>
    public const string EnabledNote = "Long paths are enabled";

    /// <summary>Note shown while long paths are off and no attempt has failed.</summary>
    public const string PromptNote = "Sets the LongPathsEnabled registry key. One-time administrator prompt.";

    /// <summary>Note shown when the user dismissed the UAC prompt.</summary>
    public const string DeclinedNote = "Long paths were not enabled - the administrator prompt was cancelled.";

    /// <summary>Note shown when the elevated helper did not finish in time.</summary>
    public const string TimedOutNote = "Long paths were not enabled - the elevated helper did not finish in time.";

    /// <summary>Note shown when the helper ran but the value is still not set.</summary>
    public const string FailedNote = "Long paths were not enabled - the registry key could not be written.";

    /// <summary>Win32 <c>ERROR_CANCELLED</c>: the user dismissed the UAC prompt.</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>
    /// Classifies a failure to start the elevated helper by its Win32 error code.
    /// </summary>
    /// <param name="nativeErrorCode">The <c>NativeErrorCode</c> of the <c>Win32Exception</c>.</param>
    /// <returns>
    /// <see cref="LongPathEnableOutcome.Declined"/> only for <see cref="ErrorCancelled"/>; every other
    /// start failure (access denied, file not found, elevation required, …) is
    /// <see cref="LongPathEnableOutcome.Failed"/>.
    /// </returns>
    // LEARN[19] (M4 round-3 item 2): the Win32 error classification is a pure function here, next to
    // the outcome vocabulary it produces, rather than an inline catch filter in the WPF helper.
    // Alternatives considered: (a) keep it inline in LongPathsHelper and document it with a LEARN note
    //   (the fallback the request allowed) — rejected: the mapping has real branches and would then be
    //   the only part of the long-path feature with no test, which is exactly the gap D18/LEARN[16]
    //   were created to close; (b) fold the check into the catch clause (`when (ex.NativeErrorCode ==
    //   1223)`) — rejected: two catches would encode the same decision in two places and still leave
    //   the non-cancelled case implicit; (c) treat every Win32Exception as Declined (the round-2 code)
    //   — rejected by review: it tells the user they cancelled a prompt they may never have seen.
    // Pros: one tested expression decides what the user is told, and the error codes that matter are
    //   named constants instead of magic numbers in a catch.
    // Cons: one more public member in Core for a Windows-specific detail (accepted under D18's
    //   documented layering concession).
    // See also: LEARN[16], LEARN[17]
    public static LongPathEnableOutcome ClassifyStartFailure(int nativeErrorCode) =>
        nativeErrorCode == ErrorCancelled ? LongPathEnableOutcome.Declined : LongPathEnableOutcome.Failed;

    /// <summary>
    /// Describes the button state from the attempt's outcome and the actual registry state. The
    /// registry decides: if the value is on, the button is disabled and says so, whatever the outcome.
    /// </summary>
    /// <param name="outcome">How the most recent attempt ended.</param>
    /// <param name="longPathsEnabled">Whether the machine-wide switch is currently on.</param>
    /// <returns>The button state to render.</returns>
    public static LongPathStatus Describe(LongPathEnableOutcome outcome, bool longPathsEnabled)
    {
        if (longPathsEnabled)
        {
            return new LongPathStatus(ButtonEnabled: false, EnabledNote);
        }

        return outcome switch
        {
            LongPathEnableOutcome.Declined => new LongPathStatus(ButtonEnabled: true, DeclinedNote),
            LongPathEnableOutcome.TimedOut => new LongPathStatus(ButtonEnabled: true, TimedOutNote),
            LongPathEnableOutcome.Failed => new LongPathStatus(ButtonEnabled: true, FailedNote),
            _ => new LongPathStatus(ButtonEnabled: true, PromptNote),
        };
    }
}

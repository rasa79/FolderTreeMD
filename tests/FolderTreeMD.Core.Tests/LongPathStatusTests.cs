using FolderTreeMD.Core;

namespace FolderTreeMD.Core.Tests;

/// <summary>
/// Tests for <see cref="LongPathStatus.Describe"/> — the pure mapping from an attempt's outcome plus
/// the registry state onto the SYSTEM panel's button state (D18). The registry and process parts of
/// the long-path feature remain uncovered by design; that remainder is KNOWN_LIMITATIONS L7.
/// </summary>
public class LongPathStatusTests
{
    /// <summary>The registry wins: with the value on, the button is disabled and says so for every outcome.</summary>
    /// <param name="outcome">Outcome to combine with an enabled registry value.</param>
    [Theory]
    [InlineData(LongPathEnableOutcome.NotAttempted)]
    [InlineData(LongPathEnableOutcome.Succeeded)]
    [InlineData(LongPathEnableOutcome.Failed)]
    [InlineData(LongPathEnableOutcome.TimedOut)]
    [InlineData(LongPathEnableOutcome.Declined)]
    public void Enabled_DisablesButtonAndSaysSo(LongPathEnableOutcome outcome)
    {
        LongPathStatus status = LongPathStatus.Describe(outcome, longPathsEnabled: true);

        Assert.False(status.ButtonEnabled);
        Assert.Equal(LongPathStatus.EnabledNote, status.Note);
    }

    /// <summary>With the value off and no attempt made, the button invites the one-time prompt.</summary>
    [Fact]
    public void DisabledWithNoAttempt_ShowsThePromptNote()
    {
        LongPathStatus status = LongPathStatus.Describe(LongPathEnableOutcome.NotAttempted, longPathsEnabled: false);

        Assert.True(status.ButtonEnabled);
        Assert.Equal(LongPathStatus.PromptNote, status.Note);
    }

    /// <summary>
    /// A declined prompt, a timeout and a helper failure each keep the button usable but say what
    /// happened, so the user is never left wondering why nothing changed.
    /// </summary>
    /// <param name="outcome">Failure outcome under test.</param>
    /// <param name="expectedNote">Note the mapping must produce.</param>
    [Theory]
    [InlineData(LongPathEnableOutcome.Declined, LongPathStatus.DeclinedNote)]
    [InlineData(LongPathEnableOutcome.TimedOut, LongPathStatus.TimedOutNote)]
    [InlineData(LongPathEnableOutcome.Failed, LongPathStatus.FailedNote)]
    public void FailedAttempts_ExplainThemselvesAndKeepTheButtonUsable(LongPathEnableOutcome outcome, string expectedNote)
    {
        LongPathStatus status = LongPathStatus.Describe(outcome, longPathsEnabled: false);

        Assert.True(status.ButtonEnabled);
        Assert.Equal(expectedNote, status.Note);
        Assert.NotEqual(LongPathStatus.PromptNote, status.Note);
        Assert.NotEqual(LongPathStatus.EnabledNote, status.Note);
    }

    /// <summary>
    /// `Succeeded` with the value still off is contradictory input; the registry state wins, so the
    /// UI falls back to the prompt note rather than claiming success.
    /// </summary>
    [Fact]
    public void SucceededButStillDisabled_DoesNotClaimSuccess()
    {
        LongPathStatus status = LongPathStatus.Describe(LongPathEnableOutcome.Succeeded, longPathsEnabled: false);

        Assert.True(status.ButtonEnabled);
        Assert.Equal(LongPathStatus.PromptNote, status.Note);
    }

    /// <summary>Every note is distinct, so a test that reads one cannot be satisfied by another.</summary>
    [Fact]
    public void Notes_AreDistinct()
    {
        string[] notes =
        [
            LongPathStatus.EnabledNote,
            LongPathStatus.PromptNote,
            LongPathStatus.DeclinedNote,
            LongPathStatus.TimedOutNote,
            LongPathStatus.FailedNote,
        ];

        Assert.Equal(notes.Length, notes.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The Win32 error code from a failed launch decides whether the user cancelled or the launch
    /// itself failed: only <c>ERROR_CANCELLED</c> (1223) is a cancellation (M4 round-3 item 2).
    /// </summary>
    /// <param name="nativeErrorCode">Error code to classify.</param>
    /// <param name="expected">Outcome the classifier must produce.</param>
    [Theory]
    [InlineData(1223, LongPathEnableOutcome.Declined)] // ERROR_CANCELLED — the user dismissed the prompt
    [InlineData(5, LongPathEnableOutcome.Failed)] // ERROR_ACCESS_DENIED
    [InlineData(2, LongPathEnableOutcome.Failed)] // ERROR_FILE_NOT_FOUND
    [InlineData(740, LongPathEnableOutcome.Failed)] // ERROR_ELEVATION_REQUIRED
    [InlineData(0, LongPathEnableOutcome.Failed)] // no error code at all
    public void ClassifyStartFailure_MapsByErrorCode(int nativeErrorCode, LongPathEnableOutcome expected)
    {
        Assert.Equal(expected, LongPathStatus.ClassifyStartFailure(nativeErrorCode));
    }

    /// <summary>
    /// The classified outcome reaches the right note: a cancelled prompt is explained as a
    /// cancellation, and any other start failure as a failure to write the key.
    /// </summary>
    /// <param name="nativeErrorCode">Error code of the failed launch.</param>
    /// <param name="expectedNote">Note the UI must show.</param>
    [Theory]
    [InlineData(1223, LongPathStatus.DeclinedNote)]
    [InlineData(5, LongPathStatus.FailedNote)]
    public void ClassifiedStartFailure_ReachesTheExpectedNote(int nativeErrorCode, string expectedNote)
    {
        LongPathEnableOutcome outcome = LongPathStatus.ClassifyStartFailure(nativeErrorCode);

        Assert.Equal(expectedNote, LongPathStatus.Describe(outcome, longPathsEnabled: false).Note);
    }
}

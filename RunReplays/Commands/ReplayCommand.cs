namespace RunReplays.Commands;

/// <summary>
/// A parsed replay command that knows its readiness requirements,
/// how to describe itself, and how to execute against the game API.
/// Subtypes own their parsing, timing, and execution logic.
/// </summary>
public abstract class ReplayCommand
{
    /// <summary>The original raw command string from the replay log.</summary>
    public string RawText { get; }

    protected ReplayCommand(string rawText)
    {
        RawText = rawText;
    }

    /// <summary>
    /// The <see cref="ReplayDispatcher.ReadyState"/> flag(s) required before
    /// this command can execute.  <see cref="ReplayDispatcher.ReadyState.None"/>
    /// means the command can execute in any context.
    /// </summary>
    public abstract ReplayDispatcher.ReadyState RequiredState { get; }

    /// <summary>
    /// True for commands consumed inline by ICardSelector implementations
    /// rather than by the dispatcher (e.g. SelectCardFromScreen, UpgradeCard).
    /// </summary>
    public virtual bool IsSelectionCommand => false;

    /// <summary>
    /// True for commands that should be blocked when combat is in progress
    /// but Combat readiness has not yet been signaled (e.g. potion use/discard
    /// during combat startup before TurnStarted fires).
    /// </summary>
    public virtual bool BlocksDuringCombatStartup => false;

    /// <summary>Human-readable description for logging and overlay display.</summary>
    public abstract string Describe();

    /// <summary>
    /// Executes this command against the game API.  Called by the dispatcher
    /// after readiness and timing checks pass.
    /// Returns true if the command executed successfully and is ready to be
    /// consumed from the queue.  Returns false if execution failed or the
    /// command should be retried.
    /// </summary>
    public virtual bool Execute()
    {
        PlayerActionBuffer.LogDispatcher(
            $"[ReplayCommand] Execute not implemented for {GetType().Name}: {RawText}");
        return false;
    }
}

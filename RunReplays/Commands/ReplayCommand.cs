namespace RunReplays.Commands;

/// <summary>
/// Result of executing a <see cref="ReplayCommand"/>.
/// </summary>
public readonly struct ExecuteResult
{
    /// <summary>Whether the command executed successfully and should be consumed.</summary>
    public bool Success { get; }

    /// <summary>
    /// Suggested retry delay in milliseconds.  0 means no retry.
    /// Only meaningful when <see cref="Success"/> is false.
    /// </summary>
    public int RetryDelayMs { get; }

    private ExecuteResult(bool success, int retryDelayMs)
    {
        Success = success;
        RetryDelayMs = retryDelayMs;
    }

    /// <summary>Command executed successfully — consume it from the queue.</summary>
    public static ExecuteResult Ok() => new(true, 0);

    /// <summary>Command failed — do not retry.</summary>
    public static ExecuteResult Fail() => new(false, 0);

    /// <summary>Command not ready — retry after the given delay.</summary>
    public static ExecuteResult Retry(int delayMs) => new(false, delayMs);
}

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
    /// The <see cref="ReplayState.ReadyState"/> flag(s) required before
    /// this command can execute.  <see cref="ReplayState.ReadyState.None"/>
    /// means the command can execute in any context.
    /// </summary>
    public abstract ReplayState.ReadyState RequiredState { get; }

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
    /// </summary>
    public virtual ExecuteResult Execute()
    {
        PlayerActionBuffer.LogDispatcher(
            $"[ReplayCommand] Execute not implemented for {GetType().Name}: {RawText}");
        return ExecuteResult.Fail();
    }
}

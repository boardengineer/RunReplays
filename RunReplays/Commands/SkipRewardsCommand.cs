using System.Collections;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace RunReplays.Commands;

/// <summary>
/// Skips all remaining reward buttons on the rewards screen by calling
/// RewardSkippedFrom for each un-claimed reward.
/// Recorded as: "SkipRewards"
/// </summary>
public sealed class SkipRewardsCommand : ReplayCommand
{
    private const string Cmd = "SkipRewards";

    private static readonly FieldInfo? RewardButtonsField =
        typeof(NRewardsScreen).GetField("_rewardButtons",
            BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo? RewardSkippedFromMethod =
        typeof(NRewardsScreen).GetMethod("RewardSkippedFrom",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

    public SkipRewardsCommand() : base("") { }

    public override string ToString() => Cmd;

    public override string Describe() => "skip rewards";

    public override ExecuteResult Execute()
    {
        var screen = ReplayState.ActiveRewardsScreen;
        if (screen == null || !screen.IsInsideTree())
            return ExecuteResult.Retry(200);

        var buttons = RewardButtonsField?.GetValue(screen) as IList;
        if (buttons == null || buttons.Count == 0)
            return ExecuteResult.Ok();

        // Snapshot before iterating — RewardSkippedFrom may mutate the list.
        var snapshot = new Node[buttons.Count];
        for (int i = 0; i < buttons.Count; i++)
            snapshot[i] = (Node)buttons[i]!;

        foreach (var button in snapshot)
            RewardSkippedFromMethod?.Invoke(screen, new object?[] { button });

        return ExecuteResult.Ok();
    }

    /// <summary>
    /// Returns true when there are reward buttons still available to skip.
    /// </summary>
    public static bool IsAvailable()
    {
        var screen = ReplayState.ActiveRewardsScreen;
        if (screen == null || !GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree())
            return false;

        var buttons = RewardButtonsField?.GetValue(screen) as IList;
        return buttons != null && buttons.Count > 0;
    }

    public static SkipRewardsCommand? TryParse(string raw)
        => raw == Cmd ? new SkipRewardsCommand() : null;
}

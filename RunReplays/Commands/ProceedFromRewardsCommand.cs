using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace RunReplays.Commands;

/// <summary>
/// Presses the proceed/skip button on the rewards screen.
/// Recorded as: "ProceedFromRewards"
/// </summary>
public sealed class ProceedFromRewardsCommand : ReplayCommand
{
    private const string Cmd = "ProceedFromRewards";

    private static readonly MethodInfo? OnProceedButtonPressedMethod =
        typeof(NRewardsScreen).GetMethod("OnProceedButtonPressed",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

    public ProceedFromRewardsCommand() : base("") { }

    public override string ToString() => Cmd;

    public override string Describe() => "proceed from rewards screen";

    public override ExecuteResult Execute()
    {
        var screen = ReplayState.ActiveRewardsScreen;
        if (screen == null || !screen.IsInsideTree())
            return ExecuteResult.Retry(200);

        var proceedBtn = Traverse.Create(screen).Field("_proceedButton").GetValue<Control>();
        if (proceedBtn == null)
            return ExecuteResult.Retry(200);

        var isEnabledProp = proceedBtn.GetType().GetProperty("IsEnabled");
        bool isEnabled = isEnabledProp != null
            && (bool)isEnabledProp.GetValue(proceedBtn)!;

        if (!isEnabled)
            return ExecuteResult.Retry(200);

        OnProceedButtonPressedMethod?.Invoke(screen, new object?[] { null });
        return ExecuteResult.Ok();
    }

    public static ProceedFromRewardsCommand? TryParse(string raw)
        => raw == Cmd ? new ProceedFromRewardsCommand() : null;
}

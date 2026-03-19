using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace RunReplays;

/// <summary>
/// Harmony postfix on EventSynchronizer.BeginEvent that, when a replay is active
/// and the next command is a ChooseStartingBonus entry, defers an automatic option
/// selection to the next Godot frame.
///
/// BeginEvent is patched (rather than a UI hook) because it fires on the
/// EventSynchronizer that owns ChooseLocalOption, giving us a direct reference
/// to the object we need to call.  A single CallDeferred is sufficient here
/// because BeginEvent is synchronous and the event options are populated before
/// it returns, so the deferred callback runs after the UI is ready.
/// </summary>
[HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.BeginEvent))]
public static class StartingBonusReplayPatch
{
    private static EventSynchronizer? _activeSynchronizer;

    [HarmonyPostfix]
    public static void Postfix(EventSynchronizer __instance, EventModel canonicalEvent)
    {
        if (canonicalEvent is not AncientEventModel)
            return;

        if (ReplayEngine.IsActive)
            ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.StartingBonus);

        if (!ReplayEngine.PeekStartingBonus(out int choiceIndex))
            return;

        _activeSynchronizer = __instance;
        ReplayDispatcher.DispatchNow();
    }

    /// <summary>Called by ReplayDispatcher to trigger starting bonus selection.</summary>
    internal static void DispatchFromEngine()
    {
        if (_activeSynchronizer == null)
            return;
        if (!ReplayEngine.PeekStartingBonus(out int choiceIndex))
            return;
        AutoSelect(_activeSynchronizer, choiceIndex);
    }

    private static void AutoSelect(EventSynchronizer synchronizer, int choiceIndex)
    {
        if (!ReplayRunner.ExecuteStartingBonus(out _))
            return;

        synchronizer.ChooseLocalOption(choiceIndex);

        // ChooseLocalOption fires eventOption.Chosen() as a fire-and-forget Task.
        // Defer one frame so that async chain has started before we proceed.
        Callable.From(Proceed).CallDeferred();
    }

    private static readonly MethodInfo? RecalculateTravelabilityMethod =
        typeof(NMapScreen).GetMethod(
            "RecalculateTravelability",
            BindingFlags.NonPublic | BindingFlags.Instance);

    private static void Proceed()
    {
        if (NMapScreen.Instance != null)
            RecalculateTravelabilityMethod?.Invoke(NMapScreen.Instance, null);

        TaskHelper.RunSafely(ProceedAsync());
    }

    private static async System.Threading.Tasks.Task ProceedAsync()
    {
        await RunManager.Instance.ProceedFromTerminalRewardsScreen();
        NMapScreen.Instance?.SetTravelEnabled(enabled: true);
    }
}

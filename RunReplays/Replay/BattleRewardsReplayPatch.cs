using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace RunReplays;

/// <summary>
/// Harmony postfix on NRewardsScreen._Ready that automatically claims reward
/// buttons in replay order, then proceeds to the map when the next command is
/// a MoveToMapCoordAction.
///
/// Gold rewards are fully handled here: the command is consumed from
/// ReplayEngine and NRewardButton.GetReward() is invoked so that the game's
/// own synchronisation logic (SyncLocalObtainedGold) runs normally.
///
/// Card rewards are triggered by clicking the card button (also via GetReward),
/// which opens NCardRewardSelectionScreen. CardRewardReplayPatch then handles
/// the actual card selection and calls OnCardRewardHandled() when done so that
/// any subsequent reward buttons or the map transition can be processed.
///
/// When no more reward commands remain and the next command is
/// MoveToMapCoordAction, ProceedFromTerminalRewardsScreen is called to close
/// the screen and SetTravelEnabled fires MapChoiceReplayPatch.
///
/// NRewardButton children are located by walking FindChildren and matching the
/// reward's runtime type name against "GoldReward" or "CardReward" (walking
/// the inheritance chain so subtypes are also matched). The type check uses
/// IsAssignableFrom rather than exact equality because Godot 4 generates
/// runtime subclasses (e.g. NRewardButton0MegaCrit) for C# node scripts.
/// </summary>
[HarmonyPatch(typeof(NRewardsScreen), "_Ready")]
public static class BattleRewardsReplayPatch
{
    // NRewardButton base type — used for IsAssignableFrom checks.
    private static readonly Type? NRewardButtonType =
        typeof(NRewardsScreen).Assembly
            .GetType("MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton");

    private static readonly MethodInfo? RecalculateTravelabilityMethod =
        typeof(NMapScreen).GetMethod(
            "RecalculateTravelability",
            BindingFlags.NonPublic | BindingFlags.Instance);

    // Kept so OnCardRewardHandled() can re-trigger processing after the card
    // selection screen closes.
    internal static NRewardsScreen? _activeScreen;

    [HarmonyPostfix]
    public static void Postfix(NRewardsScreen __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.Rewards);

        bool hasReward = ReplayEngine.PeekGoldReward(out _)
                      || ReplayEngine.PeekCardReward(out _, out _)
                      || ReplayEngine.PeekRelicReward(out _)
                      || ReplayEngine.PeekPotionReward(out _)
                      || ReplayEngine.PeekNetDiscardPotion(out _)
                      || ReplayEngine.PeekUsePotion(out _, out _, out _);
        bool hasMapNode         = ReplayEngine.PeekMapNode(out _, out _);
        bool hasProceedToNextAct = ReplayEngine.PeekProceedToNextAct();
        bool hasEventOption     = ReplayEngine.PeekEventOption(out _);

        if (!hasReward && !hasMapNode && !hasProceedToNextAct && !hasEventOption)
        {
            ReplayEngine.PeekNext(out string? next);
            PlayerActionBuffer.LogToDevConsole(
                $"[RunReplays] BattleRewardsReplayPatch: next command is not a reward, map node, or act proceed ('{next ?? "(none)"}'), skipping.");
            return;
        }

        _activeScreen = __instance;
        ReplayDispatcher.DispatchNow();
    }

    /// <summary>Called by ReplayDispatcher to process the next reward.</summary>
    internal static void DispatchFromEngine()
    {
        if (_activeScreen == null || !_activeScreen.IsInsideTree())
            return;
        Callable.From(() => ProcessNextReward(_activeScreen)).CallDeferred();
    }

    /// <summary>
    /// Called by CardRewardReplayPatch after a card has been auto-selected so
    /// that any reward buttons or map transition remaining can be processed.
    /// Also called by CardPlayReplayPatch after a potion discard that occurred
    /// during the rewards screen completes.
    /// </summary>
    public static void OnCardRewardHandled()
    {
        if (_activeScreen == null || !_activeScreen.IsInsideTree())
            return;

        NRewardsScreen screen = _activeScreen;
        Callable.From(() => ProcessNextReward(screen)).CallDeferred();
    }

    /// <summary>
    /// Called from CardPlayReplayPatch after a DiscardPotionGameAction completes
    /// with no subsequent combat action pending, so rewards processing can continue.
    /// </summary>
    public static bool TryResumeRewardsProcessing()
    {
        if (_activeScreen == null || !_activeScreen.IsInsideTree())
            return false;

        PlayerActionBuffer.LogToDevConsole("[RunReplays] BattleRewardsReplayPatch: resuming rewards processing after potion discard.");
        NRewardsScreen screen = _activeScreen;
        Callable.From(() => ProcessNextReward(screen)).CallDeferred();
        return true;
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    internal static void ProcessNextReward(NRewardsScreen screen)
    {
        if (!ReplayEngine.IsActive || !screen.IsInsideTree())
            return;

        ReplayEngine.PeekNext(out string? nextCmd);
        PlayerActionBuffer.LogDispatcher(
            $"[Rewards] ProcessNextReward: queue front = '{nextCmd ?? "(empty)"}'");

        // SacrificeCardReward is handled by SacrificeCardRewardCommand via the dispatcher.

        if (ReplayEngine.PeekNetDiscardPotion(out int discardSlot))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] Replay: potion discard slot={discardSlot} during rewards screen.");
            CardPlayReplayPatch.TryDiscardPotion();

            // AfterActionExecuted on the combat ActionExecutor may not fire
            // for actions enqueued outside combat.  Use a timer fallback to
            // resume rewards processing after the discard completes.
            NGame.Instance!.GetTree()!.CreateTimer(1.0).Connect(
                "timeout", Callable.From(() =>
                {
                    if (!TryResumeRewardsProcessing())
                        return;
                    PlayerActionBuffer.LogToDevConsole(
                        "[RunReplays] Replay: resumed rewards after potion discard (timer fallback).");
                }));
            return;
        }

        if (ReplayEngine.PeekUsePotion(out uint pIdx, out _, out bool pCombat))
        {
            PlayerActionBuffer.LogDispatcher($"[Rewards] Potion use during rewards screen: index={pIdx} combat={pCombat}");
            CardPlayReplayPatch.TryUsePotion();

            NGame.Instance!.GetTree()!.CreateTimer(1.0).Connect(
                "timeout", Callable.From(() =>
                {
                    if (!TryResumeRewardsProcessing())
                        return;
                    PlayerActionBuffer.LogDispatcher("[Rewards] Resumed rewards after potion use (timer fallback).");
                }));
            return;
        }

        if (ReplayEngine.PeekMapNode(out _, out _))
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] Replay: all rewards done, proceeding to map.");
            if (NMapScreen.Instance != null)
                RecalculateTravelabilityMethod?.Invoke(NMapScreen.Instance, null);
            TaskHelper.RunSafely(ProceedToMapAsync());
            return;
        }

        if (ReplayEngine.PeekProceedToNextAct())
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] Replay: all rewards done, proceeding to next act.");
            ReplayRunner.ExecuteProceedToNextAct();
            RunManager.Instance.ActChangeSynchronizer.SetLocalPlayerReady();
            return;
        }

        if (ReplayEngine.PeekEventOption(out _))
        {
            // Player skipped all rewards (e.g. Future of Potions event where
            // the card reward was declined).  Simulate pressing the proceed
            // button so the rewards screen closes and the event can continue.
            PlayerActionBuffer.LogToDevConsole("[RunReplays] Replay: no rewards taken, dismissing rewards screen.");
            var proceedMethod = screen.GetType().GetMethod(
                "OnProceedButtonPressed",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            proceedMethod?.Invoke(screen, new object?[] { null });
        }
    }

    private static async Task ProceedToMapAsync()
    {
        await RunManager.Instance.ProceedFromTerminalRewardsScreen();
        NMapScreen.Instance?.SetTravelEnabled(enabled: true);
    }

    internal static void InvokeGetReward(Node button)
    {
        MethodInfo? method = button.GetType()
            .GetMethod("GetReward", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (method == null)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[RunReplays] Replay: GetReward not found on {button.GetType().Name}.");
            return;
        }

        object? result = method.Invoke(button, null);
        if (result is Task task)
            TaskHelper.RunSafely(task);
    }

    /// <summary>
    /// Yields every (button, reward) pair on the rewards screen.
    /// </summary>
    internal static IEnumerable<(Node button, object reward)> EnumerateRewardButtons(Node root)
    {
        if (NRewardButtonType == null)
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] Replay: NRewardButton type not resolved.");
            yield break;
        }

        foreach (Node node in root.FindChildren("*", "", owned: false))
        {
            if (!NRewardButtonType.IsAssignableFrom(node.GetType()))
                continue;

            PropertyInfo? rewardProp = node.GetType()
                .GetProperty("Reward", BindingFlags.Public | BindingFlags.Instance);

            object? reward = rewardProp?.GetValue(node);
            if (reward != null)
                yield return (node, reward);
        }
    }

    internal static Node? FindRewardButton(Node root, string rewardBaseTypeName)
    {
        foreach (var (button, reward) in EnumerateRewardButtons(root))
        {
            if (IsRewardOfType(reward, rewardBaseTypeName))
                return button;
        }
        return null;
    }

    /// <summary>
    /// Tries to extract a card title from a reward that carries a single card
    /// (e.g. SpecialCardReward).  Returns null when the reward type doesn't
    /// expose a card or reflection fails — callers treat null as "accept any".
    /// </summary>
    internal static string? GetRewardCardTitle(object reward)
    {
        // Look for a private _card field (SpecialCardReward) or public Card property.
        var field = reward.GetType().GetField("_card", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(reward) is CardModel card)
            return card.Title;

        var prop = reward.GetType().GetProperty("Card", BindingFlags.Public | BindingFlags.Instance);
        if (prop?.GetValue(reward) is CardModel card2)
            return card2.Title;

        return null;
    }

    internal static bool IsRewardOfType(object? reward, string baseTypeName)
    {
        Type? t = reward?.GetType();
        while (t != null)
        {
            if (t.Name == baseTypeName)
                return true;
            t = t.BaseType;
        }
        return false;
    }
}

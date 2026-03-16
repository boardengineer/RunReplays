using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
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
    private static NRewardsScreen? _activeScreen;

    [HarmonyPostfix]
    public static void Postfix(NRewardsScreen __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        bool hasReward = ReplayEngine.PeekGoldReward(out _)
                      || ReplayEngine.PeekCardReward(out _, out _)
                      || ReplayEngine.PeekSacrificeCardReward()
                      || ReplayEngine.PeekRelicReward(out _)
                      || ReplayEngine.PeekPotionReward(out _)
                      || ReplayEngine.PeekNetDiscardPotion(out _);
        bool hasMapNode         = ReplayEngine.PeekMapNode(out _, out _);
        bool hasProceedToNextAct = ReplayEngine.PeekProceedToNextAct();

        if (!hasReward && !hasMapNode && !hasProceedToNextAct)
        {
            ReplayEngine.PeekNext(out string? next);
            PlayerActionBuffer.LogToDevConsole(
                $"[RunReplays] BattleRewardsReplayPatch: next command is not a reward, map node, or act proceed ('{next ?? "(none)"}'), skipping.");
            return;
        }

        _activeScreen = __instance;
        Callable.From(() => ProcessNextReward(__instance)).CallDeferred();
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

    private static void ProcessNextReward(NRewardsScreen screen)
    {
        if (!ReplayEngine.IsActive || !screen.IsInsideTree())
            return;

        if (ReplayEngine.PeekGoldReward(out int goldAmount))
        {
            Node? goldButton = FindRewardButton(screen, "GoldReward");
            if (goldButton == null)
            {
                PlayerActionBuffer.LogToDevConsole("[RunReplays] Replay: could not find gold reward button.");
                return;
            }

            ReplayRunner.ExecuteGoldReward(out _);
            InvokeGetReward(goldButton);
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] Replay: auto-claimed gold reward ({goldAmount}).");

            Callable.From(() => ProcessNextReward(screen)).CallDeferred();
            return;
        }

        if (ReplayEngine.PeekCardReward(out string cardTitle, out int rewardIndex)
            || ReplayEngine.PeekSacrificeCardReward())
        {
            // Scan every reward button for anything that yields a card.
            // Some rewards (e.g. SpecialCardReward from Thieving Hopper) add the
            // card directly without opening a selection screen, while the normal
            // CardReward opens NCardRewardSelectionScreen.  We handle the direct
            // ones here and only fall through to the screen path when needed.
            //
            // SacrificeCardReward also opens NCardRewardSelectionScreen — the
            // sacrifice alternative is selected by CardRewardReplayPatch once
            // the screen is ready, matching the real UI flow.
            //
            // When rewardIndex >= 0 (recorded with multiple card reward packs),
            // we select the Nth CardReward button rather than the first one.
            bool isSacrifice = ReplayEngine.PeekSacrificeCardReward();
            Node? screenButton = null;
            int cardRewardCount = 0;
            foreach (var (button, reward) in EnumerateRewardButtons(screen))
            {
                if (IsRewardOfType(reward, "CardReward"))
                {
                    // Regular card reward — opens a selection screen.
                    if (rewardIndex >= 0 && !isSacrifice)
                    {
                        // Indexed: pick the exact CardReward button.
                        if (cardRewardCount == rewardIndex)
                            screenButton = button;
                    }
                    else
                    {
                        // Legacy (no index) or sacrifice: use the first CardReward button.
                        screenButton ??= button;
                    }
                    cardRewardCount++;
                    continue;
                }

                // Skip direct-card-reward path for sacrifices — sacrifice always
                // goes through the card selection screen.
                if (isSacrifice)
                    continue;

                // Any other reward that goes through SyncLocalObtainedCard
                // (e.g. SpecialCardReward) adds the card immediately with no
                // selection screen.  We can only verify title for rewards that
                // expose the card, so accept any non-CardReward card-yielding
                // button when the title matches or can't be checked.
                string? rewardCardTitle = GetRewardCardTitle(reward);
                if (rewardCardTitle != null && rewardCardTitle != cardTitle)
                    continue;

                ReplayRunner.ExecuteCardReward(out _);
                InvokeGetReward(button);
                PlayerActionBuffer.LogToDevConsole(
                    $"[RunReplays] Replay: auto-claimed direct card reward '{cardTitle}' ({reward.GetType().Name}).");

                Callable.From(() => ProcessNextReward(screen)).CallDeferred();
                return;
            }

            if (screenButton != null)
            {
                // Do NOT consume the command here — CardRewardReplayPatch does
                // that once NCardRewardSelectionScreen opens and the card is
                // selected.
                InvokeGetReward(screenButton);
                PlayerActionBuffer.LogToDevConsole("[RunReplays] Replay: triggered card reward button.");
                return;
            }

            PlayerActionBuffer.LogToDevConsole(
                "[RunReplays] Replay: could not find any card reward button.");
            return;
        }

        if (ReplayEngine.PeekRelicReward(out string relicTitle))
        {
            Node? relicButton = FindRewardButton(screen, "RelicReward");
            if (relicButton == null)
            {
                PlayerActionBuffer.LogToDevConsole("[RunReplays] Replay: could not find relic reward button.");
                return;
            }

            ReplayRunner.ExecuteRelicReward(out _);
            InvokeGetReward(relicButton);
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] Replay: auto-claimed relic reward '{relicTitle}'.");

            Callable.From(() => ProcessNextReward(screen)).CallDeferred();
            return;
        }

        if (ReplayEngine.PeekPotionReward(out string potionTitle))
        {
            Node? potionButton = FindRewardButton(screen, "PotionReward");
            if (potionButton == null)
            {
                PlayerActionBuffer.LogToDevConsole("[RunReplays] Replay: could not find potion reward button.");
                return;
            }

            ReplayRunner.ExecutePotionReward(out _);
            InvokeGetReward(potionButton);
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] Replay: auto-claimed potion reward '{potionTitle}'.");

            Callable.From(() => ProcessNextReward(screen)).CallDeferred();
            return;
        }

        if (ReplayEngine.PeekNetDiscardPotion(out int discardSlot))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] Replay: potion discard slot={discardSlot} during rewards screen — handing off to CardPlayReplayPatch.");
            // TryDiscardPotion consumes the command, enqueues DiscardPotionGameAction,
            // and AfterActionExecuted → ScheduleNextCombatAction → TryResumeRewardsProcessing
            // will resume ProcessNextReward once the action completes.
            CardPlayReplayPatch.TryDiscardPotion();
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
        }
    }

    private static async Task ProceedToMapAsync()
    {
        await RunManager.Instance.ProceedFromTerminalRewardsScreen();
        NMapScreen.Instance?.SetTravelEnabled(enabled: true);
    }

    private static void InvokeGetReward(Node button)
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

    private static Node? FindRewardButton(Node root, string rewardBaseTypeName)
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
    private static string? GetRewardCardTitle(object reward)
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

using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace RunReplays;

/// <summary>
/// Harmony postfix on NRewardsScreen._Ready that automatically claims reward
/// buttons in replay order.
///
/// Gold rewards are fully handled here: the command is consumed from
/// ReplayEngine and NRewardButton.GetReward() is invoked so that the game's
/// own synchronisation logic (SyncLocalObtainedGold) runs normally.
///
/// Card rewards are triggered by clicking the card button (also via GetReward),
/// which opens NCardRewardSelectionScreen. CardRewardReplayPatch then handles
/// the actual card selection and calls OnCardRewardHandled() when done so that
/// any subsequent reward buttons can be processed.
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
    // Godot generates runtime subclasses, so exact type equality is wrong.
    private static readonly Type? NRewardButtonType =
        typeof(NRewardsScreen).Assembly
            .GetType("MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton");

    // Kept so OnCardRewardHandled() can re-trigger processing after the card
    // selection screen closes.
    private static NRewardsScreen? _activeScreen;

    [HarmonyPostfix]
    public static void Postfix(NRewardsScreen __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        PlayerActionBuffer.LogToDevConsole(
            $"[RunReplays] BattleRewardsReplayPatch: NRewardsScreen ready, NRewardButtonType={NRewardButtonType?.FullName ?? "null"}");

        if (!ReplayEngine.PeekGoldReward(out _) && !ReplayEngine.PeekCardReward(out _))
        {
            ReplayEngine.PeekNext(out string? next);
            PlayerActionBuffer.LogToDevConsole(
                $"[RunReplays] BattleRewardsReplayPatch: next command is not a reward ('{next ?? "(none)"}'), skipping.");
            return;
        }

        _activeScreen = __instance;
        Callable.From(() => ProcessNextReward(__instance)).CallDeferred();
    }

    /// <summary>
    /// Called by CardRewardReplayPatch after a card has been auto-selected so
    /// that any reward buttons remaining after the card pick can be processed.
    /// </summary>
    public static void OnCardRewardHandled()
    {
        if (_activeScreen == null || !_activeScreen.IsInsideTree())
            return;

        NRewardsScreen screen = _activeScreen;
        Callable.From(() => ProcessNextReward(screen)).CallDeferred();
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

            // After gold, check whether there is a card reward to trigger next.
            Callable.From(() => ProcessNextReward(screen)).CallDeferred();
            return;
        }

        if (ReplayEngine.PeekCardReward(out _))
        {
            Node? cardButton = FindRewardButton(screen, "CardReward");
            if (cardButton == null)
            {
                PlayerActionBuffer.LogToDevConsole("[RunReplays] Replay: could not find card reward button.");
                return;
            }

            // Do NOT consume the command here — CardRewardReplayPatch does that
            // once NCardRewardSelectionScreen opens and the card is selected.
            InvokeGetReward(cardButton);
            PlayerActionBuffer.LogToDevConsole("[RunReplays] Replay: triggered card reward button.");
        }
    }

    private static void InvokeGetReward(Node button)
    {
        // Look up GetReward on the actual runtime type so virtual dispatch works
        // correctly with Godot-generated subclasses.
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
    /// Finds the first NRewardButton (or subtype) whose Reward is of the given
    /// base type name, searching the full descendant tree of <paramref name="root"/>.
    /// Uses IsAssignableFrom for the node type check so Godot-generated runtime
    /// subclasses (e.g. NRewardButton0MegaCrit) are matched correctly.
    /// </summary>
    private static Node? FindRewardButton(Node root, string rewardBaseTypeName)
    {
        if (NRewardButtonType == null)
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] Replay: NRewardButton type not resolved.");
            return null;
        }

        var allChildren = root.FindChildren("*", "", owned: false);
        PlayerActionBuffer.LogToDevConsole(
            $"[RunReplays] FindRewardButton({rewardBaseTypeName}): searching {allChildren.Count} descendants.");

        foreach (Node node in allChildren)
        {
            if (!NRewardButtonType.IsAssignableFrom(node.GetType()))
                continue;

            // Look up Reward on the actual runtime type.
            PropertyInfo? rewardProp = node.GetType()
                .GetProperty("Reward", BindingFlags.Public | BindingFlags.Instance);

            object? reward = rewardProp?.GetValue(node);

            PlayerActionBuffer.LogToDevConsole(
                $"[RunReplays] Found NRewardButton subtype={node.GetType().Name} reward={reward?.GetType().Name ?? "null"}");

            if (IsRewardOfType(reward, rewardBaseTypeName))
                return node;
        }

        return null;
    }

    private static bool IsRewardOfType(object? reward, string baseTypeName)
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

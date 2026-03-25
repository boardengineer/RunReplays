using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using RunReplays.Commands;
using RunReplays.Patches;

namespace RunReplays;

/// <summary>
/// Harmony postfix on NMainMenu._Ready() that injects a "Run Replays" button
/// into the main menu just above the Quit button.
///
/// The game auto-discovers [HarmonyPatch] classes and calls PatchAll() when
/// no [ModInitializerAttribute] type is present in the assembly.
/// </summary>
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class MainMenuButtonInjector
{
#if RUNREPLAYS_AUTOPLAY
    /// <summary>
    /// When RUNREPLAYS_AUTOPLAY is defined, the mod will automatically launch
    /// the replay for this seed as soon as the main menu opens.
    /// Set to the seed string of the replay to auto-launch.
    /// If null or empty, auto-play is skipped.
    /// Optionally append ":floor_N" to target a specific floor (e.g. "ABC123:floor_5").
    /// </summary>
    private const string AutoPlayTarget = "8AGN4R7RPW:floor_17";

    private static bool _autoPlayFired;
#endif

    [HarmonyPostfix]
    public static void Postfix(NMainMenu __instance)
    {
        // Reset any replay in progress so the game returns to record mode.
        ReplayEngine.Clear();

        // Apply manual patches (isolated from PatchAll).
        // Deferred so the dev console is available for diagnostic logging.
        Callable.From(CrystalSphereManualPatcher.Apply).CallDeferred();
        Callable.From(ApplyCardRewardButtonPatch).CallDeferred();

        // The container holding all vertical menu buttons.
        // Uses the Godot unique-name accessor ("%MainMenuTextButtons").
        var buttonContainer = __instance.GetNode<Control>("%MainMenuTextButtons");

        // Duplicate the Quit button as a template (flags 6 = Groups | Scripts, no Signals).
        // Omitting Signals prevents copying the Quit→game-exit connection.
        // _Ready() will be called on the duplicate when it enters the tree,
        // so ConnectSignals() re-wires all internal hover/press animations fresh.
        var quitButton = __instance.GetNode<NMainMenuTextButton>("MainMenuTextButtons/QuitButton");
        int quitIndex = quitButton.GetIndex();

        var replayButton = (NMainMenuTextButton)quitButton.Duplicate(6);
        replayButton.Name = "RunReplaysButton";

        // AddChild triggers _Ready() on the duplicate, which initialises
        // the `label` child reference and connects internal button signals.
        buttonContainer.AddChild(replayButton);

        // Place directly above Quit.
        buttonContainer.MoveChild(replayButton, quitIndex);

        // Override label text now that _Ready() has run and label is valid.
        if (replayButton.label != null)
        {
            replayButton.label.Text = "Run Replays";
        }

        // Wire the Released signal to our handler.
        replayButton.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NButton>(_ => OnRunReplaysPressed(__instance))
        );

#if RUNREPLAYS_AUTOPLAY
        if (!_autoPlayFired && !string.IsNullOrEmpty(AutoPlayTarget))
        {
            _autoPlayFired = true;
            Callable.From(() => RunReplayMenu.AutoPlay(AutoPlayTarget)).CallDeferred();
        }
#endif
    }

    private static void OnRunReplaysPressed(NMainMenu mainMenu)
    {
        var menu = RunReplayMenu.Create(mainMenu);
        mainMenu.AddChild(menu);
    }

    // ── CardRewardButton manual patch ───────────────────────────────────────────

    private static bool _cardRewardButtonPatched;

    /// <summary>
    /// Manually patches NRewardButton.GetReward() to track which CardReward
    /// button (by 0-based index) was clicked during recording.
    ///
    /// Applied manually (not via [HarmonyPatch]) because NRewardButton is
    /// resolved at runtime — Godot 4 generates subclasses, and the concrete
    /// type name varies.
    /// </summary>
    private static void ApplyCardRewardButtonPatch()
    {
        if (_cardRewardButtonPatched) return;
        _cardRewardButtonPatched = true;

        try
        {
            var harmony = new Harmony("RunReplays.CardRewardButton");

            var nRewardButtonType = typeof(NRewardsScreen).Assembly
                .GetType("MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton");

            if (nRewardButtonType == null)
            {
                PlayerActionBuffer.LogToDevConsole(
                    "[CardRewardButtonPatcher] NRewardButton type not found — skipping patch.");
                return;
            }

            var getReward = AccessTools.Method(nRewardButtonType, "GetReward");
            if (getReward == null)
            {
                PlayerActionBuffer.LogToDevConsole(
                    "[CardRewardButtonPatcher] GetReward method not found — skipping patch.");
                return;
            }

            var prefix = new HarmonyMethod(
                typeof(MainMenuButtonInjector),
                nameof(GetRewardPrefix));

            harmony.Patch(getReward, prefix: prefix);
            PlayerActionBuffer.LogToDevConsole(
                "[CardRewardButtonPatcher] Patched NRewardButton.GetReward OK.");
        }
        catch (Exception ex)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[CardRewardButtonPatcher] Manual patching FAILED: {ex}");
        }
    }

    /// <summary>
    /// Harmony prefix for NRewardButton.GetReward().
    /// When a CardReward-type button is clicked (during recording, not
    /// replay), computes its 0-based index among all CardReward buttons
    /// on the parent NRewardsScreen and stores it in
    /// <see cref="BattleRewardPatch.LastCardRewardIndex"/>.
    /// </summary>
    public static void GetRewardPrefix(object __instance)
    {
        // During replay the index comes from the log, not from button clicks.
        if (ReplayEngine.IsActive) return;

        // Check whether the reward on this button is a regular CardReward.
        var rewardProp = __instance.GetType()
            .GetProperty("Reward", BindingFlags.Public | BindingFlags.Instance);
        var reward = rewardProp?.GetValue(__instance);
        if (reward == null || !CardRewardCommand.IsRewardOfType(reward, "CardReward"))
        {
            BattleRewardPatch.LastCardRewardIndex = -1;
            return;
        }

        BattleRewardPatch.IsProcessingCardReward = true;

        // Walk up the tree to find the NRewardsScreen ancestor.
        Node node = (Node)__instance;
        Node? current = node.GetParent();
        NRewardsScreen? screen = null;
        while (current != null)
        {
            if (current is NRewardsScreen s) { screen = s; break; }
            current = current.GetParent();
        }

        if (screen == null)
        {
            BattleRewardPatch.LastCardRewardIndex = -1;
            return;
        }

        // Find this button's index among all CardReward buttons.
        int index = 0;
        foreach (var (button, r) in CardRewardCommand.EnumerateRewardButtons(screen))
        {
            if (!CardRewardCommand.IsRewardOfType(r, "CardReward"))
                continue;
            if (ReferenceEquals(button, node))
            {
                BattleRewardPatch.LastCardRewardIndex = index;
                return;
            }
            index++;
        }
        BattleRewardPatch.LastCardRewardIndex = -1;
    }
}

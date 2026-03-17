using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

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
    [HarmonyPostfix]
    public static void Postfix(NMainMenu __instance)
    {
        // Reset any replay in progress so the game returns to record mode.
        ReplayEngine.Clear();

        // Apply manual patches (isolated from PatchAll).
        // Deferred so the dev console is available for diagnostic logging.
        Callable.From(CrystalSphereManualPatcher.Apply).CallDeferred();
        Callable.From(CardRewardButtonPatcher.Apply).CallDeferred();

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
    }

    private static void OnRunReplaysPressed(NMainMenu mainMenu)
    {
        var menu = RunReplayMenu.Create(mainMenu);
        mainMenu.AddChild(menu);
    }
}

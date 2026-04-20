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
using RunReplays.Utils;

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

    private static bool _startupFingerprintLogged;

    [HarmonyPostfix]
    public static void Postfix(NMainMenu __instance)
    {
        if (!_startupFingerprintLogged)
        {
            _startupFingerprintLogged = true;
            DiagnosticLog.WriteStartupFingerprint();
        }
        DiagnosticLog.Write("MainMenu", "NMainMenu._Ready postfix — resetting replay state");

        // Reset any replay in progress so the game returns to record mode.
        ReplayDispatcher.Clear();

        // Start polling for dispatchable command changes (diagnostic).
        ReplayDispatcher.StartDispatchPoll();

        // Apply manual patches (isolated from PatchAll).
        // Deferred so the dev console is available for diagnostic logging.
        Callable.From(CrystalSphereManualPatcher.Apply).CallDeferred();
        Callable.From(ApplyCardRewardButtonPatch).CallDeferred();

        // Ensure bundled sample replays exist in the user's samples directory.
        ExtractBundledReplay();

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

    // ── Bundled replay extraction ────────────────────────────────────────────

    // Each entry names a seed + floor directory under Resources/. Every file
    // in that directory must also appear as an <EmbeddedResource> in the
    // csproj, or extraction will log a "resource not found" warning.
    private static readonly (string seed, string floor, string[] files)[] BundledReplays =
    {
        ("T97Q92FYMZ", "floor_49", new[] { "actions.sts2replay", "run.save" }),
        ("4M42XQ1BSA", "floor_49", new[] { "actions.sts2replay", "run.save" }),
        ("YHREJXEUX2", "floor_49", new[] { "actions.sts2replay", "run.save" }),
        ("DTDBLEA3T9", "floor_49", new[] { "actions.sts2replay", "run.save" }),
    };

    private static void ExtractBundledReplay()
    {
        try
        {
            string logsRoot = Path.Combine(OS.GetUserDataDir(), "RunReplays", "samples");
            var assembly = Assembly.GetExecutingAssembly();

            foreach (var (seed, floor, files) in BundledReplays)
            {
                string targetDir = Path.Combine(logsRoot, seed, floor);

                // Skip if the directory already has the replay log.
                if (File.Exists(Path.Combine(targetDir, "actions.sts2replay")))
                    continue;

                Directory.CreateDirectory(targetDir);

                foreach (string fileName in files)
                {
                    // .NET prepends '_' to path segments starting with a digit in embedded resource names.
                    string resSeed = char.IsDigit(seed[0]) ? $"_{seed}" : seed;
                    string resourceName = $"RunReplays.Resources.{resSeed}.{floor}.{fileName}";
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null)
                    {
                        GD.PrintErr($"[RunReplays] Bundled resource not found: {resourceName}");
                        continue;
                    }

                    string targetPath = Path.Combine(targetDir, fileName);
                    using var fileStream = File.Create(targetPath);
                    stream.CopyTo(fileStream);
                }

                GD.Print($"[RunReplays] Extracted bundled replay: {seed}/{floor}");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RunReplays] Failed to extract bundled replay: {ex}");
        }
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
    /// Records a ClaimReward command with the button's index on the rewards screen.
    /// </summary>
    public static void GetRewardPrefix(object __instance)
    {
        if (ReplayEngine.IsActive) return;

        Node node = (Node)__instance;
        Node? current = node.GetParent();
        NRewardsScreen? screen = null;
        while (current != null)
        {
            if (current is NRewardsScreen s) { screen = s; break; }
            current = current.GetParent();
        }

        if (screen == null) return;

        // Find this button's index among all reward buttons.
        int index = 0;
        foreach (var (button, reward) in ClaimRewardCommand.EnumerateRewardButtons(screen))
        {
            if (ReferenceEquals(button, node))
            {
                var cmd = new ClaimRewardCommand(index)
                {
                    Comment = ClaimRewardCommand.DescribeReward(reward)
                };
                PlayerActionBuffer.Record(cmd.ToLogString());
                return;
            }
            index++;
        }
    }
}

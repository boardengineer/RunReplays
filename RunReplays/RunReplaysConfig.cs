using System.Reflection;
using BaseLib.Config;
using BaseLib.Config.UI;
using Godot;

namespace RunReplays;

public class RunReplaysConfig : SimpleModConfig
{
    public static bool ShowRunReplaysButton { get; set; } = true;
    public static bool ShowReplayOverlay { get; set; } = false;
    public static bool ExportLiveFight { get; set; } = true;
    /// <summary>Fight autopilot (Autopilot.cs): the hotkey starts/stops the sts-sim fight_autopilot.py helper for the current fight.</summary>
    public static bool EnableAutopilot { get; set; } = true;
    /// <summary>Godot key name of the autopilot hotkey (e.g. F8, F9, Pause, KP 0).</summary>
    public static string AutopilotHotkey { get; set; } = "F8";

    public override void SetupConfigUI(Control optionContainer)
    {
        base.SetupConfigUI(optionContainer);
        // Deferred so all rows have entered the tree and SettingControl is initialised.
        Callable.From(() => FixLabels(optionContainer)).CallDeferred();
    }

    private void FixLabels(Control optionContainer)
    {
        foreach (var child in optionContainer.GetChildren())
        {
            if (child is not NConfigOptionRow row) continue;

            string? label = GetRowPropertyName(row) switch
            {
                nameof(ShowReplayOverlay)    => "Show Replay Overlay",
                nameof(ShowRunReplaysButton) => "Show Main Menu Button (takes effect after restarting the game)",
                nameof(ExportLiveFight)      => "Export live fight snapshot (RunReplays/live_fight.json, for the sts-sim planner)",
                nameof(EnableAutopilot)      => "Fight autopilot: hotkey hands the current fight to the sts-sim MCTS (needs fight_autopilot.bat running)",
                nameof(AutopilotHotkey)      => "Fight autopilot hotkey (Godot key name, e.g. F8)",
                _ => null
            };

            if (label != null)
                ReplaceFirstLabel(row, label);
        }
    }

    private static string? GetRowPropertyName(NConfigOptionRow row)
    {
        var control = row.SettingControl;
        if (control == null || !GodotObject.IsInstanceValid(control)) return null;

        var type = control.GetType();
        while (type != null)
        {
            var f = type.GetField("_property",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (f != null)
                return (f.GetValue(control) as PropertyInfo)?.Name;
            type = type.BaseType;
        }
        return null;
    }

    private static void ReplaceFirstLabel(Node node, string text)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is RichTextLabel rtl) { rtl.Text = text; return; }
            if (child is Label lbl)         { lbl.Text = text; return; }
            ReplaceFirstLabel((Node)child, text);
        }
    }
}

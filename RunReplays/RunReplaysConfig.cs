using System.Reflection;
using BaseLib.Config;
using BaseLib.Config.UI;
using Godot;

namespace RunReplays;

public class RunReplaysConfig : SimpleModConfig
{
    public static bool ShowRunReplaysButton { get; set; } = true;
    public static bool ShowReplayOverlay { get; set; } = false;

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

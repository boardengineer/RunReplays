using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace RunReplays.Commands;

/// <summary>
/// Presses the confirm button on the card grid selection screen.
/// Handles both root-level confirm buttons (NSimpleCardSelectScreen)
/// and preview confirm buttons (NDeckUpgradeSelectScreen, etc.).
/// Recorded as: "ConfirmGridSelection"
/// </summary>
public sealed class ConfirmGridSelectionCommand : ReplayCommand
{
    private const string Cmd = "ConfirmGridSelection";

    public ConfirmGridSelectionCommand() : base("") { }

    public override string ToString() => Cmd;

    public override string Describe() => "confirm grid selection";

    public override ExecuteResult Execute()
    {
        var screen = CardGridScreenCapture.ActiveScreen;
        if (screen == null)
            return ExecuteResult.Retry(300);

        var confirmBtn = FindEnabledConfirmButton(screen);
        if (confirmBtn == null)
            return ExecuteResult.Retry(300);

        confirmBtn.EmitSignal(NClickableControl.SignalName.Released, confirmBtn);
        return ExecuteResult.Ok();
    }

    internal static NConfirmButton? FindEnabledConfirmButton(Node root)
    {
        foreach (Node node in root.FindChildren("*", "", owned: false))
            if (node is NConfirmButton btn && btn.IsEnabled)
                return btn;
        return null;
    }

    public static ConfirmGridSelectionCommand? TryParse(string raw)
        => raw == Cmd ? new ConfirmGridSelectionCommand() : null;
}

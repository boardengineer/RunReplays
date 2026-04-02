using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace RunReplays.Commands;

/// <summary>
/// Presses the close/cancel button on the card grid selection screen.
/// Tries preview cancel buttons first, then the root close button.
/// Recorded as: "CancelGridSelection"
/// </summary>
public sealed class CancelGridSelectionCommand : ReplayCommand
{
    private const string Cmd = "CancelGridSelection";

    public CancelGridSelectionCommand() : base("") { }

    public override string ToString() => Cmd;

    public override string Describe() => "cancel grid selection";

    public override ExecuteResult Execute()
    {
        var screen = CardGridScreenCapture.ActiveScreen;
        if (screen == null)
            return ExecuteResult.Retry(300);

        var cancelBtn = FindEnabledBackButton(screen);
        if (cancelBtn == null)
            return ExecuteResult.Retry(300);

        cancelBtn.EmitSignal(NClickableControl.SignalName.Released, cancelBtn);
        return ExecuteResult.Ok();
    }

    internal static NBackButton? FindEnabledBackButton(Node root)
    {
        foreach (Node node in root.FindChildren("*", "", owned: false))
            if (node is NBackButton btn && btn.IsEnabled)
                return btn;
        return null;
    }

    public static CancelGridSelectionCommand? TryParse(string raw)
        => raw == Cmd ? new CancelGridSelectionCommand() : null;
}

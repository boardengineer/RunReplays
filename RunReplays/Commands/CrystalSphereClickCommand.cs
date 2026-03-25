using System;
using Godot;
using HarmonyLib;

using RunReplays.Patches;
namespace RunReplays.Commands;

/// <summary>
/// Click a cell in the Crystal Sphere minigame.
/// Recorded as: "CrystalSphereClick {x} {y} {tool}"
///
/// The command sets PendingTool so the CellClicked prefix applies the correct
/// tool, then invokes OnCellClicked on the active screen with the matching cell.
/// </summary>
public sealed class CrystalSphereClickCommand : ReplayCommand
{
    private const string Prefix = "CrystalSphereClick ";

    public int X { get; }
    public int Y { get; }
    public int Tool { get; }


    public CrystalSphereClickCommand(int x, int y, int tool) : base("")
    {
        X = x;
        Y = y;
        Tool = tool;
    }

    public override string ToString()
        => $"{Prefix}{X} {Y} {Tool}";

    public override string Describe() => $"crystal sphere click ({X},{Y}) tool={Tool}";

    public override ExecuteResult Execute()
    {
        var screen = CrystalSphereReplayPatch.ActiveScreen;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
            return ExecuteResult.Retry(200);

        CrystalSphereReplayPatch.EnsureReflection();

        if (CrystalSphereReplayPatch.OnCellClicked == null)
        {
            PlayerActionBuffer.LogToDevConsole("[CrystalSphereClick] OnCellClicked method not found.");
            return ExecuteResult.Ok();
        }

        CrystalSphereReplayPatch.PendingTool = Tool;

        var cellContainer = Traverse.Create(screen).Field("_cellContainer").GetValue<Node>();
        GodotObject? target = CrystalSphereReplayPatch.FindCell(cellContainer, X, Y);

        if (target == null)
        {
            PlayerActionBuffer.LogToDevConsole($"[CrystalSphereClick] Cell ({X},{Y}) not found — skipping.");
            CrystalSphereReplayPatch.PendingTool = null;
            return ExecuteResult.Ok();
        }

        PlayerActionBuffer.LogToDevConsole($"[CrystalSphereClick] Clicking cell ({X},{Y}) tool={Tool}.");
        CrystalSphereReplayPatch.OnCellClicked.Invoke(screen, new object[] { target });

        return ExecuteResult.Ok();
    }

    public static CrystalSphereClickCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        ReadOnlySpan<char> rest = raw.AsSpan(Prefix.Length);
        int sp1 = rest.IndexOf(' ');
        if (sp1 <= 0) return null;

        int sp2 = rest[(sp1 + 1)..].IndexOf(' ');
        if (sp2 <= 0) return null;
        sp2 += sp1 + 1;

        if (int.TryParse(rest[..sp1], out int x)
            && int.TryParse(rest[(sp1 + 1)..sp2], out int y)
            && int.TryParse(rest[(sp2 + 1)..], out int tool))
        {
            return new CrystalSphereClickCommand(x, y, tool);
        }

        return null;
    }
}

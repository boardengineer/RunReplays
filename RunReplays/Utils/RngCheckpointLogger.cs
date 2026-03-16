using System;
using System.IO;
using System.Text;
using MegaCrit.Sts2.Core.Runs;

namespace RunReplays.Utils;

/// <summary>
/// Logs all RunRngSet counters at key checkpoints (event entry, combat start,
/// rest site, map move, rewards) to identify where RNG diverges between
/// original play and replay.
/// </summary>
internal static class RngCheckpointLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SlayTheSpire2", "RunReplays", "rng_checkpoints.log");

    internal static void Clear()
    {
        try { File.WriteAllText(LogPath, ""); }
        catch { /* ignore */ }
    }

    internal static void Log(string checkpoint)
    {
        return; // paused
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            if (state == null)
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {checkpoint} — RunState is null\n");
                return;
            }

            var rng = state.Rng;
            var sb = new StringBuilder();
            sb.Append($"[{DateTime.Now:HH:mm:ss.fff}] {checkpoint}");
            sb.Append($" | floor={state.TotalFloor}");
            sb.Append($" | UpFront={rng.UpFront.Counter}");
            sb.Append($" | Shuffle={rng.Shuffle.Counter}");
            sb.Append($" | UnknownMapPoint={rng.UnknownMapPoint.Counter}");
            sb.Append($" | CardGen={rng.CombatCardGeneration.Counter}");
            sb.Append($" | PotionGen={rng.CombatPotionGeneration.Counter}");
            sb.Append($" | CardSel={rng.CombatCardSelection.Counter}");
            sb.Append($" | EnergyCost={rng.CombatEnergyCosts.Counter}");
            sb.Append($" | Targets={rng.CombatTargets.Counter}");
            sb.Append($" | MonsterAi={rng.MonsterAi.Counter}");
            sb.Append($" | Niche={rng.Niche.Counter}");
            sb.Append($" | OrbGen={rng.CombatOrbGeneration.Counter}");
            sb.Append($" | TreasureRelics={rng.TreasureRoomRelics.Counter}");
            sb.AppendLine();

            File.AppendAllText(LogPath, sb.ToString());
        }
        catch { /* ignore */ }
    }
}

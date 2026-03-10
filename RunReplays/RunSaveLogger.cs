using System;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace RunReplays;

/// <summary>
/// Harmony postfix on RunManager.ToSave() that writes a snapshot log file
/// every time the game serialises a run for saving.
///
/// ToSave() is synchronous and returns the complete SerializableRun, so we
/// can access all run data without re-reading from disk.
/// Two files are written per save:
///   {UserDataDir}/RunReplays/logs/{seed}/floor_{floor}/{datetime}.verbose.log
///   {UserDataDir}/RunReplays/logs/{seed}/floor_{floor}/{datetime}.minimal.log
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.ToSave))]
public static class RunSaveLogger
{
    [HarmonyPostfix]
    public static void Postfix(SerializableRun __result)
    {
        try
        {
            WriteRunLog(__result);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RunReplays] Failed to write run save log: {ex}");
        }
    }

    private static void WriteRunLog(SerializableRun run)
    {
        string seed      = run.SerializableRng?.Seed ?? "unknown-seed";
        string character = run.Players?.FirstOrDefault()?.CharacterId?.Entry ?? "unknown-character";

        // SaveTime / StartTime are seconds since Unix epoch (ToUnixTimeSeconds).
        var saveTime = DateTimeOffset.FromUnixTimeSeconds(run.SaveTime).LocalDateTime;
        var startTime = DateTimeOffset.FromUnixTimeSeconds(run.StartTime).LocalDateTime;

        // TotalFloor mirrors RunState.TotalFloor: sum of visited rooms across all
        // player history columns (same formula the top-bar floor icon uses).
        int totalFloor = run.MapPointHistory?.Sum(column => column.Count) ?? 0;

        // Directory structure: logs/{seed}/floor_{floor}/
        // Use DateTime.Now for the filename so rapid saves never share a name.
        string seedDir  = SanitizeForFileName(seed);
        string floorDir = $"floor_{totalFloor + 1}";
        string logsDir  = Path.Combine(OS.GetUserDataDir(), "RunReplays", "logs", seedDir, floorDir);
        Directory.CreateDirectory(logsDir);

        string baseName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}";

        var verboseActions = PlayerActionBuffer.Snapshot();
        var minimalActions = PlayerActionBuffer.SnapshotMinimal();

        WriteVerbose(Path.Combine(logsDir, $"{baseName}.verbose.log"),
            seed, character, saveTime, totalFloor, verboseActions);

        WriteMinimal(Path.Combine(logsDir, $"{baseName}.minimal.log"),
            minimalActions);

        GD.Print($"[RunReplays] Wrote save logs to: {logsDir}");
    }

    private static void WriteVerbose(string filePath, string seed, string character,
        DateTime saveTime, int totalFloor, IReadOnlyList<string> actions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Run Replays – Action Log (Verbose) ===");
        sb.AppendLine($"Seed:        {seed}");
        sb.AppendLine($"Character:   {character}");
        sb.AppendLine($"Saved at:    {saveTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Floor:       {totalFloor + 1}");
        sb.AppendLine($"Actions:     {actions.Count}");
        sb.AppendLine();
        foreach (string entry in actions)
            sb.AppendLine(entry);
        File.WriteAllText(filePath, sb.ToString());
    }

    private static void WriteMinimal(string filePath, IReadOnlyList<string> actions)
    {
        var sb = new StringBuilder();
        foreach (string entry in actions)
            sb.AppendLine(entry);
        File.WriteAllText(filePath, sb.ToString());
    }

    private static string SanitizeForFileName(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value;
    }
}

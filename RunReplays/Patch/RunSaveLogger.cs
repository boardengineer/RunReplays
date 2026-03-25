using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace RunReplays.Patch;

/// <summary>
///     Harmony postfix on RunManager.ToSave() that writes a snapshot log file
///     every time the game serialises a run for saving.
///     ToSave() is synchronous and returns the complete SerializableRun, so we
///     can access all run data without re-reading from disk.
///     Two files are written per save:
///     {UserDataDir}/RunReplays/logs/{seed}/floor_{floor}/{datetime}.verbose.log
///     {UserDataDir}/RunReplays/logs/{seed}/floor_{floor}/{datetime}.minimal.log
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.ToSave))]
public static class RunSaveLogger
{
    [HarmonyPostfix]
    public static void Postfix(SerializableRun __result)
    {
        if (ReplayEngine.IsActive)
            return;

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
        var seed = run.SerializableRng?.Seed ?? "unknown-seed";
        var character = run.Players?.FirstOrDefault()?.CharacterId?.Entry ?? "unknown-character";

        // SaveTime / StartTime are seconds since Unix epoch (ToUnixTimeSeconds).
        var saveTime = DateTimeOffset.FromUnixTimeSeconds(run.SaveTime).LocalDateTime;
        var startTime = DateTimeOffset.FromUnixTimeSeconds(run.StartTime).LocalDateTime;

        // TotalFloor mirrors RunState.TotalFloor: sum of visited rooms across all
        // player history columns (same formula the top-bar floor icon uses).
        var totalFloor = run.MapPointHistory?.Sum(column => column.Count) ?? 0;

        // Directory structure: logs/{seed}/floor_{floor}/
        // One set of files per seed/floor — overwrites on each save.
        var seedDir = SanitizeForFileName(seed);
        var floorDir = $"floor_{totalFloor + 1}";
        var logsDir = Path.Combine(OS.GetUserDataDir(), "RunReplays", "logs", seedDir, floorDir);
        Directory.CreateDirectory(logsDir);

        var minimalActions = PlayerActionBuffer.SnapshotMinimal();

        WriteMinimal(Path.Combine(logsDir, "actions.sts2replay"),
            seed, character, run.Ascension, minimalActions);

        CopySaveBackup(logsDir);

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
        foreach (var entry in actions)
            sb.AppendLine(entry);
        File.WriteAllText(filePath, sb.ToString());
    }

    private static void WriteMinimal(string filePath, string seed, string character,
        int ascension, IReadOnlyList<string> actions)
    {
        string gameVersion;
        try
        {
            gameVersion = ReleaseInfoManager.Instance?.ReleaseInfo?.Version ?? "unknown";
        }
        catch
        {
            gameVersion = "unknown";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Character: {character}");
        sb.AppendLine($"# Seed: {seed}");
        sb.AppendLine($"# Ascension: {ascension}");
        sb.AppendLine($"# Game: {gameVersion}");
        sb.AppendLine($"# Mod: {ModVersion.Current}");
        foreach (var entry in actions)
            sb.AppendLine(entry);
        File.WriteAllText(filePath, sb.ToString());
    }

    private static void CopySaveBackup(string logsDir)
    {
        try
        {
            var profileId = SaveManager.Instance.CurrentProfileId;
            var godotPath = UserDataPathProvider.GetProfileScopedPath(
                profileId, "saves/" + RunSaveManager.runSaveFileName);
            var physicalPath = ProjectSettings.GlobalizePath(godotPath);

            if (!File.Exists(physicalPath))
            {
                GD.PrintErr($"[RunReplays] Save backup: source not found at '{physicalPath}'");
                return;
            }

            var dest = Path.Combine(logsDir, "run.save");
            File.Copy(physicalPath, dest, true);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RunReplays] Save backup failed: {ex}");
        }
    }

    private static string SanitizeForFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value;
    }
}
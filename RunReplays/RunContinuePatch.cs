using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace RunReplays;

/// <summary>
/// Harmony postfix on RunManager.SetUpSavedSinglePlayer() that restores the
/// action buffer from the most recent log files for the continued run.
///
/// Timing: this postfix fires after InitializeShared() has already constructed
/// a fresh ActionExecutor and cleared the buffer, so enqueuing here is safe.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpSavedSinglePlayer))]
public static class RunContinuePatch
{
    [HarmonyPostfix]
    public static void Postfix(SerializableRun save)
    {
        try
        {
            RestoreActionBuffer(save);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RunReplays] Failed to restore action buffer on continue: {ex}");
        }
    }

    private static void RestoreActionBuffer(SerializableRun save)
    {
        string seed = save.SerializableRng?.Seed ?? "unknown-seed";
        int totalFloor = save.MapPointHistory?.Sum(column => column.Count) ?? 0;

        string seedDir  = SanitizeForFileName(seed);
        string floorDir = $"floor_{totalFloor + 1}";
        string logsDir  = Path.Combine(OS.GetUserDataDir(), "RunReplays", "logs", seedDir, floorDir);

        if (!Directory.Exists(logsDir))
        {
            GD.Print($"[RunReplays] No log directory found for continued run at: {logsDir}");
            return;
        }

        // The basenames are yyyy-MM-dd_HH-mm-ss-fff, so lexicographic order == chronological order.
        string? latestVerbose = Directory.EnumerateFiles(logsDir, "*.verbose.log")
            .OrderByDescending(f => f)
            .FirstOrDefault();

        string? latestMinimal = Directory.EnumerateFiles(logsDir, "*.minimal.log")
            .OrderByDescending(f => f)
            .FirstOrDefault();

        if (latestVerbose == null || latestMinimal == null)
        {
            GD.Print($"[RunReplays] No log files found to restore in: {logsDir}");
            return;
        }

        var verboseEntries = ParseVerboseLog(latestVerbose);
        var minimalEntries = ParseMinimalLog(latestMinimal);

        PlayerActionBuffer.Restore(verboseEntries, minimalEntries);
        RunOverlay.RestoreRecentEntries(minimalEntries);
        GD.Print($"[RunReplays] Restored {verboseEntries.Count} verbose / {minimalEntries.Count} minimal entries from: {logsDir}");
    }

    /// <summary>
    /// Parses a verbose log file. Skips the 6-line header block (=== line + 4 metadata lines + blank),
    /// then parses "[HH:mm:ss.fff] {action}" lines.
    /// </summary>
    private static IReadOnlyList<(string Timestamp, string Action)> ParseVerboseLog(string filePath)
    {
        var entries = new List<(string, string)>();
        string[] lines = File.ReadAllLines(filePath);

        // Header is: === banner, Seed:, Character:, Saved at:, Floor:, Actions:, blank line — skip 7 lines.
        const int headerLines = 7;

        for (int i = headerLines; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0)
                continue;

            // Expected format: "[HH:mm:ss.fff] text"
            if (line.StartsWith('['))
            {
                int closeBracket = line.IndexOf(']');
                if (closeBracket > 0 && closeBracket + 2 <= line.Length)
                {
                    string timestamp = line.Substring(1, closeBracket - 1);
                    string action    = line.Substring(closeBracket + 2); // skip "] "
                    entries.Add((timestamp, action));
                    continue;
                }
            }

            // Fallback: store line without a timestamp (e.g. decorative separator).
            entries.Add((string.Empty, line));
        }

        return entries;
    }

    /// <summary>
    /// Parses a minimal log file. Each non-empty line is a plain action string.
    /// </summary>
    private static IReadOnlyList<string> ParseMinimalLog(string filePath)
    {
        return File.ReadAllLines(filePath)
            .Where(l => l.Length > 0)
            .ToList();
    }

    private static string SanitizeForFileName(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value;
    }
}

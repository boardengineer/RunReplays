using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using RunReplays.Utils;

namespace RunReplays;

/// <summary>
/// Scanning, loading, and replay-starting logic for the Run Replays mod.
/// The UI is built in <see cref="RunReplaySubmenu"/>; this class exposes the
/// helpers that the submenu calls.
///
/// Directory layout scanned:
///   {UserDataDir}/RunReplays/logs/{seed}/floor_{N}/{datetime}.verbose.log
/// </summary>
public static class RunReplayMenu
{
    private record ReplayEntry(
        string Seed,
        string CharacterId,
        int Floor,
        int Ascension,
        DateTime SavedAt,
        string MinimalLogPath,
        string? SavePath,
        bool IsSample = false);

    // ── Auto-play ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Automatically launches a replay matching the given target string.
    /// Format: "SEED" to replay the highest floor, or "SEED:floor_N" for a
    /// specific floor.
    /// </summary>
    internal static void AutoPlay(string target)
    {
        string seed;
        int? targetFloor = null;

        int colonIdx = target.IndexOf(':');
        if (colonIdx >= 0)
        {
            seed = target[..colonIdx];
            string floorPart = target[(colonIdx + 1)..];
            if (floorPart.StartsWith("floor_") &&
                int.TryParse(floorPart["floor_".Length..], out int f))
                targetFloor = f;
            else
                GD.PrintErr($"[RunReplays] AutoPlay: invalid floor specifier '{floorPart}', replaying highest floor.");
        }
        else
        {
            seed = target;
        }

        var entries = LoadEntries()
            .Where(e => string.Equals(e.Seed, seed, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Floor)
            .ToList();

        if (entries.Count == 0)
        {
            GD.PrintErr($"[RunReplays] AutoPlay: no replays found for seed '{seed}'.");
            return;
        }

        ReplayEntry entry;
        if (targetFloor.HasValue)
        {
            entry = entries.FirstOrDefault(e => e.Floor == targetFloor.Value)!;
            if (entry == null)
            {
                GD.PrintErr($"[RunReplays] AutoPlay: no replay found for seed '{seed}' floor {targetFloor.Value}.");
                return;
            }
        }
        else
        {
            entry = entries.First();
        }

        GD.Print($"[RunReplays] AutoPlay: launching seed={entry.Seed} floor={entry.Floor}");
        StartReplay(entry);
    }

    // ── List population ───────────────────────────────────────────────────────

    internal static void PopulateList(VBoxContainer list, Action closeMenu)
    {
        var entries = LoadEntries();

        if (entries.Count == 0)
        {
            var empty = new Label();
            empty.Text = "No replays found.";
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            list.AddChild(empty);
            return;
        }

        var sampleEntries = entries.Where(e => e.IsSample).ToList();
        var userEntries = entries.Where(e => !e.IsSample).ToList();

        if (userEntries.Count > 0)
        {
            var userHeader = new Label();
            userHeader.Text = "Recorded Runs";
            userHeader.AddThemeFontSizeOverride("font_size", 16);
            userHeader.HorizontalAlignment = HorizontalAlignment.Center;
            list.AddChild(userHeader);

            PopulateGroups(list, closeMenu, userEntries);
        }

        if (sampleEntries.Count > 0)
        {
            var sampleHeader = new Label();
            sampleHeader.Text = "Samples";
            sampleHeader.AddThemeFontSizeOverride("font_size", 16);
            sampleHeader.HorizontalAlignment = HorizontalAlignment.Center;
            list.AddChild(sampleHeader);

            PopulateGroups(list, closeMenu, sampleEntries);
        }
    }

    internal static void PopulateSeparate(
        VBoxContainer userContainer,
        VBoxContainer samplesContainer,
        Action closeMenu)
    {
        var entries = LoadEntries();
        var userEntries   = entries.Where(e => !e.IsSample).ToList();
        var sampleEntries = entries.Where(e =>  e.IsSample).ToList();

        if (userEntries.Count > 0)
            PopulateGroups(userContainer, closeMenu, userEntries);
        else
        {
            var lbl = new Label { Text = "No replays recorded yet." };
            lbl.HorizontalAlignment = HorizontalAlignment.Center;
            userContainer.AddChild(lbl);
        }

        if (sampleEntries.Count > 0)
            PopulateGroups(samplesContainer, closeMenu, sampleEntries);
        else
        {
            var lbl = new Label { Text = "No sample runs found." };
            lbl.HorizontalAlignment = HorizontalAlignment.Center;
            samplesContainer.AddChild(lbl);
        }
    }

    private static void PopulateGroups(VBoxContainer list, Action closeMenu, List<ReplayEntry> entries)
    {
        // Group by seed; within each group order floors highest-first.
        // Groups themselves are ordered by the most recent save in each group.
        var groups = entries
            .GroupBy(e => e.Seed)
            .Select(g => (Seed: g.Key, Entries: g.OrderByDescending(e => e.Floor).ToList()))
            .OrderByDescending(g => g.Entries.Max(e => e.SavedAt))
            .ToList();

        foreach (var (seed, seedEntries) in groups)
        {
            string character = seedEntries.First().CharacterId;
            DateTime latest  = seedEntries.Max(e => e.SavedAt);
            string latestStr = latest != DateTime.MinValue
                ? latest.ToString("yyyy-MM-dd  HH:mm:ss")
                : "unknown date";

            // ── Seed header (toggle button) ────────────────────────────────
            var headerBtn = new Button();
            headerBtn.Text = $"▶  Seed: {seed}    {character}    {seedEntries.Count} save(s)    {latestStr}";
            headerBtn.Alignment = HorizontalAlignment.Left;
            headerBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            list.AddChild(headerBtn);

            // Floor rows container — collapsed by default.
            var floorContainer = new VBoxContainer();
            floorContainer.AddThemeConstantOverride("separation", 2);
            floorContainer.Visible = false;
            list.AddChild(floorContainer);

            bool expanded = false;
            var capturedBtn       = headerBtn;
            var capturedContainer = floorContainer;
            headerBtn.Pressed += () =>
            {
                expanded = !expanded;
                capturedContainer.Visible = expanded;
                capturedBtn.Text = (expanded ? "▼" : "▶") + capturedBtn.Text[1..];
            };

            // ── Floor rows ─────────────────────────────────────────────────

            // Collect earlier floors with saves as potential starting points.
            var floorsWithSaves = seedEntries
                .Where(e => e.SavePath != null)
                .OrderBy(e => e.Floor)
                .ToList();

            foreach (ReplayEntry entry in seedEntries)
            {
                var row = new HBoxContainer();
                row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.AddThemeConstantOverride("separation", 4);
                floorContainer.AddChild(row);

                // Visual indent.
                var indent = new Label();
                indent.Text = "    ";
                row.AddChild(indent);

                var replayBtn = new Button();
                replayBtn.Text = $"Replay to floor {entry.Floor}";
                replayBtn.Alignment = HorizontalAlignment.Left;
                replayBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.AddChild(replayBtn);

                // "Start from" dropdown — lists earlier floors that have saves.
                var startOptions = floorsWithSaves
                    .Where(e => e.Floor < entry.Floor)
                    .OrderByDescending(e => e.Floor)
                    .ToList();

                OptionButton? dropdown = null;
                if (startOptions.Count > 0)
                {
                    dropdown = new OptionButton();
                    dropdown.AddItem("From start", 0);
                    for (int i = 0; i < startOptions.Count; i++)
                        dropdown.AddItem($"From Floor {startOptions[i].Floor}", i + 1);
                    dropdown.Selected = 0;
                    row.AddChild(dropdown);
                }

                var captured = entry;
                var capturedOptions = startOptions;
                var capturedDropdown = dropdown;
                replayBtn.Pressed += () =>
                {
                    int sel = capturedDropdown?.Selected ?? 0;
                    closeMenu();
                    if (sel == 0 || capturedOptions.Count == 0)
                        StartReplay(captured);
                    else
                        StartReplayFromFloor(captured, capturedOptions[sel - 1]);
                };

                if (entry.SavePath != null)
                {
                    var loadSaveBtn = new Button();
                    loadSaveBtn.Text = "Load Save";
                    row.AddChild(loadSaveBtn);
                    loadSaveBtn.Pressed += () =>
                    {
                        closeMenu();
                        LoadSave(captured);
                    };
                }

                // "Replay Floor" — loads this floor's save and replays only
                // the commands between this floor and the next floor's log.
                var nextFloor = seedEntries
                    .Where(e => e.Floor > entry.Floor)
                    .OrderBy(e => e.Floor)
                    .FirstOrDefault();
                if (entry.SavePath != null && nextFloor != null)
                {
                    var replayFloorBtn = new Button();
                    replayFloorBtn.Text = "Replay Floor";
                    row.AddChild(replayFloorBtn);
                    var capturedNext = nextFloor;
                    replayFloorBtn.Pressed += () =>
                    {
                        closeMenu();
                        StartReplayFromFloor(capturedNext, captured);
                    };
                }
            }
        }
    }

    private static string FormatFloorEntry(ReplayEntry entry)
    {
        string date = entry.SavedAt != DateTime.MinValue
            ? entry.SavedAt.ToString("yyyy-MM-dd  HH:mm:ss")
            : "unknown date";

        return $"Floor {entry.Floor}    {date}";
    }

    // ── File system scan ──────────────────────────────────────────────────────

    private static List<ReplayEntry> LoadEntries()
    {
        var entries = new List<ReplayEntry>();
        string baseDir = Path.Combine(OS.GetUserDataDir(), "RunReplays");

        ScanDirectory(Path.Combine(baseDir, "logs"), isSample: false, entries);
        ScanDirectory(Path.Combine(baseDir, "samples"), isSample: true, entries);

        // Newest saves at the top of the list.
        return [.. entries.OrderByDescending(e => e.SavedAt)];
    }

    private static void ScanDirectory(string root, bool isSample, List<ReplayEntry> entries)
    {
        if (!Directory.Exists(root))
            return;

        foreach (string seedDir in Directory.GetDirectories(root))
        {
            foreach (string floorDir in Directory.GetDirectories(seedDir))
            {
                string dirName = Path.GetFileName(floorDir);

                if (!dirName.StartsWith("floor_"))
                    continue;

                if (!int.TryParse(dirName.Substring("floor_".Length), out int floor))
                    continue;

                string? latestMinimal = Path.Combine(floorDir, "actions.sts2replay");
                if (!File.Exists(latestMinimal))
                    latestMinimal = Path.Combine(floorDir, "actions.minimal.log");
                if (!File.Exists(latestMinimal))
                    latestMinimal = Directory
                        .GetFiles(floorDir, "*.minimal.log")
                        .OrderByDescending(f => f)
                        .FirstOrDefault();

                if (latestMinimal == null)
                    continue;

                string? latestVerbose = Path.Combine(floorDir, "actions.verbose.log");
                if (!File.Exists(latestVerbose))
                    latestVerbose = Directory
                        .GetFiles(floorDir, "*.verbose.log")
                        .OrderByDescending(f => f)
                        .FirstOrDefault();

                var (seed, characterId, savedAt) = latestVerbose != null
                    ? ReadVerboseHeader(latestVerbose)
                    : ReadMinimalHeader(latestMinimal);

                string fixedSave = Path.Combine(floorDir, "run.save");
                string? legacySave = latestVerbose != null
                    ? latestVerbose[..^".verbose.log".Length] + ".save" : null;
                string? savePath = File.Exists(fixedSave) ? fixedSave
                    : (legacySave != null && File.Exists(legacySave)) ? legacySave : null;

                int ascension = ReadMinimalHeaderAscension(latestMinimal);

                entries.Add(new ReplayEntry(seed, characterId, floor, ascension, savedAt, latestMinimal, savePath, isSample));
            }
        }
    }

    /// <summary>
    /// Reads the first few header lines of a verbose log to extract the seed,
    /// character ID, and save timestamp without reading the whole file.
    /// Header format (7 lines total including trailing blank):
    ///   === Run Replays – Action Log (Verbose) ===
    ///   Seed:        {seed}
    ///   Character:   {characterId}
    ///   Saved at:    yyyy-MM-dd HH:mm:ss
    ///   Floor:       {n}
    ///   Actions:     {n}
    ///   (blank)
    /// </summary>
    private static (string Seed, string CharacterId, DateTime SavedAt) ReadVerboseHeader(string filePath)
    {
        string seed        = "unknown-seed";
        string characterId = "unknown-character";
        DateTime savedAt   = DateTime.MinValue;

        try
        {
            using var reader = new StreamReader(filePath);
            reader.ReadLine(); // === banner ===

            string? seedLine = reader.ReadLine();
            if (seedLine != null)
                seed = seedLine.Substring("Seed:        ".Length).Trim();

            string? charLine = reader.ReadLine();
            if (charLine != null)
                characterId = charLine.Substring("Character:   ".Length).Trim();

            string? dateLine = reader.ReadLine();
            if (dateLine != null)
                DateTime.TryParseExact(
                    dateLine.Substring("Saved at:    ".Length).Trim(),
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out savedAt);
        }
        catch
        {
            // Malformed log — return defaults and let the entry be listed with unknowns.
        }

        return (seed, characterId, savedAt);
    }

    /// <summary>
    /// Reads the "# Ascension: N" line from a minimal log header.
    /// Returns 0 if the header is missing or malformed.
    /// </summary>
    private static int ReadMinimalHeaderAscension(string filePath)
    {
        const string prefix = "# Ascension: ";
        try
        {
            foreach (string line in File.ReadLines(filePath))
            {
                if (!line.StartsWith('#'))
                    break; // Past the header.
                if (line.StartsWith(prefix) && int.TryParse(line[prefix.Length..].Trim(), out int asc))
                    return asc;
            }
        }
        catch { /* malformed log */ }

        return 0;
    }

    /// <summary>
    /// Reads seed and character from a minimal log header (# comments).
    /// SavedAt is derived from the file's last-write time.
    /// </summary>
    private static (string Seed, string CharacterId, DateTime SavedAt) ReadMinimalHeader(string filePath)
    {
        string seed        = "unknown-seed";
        string characterId = "unknown-character";

        try
        {
            foreach (string line in File.ReadLines(filePath))
            {
                if (!line.StartsWith('#'))
                    break;
                if (line.StartsWith("# Seed: "))
                    seed = line["# Seed: ".Length..].Trim();
                else if (line.StartsWith("# Character: "))
                    characterId = line["# Character: ".Length..].Trim();
            }
        }
        catch { /* malformed log */ }

        DateTime savedAt = File.GetLastWriteTime(filePath);
        return (seed, characterId, savedAt);
    }

    // ── Save load ─────────────────────────────────────────────────────────────

    private static void LoadSave(ReplayEntry entry)
    {
        ReplayEngine.ActiveSeed = entry.Seed;
        TaskHelper.RunSafely(LoadSaveAsync(entry));
    }

    private static async Task LoadSaveAsync(ReplayEntry entry)
    {
        // Copy the backup save file over the game's live save slot.
        try
        {
            int profileId = SaveManager.Instance.CurrentProfileId;
            string godotPath = UserDataPathProvider.GetProfileScopedPath(
                profileId, "saves/" + RunSaveManager.runSaveFileName);
            string destPath = ProjectSettings.GlobalizePath(godotPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(entry.SavePath!, destPath, overwrite: true);
            GD.Print($"[RunReplays] Copied backup save to: {destPath}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RunReplays] Failed to copy backup save: {ex}");
            return;
        }

        // Load and continue the run, mirroring OnContinueButtonPressedAsync.
        ReadSaveResult<SerializableRun> result = SaveManager.Instance.LoadRunSave();
        if (!result.Success || result.SaveData == null)
        {
            GD.PrintErr("[RunReplays] Failed to load copied save.");
            return;
        }

        if (NGame.Instance == null)
        {
            GD.PrintErr("[RunReplays] NGame.Instance is null — cannot continue run.");
            return;
        }

        SerializableRun serializableRun = result.SaveData;
        DiagnosticLog.WriteAndEcho("Load",
            $"LoadRunSave — seed='{serializableRun.SerializableRng?.Seed}' " +
            $"gameMode={serializableRun.GameMode} ascension={serializableRun.Ascension} " +
            $"character={serializableRun.Players?.FirstOrDefault()?.CharacterId?.Entry}");
        RunState runState = RunState.FromSerializable(serializableRun);
        RunManager.Instance.SetUpSavedSinglePlayer(runState, serializableRun);

        NAudioManager.Instance?.StopMusic();
        SfxCmd.Play(runState.Players[0].Character.CharacterTransitionSfx);
        await NGame.Instance.Transition.FadeOut(0.8f, runState.Players[0].Character.CharacterSelectTransitionPath);
        NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
        await NGame.Instance.LoadRun(runState, serializableRun.PreFinishedRoom);
        await NGame.Instance.Transition.FadeIn();
    }

    // ── Run start ─────────────────────────────────────────────────────────────

    private static void StartReplay(ReplayEntry entry)
    {
        // Load the minimal log into the replay engine so auto-execution kicks in
        // as the new run reaches each decision point.
        string[] lines   = File.ReadAllLines(entry.MinimalLogPath);
        var      commands = lines.Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#')).ToList();
        ReplayDispatcher.Load(commands);
        ReplayEngine.ActiveSeed = entry.Seed;

        // Resolve the character model by matching the stored entry string against
        // all registered characters. Fall back to the first available character
        // for old logs that predate the Character header line.
        CharacterModel? character =
            ModelDb.AllCharacters.FirstOrDefault(c => c.Id.Entry == entry.CharacterId)
            ?? ModelDb.AllCharacters.FirstOrDefault();

        if (character == null || NGame.Instance == null)
        {
            GD.PrintErr("[RunReplays] Could not resolve character or NGame instance — cannot start run.");
            return;
        }

        GD.Print($"[RunReplays] Starting replay: seed={entry.Seed} character={character.Id} floor={entry.Floor} ascension={entry.Ascension} ({commands.Count} commands)");
        DiagnosticLog.WriteAndEcho("Replay",
            $"StartReplay — seed={entry.Seed} character={character.Id} floor={entry.Floor} " +
            $"ascension={entry.Ascension} commands={commands.Count} log={entry.MinimalLogPath}");

        NAudioManager.Instance?.StopMusic();
        SfxCmd.Play(character.CharacterTransitionSfx);

        TaskHelper.RunSafely(
            NGame.Instance.StartNewSingleplayerRun(
                character,
                shouldSave: true,
                ActModel.GetDefaultList(),
                [],
                entry.Seed,
                GameMode.Standard,
                entry.Ascension));
    }

    /// <summary>
    /// Starts a replay from an intermediate floor by loading the starting
    /// floor's save and replaying only the commands that follow it.
    /// </summary>
    private static void StartReplayFromFloor(ReplayEntry target, ReplayEntry startFrom)
    {
        if (startFrom.SavePath == null)
        {
            GD.PrintErr("[RunReplays] Starting floor has no save — falling back to full replay.");
            StartReplay(target);
            return;
        }

        // The starting floor's log contains all commands up to that point.
        // The target's log contains all commands up to the target floor.
        // The difference is the commands we need to replay.
        string[] startLines  = File.ReadAllLines(startFrom.MinimalLogPath);
        int      skipCount   = startLines.Count(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'));

        string[] targetLines = File.ReadAllLines(target.MinimalLogPath);
        var      allCommands = targetLines.Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#')).ToList();

        if (skipCount >= allCommands.Count)
        {
            GD.PrintErr($"[RunReplays] Starting floor has {skipCount} commands but target only has {allCommands.Count} — loading save directly.");
            LoadSave(startFrom);
            return;
        }

        var remainingCommands = allCommands.Skip(skipCount).ToList();
        ReplayDispatcher.Load(remainingCommands);
        ReplayEngine.ActiveSeed = target.Seed;

        GD.Print($"[RunReplays] Starting replay from floor {startFrom.Floor}: " +
                 $"skipping {skipCount} commands, replaying {remainingCommands.Count} remaining");

        TaskHelper.RunSafely(LoadSaveAndReplayAsync(startFrom));
    }

    private static async Task LoadSaveAndReplayAsync(ReplayEntry startFrom)
    {
        try
        {
            int profileId = SaveManager.Instance.CurrentProfileId;
            string godotPath = UserDataPathProvider.GetProfileScopedPath(
                profileId, "saves/" + RunSaveManager.runSaveFileName);
            string destPath = ProjectSettings.GlobalizePath(godotPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(startFrom.SavePath!, destPath, overwrite: true);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RunReplays] Failed to copy save for mid-floor replay: {ex}");
            return;
        }

        ReadSaveResult<SerializableRun> result = SaveManager.Instance.LoadRunSave();
        if (!result.Success || result.SaveData == null)
        {
            GD.PrintErr("[RunReplays] Failed to load save for mid-floor replay.");
            return;
        }

        if (NGame.Instance == null)
        {
            GD.PrintErr("[RunReplays] NGame.Instance is null — cannot continue run.");
            return;
        }

        SerializableRun serializableRun = result.SaveData;
        DiagnosticLog.WriteAndEcho("Load",
            $"LoadRunSave — seed='{serializableRun.SerializableRng?.Seed}' " +
            $"gameMode={serializableRun.GameMode} ascension={serializableRun.Ascension} " +
            $"character={serializableRun.Players?.FirstOrDefault()?.CharacterId?.Entry}");
        RunState runState = RunState.FromSerializable(serializableRun);
        RunManager.Instance.SetUpSavedSinglePlayer(runState, serializableRun);

        NAudioManager.Instance?.StopMusic();
        SfxCmd.Play(runState.Players[0].Character.CharacterTransitionSfx);
        await NGame.Instance.Transition.FadeOut(0.8f, runState.Players[0].Character.CharacterSelectTransitionPath);
        NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
        await NGame.Instance.LoadRun(runState, serializableRun.PreFinishedRoom);
        await NGame.Instance.Transition.FadeIn();
    }
}

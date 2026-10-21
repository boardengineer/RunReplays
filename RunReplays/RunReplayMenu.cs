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

    // ── Load a floor's save (live bridge, bot mode) ───────────────────────────────

    /// <summary>
    /// Continues a recorded run from the start of one floor (its saved run.save, as "load save directly" in the
    /// replay menu) without replaying any command, so an external bot can play that floor. Returns an error message or
    /// null when the load was started.
    /// </summary>
    internal static string? LoadFloorSave(string seed, int floor)
    {
        var entry = LoadSeedEntries(seed).FirstOrDefault(e => e.Floor == floor && !e.IsSample);
        if (entry == null) return $"no recorded floor_{floor} for seed {seed}";
        if (entry.SavePath == null) return $"floor_{floor} of {seed} has no run.save";
        ReplayEngine.ActiveSeed = entry.Seed;
        TaskHelper.RunSafely(LoadSaveAsync(entry));
        ReplayEngine.IsReplayRun = false;   // played by the bot, not replayed
        return null;
    }

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

        var entries = LoadSeedEntries(seed)
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

    /// <summary>
    /// Fills both tabs as the runs are found, newest first. A background task walks the run folders in order of their
    /// last change and reads only each run's highest floor (the header line: character, save count, date); the UI gets
    /// them in small batches, so the first runs show at once however many are recorded. A run's floors are scanned
    /// when its header is first expanded.
    /// </summary>
    internal static void PopulateSeparateDeferred(
        VBoxContainer userContainer,
        VBoxContainer samplesContainer,
        Action closeMenu)
    {
        string baseDir = Path.Combine(OS.GetUserDataDir(), "RunReplays");
        StreamSeeds(userContainer, Path.Combine(baseDir, "logs"), false, closeMenu, "No replays recorded yet.");
        StreamSeeds(samplesContainer, Path.Combine(baseDir, "samples"), true, closeMenu, "No sample runs found.");
    }

    private static void StreamSeeds(VBoxContainer list, string root, bool isSample, Action closeMenu, string emptyText)
    {
        var loading = new Label { Text = "Loading…", HorizontalAlignment = HorizontalAlignment.Center };
        list.AddChild(loading);

        var pending = new List<SeedSummary>();
        var closed = new System.Threading.CancellationTokenSource();   // the menu was freed: stop scanning
        int shown = 0;

        // Runs on the main thread: adds the batch above the loading label.
        void Flush(bool done)
        {
            if (!GodotObject.IsInstanceValid(list)) { closed.Cancel(); return; }
            List<SeedSummary> batch;
            lock (pending) { batch = [.. pending]; pending.Clear(); }
            foreach (var s in batch)
            {
                AddSeedGroup(list, closeMenu, s);
                shown++;
            }
            list.MoveChild(loading, list.GetChildCount() - 1);
            if (!done) return;
            loading.QueueFree();
            if (shown == 0)
                list.AddChild(new Label { Text = emptyText, HorizontalAlignment = HorizontalAlignment.Center });
        }

        Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastFlush = 0;
            int found = 0;
            foreach (var dir in SeedDirsNewestFirst(root))
            {
                if (closed.IsCancellationRequested) return;
                SeedSummary? s = null;
                try { s = SummarizeSeed(dir, isSample); }
                catch (Exception e) { DiagnosticLog.Write("Menu", $"scan {dir.FullName} failed: {e.Message}"); }
                if (s == null) continue;
                lock (pending) pending.Add(s);
                // the first run at once, then every 100 ms
                if (++found == 1 || sw.ElapsedMilliseconds - lastFlush >= 100)
                {
                    lastFlush = sw.ElapsedMilliseconds;
                    Callable.From(() => Flush(false)).CallDeferred();
                }
            }
            DiagnosticLog.Write("Menu", $"scanned {root} in {sw.ElapsedMilliseconds} ms");
        }).ContinueWith(task =>
        {
            if (task.IsFaulted) DiagnosticLog.Write("Menu", $"scan {root} failed: {task.Exception}");
            Callable.From(() => Flush(true)).CallDeferred();
        });
    }

    /// <summary>A recorded run as its list header shows it, from its highest floor only.</summary>
    private record SeedSummary(string Seed, string CharacterId, int Saves, DateTime Latest, string SeedDir, bool IsSample);

    /// <summary>The run folders under <paramref name="root"/>, the most recently changed first (a new floor folder
    /// touches its run folder). The times come with the directory listing, so this opens no file.</summary>
    private static List<DirectoryInfo> SeedDirsNewestFirst(string root)
    {
        if (!Directory.Exists(root)) return [];
        return [.. new DirectoryInfo(root).EnumerateDirectories().OrderByDescending(d => d.LastWriteTimeUtc)];
    }

    private static SeedSummary? SummarizeSeed(DirectoryInfo seedDir, bool isSample)
    {
        var floors = new List<(int Floor, string Dir)>();
        foreach (var fd in seedDir.EnumerateDirectories("floor_*"))
            if (int.TryParse(fd.Name["floor_".Length..], out int f))
                floors.Add((f, fd.FullName));
        // the highest floor with a log (normally the very highest)
        foreach (var (floor, dir) in floors.OrderByDescending(x => x.Floor))
        {
            var top = ScanFloor(dir, floor, isSample);
            if (top == null) continue;
            int saves = floors.Count(x => HasLog(x.Dir));
            return new SeedSummary(top.Seed, top.CharacterId, saves, top.SavedAt, seedDir.FullName, isSample);
        }
        return null;
    }

    private static bool HasLog(string floorDir) =>
        File.Exists(Path.Combine(floorDir, "actions.sts2replay")) || File.Exists(Path.Combine(floorDir, "actions.minimal.log"))
        || Directory.EnumerateFiles(floorDir, "*.minimal.log").Any();

    private static void AddSeedGroup(VBoxContainer list, Action closeMenu, SeedSummary summary)
    {
        string latestStr = summary.Latest != DateTime.MinValue
            ? summary.Latest.ToString("yyyy-MM-dd  HH:mm:ss")
            : "unknown date";

        // ── Seed header (toggle button) ────────────────────────────────
        var headerBtn = new Button();
        headerBtn.Text = $"▶  Seed: {summary.Seed}    {summary.CharacterId}    {summary.Saves} save(s)    {latestStr}";
        headerBtn.Alignment = HorizontalAlignment.Left;
        headerBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        list.AddChild(headerBtn);

        // Floor rows container — collapsed by default.
        var floorContainer = new VBoxContainer();
        floorContainer.AddThemeConstantOverride("separation", 2);
        floorContainer.Visible = false;
        list.AddChild(floorContainer);

        bool expanded  = false;
        bool populated = false;
        headerBtn.Pressed += () =>
        {
            expanded = !expanded;
            if (expanded && !populated)
            {
                populated = true;
                BuildFloorRows();
            }
            floorContainer.Visible = expanded;
            headerBtn.Text = (expanded ? "▼" : "▶") + headerBtn.Text[1..];
        };

        void BuildFloorRows()
        {
            // this run's floors, highest first (scanned now: a run has a few dozen)
            var seedEntries = ScanSeed(summary.SeedDir, summary.IsSample)
                .OrderByDescending(e => e.Floor)
                .ToList();

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

    /// <summary>Every recorded floor of one seed (logs/ and samples/). Run folders are named by seed; any other
    /// folder is checked by the seed in its header, so this stays right for folders named otherwise.</summary>
    private static List<ReplayEntry> LoadSeedEntries(string seed)
    {
        var entries = new List<ReplayEntry>();
        string baseDir = Path.Combine(OS.GetUserDataDir(), "RunReplays");
        foreach (var (root, isSample) in new[] { (Path.Combine(baseDir, "logs"), false), (Path.Combine(baseDir, "samples"), true) })
        {
            if (!Directory.Exists(root)) continue;
            string named = Path.Combine(root, seed);
            if (Directory.Exists(named))
                entries.AddRange(ScanSeed(named, isSample));
            else
                foreach (string dir in Directory.GetDirectories(root))
                    entries.AddRange(ScanSeed(dir, isSample).Where(e => string.Equals(e.Seed, seed, StringComparison.OrdinalIgnoreCase)));
        }
        return entries;
    }

    private static List<ReplayEntry> ScanSeed(string seedDir, bool isSample)
    {
        var entries = new List<ReplayEntry>();
        if (!Directory.Exists(seedDir))
            return entries;

        foreach (string floorDir in Directory.GetDirectories(seedDir))
        {
            string dirName = Path.GetFileName(floorDir);

            if (!dirName.StartsWith("floor_"))
                continue;

            if (!int.TryParse(dirName.Substring("floor_".Length), out int floor))
                continue;

            if (ScanFloor(floorDir, floor, isSample) is ReplayEntry e)
                entries.Add(e);
        }
        return entries;
    }

    private static ReplayEntry? ScanFloor(string floorDir, int floor, bool isSample)
    {
        string? latestMinimal = Path.Combine(floorDir, "actions.sts2replay");
        if (!File.Exists(latestMinimal))
            latestMinimal = Path.Combine(floorDir, "actions.minimal.log");
        if (!File.Exists(latestMinimal))
            latestMinimal = Directory
                .GetFiles(floorDir, "*.minimal.log")
                .OrderByDescending(f => f)
                .FirstOrDefault();

        if (latestMinimal == null)
            return null;

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

        return new ReplayEntry(seed, characterId, floor, ascension, savedAt, latestMinimal, savePath, isSample);
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
    /// Reads the "# Acts: A, B, C" line from a minimal log header.
    /// Returns null for logs that predate the header.
    /// </summary>
    private static string[]? ReadMinimalHeaderActs(string filePath)
    {
        const string prefix = "# Acts: ";
        try
        {
            foreach (string line in File.ReadLines(filePath))
            {
                if (!line.StartsWith('#'))
                    break; // Past the header.
                if (line.StartsWith(prefix))
                {
                    var acts = line[prefix.Length..]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    return acts.Length > 0 ? acts : null;
                }
            }
        }
        catch { /* malformed log */ }

        return null;
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

    /// <summary>Loads a floor save written to samples/ (a stream fight, <see cref="StreamFight"/>), as the menu's Load Save does.</summary>
    internal static void LoadSampleSave(string seed, string characterId, int floor, int ascension, string actionsPath, string savePath)
        => LoadSave(new ReplayEntry(seed, characterId, floor, ascension, DateTime.Now, actionsPath, savePath, true));

    private static void LoadSave(ReplayEntry entry)
    {
        ReplayEngine.ActiveSeed = entry.Seed;
        ReplayEngine.IsReplayRun = true;
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
        RunManager.Instance.SetUpSavedSingleplayer(runState, serializableRun);

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
        ReplayEngine.ActiveActs = ReadMinimalHeaderActs(entry.MinimalLogPath);
        ReplayEngine.IsReplayRun = true;

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
        ReplayEngine.IsReplayRun = true;

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
        RunManager.Instance.SetUpSavedSingleplayer(runState, serializableRun);

        NAudioManager.Instance?.StopMusic();
        SfxCmd.Play(runState.Players[0].Character.CharacterTransitionSfx);
        await NGame.Instance.Transition.FadeOut(0.8f, runState.Players[0].Character.CharacterSelectTransitionPath);
        NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
        await NGame.Instance.LoadRun(runState, serializableRun.PreFinishedRoom);
        await NGame.Instance.Transition.FadeIn();
    }
}

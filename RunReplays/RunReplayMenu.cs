using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace RunReplays;

/// <summary>
/// Builds and manages the Run Replays browse screen that opens when the player
/// presses the "Run Replays" button from the main menu.
///
/// The menu is a plain Godot Control tree created entirely from code — no .tscn
/// required. It is added as a direct child of NMainMenu so it covers the menu
/// layout, and inherits the main menu's active theme so buttons and labels
/// automatically adopt the game's visual style.
///
/// Directory layout scanned:
///   {UserDataDir}/RunReplays/logs/{seed}/floor_{N}/{datetime}.verbose.log
///
/// The verbose log header supplies the seed, character ID, and save date.
/// Each unique (seed, floor) pair is represented by its most recent log pair.
/// Entries are sorted newest-first. Selecting an entry loads the matching
/// minimal log into ReplayEngine and starts a new run with the stored seed and
/// character.
/// </summary>
public static class RunReplayMenu
{
    private record ReplayEntry(
        string Seed,
        string CharacterId,
        int Floor,
        DateTime SavedAt,
        string MinimalLogPath);

    public static Control Create(NMainMenu mainMenu)
    {
        // Root: full-rect, stops mouse events reaching the menu behind it.
        var root = new Control();
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.MouseFilter = Control.MouseFilterEnum.Stop;

        // Inherit the main menu's theme so all child controls use the game's fonts/styles.
        root.Theme = mainMenu.Theme;

        // Dim overlay behind the panel.
        var bg = new ColorRect();
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        bg.Color = new Color(0f, 0f, 0f, 0.75f);
        root.AddChild(bg);

        // CenterContainer places the panel in the middle of the screen.
        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(center);

        // Outer panel (game-styled background box).
        var panel = new PanelContainer();
        panel.CustomMinimumSize = new Vector2(960, 600);
        center.AddChild(panel);

        // Inner layout.
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        panel.AddChild(vbox);

        // ── Title ────────────────────────────────────────────────────────────
        var title = new Label();
        title.Text = "Run Replays";
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);

        vbox.AddChild(new HSeparator());

        // ── Replay list ───────────────────────────────────────────────────────
        var scroll = new ScrollContainer();
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        vbox.AddChild(scroll);

        var list = new VBoxContainer();
        list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        list.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(list);

        PopulateList(list, root);

        // ── Back button ───────────────────────────────────────────────────────
        vbox.AddChild(new HSeparator());

        var backBtn = new Button();
        backBtn.Text = "Back";
        backBtn.Pressed += () => root.QueueFree();
        vbox.AddChild(backBtn);

        return root;
    }

    // ── List population ───────────────────────────────────────────────────────

    private static void PopulateList(VBoxContainer list, Control root)
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

        foreach (ReplayEntry entry in entries)
        {
            var row = new Button();
            row.Text = FormatEntry(entry);
            row.Alignment = HorizontalAlignment.Left;
            row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

            var captured = entry;
            row.Pressed += () =>
            {
                root.QueueFree();
                StartReplay(captured);
            };

            list.AddChild(row);
        }
    }

    private static string FormatEntry(ReplayEntry entry)
    {
        string date = entry.SavedAt != DateTime.MinValue
            ? entry.SavedAt.ToString("yyyy-MM-dd  HH:mm:ss")
            : "unknown date";

        return $"Seed: {entry.Seed}    {entry.CharacterId}    Floor {entry.Floor}    {date}";
    }

    // ── File system scan ──────────────────────────────────────────────────────

    private static List<ReplayEntry> LoadEntries()
    {
        string logsRoot = Path.Combine(OS.GetUserDataDir(), "RunReplays", "logs");
        var entries = new List<ReplayEntry>();

        if (!Directory.Exists(logsRoot))
            return entries;

        foreach (string seedDir in Directory.GetDirectories(logsRoot))
        {
            foreach (string floorDir in Directory.GetDirectories(seedDir))
            {
                string dirName = Path.GetFileName(floorDir);

                if (!dirName.StartsWith("floor_"))
                    continue;

                if (!int.TryParse(dirName.Substring("floor_".Length), out int floor))
                    continue;

                // Pick the most recent log pair. Filenames are yyyy-MM-dd_HH-mm-ss-fff
                // so lexicographic order is identical to chronological order.
                string? latestVerbose = Directory
                    .GetFiles(floorDir, "*.verbose.log")
                    .OrderByDescending(f => f)
                    .FirstOrDefault();

                string? latestMinimal = Directory
                    .GetFiles(floorDir, "*.minimal.log")
                    .OrderByDescending(f => f)
                    .FirstOrDefault();

                if (latestVerbose == null || latestMinimal == null)
                    continue;

                // Parse the verbose header for seed, character, and date.
                var (seed, characterId, savedAt) = ReadVerboseHeader(latestVerbose);

                entries.Add(new ReplayEntry(seed, characterId, floor, savedAt, latestMinimal));
            }
        }

        // Newest saves at the top of the list.
        return [.. entries.OrderByDescending(e => e.SavedAt)];
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

    // ── Run start ─────────────────────────────────────────────────────────────

    private static void StartReplay(ReplayEntry entry)
    {
        // Load the minimal log into the replay engine so auto-execution kicks in
        // as the new run reaches each decision point.
        string[] lines   = File.ReadAllLines(entry.MinimalLogPath);
        var      commands = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        ReplayRunner.Load(commands);

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

        GD.Print($"[RunReplays] Starting replay: seed={entry.Seed} character={character.Id} floor={entry.Floor} ({commands.Count} commands)");

        TaskHelper.RunSafely(
            NGame.Instance.StartNewSingleplayerRun(
                character,
                shouldSave: true,
                ActModel.GetDefaultList(),
                [],
                entry.Seed));
    }
}

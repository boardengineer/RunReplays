using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
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
///   {UserDataDir}/RunReplays/logs/{seed}/floor_{N}/{datetime}.minimal.log
///
/// Each unique (seed, floor) pair is represented by its most recent minimal log.
/// Entries are sorted newest-first. Selecting an entry loads that log into
/// ReplayEngine so the mod can auto-execute the recorded choices.
/// </summary>
public static class RunReplayMenu
{
    private record ReplayEntry(string Seed, int Floor, DateTime SavedAt, string MinimalLogPath);

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
                LoadReplay(captured);
                root.QueueFree();
            };

            list.AddChild(row);
        }
    }

    private static string FormatEntry(ReplayEntry entry)
    {
        string date = entry.SavedAt != DateTime.MinValue
            ? entry.SavedAt.ToString("yyyy-MM-dd  HH:mm:ss")
            : "unknown date";

        return $"Seed: {entry.Seed}    Floor {entry.Floor}    {date}";
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
            string seed = Path.GetFileName(seedDir);

            foreach (string floorDir in Directory.GetDirectories(seedDir))
            {
                string dirName = Path.GetFileName(floorDir);

                if (!dirName.StartsWith("floor_"))
                    continue;

                if (!int.TryParse(dirName.Substring("floor_".Length), out int floor))
                    continue;

                // Pick the most recent minimal log in this floor directory.
                // Filenames are yyyy-MM-dd_HH-mm-ss-fff so lexicographic order
                // is identical to chronological order.
                string? latest = Directory
                    .GetFiles(floorDir, "*.minimal.log")
                    .OrderByDescending(f => f)
                    .FirstOrDefault();

                if (latest == null)
                    continue;

                // Strip ".minimal.log" to recover the datetime base name.
                string baseName = Path.GetFileName(latest);
                baseName = baseName[..^".minimal.log".Length];

                DateTime.TryParseExact(
                    baseName, "yyyy-MM-dd_HH-mm-ss-fff",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime savedAt);

                entries.Add(new ReplayEntry(seed, floor, savedAt, latest));
            }
        }

        // Newest saves at the top of the list.
        return [.. entries.OrderByDescending(e => e.SavedAt)];
    }

    // ── Replay loading ────────────────────────────────────────────────────────

    private static void LoadReplay(ReplayEntry entry)
    {
        string[] lines = File.ReadAllLines(entry.MinimalLogPath);
        var commands = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        ReplayEngine.Load(commands);
        GD.Print($"[RunReplays] Loaded replay: seed={entry.Seed} floor={entry.Floor} ({commands.Count} commands)");
    }
}

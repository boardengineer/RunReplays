using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace RunReplays;

/// <summary>
/// Vote-screen capture, for drafting the chat vote screens (the stream doc's "Vote screens" pages): while the file
/// %APPDATA%\SlayTheSpire2\RunReplays\vote_capture.on exists, every decision screen the run reaches is saved once it has
/// settled - a PNG of the game window plus a JSON with the screen name, the available commands and the run details the
/// live bridge exports (WriteRun / WriteDetails) - into RunReplays\vote_screens\. A screen is identified by its name and
/// the set of command types on offer; each new one is captured after 1.5 s without change (animations done). Fights are
/// captured once each, on the first player turn. Without the flag file nothing is read or written.
/// </summary>
internal static class VoteScreenCapture
{
    private static string FlagPath => Path.Combine(LiveBridge.Dir, "vote_capture.on");
    private static string OutDir => Path.Combine(LiveBridge.Dir, "vote_screens");

    private const int StableTicks = 15;   // 0.1 s ticks
    private const int MaxPerSession = 300;

    private static readonly PropertyInfo? RunStateProp =
        typeof(RunManager).GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static string _sig = "", _lastSaved = "";
    private static int _stable, _saved, _checkTick;
    private static bool _on;

    internal static void Tick()
    {
        if (++_checkTick % 10 == 0) _on = File.Exists(FlagPath);   // once a second
        if (!_on || _saved >= MaxPerSession) return;

        var state = RunStateProp?.GetValue(RunManager.Instance) as IRunState;
        Player? player = state?.Players.FirstOrDefault();
        bool inProgress = RunManager.Instance?.IsInProgress == true;
        if (!inProgress || state == null || player == null) { _sig = ""; return; }

        string screen = LiveBridge.DetectScreen(state, player, inProgress);
        if (screen is "loading" or "main_menu") return;
        List<ReplayCommandInfo> cmds;
        try { cmds = ReplayDispatcher.GetAvailableCommands().Select(c => new ReplayCommandInfo(c.GetType().Name, c.ToString() ?? "")).ToList(); }
        catch { return; }
        if (cmds.Count == 0 && screen != "game_over") return;

        // A fight is one screen: its turns, hand and targets change all the time.
        string sig = screen == "combat"
            ? $"combat|{state.TotalFloor}"
            : $"{screen}|{state.TotalFloor}|{string.Join(",", cmds.Select(c => c.Type).Distinct().OrderBy(x => x))}|{cmds.Count}";
        if (screen == "combat" && !cmds.Any(c => c.Type == "EndTurnCommand")) return;
        if (sig != _sig) { _sig = sig; _stable = 0; return; }
        if (++_stable != StableTicks || sig == _lastSaved) return;
        _lastSaved = sig;
        Save(state, player, screen, cmds);
    }

    private record ReplayCommandInfo(string Type, string Text);

    private static void Save(IRunState state, Player player, string screen, List<ReplayCommandInfo> cmds)
    {
        Directory.CreateDirectory(OutDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string name = $"{stamp}_f{state.TotalFloor:00}_{screen}";
        try
        {
            var img = NGame.Instance?.GetViewport()?.GetTexture()?.GetImage();
            img?.SavePng(Path.Combine(OutDir, name + ".png"));
        }
        catch (Exception e) { LiveBridge.Log("vote capture: screenshot failed: " + e.Message); }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("screen", screen);
            w.WriteNumber("floor", state.TotalFloor);
            w.WriteBoolean("inCombat", CombatManager.Instance?.IsInProgress == true);
            var vp = NGame.Instance?.GetViewport()?.GetVisibleRect().Size;
            if (vp != null) { w.WriteNumber("width", vp.Value.X); w.WriteNumber("height", vp.Value.Y); }
            w.WritePropertyName("commands"); w.WriteStartArray();
            foreach (var c in cmds)
            {
                w.WriteStartObject(); w.WriteString("type", c.Type); w.WriteString("text", c.Text); w.WriteEndObject();
            }
            w.WriteEndArray();
            try { LiveBridge.WriteRun(w, state, player); } catch (Exception e) { w.WriteString("runError", e.Message); }
            try { LiveBridge.WriteDetails(w, state, player, screen); } catch (Exception e) { w.WriteString("detailError", e.Message); }
            w.WriteEndObject();
        }
        File.WriteAllText(Path.Combine(OutDir, name + ".json"), Encoding.UTF8.GetString(ms.ToArray()));
        _saved++;
        LiveBridge.Log($"vote capture: {name} ({cmds.Count} commands)");
    }
}

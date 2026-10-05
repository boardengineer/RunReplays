using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using RunReplays.Patches.Replay;

namespace RunReplays;

/// <summary>
/// In-game "fight autopilot" for the sts-sim fight MCTS (C:\sts-sim\tools\fight_autopilot.py).
///
/// The player presses the hotkey (RunReplaysConfig.AutopilotHotkey, default F8) during a fight. The mod then opens an
/// autopilot session for this fight: it writes %APPDATA%\SlayTheSpire2\RunReplays\autopilot_request.json
/// {"session": id, "action": "start"}, resets the live command channel (live_cmd.txt / live_ack.txt) and, while the
/// session is active, exports live_state.json and executes the helper's commands through the normal replay dispatcher
/// (so RunReplays records them like the player's own actions). The helper reports its progress in
/// autopilot_status.json {"session", "state", "text", "heartbeat"}, which is shown in an on-screen label. The session
/// ends when the helper reports a final state (done / refused / stopped / error: fight won or lost, content the sim
/// cannot play, ...), when the hotkey is pressed again (cancel: queued commands are dropped, the action in flight
/// finishes), when the helper does not answer, or when the run ends. Control is then back with the player.
///
/// While a session is active (and until the action in flight has finished) the player's mouse and keyboard input to
/// the game is blocked (a full-screen input catcher + the combat input handlers), except the hotkey itself.
///
/// Without a session the autopilot is completely passive: no files are read or written and nothing changes for
/// normal play, recording or replays.
///
/// Test hook: with the environment variable RUNREPLAYS_AUTOPILOT_TEST=1 the file autopilot_hotkey.trigger (same
/// folder) acts like a hotkey press (checked every 0.1 s; deleted when consumed) - same code path as the key.
/// </summary>
public static class Autopilot
{
    public static bool ConfigEnabled => RunReplaysConfig.EnableAutopilot || LiveBridge.Enabled;

    /// <summary>A session is active: the helper's commands are accepted.</summary>
    public static bool SessionActive { get; private set; }

    /// <summary>The session ended; waiting for the action in flight to finish before giving control back.</summary>
    public static bool Draining { get; private set; }

    /// <summary>The autopilot drives the game (player input blocked, dispatcher executing commands).</summary>
    public static bool Driving => SessionActive || Draining;

    /// <summary>Id of the current / last session (unix ms of the hotkey press); 0 = none yet.</summary>
    public static long SessionId { get; private set; }

    private static readonly bool TestHook = System.Environment.GetEnvironmentVariable("RUNREPLAYS_AUTOPILOT_TEST") == "1";

    private static string RequestPath => Path.Combine(LiveBridge.Dir, "autopilot_request.json");
    private static string StatusPath => Path.Combine(LiveBridge.Dir, "autopilot_status.json");
    private static string TriggerPath => Path.Combine(LiveBridge.Dir, "autopilot_hotkey.trigger");

    private const long HelperStartTimeoutMs = 5000;
    private const long HelperSilenceTimeoutMs = 20000;
    private const long NoCombatTimeoutMs = 15000;
    private const long DrainTimeoutMs = 8000;

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static long _sessionStartMs, _drainStartMs, _noCombatSinceMs, _lastHotkeyMs;
    private static bool _helperSeen;
    private static long _helperHeartbeatMs;
    private static string? _statusRaw;
    private static float _savedDelay = 1.0f;
    private static string _endText = "";
    private static Color _endColor;

    private static readonly Color Gold = new(0.937f, 0.784f, 0.318f, 1f);
    private static readonly Color Green = new(0.55f, 0.9f, 0.55f, 1f);
    private static readonly Color Red = new(1f, 0.5f, 0.45f, 1f);
    private static readonly Color Cream = new(1f, 0.965f, 0.886f, 1f);

    // ------------------------------------------------------------------ hotkey

    private static string? _hotkeyText;
    private static Key _hotkey = Key.F8;

    internal static string HotkeyName => string.IsNullOrWhiteSpace(RunReplaysConfig.AutopilotHotkey) ? "F8" : RunReplaysConfig.AutopilotHotkey.Trim();

    private static Key HotkeyKey()
    {
        string s = HotkeyName;
        if (s != _hotkeyText)
        {
            _hotkeyText = s;
            Key k = Key.None;
            try { k = OS.FindKeycodeFromString(s); } catch { }
            if (k == Key.None) { LiveBridge.Log($"autopilot: unknown hotkey '{s}', using F8"); k = Key.F8; }
            _hotkey = k;
        }
        return _hotkey;
    }

    /// <summary>Every input event that reaches NGame._Input (Harmony postfix below).</summary>
    internal static void OnInputEvent(InputEvent e)
    {
        if (e is not InputEventKey k || !k.Pressed || k.Echo) return;
        if (!ConfigEnabled) return;
        Key want = HotkeyKey();
        if (k.Keycode != want && k.PhysicalKeycode != want) return;
        OnHotkey("key");
    }

    internal static void OnHotkey(string source)
    {
        long now = NowMs;
        if (now - _lastHotkeyMs < 300) return;   // debounce (key + trigger file, key repeat)
        _lastHotkeyMs = now;
        LiveBridge.Log($"autopilot: hotkey ({source}) active={SessionActive} draining={Draining}");
        if (SessionActive) { Cancel(); return; }
        if (Draining) { Show("AUTOPILOT: stopping - finishing the current action...", Cream, 0); return; }
        string? why = WhyNotStart();
        if (why != null)
        {
            Show($"AUTOPILOT: {why}", Red, 3500);
            return;
        }
        Start(source);
    }

    private static string? WhyNotStart()
    {
        if (LiveBridge.InRun) return "a bot controls this run";
        // Only while recorded commands are queued / executing: a run started from the Run Replays menu (IsReplayRun) is
        // free to play once nothing is left to replay - a plain save load queues nothing, a replay from a floor hands
        // control back when its commands run out.
        if (ReplayEngine._replayActive || ReplayEngine._pending.Count > 0)
            return "not available while a replay is running";
        if (RunManager.Instance?.IsInProgress != true || CombatManager.Instance?.IsInProgress != true)
            return $"only available during a fight ({HotkeyName})";
        return null;
    }

    // ------------------------------------------------------------------ session

    private static void Start(string source)
    {
        long now = NowMs;
        SessionId = now;
        _sessionStartMs = now;
        _helperSeen = false;
        _helperHeartbeatMs = 0;
        _noCombatSinceMs = 0;
        _statusRaw = null;
        LiveBridge.ResetChannel();
        CardPlayReplayPatch.PrepareExternalControl();
        _savedDelay = ReplayDispatcher.DelayBetweenCommands;
        ReplayDispatcher.DelayBetweenCommands = 0.3f;
        SessionActive = true;
        WriteRequest("start", source);
        LiveBridge.Log($"autopilot: session {SessionId} started ({source})");
        Show($"AUTOPILOT: starting - waiting for the helper...   ({HotkeyName} to stop)", Gold, 0);
    }

    private static void Cancel()
    {
        WriteRequest("stop", "hotkey");
        EndSession($"stopped ({HotkeyName})", Cream);
    }

    private static void EndSession(string text, Color color)
    {
        if (!SessionActive) return;
        SessionActive = false;
        // Only autopilot commands can be queued during a session (replays / bot runs refuse to start one).
        ReplayEngine._pending.Clear();
        LiveBridge.DropQueuedCommands();
        Draining = true;
        _drainStartMs = NowMs;
        _endText = text;
        _endColor = color;
        LiveBridge.Log($"autopilot: session {SessionId} ended: {text}");
        Show($"AUTOPILOT: {text} - finishing the current action...", color, 0);
    }

    private static void FinishDrain(string how)
    {
        Draining = false;
        ReplayDispatcher.DelayBetweenCommands = _savedDelay;
        LiveBridge.Log($"autopilot: control returned ({how})");
        Show($"AUTOPILOT: {_endText} - control returned", _endColor, 6000);
    }

    internal static void OnMainMenu()
    {
        if (SessionActive) EndSession("the run ended", Cream);
        if (Draining) FinishDrain("main menu");
    }

    private static void WriteRequest(string action, string source)
    {
        try
        {
            string json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["session"] = SessionId, ["action"] = action, ["source"] = source, ["time"] = NowMs,
                ["hotkey"] = HotkeyName,
            });
            string tmp = RequestPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, RequestPath, overwrite: true);
        }
        catch (Exception e) { LiveBridge.Log("autopilot: cannot write the request: " + e.Message); }
    }

    // ------------------------------------------------------------------ tick (every 0.1 s, LiveBridge timer)

    internal static void Tick()
    {
        if (TestHook && File.Exists(TriggerPath))
        {
            try { File.Delete(TriggerPath); } catch { }
            OnHotkey("test-trigger");
        }
        long now = NowMs;
        if (SessionActive)
        {
            ReadStatus();
            if (SessionActive)
            {
                if (!_helperSeen && now - _sessionStartMs > HelperStartTimeoutMs)
                {
                    WriteRequest("stop", "no-helper");
                    EndSession(@"the helper is not running - start C:\sts-sim\fight_autopilot.bat", Red);
                }
                else if (_helperSeen && now - _helperHeartbeatMs > HelperSilenceTimeoutMs)
                {
                    WriteRequest("stop", "helper-silent");
                    EndSession("the helper stopped responding", Red);
                }
                else if (RunManager.Instance?.IsInProgress != true)
                {
                    WriteRequest("stop", "run-ended");
                    EndSession("the run ended", Cream);
                }
                else if (CombatManager.Instance?.IsInProgress != true && ReplayEngine._pending.Count == 0)
                {
                    if (_noCombatSinceMs == 0) _noCombatSinceMs = now;
                    else if (now - _noCombatSinceMs > NoCombatTimeoutMs)
                    {
                        WriteRequest("stop", "fight-over");
                        EndSession("fight over", Cream);
                    }
                }
                else _noCombatSinceMs = 0;
            }
        }
        if (Draining)
        {
            bool busy = ReplayState.CardPlayInFlight || ReplayState.PotionInFlight
                        || CardPlayReplayPatch.IsAwaitingEndTurnCompletion || ReplayState.ActionInFlight;
            if (!busy) FinishDrain("idle");
            else if (now - _drainStartMs > DrainTimeoutMs) FinishDrain("timeout");
        }
        UpdateUi(now);
    }

    private static void ReadStatus()
    {
        string raw;
        try
        {
            if (!File.Exists(StatusPath)) return;
            raw = File.ReadAllText(StatusPath);
        }
        catch { return; }
        if (raw == _statusRaw) return;
        _statusRaw = raw;
        long session, heartbeat;
        string state, text;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            session = r.TryGetProperty("session", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            heartbeat = r.TryGetProperty("heartbeat", out v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            state = r.TryGetProperty("state", out v) ? v.GetString() ?? "" : "";
            text = r.TryGetProperty("text", out v) ? v.GetString() ?? "" : "";
        }
        catch { return; }
        if (session != SessionId) return;
        _helperSeen = true;
        _helperHeartbeatMs = Math.Max(heartbeat, NowMs - 1000);
        switch (state)
        {
            case "done": EndSession(text, Green); break;
            case "refused": EndSession(text, Red); break;
            case "error": EndSession(text, Red); break;
            case "stopped": EndSession(text, Cream); break;
            default: Show($"AUTOPILOT: {text}   ({HotkeyName} to stop)", Gold, 0); break;
        }
    }

    // ------------------------------------------------------------------ UI: label + input catcher

    private static CanvasLayer? _layer;
    private static Control? _blocker;
    private static PanelContainer? _panel;
    private static Label? _label;
    private static long _hideAtMs;   // 0 = shown until replaced

    private static bool EnsureUi()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer) && _layer.IsInsideTree()) return true;
        var game = NGame.Instance;
        if (game == null || !GodotObject.IsInstanceValid(game)) return false;

        _layer = new CanvasLayer { Layer = 120, Name = "RunReplaysAutopilot" };
        game.AddChild(_layer);

        // Full-screen input catcher (mouse), only visible while the autopilot drives.
        _blocker = new Control { Name = "InputCatcher", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        _blocker.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(_blocker);

        _panel = new PanelContainer { Name = "AutopilotLabel", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.04f, 0.03f, 0.88f),
            BorderColor = new Color(0.937f, 0.784f, 0.318f, 0.6f),
            ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 8, ContentMarginBottom = 8,
        };
        sb.SetBorderWidthAll(2);
        sb.SetCornerRadiusAll(8);
        _panel.AddThemeStyleboxOverride("panel", sb);
        _panel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _panel.GrowHorizontal = Control.GrowDirection.Both;
        _panel.OffsetTop = 64;
        _panel.OffsetBottom = 64;
        _layer.AddChild(_panel);

        _label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
        _label.AddThemeFontSizeOverride("font_size", 22);
        _label.AddThemeConstantOverride("outline_size", 6);
        _label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 1f));
        _panel.AddChild(_label);
        return true;
    }

    private static void Show(string text, Color color, long lingerMs)
    {
        if (!EnsureUi()) return;
        _label!.Text = text;
        _label.AddThemeColorOverride("font_color", color);
        _panel!.Visible = true;
        _hideAtMs = lingerMs > 0 ? NowMs + lingerMs : 0;
        UpdateBlocker();
    }

    private static void UpdateUi(long now)
    {
        if (_panel == null || !GodotObject.IsInstanceValid(_panel)) return;
        if (_panel.Visible && _hideAtMs > 0 && now > _hideAtMs && !Driving) _panel.Visible = false;
        UpdateBlocker();
    }

    private static void UpdateBlocker()
    {
        if (_blocker != null && GodotObject.IsInstanceValid(_blocker) && _blocker.Visible != Driving)
            _blocker.Visible = Driving;
    }
}

/// <summary>Autopilot hotkey: every input event that reaches NGame._Input.</summary>
[HarmonyPatch(typeof(NGame), "_Input")]
public static class AutopilotHotkeyPatch
{
    [HarmonyPostfix]
    public static void Postfix(InputEvent __0)
    {
        try { Autopilot.OnInputEvent(__0); }
        catch (Exception e) { LiveBridge.Log("autopilot hotkey error: " + e.Message); }
    }
}

/// <summary>
/// While the autopilot drives: the player's keyboard / mouse input to the combat UI is ignored (card drag, targeting,
/// hand hotkeys, end turn and other hotkeys, potion popups). Mouse clicks are also caught by the autopilot's
/// full-screen input catcher. The autopilot's own commands do not go through these handlers.
/// </summary>
[HarmonyPatch]
public static class AutopilotInputBlockPatch
{
    private static readonly (string Type, string Method)[] Handlers =
    {
        ("MegaCrit.Sts2.Core.Nodes.CommonUi.NHotkeyManager", "_UnhandledInput"),
        ("MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand", "_UnhandledInput"),
        ("MegaCrit.Sts2.Core.Nodes.Combat.NMouseCardPlay", "_Input"),
        ("MegaCrit.Sts2.Core.Nodes.Combat.NControllerCardPlay", "_Input"),
        ("MegaCrit.Sts2.Core.Nodes.Combat.NTargetManager", "_Input"),
        ("MegaCrit.Sts2.Core.Nodes.Combat.NCombatUi", "_Input"),
        ("MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopup", "_Input"),
    };

    public static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var (type, method) in Handlers)
        {
            var t = AccessTools.TypeByName(type);
            var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
            if (m != null) yield return m;
        }
    }

    [HarmonyPrefix]
    public static bool Prefix() => !Autopilot.Driving;
}

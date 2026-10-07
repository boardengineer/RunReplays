using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using RunReplays.Commands;
using RunReplays.Patches.Replay;
using RunReplays.Utils;

namespace RunReplays;

/// <summary>
/// Live command bridge for an external bot (C:\sts-sim\tools\real_game_bot.py).
///
/// Two modes:
///   * bot mode: the game is started with the environment variable RUNREPLAYS_LIVE=1 (tools\real_game_bot.py): the bridge
///     polls live_cmd.txt and exports live_state.json all the time and a bot can start/drive whole runs (StartRun ...).
///   * fight autopilot (<see cref="Autopilot"/>, RunReplaysConfig.EnableAutopilot, default on): completely passive
///     (no file is read or written, recording/replays unaffected) until the player presses the autopilot hotkey in a
///     fight; then, for that fight only, it exports the state and executes the commands of
///     C:\sts-sim\tools\fight_autopilot.py, like in bot mode.
///
///   bot  -> game : %APPDATA%\SlayTheSpire2\RunReplays\live_cmd.txt   lines "seq&lt;TAB&gt;command[ # comment]" (append-only)
///   game -> bot  : live_ack.txt    "ackSeq&lt;TAB&gt;status&lt;TAB&gt;detail" of the last consumed/rejected command
///                  live_state.json the current screen, the concrete dispatchable commands, run info, open selections and,
///                                  at a combat input point, the fight snapshot (FightExport, format sts2-fight-snapshot)
///                  live_fights.jsonl every exported fight snapshot (one per state id)
///
/// Commands use the .sts2replay syntax and are executed by the normal replay dispatcher (ReplayEngine queue); a live
/// run is a replay that is fed one command at a time. Every consumed command is appended to the run's action buffer,
/// so RunSaveLogger writes the usual per-floor logs (actions.sts2replay + run.save) and the run can be replayed.
/// Special commands: "StartRun CHARACTER SEED ASC" (main menu only; acts rolled like StartRunLobby), "Quit".
/// </summary>
public static class LiveBridge
{
    /// <summary>Bot mode (environment variable RUNREPLAYS_LIVE=1).</summary>
    public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("RUNREPLAYS_LIVE") == "1";

    /// <summary>True from StartRun until the game returns to the main menu (bot mode; "Detach" clears it).</summary>
    public static bool InRun { get; private set; }

    /// <summary>
    /// The replay dispatcher executes externally fed commands: a bot-mode run or an active autopilot session. While
    /// true, ReplayEngine.IsActive is true (the game's own action recording is replaced by recording the executed
    /// commands, exactly like in a live bot run).
    /// </summary>
    public static bool Driving => InRun || Autopilot.Driving;

    private static readonly PropertyInfo? RunStateProp =
        typeof(RunManager).GetProperty("State", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static string? _dir;
    internal static string Dir
    {
        get
        {
            if (_dir != null) return _dir;
            try { _dir = Path.Combine(OS.GetUserDataDir(), "RunReplays"); }
            catch { _dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "SlayTheSpire2", "RunReplays"); }
            Directory.CreateDirectory(_dir);
            return _dir;
        }
    }

    private static string CmdPath => Path.Combine(Dir, "live_cmd.txt");
    private static string AckPath => Path.Combine(Dir, "live_ack.txt");
    private static string StatePath => Path.Combine(Dir, "live_state.json");
    private static string FightLogPath => Path.Combine(Dir, "live_fights.jsonl");
    private static string LogPath => Path.Combine(Dir, "live_bridge.log");

    private static bool _polling;
    private static long _cmdFilePos;
    private static string _partial = "";
    private static int _lastSeq;
    private static int _ackSeq;
    private static readonly Dictionary<ReplayCommand, int> _seqOf = new(ReferenceEqualityComparer.Instance);
    private static long _tick;
    private static int _stateId;
    private static string? _lastKey;
    private static int _stableTicks;
    private static int _snapStateId = -1;
    /// <summary>Poll ticks (0.1 s) a state must stay unchanged before it is reported ready (RUNREPLAYS_LIVE_STABLE).</summary>
    private static readonly int StableTicks =
        int.TryParse(System.Environment.GetEnvironmentVariable("RUNREPLAYS_LIVE_STABLE"), out int st) && st > 0 ? st : 4;
    private static string? _snapJson;

    // ------------------------------------------------------------------ lifecycle

    public static void OnMainMenu()
    {
        Autopilot.OnMainMenu();
        if (!Enabled && !Autopilot.ConfigEnabled) return;
        if (InRun) Log("back at the main menu");
        InRun = false;
        StartPoll();
    }

    private static void StartPoll()
    {
        if (_polling) return;
        _polling = true;
        if (Enabled) Log($"live bridge started (bot mode, dir={Dir})");
        Schedule();
    }

    private static void Schedule()
    {
        var tree = NGame.Instance?.GetTree();
        if (tree == null) { _polling = false; return; }
        tree.CreateTimer(0.1, true, false, true).Connect("timeout", Callable.From(Tick));
    }

    private static void Tick()
    {
        _tick++;
        // Autopilot mode: nothing is read or written unless a session is active (the hotkey was pressed in a fight).
        if (Enabled || Autopilot.SessionActive)
            try { ReadInbox(); } catch (Exception e) { Log("inbox error: " + e); }
        // A queued command whose type was not dispatchable when it arrived waits for the next dispatch trigger; nudge.
        if (Driving && ReplayEngine._pending.Count > 0 && _tick % 5 == 0)
            try { ReplayDispatcher.TryDispatch(); } catch (Exception e) { Log("dispatch nudge error: " + e.Message); }
        if (Enabled || Autopilot.Driving)
            try { ExportState(); } catch (Exception e) { Log("export error: " + e); }
        try { Autopilot.Tick(); } catch (Exception e) { Log("autopilot error: " + e); }
        try { VoteScreenCapture.Tick(); } catch (Exception e) { Log("vote capture error: " + e.Message); }
        Schedule();
    }

    /// <summary>Autopilot session start: a fresh command channel (seq numbers restart at 1).</summary>
    internal static void ResetChannel()
    {
        try { File.WriteAllText(CmdPath, ""); } catch (Exception e) { Log("cannot truncate live_cmd.txt: " + e.Message); }
        try { File.Delete(AckPath); } catch { }
        _cmdFilePos = 0;
        _partial = "";
        _lastSeq = 0;
        _ackSeq = 0;
        _seqOf.Clear();
        _lastKey = null;
        _snapStateId = -1;
        _snapJson = null;
    }

    /// <summary>Autopilot session end: the queued (not yet executed) commands were dropped; reject them.</summary>
    internal static void DropQueuedCommands()
    {
        foreach (var kv in _seqOf.ToList())
            Ack(kv.Value, "error", "autopilot session ended");
        _seqOf.Clear();
    }

    internal static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { }
        DiagnosticLog.Write("Live", msg);
    }

    // ------------------------------------------------------------------ inbox

    private static void ReadInbox()
    {
        if (!File.Exists(CmdPath)) { _cmdFilePos = 0; _partial = ""; return; }
        using var fs = new FileStream(CmdPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _cmdFilePos) { _cmdFilePos = 0; _partial = ""; }   // file was recreated
        if (fs.Length == _cmdFilePos) return;
        fs.Seek(_cmdFilePos, SeekOrigin.Begin);
        var buf = new byte[fs.Length - _cmdFilePos];
        int n = fs.Read(buf, 0, buf.Length);
        _cmdFilePos += n;
        string text = _partial + Encoding.UTF8.GetString(buf, 0, n);
        int nl;
        while ((nl = text.IndexOf('\n')) >= 0)
        {
            string line = text[..nl].TrimEnd('\r');
            text = text[(nl + 1)..];
            HandleLine(line);
        }
        _partial = text;
    }

    private static void HandleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        int tab = line.IndexOf('\t');
        if (tab < 0 || !int.TryParse(line[..tab], out int seq)) { Log("bad inbox line: " + line); return; }
        if (seq <= _lastSeq) return;
        _lastSeq = seq;
        string raw = line[(tab + 1)..].Trim();
        string? comment = null;
        int ci = raw.IndexOf(" # ", StringComparison.Ordinal);
        if (ci >= 0) { comment = raw[(ci + 3)..]; raw = raw[..ci]; }
        Log($"recv {seq}: {raw}" + (comment != null ? $" # {comment}" : ""));

        bool special = raw.StartsWith("StartRun ") || raw.StartsWith("Replay ") || raw == "Quit" || raw == "Attach" || raw == "Detach" || raw == "MainMenu"
                       || raw.StartsWith("Console ") || raw.StartsWith("LoadFloor ");
        if (special && !Enabled) { Ack(seq, "error", "only in bot mode (RUNREPLAYS_LIVE=1)"); return; }
        if (raw.StartsWith("LoadFloor "))
        {
            // Bot mode: continue a recorded run from the start of one floor ("LoadFloor SEED:floor_N", its run.save) and
            // attach, so the bot plays that floor (e.g. re-play a fight with the current sim / MCTS and record it).
            if (InRun || RunManager.Instance.IsInProgress) { Ack(seq, "error", "a run is in progress"); return; }
            string spec = raw["LoadFloor ".Length..].Trim();
            int colon = spec.IndexOf(":floor_", StringComparison.OrdinalIgnoreCase);
            if (colon <= 0 || !int.TryParse(spec[(colon + ":floor_".Length)..], out int floor))
            { Ack(seq, "error", "usage: LoadFloor SEED:floor_N"); return; }
            string? err = RunReplayMenu.LoadFloorSave(spec[..colon], floor);
            if (err != null) { Ack(seq, "error", err); return; }
            CardPlayReplayPatch.PrepareExternalControl();
            InRun = true;
            ReplayDispatcher.DelayBetweenCommands = 0.3f;
            Ack(seq, "ok", $"loading {spec}");
            return;
        }
        if (raw.StartsWith("Console "))
        {
            // Bot mode (tests): a dev-console command for the local player, e.g. "relic add GAMBLING_CHIP" or
            // "fight TEST_SUBJECT_BOSS" (set up autopilot test situations). ProcessNetCommand: no console history write.
            if (RunManager.Instance?.IsInProgress != true) { Ack(seq, "error", "no run in progress"); return; }
            try
            {
                var me = MegaCrit.Sts2.Core.Context.LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
                var console = new MegaCrit.Sts2.Core.DevConsole.DevConsole(true);
                string text = raw["Console ".Length..].Trim();
                var res = console.ProcessNetCommand(me, text);
                if (res.task != null) TaskHelper.RunSafely(res.task);
                // A jump into a room (event / ancient / room / fight) from the map leaves the map screen open on top of it.
                if (res.success && (text.StartsWith("event ") || text.StartsWith("ancient ") || text.StartsWith("room ") || text.StartsWith("fight ")))
                {
                    var map = NMapScreen.Instance;
                    if (map != null && GodotObject.IsInstanceValid(map) && map.IsOpen) map.Close(false);
                }
                Ack(seq, res.success ? "ok" : "error", res.msg ?? "");
            }
            catch (Exception e) { Ack(seq, "error", "console: " + e.Message); }
            return;
        }
        if (raw.StartsWith("StartRun ")) { StartRun(seq, raw); return; }
        if (raw == "Detach")
        {
            // Bot mode (tests): hand the running run over to normal play (as if the player had started it), e.g. to
            // exercise the autopilot hotkey. "Attach" takes it back.
            if (!InRun) { Ack(seq, "error", "not attached"); return; }
            if (ReplayEngine._pending.Count > 0) { Ack(seq, "error", "commands still queued"); return; }
            InRun = false;
            ReplayDispatcher.DelayBetweenCommands = 1.0f;
            Ack(seq, "ok", "detached");
            return;
        }
        if (raw == "Attach")
        {
            if (InRun) { Ack(seq, "error", "already attached"); return; }
            if (Autopilot.Driving) { Ack(seq, "error", "autopilot session active"); return; }
            if (RunManager.Instance?.IsInProgress != true) { Ack(seq, "error", "no run in progress"); return; }
            CardPlayReplayPatch.PrepareExternalControl();
            InRun = true;
            ReplayDispatcher.DelayBetweenCommands = 0.3f;
            Ack(seq, "ok", "attached");
            return;
        }
        if (raw.StartsWith("Replay "))
        {
            // Watch-back check: replay a recorded run (RunReplays logs, "SEED" or "SEED:floor_N") like the menu does.
            if (InRun || RunManager.Instance.IsInProgress) { Ack(seq, "error", "a run is in progress"); return; }
            Ack(seq, "ok", raw);
            RunReplayMenu.AutoPlay(raw["Replay ".Length..].Trim());
            return;
        }
        if (raw == "MainMenu")
        {
            // Bot mode: back to the main menu from anywhere in a run (the game over screen included); ends the attachment.
            if (NGame.Instance == null) { Ack(seq, "error", "no game"); return; }
            ReplayEngine._pending.Clear();
            InRun = false;
            Ack(seq, "ok", "main menu");
            TaskHelper.RunSafely(NGame.Instance.ReturnToMainMenu());
            return;
        }
        if (raw == "Quit")
        {
            Ack(seq, "ok", "quit");
            NGame.Instance?.GetTree()?.Quit();
            return;
        }
        if (!InRun && !Autopilot.SessionActive) { Ack(seq, "error", "no live run / autopilot session in progress"); return; }

        ReplayCommand? cmd = ReplayCommandParser.TryParse(raw);
        if (cmd == null) { Ack(seq, "error", "unparseable: " + raw); return; }
        cmd.Comment = comment;
        _seqOf[cmd] = seq;
        ReplayEngine._pending.Enqueue(cmd);
        ReplayDispatcher.TryDispatch();
    }

    /// <summary>Called by ReplayEngine when a live command was executed successfully.</summary>
    internal static void OnConsumed(ReplayCommand cmd)
    {
        PlayerActionBuffer.RecordLive(cmd.ToLogString());
        if (_seqOf.Remove(cmd, out int seq)) Ack(seq, "ok", cmd.ToString() ?? "");
        _lastKey = null;   // force a fresh stability window
    }

    private static void Ack(int seq, string status, string detail)
    {
        _ackSeq = Math.Max(_ackSeq, seq);
        Log($"ack {seq} {status} {detail}");
        WriteAtomic(AckPath, $"{seq}\t{status}\t{detail}\n");
        _lastKey = null;
    }

    private static void StartRun(int seq, string raw)
    {
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) { Ack(seq, "error", "usage: StartRun CHARACTER SEED [ASC]"); return; }
        if (InRun || RunManager.Instance.IsInProgress) { Ack(seq, "error", "a run is already in progress"); return; }
        string charName = parts[1].ToUpperInvariant();
        string seed = parts[2].ToUpperInvariant();
        int asc = parts.Length > 3 && int.TryParse(parts[3], out int a) ? a : 0;
        CharacterModel? character = ModelDb.AllCharacters.FirstOrDefault(c => c.Id.Entry == charName);
        if (character == null || NGame.Instance == null) { Ack(seq, "error", "unknown character " + charName); return; }

        // Same act roll as StartRunLobby.BeginRunLocally (RunReplays forces UnlockState.all for act selection).
        var rng = new Rng(StringHelper.GetDeterministicHashCode(seed), "act_selection");
        var acts = ActModel.GetRandomList(rng, UnlockState.all, false).ToList();

        ReplayDispatcher.Clear();
        ReplayEngine.ActiveSeed = null;   // StartRunActOverride: keep our seed and acts
        ReplayEngine.ActiveActs = null;
        _seqOf.Clear();
        InRun = true;
        ReplayDispatcher.DelayBetweenCommands = 0.3f;
        Log($"StartRun {charName} {seed} asc={asc} acts=[{string.Join(", ", acts.Select(x => x.Id.Entry))}]");
        string mode = $"nonInteractive={MegaCrit.Sts2.Core.Helpers.NonInteractiveMode.IsActive} display={DisplayServer.GetName()}";
        Log(mode);
        Ack(seq, "ok", $"StartRun {charName} {seed} {asc} acts={string.Join(",", acts.Select(x => x.Id.Entry))} {mode}");
        TaskHelper.RunSafely(NGame.Instance.StartNewSingleplayerRun(character, true, acts, [], seed, GameMode.Standard, asc));
    }

    // ------------------------------------------------------------------ selection capture (CardSelectCmd option lists)

    internal sealed class Selection
    {
        public string Method = "";
        public int Min, Max;
        public bool CanSkip;
        public Player? Player;
        public Func<CardModel, bool>? Filter;
        public CardPile? Pile;
        public List<CardModel>? Cards;
        public long Tick;

        /// <summary>The option list in CardSelectCmd's selector order (what the sim's choice indices refer to).</summary>
        public List<CardModel> Options()
        {
            if (Cards != null) return Cards;
            if (Pile != null)
            {
                IEnumerable<CardModel> e = Pile.Cards.Where(Filter ?? (_ => true));
                if (Pile.Type == PileType.Draw) e = e.OrderBy(c => c.Rarity).ThenBy(c => c.Id);
                return e.ToList();
            }
            if (Player?.PlayerCombatState != null)
                return Player.PlayerCombatState.Hand.Cards.Where(Filter ?? (_ => true)).ToList();
            return new List<CardModel>();
        }
    }

    internal static Selection? CurrentSelection;

    internal static void CaptureSelection(Selection s)
    {
        if (!Enabled && !Autopilot.ConfigEnabled) return;
        s.Tick = _tick;
        CurrentSelection = s;
    }

    // ------------------------------------------------------------------ state export

    private static void ExportState()
    {
        var state = RunStateProp?.GetValue(RunManager.Instance) as IRunState;
        Player? player = state?.Players.FirstOrDefault();
        bool inProgress = RunManager.Instance?.IsInProgress == true;

        string screen = DetectScreen(state, player, inProgress);
        List<string> available = new();
        List<string> types = new();
        bool blocked = false;
        if (Driving && inProgress)
        {
            try { available = ReplayDispatcher.GetAvailableCommands().Select(c => c.ToString() ?? "").ToList(); } catch { }
            try { types = ReplayDispatcher.GetDispatchableTypesInternal(out blocked).Select(t => t.Name).OrderBy(x => x).ToList(); } catch { }
        }

        bool inputPoint = player != null && screen == "combat" && StsSim.FightExport.IsPlayerInputPoint(player);
        string fightFp = inputPoint ? FightFingerprint(player!) : "";
        string key = $"{screen}|{inputPoint}|{blocked}|{string.Join(";", available)}|{fightFp}|{player?.Creature?.CurrentHp}|{player?.Gold}|{state?.TotalFloor}|{ReplayEngine._pending.Count}";
        if (key != _lastKey) { _lastKey = key; _stableTicks = 0; _stateId++; }
        else _stableTicks++;

        int pending = ReplayEngine._pending.Count;
        bool ready = screen == "main_menu"
            ? !Driving && _stableTicks >= 3
            : Driving && pending == 0 && _stableTicks >= StableTicks && (available.Count > 0 || screen == "game_over")
              && (!blocked || screen == "hand_select" || screen == "grid_select" || screen == "choose_card")   // selections open mid-action
              && (screen != "combat" || inputPoint);

        if (inputPoint && ready && _snapStateId != _stateId)
        {
            _snapStateId = _stateId;
            _snapJson = null;
            try { _snapJson = StsSim.FightExport.Export(player!); } catch (Exception e) { Log("fight export failed: " + e.Message); }
            if (_snapJson != null)
                try { File.AppendAllText(FightLogPath, $"{{\"stateId\":{_stateId},\"ack\":{_ackSeq},\"snap\":{_snapJson}}}\n"); } catch { }
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteNumber("tick", _tick);
            w.WriteNumber("stateId", _stateId);
            w.WriteNumber("ack", _ackSeq);
            w.WriteNumber("lastSeq", _lastSeq);
            w.WriteNumber("pending", pending);
            w.WriteBoolean("replayActive", ReplayEngine.IsActive && !Driving);
            w.WriteNumber("replayLoaded", ReplayEngine._loadedCommands.Count);
            w.WriteBoolean("ready", ready);
            w.WriteBoolean("inRun", InRun);
            w.WritePropertyName("autopilot");
            w.WriteStartObject();
            w.WriteNumber("session", Autopilot.SessionId);
            w.WriteBoolean("active", Autopilot.SessionActive);
            w.WriteBoolean("driving", Autopilot.Driving);
            w.WriteEndObject();
            w.WriteString("screen", screen);
            w.WriteBoolean("blocked", blocked);
            w.WriteBoolean("inputPoint", inputPoint);
            w.WritePropertyName("types"); w.WriteStartArray(); foreach (var t in types) w.WriteStringValue(t); w.WriteEndArray();
            w.WritePropertyName("available"); w.WriteStartArray(); foreach (var c in available) w.WriteStringValue(c); w.WriteEndArray();
            if (state != null && player != null && inProgress)
            {
                try { WriteRun(w, state, player); } catch (Exception e) { w.WriteString("runError", e.Message); }
                try { WriteDetails(w, state, player, screen); } catch (Exception e) { w.WriteString("detailError", e.Message); }
            }
            if (inputPoint && ready && _snapStateId == _stateId && _snapJson != null)
            {
                w.WritePropertyName("fight");
                w.WriteRawValue(_snapJson, skipInputValidation: true);
            }
            w.WriteEndObject();
        }
        WriteAtomic(StatePath, Encoding.UTF8.GetString(ms.ToArray()));
    }

    internal static string DetectScreen(IRunState? state, Player? player, bool inProgress)
    {
        if (!inProgress || state == null || player == null) return Driving ? "loading" : "main_menu";
        // A dead player during combat is not (yet) a game over: a prevented death (Fairy in a Bottle, Lizard Tail-style
        // revives) passes through HP 0 mid-combat. RunState.IsGameOver is just "every player is dead"; a real loss ends the
        // combat (CombatManager.ProcessPendingLoss: IsInProgress = false), so require that too.
        try
        {
            if ((RunManager.Instance.IsGameOver || player.Creature.IsDead) && CombatManager.Instance?.IsInProgress != true)
                return "game_over";
        }
        catch { }
        var hand = HandSelectionCapture.ActiveHand;
        if (hand != null && GodotObject.IsInstanceValid(hand) && hand.IsInsideTree() && hand.IsInCardSelection) return "hand_select";
        var grid = CardGridScreenCapture.ActiveScreen;
        if (grid != null && GodotObject.IsInstanceValid(grid) && grid.IsInsideTree()) return "grid_select";
        var choose = ChooseACardScreenCapture.ActiveScreen;
        if (choose != null && GodotObject.IsInstanceValid(choose) && choose.IsInsideTree()) return "choose_card";
        var cr = ReplayState.CardRewardSelectionScreen;
        if (cr != null && GodotObject.IsInstanceValid(cr) && cr.IsInsideTree()) return "card_reward";
        if (CombatManager.Instance?.IsInProgress == true) return "combat";
        var rs = ReplayState.ActiveRewardsScreen;
        if (rs != null && GodotObject.IsInstanceValid(rs) && rs.IsInsideTree()) return "rewards";
        var room = state.CurrentRoom;
        if (NMapScreen.Instance != null && GodotObject.IsInstanceValid(NMapScreen.Instance) && NMapScreen.Instance.IsOpen
            && NMapScreen.Instance.IsTravelEnabled) return "map";
        switch (room)
        {
            case EventRoom: return "event";
            case RestSiteRoom: return "rest";
            case MerchantRoom: return "shop";
            case TreasureRoom: return "treasure";
        }
        if (NMapScreen.Instance != null && GodotObject.IsInstanceValid(NMapScreen.Instance) && NMapScreen.Instance.IsTravelEnabled) return "map";
        return room?.GetType().Name ?? "unknown";
    }

    private static string FightFingerprint(Player player)
    {
        var pcs = player.PlayerCombatState!;
        var cs = player.Creature.CombatState!;
        int entries = CombatManager.Instance?.History?.Entries?.Count() ?? 0;
        string hand = string.Join(",", pcs.Hand.Cards.Select(c => c.Id.Entry + c.CurrentUpgradeLevel));
        string enemies = string.Join(",", cs.Enemies.Select(e => $"{e.CombatId}:{e.CurrentHp}:{e.Block}:{e.Powers.Count}:{e.Monster?.NextMove?.Id}"));
        return $"{cs.RoundNumber}|{pcs.TurnNumber}|{pcs.Energy}|{player.Creature.CurrentHp}|{player.Creature.Block}|{entries}|{hand}|{enemies}|{pcs.DrawPile.Cards.Count}|{pcs.DiscardPile.Cards.Count}|{pcs.ExhaustPile.Cards.Count}";
    }

    private static void WriteCard(Utf8JsonWriter w, CardModel c)
    {
        w.WriteStartObject();
        w.WriteString("id", c.Id.Entry);
        w.WriteNumber("up", c.CurrentUpgradeLevel);
        try { if (NetCombatCardDb.Instance.TryGetCardId(c, out uint nid)) w.WriteNumber("ncid", nid); } catch { }
        w.WriteEndObject();
    }

    internal static void WriteRun(Utf8JsonWriter w, IRunState state, Player player)
    {
        w.WritePropertyName("run");
        w.WriteStartObject();
        w.WriteString("seed", state.Rng?.StringSeed);
        w.WriteString("character", player.Character.Id.Entry);
        w.WriteNumber("act", state.CurrentActIndex);
        w.WriteString("actId", state.Act?.Id.Entry);
        w.WriteNumber("actFloor", state.ActFloor);
        w.WriteNumber("totalFloor", state.TotalFloor);
        w.WriteNumber("asc", state.AscensionLevel);
        w.WriteString("roomType", state.CurrentRoom?.RoomType.ToString());
        w.WriteString("room", state.CurrentRoom?.GetType().Name);
        if (state.CurrentRoom is CombatRoom cr) w.WriteString("encounter", cr.Encounter?.Id.Entry);
        w.WriteNumber("hp", player.Creature.CurrentHp);
        w.WriteNumber("maxHp", player.Creature.MaxHp);
        w.WriteNumber("gold", player.Gold);
        w.WriteNumber("potionSlots", player.PotionSlots.Count);
        w.WritePropertyName("potions"); w.WriteStartArray();
        foreach (var p in player.PotionSlots) w.WriteStringValue(p?.Id.Entry);
        w.WriteEndArray();
        w.WritePropertyName("relics"); w.WriteStartArray();
        foreach (var r in player.Relics) w.WriteStringValue(r.Id.Entry);
        w.WriteEndArray();
        w.WritePropertyName("deck"); w.WriteStartArray();
        foreach (var c in player.Deck.Cards) { w.WriteStartObject(); w.WriteString("id", c.Id.Entry); w.WriteNumber("up", c.CurrentUpgradeLevel); w.WriteBoolean("upgradable", c.IsUpgradable); w.WriteEndObject(); }
        w.WriteEndArray();
        var coord = state.CurrentMapCoord;
        if (coord.HasValue) { w.WriteNumber("col", coord.Value.col); w.WriteNumber("row", coord.Value.row); }
        w.WriteEndObject();
    }

    internal static void WriteDetails(Utf8JsonWriter w, IRunState state, Player player, string screen)
    {
        // combat hand with NetCombatCardDb ids (PlayCard arguments), in hand order
        var pcs = player.PlayerCombatState;
        if (CombatManager.Instance?.IsInProgress == true && pcs != null)
        {
            w.WritePropertyName("hand"); w.WriteStartArray();
            foreach (var c in pcs.Hand.Cards) WriteCard(w, c);
            w.WriteEndArray();
            var cs = CombatManager.Instance.DebugOnlyGetState();
            if (cs != null)
            {
                w.WritePropertyName("enemies"); w.WriteStartArray();
                foreach (var e in cs.Enemies)
                {
                    w.WriteStartObject();
                    w.WriteString("id", e.Monster?.Id.Entry);
                    if (e.CombatId.HasValue) w.WriteNumber("cid", e.CombatId.Value);
                    w.WriteNumber("hp", e.CurrentHp); w.WriteNumber("maxHp", e.MaxHp); w.WriteBoolean("alive", e.IsAlive);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
        }

        // open card selection: options in CardSelectCmd order + their index in the UI command's terms
        var sel = CurrentSelection;
        if (sel != null && (screen == "hand_select" || screen == "grid_select" || screen == "choose_card"))
        {
            w.WritePropertyName("selection");
            w.WriteStartObject();
            w.WriteString("method", sel.Method);
            w.WriteNumber("min", sel.Min); w.WriteNumber("max", sel.Max); w.WriteBoolean("canSkip", sel.CanSkip);
            w.WriteNumber("age", _tick - sel.Tick);
            List<CardModel> ui = UiCards(screen, player);
            w.WritePropertyName("options"); w.WriteStartArray();
            foreach (var c in sel.Options())
            {
                w.WriteStartObject();
                w.WriteString("id", c.Id.Entry); w.WriteNumber("up", c.CurrentUpgradeLevel);
                w.WriteNumber("ui", ui.FindIndex(x => ReferenceEquals(x, c)));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        if (screen == "grid_select" || screen == "choose_card")
        {
            w.WritePropertyName("uiCards"); w.WriteStartArray();
            foreach (var c in UiCards(screen, player)) WriteCard(w, c);
            w.WriteEndArray();
        }

        if (screen == "rewards")
        {
            var rs = ReplayState.ActiveRewardsScreen;
            if (rs != null)
            {
                w.WritePropertyName("rewards"); w.WriteStartArray();
                foreach (var (_, reward) in ClaimRewardCommand.EnumerateRewardButtons(rs))
                {
                    w.WriteStartObject();
                    w.WriteString("type", reward.GetType().Name);
                    w.WriteString("desc", SafeStr(() => ClaimRewardCommand.DescribeReward(reward)));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
        }
        if (screen == "card_reward")
        {
            var scr = ReplayState.CardRewardSelectionScreen;
            var cardRow = typeof(NCardRewardSelectionScreen).GetField("_cardRow", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(scr) as Node;
            if (cardRow != null)
            {
                var holders = new List<(float x, CardModel c)>();
                foreach (Node child in cardRow.GetChildren())
                {
                    if (child is not Control ctrl) continue;
                    if (child.GetType().GetProperty("CardModel", BindingFlags.Public | BindingFlags.Instance)?.GetValue(child) is CardModel cm)
                        holders.Add((ctrl.Position.X, cm));
                }
                w.WritePropertyName("cardReward"); w.WriteStartArray();
                foreach (var h in holders.OrderBy(h => h.x)) { w.WriteStartObject(); w.WriteString("id", h.c.Id.Entry); w.WriteNumber("up", h.c.CurrentUpgradeLevel); w.WriteString("rarity", h.c.Rarity.ToString()); w.WriteEndObject(); }
                w.WriteEndArray();
            }
        }
        if (screen == "event")
        {
            var sync = ReplayState.ActiveEventSynchronizer;
            if (sync != null && sync.Events.Count > 0)
            {
                var ev = sync.Events[0];
                w.WritePropertyName("event");
                w.WriteStartObject();
                w.WriteString("id", ev.Id.Entry);
                w.WriteBoolean("finished", ev.IsFinished);
                w.WritePropertyName("options"); w.WriteStartArray();
                foreach (var o in ev.CurrentOptions)
                {
                    w.WriteStartObject();
                    w.WriteString("key", o.TextKey);
                    w.WriteString("title", SafeStr(() => o.Title?.GetFormattedText()));
                    w.WriteString("desc", SafeStr(() => o.Description?.GetFormattedText()));
                    w.WritePropertyName("tips"); w.WriteStartArray();
                    try
                    {
                        foreach (var tip in o.HoverTips ?? Enumerable.Empty<MegaCrit.Sts2.Core.HoverTips.IHoverTip>())
                        {
                            var card = tip.GetType().GetProperty("Card", BindingFlags.Public | BindingFlags.Instance)?.GetValue(tip) as CardModel;
                            var model = tip.GetType().GetProperty("CanonicalModel", BindingFlags.Public | BindingFlags.Instance)?.GetValue(tip) as AbstractModel;
                            string kind = model switch
                            {
                                EnchantmentModel => "ENCHANT", AfflictionModel => "AFFL", RelicModel => "RELIC", PowerModel => "POWER",
                                PotionModel => "POTION", _ => model?.GetType().BaseType?.Name ?? ""
                            };
                            w.WriteStringValue(card != null ? "CARD:" + card.Id.Entry + ":" + card.Type
                                : model != null ? kind + ":" + model.Id.Entry : tip.GetType().Name);
                        }
                    }
                    catch { }
                    w.WriteEndArray();
                    w.WriteBoolean("locked", o.IsLocked);
                    w.WriteBoolean("proceed", o.IsProceed);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
        }

        // map: all points of the act with types and children (for path planning)
        var map = state.Map;
        if (map != null && (screen == "map" || screen == "rewards" || screen == "event" || screen == "rest" || screen == "shop" || screen == "treasure"))
        {
            w.WritePropertyName("map"); w.WriteStartArray();
            foreach (var p in map.GetAllMapPoints())
            {
                w.WriteStartObject();
                w.WriteNumber("col", p.coord.col); w.WriteNumber("row", p.coord.row);
                w.WriteString("type", p.PointType.ToString());
                w.WritePropertyName("ch"); w.WriteStartArray();
                foreach (var ch in p.Children) { w.WriteStartArray(); w.WriteNumberValue(ch.coord.col); w.WriteNumberValue(ch.coord.row); w.WriteEndArray(); }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            try
            {
                var bp = map.BossMapPoint;
                if (bp != null) { w.WritePropertyName("boss"); w.WriteStartArray(); w.WriteNumberValue(bp.coord.col); w.WriteNumberValue(bp.coord.row); w.WriteEndArray(); }
            }
            catch { }
        }
    }

    /// <summary>Cards of the open selection UI in the index space of its replay command.</summary>
    private static List<CardModel> UiCards(string screen, Player player)
    {
        if (screen == "hand_select")
            return player.PlayerCombatState?.Hand.Cards.ToList() ?? new List<CardModel>();
        if (screen == "grid_select" && CardGridScreenCapture.ActiveScreen != null)
            return CardGridScreenCapture.GetSelectableCards(CardGridScreenCapture.ActiveScreen)?.ToList() ?? new List<CardModel>();
        if (screen == "choose_card" && ChooseACardScreenCapture.ActiveScreen != null)
        {
            var list = new List<CardModel>();
            foreach (Node node in ChooseACardScreenCapture.ActiveScreen.FindChildren("*", "", owned: false))
                if (node.GetType().GetProperty("CardModel", BindingFlags.Public | BindingFlags.Instance)?.GetValue(node) is CardModel cm)
                    list.Add(cm);
            return list;
        }
        return new List<CardModel>();
    }

    private static string? SafeStr(Func<string?> f)
    {
        try { return f(); } catch { return null; }
    }

    private static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        for (int i = 0; i < 5; i++)
        {
            try
            {
                File.WriteAllText(tmp, text);
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (IOException) { System.Threading.Thread.Sleep(5); }
        }
    }
}

// ---------------------------------------------------------------------- CardSelectCmd option capture (live mode)

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHand))]
public static class LiveSelectFromHandPatch
{
    [HarmonyPrefix]
    public static void Prefix(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter)
        => LiveBridge.CaptureSelection(new LiveBridge.Selection { Method = "FromHand", Player = player, Filter = filter, Min = prefs.MinSelect, Max = prefs.MaxSelect });
}

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForUpgrade))]
public static class LiveSelectFromHandForUpgradePatch
{
    [HarmonyPrefix]
    public static void Prefix(Player player)
        => LiveBridge.CaptureSelection(new LiveBridge.Selection { Method = "FromHandForUpgrade", Player = player, Filter = c => c.IsUpgradable, Min = 1, Max = 1 });
}

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromCombatPile),
    new[] { typeof(PlayerChoiceContext), typeof(CardPile), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>) })]
public static class LiveSelectFromCombatPilePatch
{
    [HarmonyPrefix]
    public static void Prefix(CardPile pile, Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter)
        => LiveBridge.CaptureSelection(new LiveBridge.Selection { Method = "FromCombatPile:" + pile.Type, Player = player, Pile = pile, Filter = filter, Min = prefs.MinSelect, Max = prefs.MaxSelect });
}

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGrid))]
public static class LiveSelectFromSimpleGridPatch
{
    [HarmonyPrefix]
    public static void Prefix(IReadOnlyList<CardModel> cardsIn, Player player, CardSelectorPrefs prefs)
        => LiveBridge.CaptureSelection(new LiveBridge.Selection { Method = "FromSimpleGrid", Player = player, Cards = cardsIn.ToList(), Min = prefs.MinSelect, Max = prefs.MaxSelect });
}

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen))]
public static class LiveSelectFromChooseACardPatch
{
    [HarmonyPrefix]
    public static void Prefix(IReadOnlyList<CardModel> cards, Player player, bool canSkip)
        => LiveBridge.CaptureSelection(new LiveBridge.Selection { Method = "FromChooseACardScreen", Player = player, Cards = cards.ToList(), Min = canSkip ? 0 : 1, Max = 1, CanSkip = canSkip });
}

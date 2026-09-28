using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using RunReplays.Utils;

namespace RunReplays;

/// <summary>
/// Writes the current fight as a C:\sts-sim "fight snapshot" (FightExport.cs) to
/// %APPDATA%\SlayTheSpire2\RunReplays\live_fight.json whenever the player can act in combat, so an external planner
/// (sts-sim tools\live_advisor.py -> build\mcts.exe plan) can recommend moves. Atomic write (temp file + rename);
/// the file is only rewritten when the state changed. Controlled by RunReplaysConfig.ExportLiveFight.
/// </summary>
public static class LiveFightExporter
{
    private static readonly PropertyInfo? RunStateProp =
        typeof(RunManager).GetProperty("State", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static string? _lastFingerprint;
    private static string? _path;
    private static bool _loggedError;

    public static string OutputPath
    {
        get
        {
            if (_path != null) return _path;
            string dir;
            try { dir = Path.Combine(OS.GetUserDataDir(), "RunReplays"); }
            catch { dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "SlayTheSpire2", "RunReplays"); }
            _path = Path.Combine(dir, "live_fight.json");
            return _path;
        }
    }

    /// <summary>Called on SignalInputRequired and from the dispatch poll tick.</summary>
    public static void TryExport()
    {
        if (!RunReplaysConfig.ExportLiveFight) return;
        try
        {
            var state = RunStateProp?.GetValue(RunManager.Instance) as IRunState;
            Player? player = state?.Players.FirstOrDefault();
            if (player == null || !StsSim.FightExport.IsPlayerInputPoint(player)) return;
            string fp = Fingerprint(player);
            if (fp == _lastFingerprint) return;
            string? json = StsSim.FightExport.Export(player);
            if (json == null) return;
            WriteAtomic(OutputPath, json);
            _lastFingerprint = fp;
        }
        catch (Exception e)
        {
            if (!_loggedError)
            {
                _loggedError = true;
                DiagnosticLog.Write("LiveFightExport", "export failed: " + e);
            }
        }
    }

    // Cheap change detector: combat history length + the visible numbers that can change without a history entry.
    private static string Fingerprint(Player player)
    {
        var pcs = player.PlayerCombatState!;
        var cs = player.Creature.CombatState!;
        int entries = CombatManager.Instance?.History?.Entries?.Count() ?? 0;
        string hand = string.Join(",", pcs.Hand.Cards.Select(c => c.Id.Entry + c.CurrentUpgradeLevel));
        string pots = string.Join(",", player.PotionSlots.Select(p => p?.Id.Entry ?? "-"));
        string enemies = string.Join(",", cs.Enemies.Select(e => $"{e.CombatId}:{e.CurrentHp}:{e.Block}:{e.Powers.Count}:{e.Monster?.NextMove?.Id}"));
        return $"{cs.RoundNumber}|{pcs.TurnNumber}|{pcs.Energy}|{player.Creature.CurrentHp}|{player.Creature.Block}|{entries}|{hand}|{pots}|{enemies}|{pcs.DrawPile.Cards.Count}|{pcs.DiscardPile.Cards.Count}|{pcs.ExhaustPile.Cards.Count}";
    }

    private static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}

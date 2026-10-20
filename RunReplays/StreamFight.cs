using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace RunReplays;

/// <summary>
/// "Play a stream fight": a viewer pastes the code (or the whole link) the stream's site gives for a fight they watched,
/// and plays it in their own game from the save taken when that fight's room was entered.
///   GET https://chatplaysgames.com/v1/fights/{code}/package
///     -> { v, code, game, runId, floor, at, meta: { seed, character, ascension, act, encounter, roomType, gameVersion },
///          files: { "run.save": "...", "actions.sts2replay": "..." } }   (404: unknown code)
/// The files go to RunReplays/samples/{seed}/floor_{floor}/ (where they show up as a sample run too) and the save is
/// loaded as the menu's Load Save does. The player's own current_run.save is backed up first, to
/// RunReplays/backups/, and can be put back with Restore.
/// </summary>
public static class StreamFight
{
    const string Hub = "https://chatplaysgames.com";
    // the hub's code alphabet: digits 2-9 and letters without I, L, O, U
    static readonly Regex Code = new(@"^[2-9A-HJKMNP-TV-Z]{6}$", RegexOptions.Compiled);
    static readonly Regex Sep = new(@"[^A-Z0-9]+", RegexOptions.Compiled);
    static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static string BaseDir => Path.Combine(OS.GetUserDataDir(), "RunReplays");
    static string BackupDir => Path.Combine(BaseDir, "backups");

    /// <summary>The 6-character code in a pasted code or link (the last letters-and-digits piece in the code alphabet:
    /// ".../f/K7Q2XM", ".../f/k7q2xm/?ref=chat"), or null.</summary>
    public static string? ParseCode(string text)
    {
        var parts = Sep.Split((text ?? "").ToUpperInvariant());
        for (int i = parts.Length - 1; i >= 0; i--)
            if (Code.IsMatch(parts[i])) return parts[i];
        return null;
    }

    static string RunSavePath()
    {
        int profileId = SaveManager.Instance.CurrentProfileId;
        return ProjectSettings.GlobalizePath(UserDataPathProvider.GetProfileScopedPath(profileId, "saves/" + RunSaveManager.runSaveFileName));
    }

    static string GameVersion()
    {
        try { return ReleaseInfoManager.Instance?.ReleaseInfo?.Version ?? "unknown"; } catch { return "unknown"; }
    }

    static string Safe(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    /// <summary>Fetches the fight and loads it; returns a message for the player (an error, or null once it's loading).</summary>
    public static async Task<string?> LoadAsync(string pasted)
    {
        string? code = ParseCode(pasted);
        if (code == null) return "That doesn't look like a fight code: it's 6 letters and digits, e.g. K7Q2XM, or the whole link.";
        string json;
        try
        {
            using var r = await Http.GetAsync($"{Hub}/v1/fights/{code}/package");
            if ((int)r.StatusCode == 404) return $"No fight with the code {code}. Check the code, or copy the link again.";
            if (!r.IsSuccessStatusCode) return $"The stream's site answered {(int)r.StatusCode}. Try again in a moment.";
            json = await r.Content.ReadAsStringAsync();
        }
        catch (Exception e) { return "Couldn't reach chatplaysgames.com: " + e.Message; }

        string seed, character, version, save, actions;
        int floor, ascension;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var meta = root.GetProperty("meta");
            var files = root.GetProperty("files");
            floor = root.GetProperty("floor").GetInt32();
            seed = meta.GetProperty("seed").GetString() ?? "";
            character = meta.TryGetProperty("character", out var ch) ? ch.GetString() ?? "" : "";
            ascension = meta.TryGetProperty("ascension", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
            version = meta.TryGetProperty("gameVersion", out var gv) ? gv.GetString() ?? "" : "";
            save = files.GetProperty("run.save").GetString() ?? "";
            actions = files.TryGetProperty("actions.sts2replay", out var ac) ? ac.GetString() ?? "" : "";
        }
        catch (Exception e) { return "The fight's data couldn't be read: " + e.Message; }
        if (seed.Length == 0 || save.Length == 0) return "The fight's data is incomplete.";

        string mine = GameVersion();
        if (version.Length > 0 && mine != "unknown" && version != mine)
            return $"This fight was played on game version {version}; you have {mine}. Update the game (or switch its Steam branch) to play it.";

        // the player's own run: backed up before anything is overwritten
        try
        {
            string cur = RunSavePath();
            if (File.Exists(cur))
            {
                Directory.CreateDirectory(BackupDir);
                File.Copy(cur, Path.Combine(BackupDir, $"current_run.{DateTime.Now:yyyyMMdd_HHmmss}.save"), overwrite: true);
            }
        }
        catch (Exception e) { return "Couldn't back up your current run, so nothing was loaded: " + e.Message; }

        string dir = Path.Combine(BaseDir, "samples", Safe(seed), $"floor_{floor}");
        string savePath = Path.Combine(dir, "run.save"), actionsPath = Path.Combine(dir, "actions.sts2replay");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(savePath, save);
            File.WriteAllText(actionsPath, actions);
        }
        catch (Exception e) { return "Couldn't write the fight's files: " + e.Message; }

        GD.Print($"[RunReplays] stream fight {code}: seed {seed} floor {floor} -> {dir}");
        RunReplayMenu.LoadSampleSave(seed, character, floor, ascension, actionsPath, savePath);
        return null;
    }

    /// <summary>The newest backup of the player's own run, or null.</summary>
    public static string? LatestBackup()
    {
        try
        {
            return Directory.Exists(BackupDir)
                ? Directory.GetFiles(BackupDir, "current_run.*.save").OrderByDescending(f => f).FirstOrDefault()
                : null;
        }
        catch { return null; }
    }

    /// <summary>Puts the newest backup back as the profile's current_run.save (the main menu's Continue then loads it).</summary>
    public static string RestoreLatest()
    {
        string? b = LatestBackup();
        if (b == null) return "There's no backup to restore.";
        try
        {
            File.Copy(b, RunSavePath(), overwrite: true);
            return $"Your run from {Path.GetFileNameWithoutExtension(b).Replace("current_run.", "")} is back: use Continue on the main menu.";
        }
        catch (Exception e) { return "Couldn't restore: " + e.Message; }
    }
}

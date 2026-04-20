using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Godot;
using HarmonyLib;

namespace RunReplays.Utils;

/// <summary>
/// Always-on diagnostic log. Writes to
/// {UserDataDir}/RunReplays/diagnostic.log — one line per event, appended
/// across sessions until the file exceeds MaxBytes, at which point it rolls.
///
/// Unlike RngLog (gated behind a const flag) and the Conditional
/// LogToDevConsole / LogDispatcher / LogMigrationWarning helpers, this writes
/// unconditionally so users can attach the file to bug reports without having
/// to rebuild the mod.
///
/// Keep call sites cheap — format once, write once. No heavy reflection in
/// hot paths.
/// </summary>
internal static class DiagnosticLog
{
    private const long MaxBytes = 5 * 1024 * 1024;   // 5 MB before roll
    private const string FileName = "diagnostic.log";
    private const string RolledFileName = "diagnostic.prev.log";

    private static readonly object _lock = new();
    private static StreamWriter? _writer;
    private static string? _path;
    private static bool _sessionHeaderWritten;

    internal static string? Path => _path;

    private static void Ensure()
    {
        if (_writer != null) return;

        try
        {
            string dir = System.IO.Path.Combine(OS.GetUserDataDir(), "RunReplays");
            Directory.CreateDirectory(dir);

            string path = System.IO.Path.Combine(dir, FileName);

            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.Length > MaxBytes)
                {
                    string rolled = System.IO.Path.Combine(dir, RolledFileName);
                    if (File.Exists(rolled)) File.Delete(rolled);
                    File.Move(path, rolled);
                }
            }

            _path = path;
            _writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RunReplays:DiagnosticLog] Failed to open log: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes a single timestamped line. Tag should identify the subsystem
    /// (e.g. "Startup", "RunStart", "Dispatch", "Save", "Harmony").
    /// </summary>
    internal static void Write(string tag, string message)
    {
        lock (_lock)
        {
            Ensure();
            if (_writer == null) return;

            if (!_sessionHeaderWritten)
            {
                _sessionHeaderWritten = true;
                _writer.WriteLine();
                _writer.WriteLine($"=== RunReplays session @ {DateTime.Now:O} ===");
            }

            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            _writer.WriteLine($"{ts} [{tag}] {message}");
        }
    }

    /// <summary>
    /// Also mirrors the line to the in-game dev console so diagnostics are
    /// visible in real-time, not only in the file.
    /// </summary>
    internal static void WriteAndEcho(string tag, string message)
    {
        Write(tag, message);
        string line = $"[{tag}] {message}";
        GD.Print(line);
        PlayerActionBuffer.ForceLogToDevConsole(line);
    }

    /// <summary>
    /// Writes a multi-line block under a single tag (e.g. a harmony patch
    /// roster or startup fingerprint). Each line gets its own timestamp so
    /// interleaved events stay readable.
    /// </summary>
    internal static void WriteBlock(string tag, IEnumerable<string> lines)
    {
        foreach (var line in lines) Write(tag, line);
    }

    /// <summary>
    /// One-time startup fingerprint: mod version, sts2.dll path + timestamp,
    /// user-data dir, and a roster of all [HarmonyPatch] classes so we can
    /// tell whether patches applied (or silently failed after the API rename).
    /// </summary>
    internal static void WriteStartupFingerprint()
    {
        try
        {
            var modAssembly = Assembly.GetExecutingAssembly();

            Write("Startup", $"mod version={ModVersion.Current}");
            Write("Startup", $"mod assembly={modAssembly.Location}");
            Write("Startup", $"userDataDir={OS.GetUserDataDir()}");
            Write("Startup", $"diagnosticLog={_path}");

            // sts2.dll fingerprint — lets us correlate issues with specific
            // game builds when the game updates.
            try
            {
                var sts2 = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var asm in sts2)
                {
                    var name = asm.GetName().Name;
                    if (name == "sts2")
                    {
                        string loc = asm.Location;
                        Write("Startup", $"sts2.dll={loc}");
                        if (File.Exists(loc))
                        {
                            var info = new FileInfo(loc);
                            Write("Startup",
                                $"sts2.dll size={info.Length} writeTime={info.LastWriteTime:O}");
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Write("Startup", $"sts2 fingerprint error: {ex.Message}");
            }

            // Dump registered Harmony patches so we can see what applied.
            try
            {
                var allPatched = Harmony.GetAllPatchedMethods();
                int count = 0;
                foreach (var m in allPatched)
                {
                    count++;
                    var info = Harmony.GetPatchInfo(m);
                    if (info == null) continue;
                    string owners = string.Join(",", info.Owners);
                    Write("Harmony",
                        $"patched {m.DeclaringType?.FullName}.{m.Name} by [{owners}] " +
                        $"prefix={info.Prefixes.Count} postfix={info.Postfixes.Count} " +
                        $"transpiler={info.Transpilers.Count} finalizer={info.Finalizers.Count}");
                }
                Write("Harmony", $"total patched methods={count}");
            }
            catch (Exception ex)
            {
                Write("Harmony", $"patch enumeration error: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Write("Startup", $"fingerprint error: {ex.Message}");
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Debug;

namespace RunReplays;

/// <summary>
/// Accumulates recordable player actions for the duration of a run.
///
/// A new ActionExecutor is constructed at the start of every run, so its
/// constructor is the right place to both clear the previous run's buffer
/// and subscribe to AfterActionExecuted for the new one.
///
/// The buffer is never drained between saves — each save log is a full
/// snapshot of all actions from run-start to that save point.
/// </summary>
[HarmonyPatch(typeof(ActionExecutor), MethodType.Constructor, new[] { typeof(ActionQueueSet) })]
public static class PlayerActionBuffer
{
    // Thread-safe: AfterActionExecuted fires from async action execution.
    private static readonly ConcurrentQueue<string> _entries = new();

    [HarmonyPostfix]
    public static void Postfix(ActionExecutor __instance)
    {
        // Clear the previous run's actions whenever a new executor is created.
        while (_entries.TryDequeue(out _)) { }

        __instance.AfterActionExecuted += action =>
        {
            if (!action.RecordableToReplay)
                return;

            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string entry = $"[{timestamp}] {action}";
            _entries.Enqueue(entry);
            LogToDevConsole(entry);
        };
    }

    /// <summary>
    /// Returns a point-in-time copy of all recorded entries without clearing
    /// the buffer, so subsequent saves still include the full history.
    /// </summary>
    public static IReadOnlyList<string> Snapshot()
    {
        return new List<string>(_entries);
    }

    // Reflected once; null until the field is found.
    private static readonly FieldInfo? _instanceField =
        typeof(NDevConsole).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);

    private static void LogToDevConsole(string entry)
    {
        // Check the backing field directly to avoid the InvalidOperationException
        // that NDevConsole.Instance throws when the console hasn't been created yet.
        if (_instanceField?.GetValue(null) is not NDevConsole console)
            return;

        // AfterActionExecuted fires from async context — defer the UI write to
        // the Godot main thread to avoid cross-thread node access.
        var outputBuffer = console.GetNode<RichTextLabel>("OutputContainer/OutputBuffer");
        outputBuffer.CallDeferred(RichTextLabel.MethodName.AppendText, entry + "\n");
    }
}

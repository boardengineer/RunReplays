using System.IO;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace RunReplays.Utils;

/// <summary>
/// Tracks which Rng instance is the UpFront RNG and logs every call to it
/// between CreateForNewRun and GenerateRooms, to find what consumes RNG
/// non-deterministically before encounter generation.
/// </summary>
public static class UpFrontRngTracker
{
    internal static Rng? TrackedRng;
    internal static bool Active;

    internal static void LogCall(string method)
    {
        if (!Active || TrackedRng == null) return;
        var stackTrace = new System.Diagnostics.StackTrace(2, true);
        var msg = $"[RngTracker] UpFront.{method} (counter={TrackedRng.Counter})\n{stackTrace}";
        RngLog.Write(msg);
    }
}

/// <summary>
/// Simple file logger for RNG tracking output.
/// Writes to rng_tracker_TIMESTAMP.log next to the mod DLL.
/// </summary>
internal static class RngLog
{
    private static StreamWriter? _writer;
    private static readonly object _lock = new();

    internal static void Reset()
    {
        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
            EnsureInitialized();
        }
    }

    internal static void EnsureInitialized()
    {
        lock (_lock)
        {
            if (_writer != null) return;
            var dir = Path.GetDirectoryName(typeof(RngLog).Assembly.Location) ?? ".";
            var path = Path.Combine(dir, $"rng_tracker_{System.DateTime.Now:yyyyMMdd_HHmmss}.log");
            _writer = new StreamWriter(path, append: false) { AutoFlush = true };
            _writer.WriteLine($"=== RNG Tracker Log — {System.DateTime.Now:O} ===");
        }
    }

    internal static void Write(string message)
    {
        lock (_lock)
        {
            _writer?.WriteLine(message);
        }
    }
}

/// <summary>
/// Track GetRandomList to see what seed and unlockState it receives.
/// NOTE: Do NOT iterate __result in postfix — it's a lazy IEnumerable
/// and consuming it would break the caller.
/// </summary>
[HarmonyPatch(typeof(ActModel), nameof(ActModel.GetRandomList))]
public static class GetRandomListTracker
{
    [HarmonyPrefix]
    public static void Prefix(string seed, UnlockState unlockState, bool isMultiplayer)
    {
        RngLog.EnsureInitialized();
        bool isAll = ReferenceEquals(unlockState, UnlockState.all);
        var msg = $"[EncounterTracker] GetRandomList called — seed='{seed}', isMultiplayer={isMultiplayer}, unlockState.isAll={isAll}, epochCount={unlockState.EpochUnlockCount()}";
        RngLog.Write(msg);
    }
}

/// <summary>
/// Override acts in NGame.StartNewSingleplayerRun to use deterministic act list
/// from GetRandomList with forced seed and UnlockState.all.
/// </summary>
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.NGame), nameof(MegaCrit.Sts2.Core.Nodes.NGame.StartNewSingleplayerRun))]
public static class StartRunActOverride
{
    [HarmonyPrefix]
    public static void Prefix(ref System.Collections.Generic.IReadOnlyList<ActModel> acts, ref string seed)
    {
        RngLog.EnsureInitialized();

        // Log original acts
        var origIds = new System.Collections.Generic.List<string>();
        foreach (var a in acts) origIds.Add(a.Id.ToString());
        string mode = ReplayEngine.IsActive ? "REPLAY" : "RECORD";
        var origMsg = $"[EncounterTracker] NGame.StartNewSingleplayerRun [{mode}] original — seed='{seed}', acts=[{string.Join(", ", origIds)}]";
        RngLog.Write(origMsg);

        // Use the active replay/save seed if available, otherwise the forced seed.
        string? activeSeed = ReplayEngine.ActiveSeed;
        if (activeSeed == null && !ForcedSeedPatch.Enabled) return;

        string useSeed = activeSeed ?? ForcedSeedPatch.ForcedSeed;
        ReplayEngine.ActiveSeed = null;
        seed = useSeed;

        // Override acts with deterministic list
        var newActs = new System.Collections.Generic.List<ActModel>(
            ActModel.GetRandomList(useSeed, UnlockState.all, false));
        acts = newActs;

        var msg = $"[EncounterTracker] NGame.StartNewSingleplayerRun [{mode}] overridden — seed='{seed}', acts=[{string.Join(", ", newActs.ConvertAll(a => a.Id.ToString()))}]";
        RngLog.Write(msg);
    }
}

/// <summary>
/// After CreateForNewRun, capture the UpFront Rng and start tracking.
/// </summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
public static class CreateForNewRunTracker
{
    [HarmonyPostfix]
    public static void Postfix(RunState __result)
    {
        RngLog.Reset();
        string mode = ReplayEngine.IsActive ? "REPLAY" : "RECORD";
        UpFrontRngTracker.TrackedRng = __result.Rng.UpFront;
        UpFrontRngTracker.Active = true;
        var msg = $"[RngTracker] CreateForNewRun done [{mode}] — UpFront seed={__result.Rng.UpFront.Seed}, counter={__result.Rng.UpFront.Counter}";
        RngLog.Write(msg);
    }
}

/// <summary>
/// Logs RNG state on entry to GenerateRooms and stops pre-GenerateRooms tracking.
/// After GenerateRooms, logs the full encounter list.
/// </summary>
[HarmonyPatch(typeof(ActModel), nameof(ActModel.GenerateRooms))]
public static class GenerateRoomsTracker
{
    [HarmonyPrefix]
    public static void Prefix(ActModel __instance, Rng rng, UnlockState unlockState, bool isMultiplayer)
    {
        string mode = ReplayEngine.IsActive ? "REPLAY" : "RECORD";
        var msg = $"[EncounterTracker] GenerateRooms called [{mode}] — RNG seed={rng.Seed}, counter={rng.Counter}, multiplayer={isMultiplayer}";
        RngLog.Write(msg);

        // Stop pre-GenerateRooms tracking to avoid noise from the generation itself
        UpFrontRngTracker.Active = false;
    }

    [HarmonyPostfix]
    public static void Postfix(ActModel __instance)
    {
        var roomsField = typeof(ActModel).GetField("_rooms", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        if (roomsField?.GetValue(__instance) is not RoomSet rooms)
        {
            var msg0 = "[EncounterTracker] GenerateRooms done — could not read _rooms";
            RngLog.Write(msg0);
            return;
        }

        var summary = $"[EncounterTracker] GenerateRooms done — {rooms.normalEncounters.Count} normal, {rooms.eliteEncounters.Count} elite encounters";
        RngLog.Write(summary);

        for (int i = 0; i < rooms.normalEncounters.Count; i++)
        {
            var line = $"[EncounterTracker]   Normal[{i}]: {rooms.normalEncounters[i].Id}";
            RngLog.Write(line);
        }
        for (int i = 0; i < rooms.eliteEncounters.Count; i++)
        {
            var line = $"[EncounterTracker]   Elite[{i}]: {rooms.eliteEncounters[i].Id}";
            RngLog.Write(line);
        }
    }
}

[HarmonyPatch(typeof(ActModel), nameof(ActModel.PullNextEncounter))]
public static class PullNextEncounterTracker
{
    [HarmonyPostfix]
    public static void Postfix(ActModel __instance, RoomType roomType, EncounterModel __result)
    {
        string mode = ReplayEngine.IsActive ? "REPLAY" : "RECORD";
        string id = __result?.Id.ToString() ?? "(null)";
        var msg = $"[EncounterTracker] PullNextEncounter [{mode}] ({roomType}) => '{id}'";
        RngLog.Write(msg);
    }
}

[HarmonyPatch(typeof(ActModel), "ApplyDiscoveryOrderModifications")]
public static class DiscoveryOrderTracker
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        var msg = "[EncounterTracker] ApplyDiscoveryOrderModifications called";
        RngLog.Write(msg);
    }
}

/// <summary>
/// Track RollRoomTypeFor to see what room types the map assigns.
/// </summary>
[HarmonyPatch(typeof(RunManager), "RollRoomTypeFor")]
public static class RollRoomTypeTracker
{
    [HarmonyPostfix]
    public static void Postfix(MegaCrit.Sts2.Core.Map.MapPointType pointType, RoomType __result)
    {
        string mode = ReplayEngine.IsActive ? "REPLAY" : "RECORD";
        var msg = $"[EncounterTracker] RollRoomTypeFor [{mode}] ({pointType}) => {__result}";
        RngLog.Write(msg);
    }
}

/// <summary>
/// Track CreateMap to see when map generation happens and what RNG state it uses.
/// </summary>
[HarmonyPatch(typeof(ActModel), nameof(ActModel.CreateMap))]
public static class CreateMapTracker
{
    [HarmonyPrefix]
    public static void Prefix(ActModel __instance, RunState runState)
    {
        var rng = runState.Rng.UpFront;
        var msg = $"[EncounterTracker] CreateMap called — UpFront counter={rng.Counter}";
        RngLog.Write(msg);
    }
}

/// <summary>
/// Intercepts all Rng.NextInt / NextBool calls. When the instance matches
/// the UpFront Rng and tracking is active, logs with full stack trace.
/// </summary>
[HarmonyPatch(typeof(Rng))]
public static class RngCallTracker
{
    [HarmonyPatch(nameof(Rng.NextInt), new[] { typeof(int) })]
    [HarmonyPrefix]
    public static void NextIntPrefix(Rng __instance, int maxExclusive)
    {
        if (!UpFrontRngTracker.Active) return;
        if (!ReferenceEquals(__instance, UpFrontRngTracker.TrackedRng)) return;
        UpFrontRngTracker.LogCall($"NextInt({maxExclusive})");
    }

    [HarmonyPatch(nameof(Rng.NextInt), new[] { typeof(int), typeof(int) })]
    [HarmonyPrefix]
    public static void NextIntRangePrefix(Rng __instance, int minInclusive, int maxExclusive)
    {
        if (!UpFrontRngTracker.Active) return;
        if (!ReferenceEquals(__instance, UpFrontRngTracker.TrackedRng)) return;
        UpFrontRngTracker.LogCall($"NextInt({minInclusive},{maxExclusive})");
    }

    [HarmonyPatch(nameof(Rng.NextBool))]
    [HarmonyPrefix]
    public static void NextBoolPrefix(Rng __instance)
    {
        if (!UpFrontRngTracker.Active) return;
        if (!ReferenceEquals(__instance, UpFrontRngTracker.TrackedRng)) return;
        UpFrontRngTracker.LogCall("NextBool()");
    }

    [HarmonyPatch(nameof(Rng.NextFloat), new[] { typeof(float) })]
    [HarmonyPrefix]
    public static void NextFloatPrefix(Rng __instance, float max)
    {
        if (!UpFrontRngTracker.Active) return;
        if (!ReferenceEquals(__instance, UpFrontRngTracker.TrackedRng)) return;
        UpFrontRngTracker.LogCall($"NextFloat({max})");
    }

    [HarmonyPatch(nameof(Rng.NextFloat), new[] { typeof(float), typeof(float) })]
    [HarmonyPrefix]
    public static void NextFloatRangePrefix(Rng __instance, float min, float max)
    {
        if (!UpFrontRngTracker.Active) return;
        if (!ReferenceEquals(__instance, UpFrontRngTracker.TrackedRng)) return;
        UpFrontRngTracker.LogCall($"NextFloat({min},{max})");
    }

    [HarmonyPatch(nameof(Rng.NextDouble), new System.Type[] {})]
    [HarmonyPrefix]
    public static void NextDoublePrefix(Rng __instance)
    {
        if (!UpFrontRngTracker.Active) return;
        if (!ReferenceEquals(__instance, UpFrontRngTracker.TrackedRng)) return;
        UpFrontRngTracker.LogCall("NextDouble()");
    }
}

using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace RunReplays.Patches;
using RunReplays;

/// <summary>
/// Crystal Sphere minigame recording and replay support.
///
/// These patches are applied manually (not via [HarmonyPatch]) to avoid
/// crashing PatchAll() if the Crystal Sphere types cannot be resolved.
/// Call <see cref="CrystalSphereManualPatcher.Apply"/> after PatchAll().
/// </summary>
public static class CrystalSphereManualPatcher
{
    private static bool _applied;

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;

        try
        {
            var harmony = new Harmony("RunReplays.CrystalSphere");

            var minigameType = AccessTools.TypeByName(
                "MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame");
            var screenType = AccessTools.TypeByName(
                "MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen");

            PlayerActionBuffer.LogToDevConsole($"[CrystalSphere] minigameType={minigameType?.FullName ?? "NULL"} screenType={screenType?.FullName ?? "NULL"}");

            if (minigameType != null)
            {
                var cellClicked = AccessTools.Method(minigameType, "CellClicked");
                PlayerActionBuffer.LogToDevConsole($"[CrystalSphere] CellClicked={cellClicked?.Name ?? "NULL"}");
                if (cellClicked != null)
                {
                    var prefix = new HarmonyMethod(
                        typeof(CrystalSphereCellClickedPatch),
                        nameof(CrystalSphereCellClickedPatch.Prefix));
                    harmony.Patch(cellClicked, prefix: prefix);
                    PlayerActionBuffer.LogToDevConsole("[CrystalSphere] Patched CellClicked OK.");
                }
            }

            if (screenType != null)
            {
                var afterOpened = AccessTools.Method(screenType, "AfterOverlayOpened");
                PlayerActionBuffer.LogToDevConsole($"[CrystalSphere] AfterOverlayOpened={afterOpened?.Name ?? "NULL"}");
                if (afterOpened != null)
                {
                    var postfix = new HarmonyMethod(
                        typeof(CrystalSphereReplayPatch),
                        nameof(CrystalSphereReplayPatch.Postfix));
                    harmony.Patch(afterOpened, postfix: postfix);
                    PlayerActionBuffer.LogToDevConsole("[CrystalSphere] Patched AfterOverlayOpened OK.");
                }
            }
        }
        catch (Exception ex)
        {
            PlayerActionBuffer.LogToDevConsole($"[CrystalSphere] Manual patching FAILED: {ex}");
        }
    }
}

// ── Recording + tool override ────────────────────────────────────────────────

public static class CrystalSphereCellClickedPatch
{
    public static void Prefix(object __instance, object __0)
    {
        if (CrystalSphereReplayPatch.PendingTool.HasValue)
        {
            int tool = CrystalSphereReplayPatch.PendingTool.Value;
            CrystalSphereReplayPatch.PendingTool = null;

            var prop = AccessTools.Property(__instance.GetType(), "CrystalSphereTool");
            if (prop != null)
                prop.SetValue(__instance, Enum.ToObject(prop.PropertyType, tool));
        }

        if (ReplayEngine.IsActive)
            return;

        int toolVal = Convert.ToInt32(
            Traverse.Create(__instance).Property("CrystalSphereTool").GetValue());
        int x = Traverse.Create(__0).Property("X").GetValue<int>();
        int y = Traverse.Create(__0).Property("Y").GetValue<int>();

        PlayerActionBuffer.Record($"CrystalSphereClick {x} {y} {toolVal}");
    }
}

// ── Replay: auto-click cells then proceed ────────────────────────────────────

public static class CrystalSphereReplayPatch
{
    internal static int? PendingTool;

    internal static GodotObject? ActiveScreen;
    internal static MethodInfo? OnCellClicked;
    private static MethodInfo? _onProceedButtonPressed;
    private static Type? _screenType;
    private static Type? _cellNodeType;

    internal static void EnsureReflection()
    {
        if (_screenType != null) return;

        _screenType = AccessTools.TypeByName(
            "MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen");
        _cellNodeType = AccessTools.TypeByName(
            "MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereCell");

        if (_screenType != null)
        {
            OnCellClicked = AccessTools.Method(_screenType, "OnCellClicked");
            _onProceedButtonPressed = AccessTools.Method(_screenType, "OnProceedButtonPressed");
        }
    }

    public static void Postfix(object __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        ActiveScreen = (GodotObject)__instance;
        EnsureReflection();
        ReplayDispatcher.TryDispatch();

        PlayerActionBuffer.LogToDevConsole(
            "[CrystalSphereReplayPatch] Screen opened — dispatching clicks.");
        ReplayDispatcher.DispatchNow();
    }

    internal static GodotObject? FindCell(Node? container, int x, int y)
    {
        if (container == null) return null;

        foreach (var child in container.GetChildren())
        {
            if (_cellNodeType != null && !_cellNodeType.IsInstanceOfType(child))
                continue;

            try
            {
                var entity = Traverse.Create(child).Property("Entity").GetValue();
                if (entity == null) continue;

                int cx = Traverse.Create(entity).Property("X").GetValue<int>();
                int cy = Traverse.Create(entity).Property("Y").GetValue<int>();
                if (cx == x && cy == y)
                    return (GodotObject)child;
            }
            catch { }
        }
        return null;
    }

    private const int MaxProceedRetries = 30;

    internal static void ScheduleProceed(GodotObject screen)
    {
        NGame.Instance!.GetTree()!.CreateTimer(1.0).Connect(
            "timeout", Callable.From(() => WaitForProceed(screen)));
    }

    private static void WaitForProceed(GodotObject screen, int retriesLeft = MaxProceedRetries)
    {
        if (!GodotObject.IsInstanceValid(screen))
            return;

        if (retriesLeft <= 0)
        {
            PlayerActionBuffer.LogToDevConsole(
                "[CrystalSphereReplayPatch] Gave up waiting for proceed button.");
            return;
        }

        var proceedBtn = Traverse.Create(screen).Field("_proceedButton").GetValue<Control>();
        if (proceedBtn != null)
        {
            var isEnabledProp = proceedBtn.GetType().GetProperty("IsEnabled");
            bool isEnabled = isEnabledProp != null
                && (bool)isEnabledProp.GetValue(proceedBtn)!;

            if (isEnabled)
            {
                PlayerActionBuffer.LogToDevConsole(
                    "[CrystalSphereReplayPatch] Clicking proceed button.");
                _onProceedButtonPressed?.Invoke(screen, Array.Empty<object>());
                return;
            }
        }

        NGame.Instance!.GetTree()!.CreateTimer(0.5).Connect(
            "timeout", Callable.From(() => WaitForProceed(screen, retriesLeft - 1)));
    }
}

using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace RunReplays;

/// <summary>
/// A lightweight CanvasLayer overlay shown during a run.
///
/// Recording mode: displays the 5 most recent lines added to the action log.
/// Replay mode:    displays the current pending command flanked by the 2 most
///                 recently consumed commands above it and the 2 next-up commands
///                 below it.  The current command is full-brightness; the rest
///                 are dimmed.
///
/// Created (deferred) whenever a new ActionExecutor is constructed (run start)
/// and destroyed automatically when NGame cleans up the scene.
/// </summary>
internal static class RunOverlay
{
    private const int LineCount  = 5;
    private const int PanelWidth = 480;
    private const int FontSize   = 11;

    private static CanvasLayer? _canvas;
    private static Label?       _titleLabel;
    private static Label[]      _lineLabels = new Label[LineCount];

    /// <summary>
    /// Controls whether the overlay is visible. Persists across runs within
    /// the same session. Toggled from the Run Replays menu.
    /// </summary>
    internal static bool OverlayVisible
    {
        get => _overlayVisible;
        set
        {
            _overlayVisible = value;
            if (_canvas != null && GodotObject.IsInstanceValid(_canvas))
                _canvas.Visible = value;
        }
    }
    private static bool _overlayVisible = true;

    // Rolling buffer of the last LineCount recorded entries (recording mode).
    private static readonly Queue<string> _recentEntries = new();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Called after a continued run restores the action buffer so the overlay
    /// immediately shows the last entries instead of appearing empty.
    /// </summary>
    internal static void RestoreRecentEntries(IReadOnlyList<string> allEntries)
    {
        _recentEntries.Clear();
        int start = Math.Max(0, allEntries.Count - LineCount);
        for (int i = start; i < allEntries.Count; i++)
            _recentEntries.Enqueue(allEntries[i]);

        if (_canvas != null && GodotObject.IsInstanceValid(_canvas))
            Callable.From(RefreshDisplay).CallDeferred();
    }

    internal static void InitForRun()
    {
        // Unsubscribe stale handlers from the previous run.
        PlayerActionBuffer.EntryRecorded -= OnEntryRecorded;
        ReplayEngine.ContextChanged      -= OnContextChanged;

        // Subscribe fresh for this run.
        PlayerActionBuffer.EntryRecorded += OnEntryRecorded;
        ReplayEngine.ContextChanged      += OnContextChanged;

        _recentEntries.Clear();

        // Build the node tree on the main thread (we may be in a constructor postfix).
        Callable.From(BuildOverlay).CallDeferred();
    }

    private static void BuildOverlay()
    {
        // Destroy the overlay from the previous run (if still alive).
        if (_canvas != null && GodotObject.IsInstanceValid(_canvas))
            _canvas.QueueFree();
        _canvas = null;

        if (NGame.Instance == null)
            return;

        // ── CanvasLayer (always on top) ───────────────────────────────────────
        _canvas = new CanvasLayer();
        _canvas.Layer = 64;
        _canvas.Visible = _overlayVisible;
        NGame.Instance.AddChild(_canvas);

        // Full-rect control so child anchors work relative to the viewport.
        var root = new Control();
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.MouseFilter = Control.MouseFilterEnum.Ignore;
        _canvas.AddChild(root);

        // ── Panel anchored to the top-right corner ────────────────────────────
        var panel = new PanelContainer();
        panel.AnchorLeft   = 1f;
        panel.AnchorRight  = 1f;
        panel.AnchorTop    = 0f;
        panel.AnchorBottom = 0f;
        panel.OffsetLeft   = -(PanelWidth + 8);
        panel.OffsetRight  = -8;
        panel.OffsetTop    = 140;
        panel.GrowVertical = Control.GrowDirection.End;
        root.AddChild(panel);

        // ── Inner layout ──────────────────────────────────────────────────────
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 1);
        panel.AddChild(vbox);

        _titleLabel = new Label();
        _titleLabel.AddThemeFontSizeOverride("font_size", FontSize + 1);
        vbox.AddChild(_titleLabel);

        vbox.AddChild(new HSeparator());

        _lineLabels = new Label[LineCount];
        for (int i = 0; i < LineCount; i++)
        {
            var lbl = new Label();
            lbl.AddThemeFontSizeOverride("font_size", FontSize);
            lbl.ClipText           = true;
            lbl.CustomMinimumSize  = new Vector2(PanelWidth - 16, 0);
            lbl.AutowrapMode       = TextServer.AutowrapMode.Off;
            _lineLabels[i]         = lbl;
            vbox.AddChild(lbl);
        }

        RefreshDisplay();
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private static void OnEntryRecorded(string text)
    {
        _recentEntries.Enqueue(text);
        while (_recentEntries.Count > LineCount)
            _recentEntries.Dequeue();

        if (_canvas != null && GodotObject.IsInstanceValid(_canvas))
            Callable.From(RefreshDisplay).CallDeferred();
    }

    private static void OnContextChanged()
    {
        if (_canvas != null && GodotObject.IsInstanceValid(_canvas))
            Callable.From(RefreshDisplay).CallDeferred();
    }

    // ── Display refresh ───────────────────────────────────────────────────────

    private static void RefreshDisplay()
    {
        if (_canvas == null || !GodotObject.IsInstanceValid(_canvas))
            return;

        if (ReplayEngine.IsActive)
            RefreshReplay();
        else
            RefreshRecording();
    }

    private static void RefreshRecording()
    {
        if (_titleLabel != null)
            _titleLabel.Text = "● REC";

        string[] entries = _recentEntries.ToArray();
        for (int i = 0; i < LineCount; i++)
        {
            var lbl = _lineLabels[i];
            if (lbl == null) continue;

            int entryIdx = entries.Length - LineCount + i;
            if (entryIdx >= 0 && entryIdx < entries.Length)
            {
                lbl.Text     = Truncate(entries[entryIdx]);
                lbl.Modulate = i == LineCount - 1
                    ? Colors.White
                    : new Color(1f, 1f, 1f, 0.45f);
            }
            else
            {
                lbl.Text     = string.Empty;
                lbl.Modulate = Colors.White;
            }
        }
    }

    // Colors for replay overlay status.
    private static readonly Color CompletedColor  = new(0.4f, 1f, 0.4f, 0.7f);   // green
    private static readonly Color InProgressColor = new(1f, 1f, 0.3f, 1f);        // yellow
    private static readonly Color PendingColor    = new(1f, 1f, 1f, 0.45f);       // dimmed white

    private static void RefreshReplay()
    {
        if (_titleLabel != null)
            _titleLabel.Text = "▶ REPLAY";

        ReplayEngine.GetReplayContext(out var prev, out string? current, out var next);

        // 5 display slots: [prev-2, prev-1, current, next+1, next+2]
        string?[] slots = new string?[LineCount];
        slots[0] = prev.Count >= 2 ? prev[0] : null;
        slots[1] = prev.Count >= 1 ? prev[prev.Count - 1] : null;
        slots[2] = current;
        slots[3] = next.Count >= 1 ? next[0] : null;
        slots[4] = next.Count >= 2 ? next[1] : null;

        for (int i = 0; i < LineCount; i++)
        {
            var lbl = _lineLabels[i];
            if (lbl == null) continue;

            lbl.Text = slots[i] != null ? Truncate(slots[i]!) : string.Empty;
            lbl.Modulate = i < 2  ? CompletedColor    // completed (consumed)
                         : i == 2 ? InProgressColor    // current (in progress)
                         :          PendingColor;       // pending (upcoming)
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Truncate(string s) =>
        s.Length <= 68 ? s : s[..65] + "...";
}

using System;
using System.Collections.Generic;
using BaseLib.Config;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

using RunReplays.Commands;
using RunReplays.Patches;
using RunReplays.Patches.Replay;
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
    private const int PanelWidth = 435;
    private const int FontSize   = 11;

    // STS2 color palette (from StsColors.cs / top_bar.tscn / vertical_popup.tscn).
    private static readonly Color CreamColor      = new(1f, 0.965f, 0.886f, 1f);         // #FFF6E2
    private static readonly Color GoldColor       = new(0.937f, 0.784f, 0.318f, 1f);     // #EFC851
    private static readonly Color PanelBg         = new(0.05f, 0.04f, 0.03f, 0.88f);     // fallback
    private static readonly Color ButtonBg        = new(0.15f, 0.10f, 0.06f, 0.9f);      // dark brown
    private static readonly Color ButtonHl        = new(0.25f, 0.18f, 0.10f, 0.9f);      // lighter brown
    private static readonly Color SepColor        = new(0.937f, 0.784f, 0.318f, 0.3f);   // gold dimmed
    private static readonly Color ShadowColor     = new(0f, 0f, 0f, 0.125f);             // text shadow
    private static readonly Color OutlineColor    = new(0.098f, 0.161f, 0.188f, 1f);     // dark blue-grey outline
    private static readonly Color BtnOutlineColor = new(0.35f, 0.07f, 0f, 1f);           // dark brown outline

    private static CanvasLayer? _canvas;
    private static Label?       _titleLabel;
    private static Label[]      _lineLabels = new Label[LineCount];

    // Control bar elements (replay mode only).
    private static Control?  _controlBar;
    private static Button?   _pauseButton;
    private static Button?   _stepButton;
    private static Button?   _stopButton;
    private static Label?    _speedLabel;

    /// <summary>
    /// Controls whether the overlay is visible. Backed by <see cref="RunReplaysConfig.ShowReplayOverlay"/>
    /// so it persists across sessions. Toggled from the Run Replays menu.
    /// </summary>
    internal static bool OverlayVisible
    {
        get => RunReplaysConfig.ShowReplayOverlay;
        set
        {
            RunReplaysConfig.ShowReplayOverlay = value;
            if (_canvas != null && GodotObject.IsInstanceValid(_canvas))
                _canvas.Visible = value;
            ModConfig.SaveDebounced<RunReplaysConfig>();
        }
    }

    // Rolling buffer of the last LineCount recorded entries (recording mode).
    private static readonly Queue<string> _recentEntries = new();

    /// <summary>
    /// True while a card play command has been consumed but the PlayCardAction
    /// hasn't completed yet.  Set by NotifyCardPlayStarted, cleared by
    /// NotifyCardPlayFinished.  Used to show the last consumed command as
    /// yellow (in progress) instead of green (completed).
    /// </summary>
    private static bool _cardPlayInProgress;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Hides the overlay canvas while in the main menu without changing the saved config.</summary>
    internal static void HideForMainMenu()
    {
        if (_canvas != null && GodotObject.IsInstanceValid(_canvas))
            _canvas.Visible = false;
    }

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
        _canvas.Visible = RunReplaysConfig.ShowReplayOverlay;
        NGame.Instance.AddChild(_canvas);

        // Full-rect control so child anchors work relative to the viewport.
        var root = new Control();
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.MouseFilter = Control.MouseFilterEnum.Ignore;
        _canvas.AddChild(root);


        // ── Load STS2 fonts ──────────────────────────────────────────────────
        Font? fontBold = GD.Load<Font>("res://themes/kreon_bold.ttf");
        Font? fontRegular = GD.Load<Font>("res://themes/kreon_regular.ttf");

        // ── Panel anchored to the top-right corner ────────────────────────────
        var panel = new PanelContainer();
        panel.AnchorLeft   = 1f;
        panel.AnchorRight  = 1f;
        panel.AnchorTop    = 0f;
        panel.AnchorBottom = 0f;
        panel.OffsetLeft   = -(PanelWidth + 8);
        panel.OffsetRight  = -8;
        panel.OffsetTop    = 160;
        panel.GrowVertical = Control.GrowDirection.End;

        // Use the game's top bar texture as panel background.
        var topBarTex = GD.Load<Texture2D>("res://images/atlases/ui_atlas.sprites/top_bar/top_bar.tres");
        if (topBarTex != null)
        {
            var texStyle = new StyleBoxTexture();
            texStyle.Texture = topBarTex;
            texStyle.SetContentMarginAll(8);
            texStyle.ContentMarginBottom = 55;
            panel.AddThemeStyleboxOverride("panel", texStyle);
        }
        else
        {
            var flatStyle = new StyleBoxFlat();
            flatStyle.BgColor = PanelBg;
            flatStyle.SetCornerRadiusAll(6);
            flatStyle.SetContentMarginAll(8);
            panel.AddThemeStyleboxOverride("panel", flatStyle);
        }
        root.AddChild(panel);

        // ── Inner layout ──────────────────────────────────────────────────────
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 2);
        panel.AddChild(vbox);

        // ── Header row: controls (left) + title (right) ─────────────────────
        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(titleRow);

        // ── Control bar (play/pause + speed) — left aligned ─────────────────
        var controlHbox = new HBoxContainer();
        controlHbox.AddThemeConstantOverride("separation", 4);
        controlHbox.MouseFilter = Control.MouseFilterEnum.Stop;
        titleRow.AddChild(controlHbox);
        _controlBar = controlHbox;

        // Spacer pushes title to the right.
        var spacer = new Control();
        spacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        titleRow.AddChild(spacer);

        _titleLabel = new Label();
        _titleLabel.AddThemeFontSizeOverride("font_size", FontSize + 2);
        if (fontBold != null) _titleLabel.AddThemeFontOverride("font", fontBold);
        _titleLabel.AddThemeColorOverride("font_color", GoldColor);
        StyleLabel(_titleLabel, 10);
        _titleLabel.HorizontalAlignment = HorizontalAlignment.Right;
        titleRow.AddChild(_titleLabel);

        _pauseButton = MakeButton("⏸ Pause", fontBold, 90);
        _pauseButton.Connect(BaseButton.SignalName.Pressed,
            Callable.From(OnPausePressed));
        controlHbox.AddChild(_pauseButton);

        var speedDown = MakeArrowButton(
            "res://images/atlases/ui_atlas.sprites/settings_tiny_left_arrow.tres");
        speedDown.Connect(BaseButton.SignalName.Pressed,
            Callable.From(OnSpeedDown));
        controlHbox.AddChild(speedDown);

        _speedLabel = new Label();
        _speedLabel.AddThemeFontSizeOverride("font_size", (int)(FontSize * 1.5f));
        if (fontBold != null) _speedLabel.AddThemeFontOverride("font", fontBold);
        _speedLabel.AddThemeColorOverride("font_color", GoldColor);
        StyleLabel(_speedLabel);
        _speedLabel.CustomMinimumSize = new Vector2(50, 0);
        _speedLabel.HorizontalAlignment = HorizontalAlignment.Center;
        controlHbox.AddChild(_speedLabel);

        var speedUp = MakeArrowButton(
            "res://images/atlases/ui_atlas.sprites/settings_tiny_right_arrow.tres");
        speedUp.Connect(BaseButton.SignalName.Pressed,
            Callable.From(OnSpeedUp));
        controlHbox.AddChild(speedUp);

        _stepButton = MakeButton("⏭ Step", fontBold);
        _stepButton.Visible = false;
        _stepButton.Connect(BaseButton.SignalName.Pressed,
            Callable.From(OnStepPressed));
        controlHbox.AddChild(_stepButton);

        _stopButton = MakeButton("⏹ Stop", fontBold);
        _stopButton.Visible = false;
        _stopButton.Connect(BaseButton.SignalName.Pressed,
            Callable.From(OnStopPressed));
        controlHbox.AddChild(_stopButton);

        // ── Separator + command lines ───────────────────────────────────────
        var sep = new HSeparator();
        var sepStyle = new StyleBoxFlat();
        sepStyle.BgColor = SepColor;
        sepStyle.SetContentMarginAll(0);
        sepStyle.ContentMarginTop = 1;
        sepStyle.ContentMarginBottom = 1;
        sep.AddThemeStyleboxOverride("separator", sepStyle);
        vbox.AddChild(sep);

        _lineLabels = new Label[LineCount];
        for (int i = 0; i < LineCount; i++)
        {
            var lbl = new Label();
            lbl.AddThemeFontSizeOverride("font_size", FontSize);
            if (fontRegular != null) lbl.AddThemeFontOverride("font", fontRegular);
            lbl.AddThemeColorOverride("font_color", CreamColor);
            StyleLabel(lbl);
            lbl.ClipText           = true;
            lbl.CustomMinimumSize  = new Vector2(PanelWidth - 16, 0);
            lbl.AutowrapMode       = TextServer.AutowrapMode.Off;
            _lineLabels[i]         = lbl;
            vbox.AddChild(lbl);
        }

        RefreshDisplay();
        RefreshControls();
    }

    // ── Control bar handlers ─────────────────────────────────────────────────

    private static void OnPausePressed()
    {
        bool wasPaused = ReplayDispatcher.Paused;
        ReplayDispatcher.Paused = !wasPaused;

        if (!wasPaused)
            Godot.Engine.TimeScale = 1.0;
        else
            ReplayDispatcher.ApplyGameSpeed();

        RefreshControls();
    }

    private static readonly float[] SpeedSteps = { 0.5f, 1.0f, 1.5f, 2.0f, 3.0f, 5.0f };

    private static void OnSpeedDown()
    {
        float cur = ReplayDispatcher.GameSpeed;
        for (int i = SpeedSteps.Length - 1; i >= 0; i--)
        {
            if (SpeedSteps[i] < cur - 0.01f)
            {
                ReplayDispatcher.GameSpeed = SpeedSteps[i];
                RefreshControls();
                return;
            }
        }
    }

    private static void OnSpeedUp()
    {
        float cur = ReplayDispatcher.GameSpeed;
        for (int i = 0; i < SpeedSteps.Length; i++)
        {
            if (SpeedSteps[i] > cur + 0.01f)
            {
                ReplayDispatcher.GameSpeed = SpeedSteps[i];
                RefreshControls();
                return;
            }
        }
    }

    private static void OnStepPressed()
    {
        ReplayDispatcher.Step();
        Callable.From(RefreshDisplay).CallDeferred();
        Callable.From(RefreshControls).CallDeferred();
    }

    private static void OnStopPressed()
    {
        ReplayDispatcher.StopAndRecord();
        RefreshControls();
        RefreshDisplay();
    }

    private static void RefreshControls()
    {
        bool isActive = ReplayEngine.IsActive;
        bool isPaused = ReplayDispatcher.Paused;

        if (_pauseButton != null)
            _pauseButton.Text = isPaused ? "▶ Play" : "⏸ Pause";
        if (_speedLabel != null)
            _speedLabel.Text = $"{ReplayDispatcher.GameSpeed:0.0}x";
        if (_stepButton != null)
            _stepButton.Visible = isActive && isPaused;
        if (_stopButton != null)
            _stopButton.Visible = isActive && isPaused;
        if (_controlBar != null)
            _controlBar.Visible = isActive;
    }

    // ── Card play progress tracking ─────────────────────────────────────────

    /// <summary>Called when a card play command is consumed from the queue.</summary>
    internal static void NotifyCardPlayStarted()
    {
        _cardPlayInProgress = true;
        if (_canvas != null && GodotObject.IsInstanceValid(_canvas))
            Callable.From(RefreshDisplay).CallDeferred();
    }

    /// <summary>Called when AfterActionExecuted fires for PlayCardAction.</summary>
    internal static void NotifyCardPlayFinished()
    {
        _cardPlayInProgress = false;
        if (_canvas != null && GodotObject.IsInstanceValid(_canvas))
            Callable.From(RefreshDisplay).CallDeferred();
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

        RefreshControls();
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
                lbl.AddThemeColorOverride("font_color", i == LineCount - 1
                    ? CreamColor
                    : CreamColor * new Color(1, 1, 1, 0.45f));
            }
            else
            {
                lbl.Text     = string.Empty;
                lbl.Modulate = Colors.White;
            }
        }
    }

    // Colors for replay overlay status (using STS2 palette).
    private static readonly Color CompletedColor  = new(0.5f, 0.8f, 0.4f, 0.6f);  // muted green
    private static readonly Color InProgressColor = GoldColor;                      // gold
    private static readonly Color PendingColor    = CreamColor * new Color(1, 1, 1, 0.4f); // dimmed cream

    private static void RefreshReplay()
    {
        if (_titleLabel != null)
            _titleLabel.Text = "▶ REPLAY";

        ReplayEngine.GetReplayContext(out var prev, out ReplayCommand? current, out var next);

        // 5 display slots: [prev-2, prev-1, current, next+1, next+2]
        string?[] slots = new string?[LineCount];
        slots[0] = prev.Count >= 2 ? prev[0]?.ToLogString() : null;
        slots[1] = prev.Count >= 1 ? prev[prev.Count - 1]?.ToLogString() : null;
        slots[2] = current?.ToLogString();
        slots[3] = next.Count >= 1 ? next[0]?.ToLogString() : null;
        slots[4] = next.Count >= 2 ? next[1]?.ToLogString() : null;

        // Detect in-flight commands: the last consumed command may still be
        // executing (EndTurn awaiting TurnStarted, or card play awaiting
        // PlayCardAction completion).  Show it as yellow, and demote the
        // queue front to pending since the replay is blocked until it finishes.
        bool lastConsumedInProgress = false;
        if (prev.Count > 0)
        {
            if (CardPlayReplayPatch.IsAwaitingEndTurnCompletion
                && prev[^1] is EndTurnCommand)
                lastConsumedInProgress = true;
            else if (_cardPlayInProgress
                && prev[^1] is PlayCardCommand)
                lastConsumedInProgress = true;
        }

        for (int i = 0; i < LineCount; i++)
        {
            var lbl = _lineLabels[i];
            if (lbl == null) continue;

            lbl.Text = slots[i] != null ? Truncate(slots[i]!) : string.Empty;

            if (i < 2)
            {
                // Last consumed slot that is still executing → yellow.
                bool isInFlight = lastConsumedInProgress
                    && slots[i] != null
                    && slots[i] == prev[^1]?.ToLogString();
                lbl.AddThemeColorOverride("font_color", isInFlight ? InProgressColor : CompletedColor);
            }
            else if (i == 2)
            {
                lbl.AddThemeColorOverride("font_color", lastConsumedInProgress ? PendingColor : InProgressColor);
            }
            else
                lbl.AddThemeColorOverride("font_color", PendingColor);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Apply STS2-style text shadow and outline to a label.</summary>
    private static void StyleLabel(Label lbl, int outlineSize = 8)
    {
        lbl.AddThemeColorOverride("font_shadow_color", ShadowColor);
        lbl.AddThemeColorOverride("font_outline_color", OutlineColor);
        lbl.AddThemeConstantOverride("shadow_offset_x", 3);
        lbl.AddThemeConstantOverride("shadow_offset_y", 3);
        lbl.AddThemeConstantOverride("outline_size", outlineSize);
    }

    private static Button MakeButton(string text, Font? font, int minWidth = 0)
    {
        var btn = new Button();
        btn.Text = text;
        btn.AddThemeFontSizeOverride("font_size", FontSize);
        if (font != null) btn.AddThemeFontOverride("font", font);
        btn.AddThemeColorOverride("font_color", CreamColor);
        btn.AddThemeColorOverride("font_hover_color", GoldColor);
        btn.AddThemeColorOverride("font_shadow_color", ShadowColor);
        btn.AddThemeColorOverride("font_outline_color", BtnOutlineColor);
        btn.AddThemeConstantOverride("shadow_offset_x", 3);
        btn.AddThemeConstantOverride("shadow_offset_y", 3);
        btn.AddThemeConstantOverride("outline_size", 8);

        // Try to use the game's button texture.
        var btnTex = GD.Load<Texture2D>(
            "res://images/atlases/ui_atlas.sprites/popup_cancel_button.tres");

        if (btnTex != null)
        {
            var texNormal = new StyleBoxTexture();
            texNormal.Texture = btnTex;
            texNormal.SetContentMarginAll(4);
            texNormal.ContentMarginLeft = 10;
            texNormal.ContentMarginRight = 10;
            btn.AddThemeStyleboxOverride("normal", texNormal);

            var texHover = (StyleBoxTexture)texNormal.Duplicate();
            texHover.ModulateColor = new Color(1.2f, 1.1f, 0.9f, 1f);
            btn.AddThemeStyleboxOverride("hover", texHover);

            var texPressed = (StyleBoxTexture)texNormal.Duplicate();
            texPressed.ModulateColor = new Color(0.8f, 0.7f, 0.6f, 1f);
            btn.AddThemeStyleboxOverride("pressed", texPressed);
        }
        else
        {
            // Fallback to flat style.
            var normal = new StyleBoxFlat();
            normal.BgColor = ButtonBg;
            normal.SetCornerRadiusAll(4);
            normal.SetContentMarginAll(4);
            normal.ContentMarginLeft = 10;
            normal.ContentMarginRight = 10;
            btn.AddThemeStyleboxOverride("normal", normal);

            var hover = (StyleBoxFlat)normal.Duplicate();
            hover.BgColor = ButtonHl;
            hover.BorderColor = GoldColor * new Color(1, 1, 1, 0.4f);
            hover.SetBorderWidthAll(1);
            btn.AddThemeStyleboxOverride("hover", hover);

            var pressed = (StyleBoxFlat)normal.Duplicate();
            pressed.BgColor = ButtonBg * new Color(0.7f, 0.7f, 0.7f, 1f);
            btn.AddThemeStyleboxOverride("pressed", pressed);
        }

        if (minWidth > 0)
            btn.CustomMinimumSize = new Vector2(minWidth, 0);

        return btn;
    }

    private static TextureButton MakeArrowButton(string texturePath)
    {
        var btn = new TextureButton();
        var tex = GD.Load<Texture2D>(texturePath);
        if (tex != null)
        {
            btn.TextureNormal = tex;
            btn.TextureHover = tex;
            btn.TexturePressed = tex;
        }
        btn.StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered;
        btn.IgnoreTextureSize = true;
        btn.CustomMinimumSize = new Vector2(24, 24);
        // Brighten on hover.
        btn.Modulate = new Color(0.9f, 0.9f, 0.9f, 1f);
        btn.MouseEntered += () => btn.Modulate = new Color(1.2f, 1.1f, 0.9f, 1f);
        btn.MouseExited += () => btn.Modulate = new Color(0.9f, 0.9f, 0.9f, 1f);
        return btn;
    }

    private static string Truncate(string s) =>
        s.Length <= 68 ? s : s[..65] + "...";
}


using System;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using RunReplays.Utils;

namespace RunReplays;

/// <summary>
/// Browse screen for Run Replays, integrated with the game's NSubmenu/NSubmenuStack
/// system so that opening it triggers the standard blur/backstop fade and the stack's
/// Pop() restores the main menu cleanly.
///
/// Before this node enters the tree, <see cref="MainMenuButtonInjector"/> clones an
/// existing NBackButton (from NTimelineScreen, which is always in the stack) and adds
/// it as a child named "BackButton". We wire it ourselves so the delegate definitely
/// resolves on our instance — bypassing base.ConnectSignals() avoids any IL dispatch
/// issues with Godot Callables created in the game's compiled assembly.
///
/// NSubmenu._Ready() throws for any concrete subclass, so we override _Ready() and
/// call ConnectSignals() directly.
///
/// PopAction is set by MainMenuButtonInjector before Push() so we never depend on
/// _stack (which NSubmenuStack.Push() may not set on subclass instances).
/// </summary>
public class RunReplaySubmenu : NSubmenu
{
    // Injected by MainMenuButtonInjector so we never rely on _stack being set.
    internal Action? PopAction { get; set; }

    // Fallback for the rare case where no existing NBackButton could be cloned.
    private Button? _fallbackBackBtn;

    protected override Control? InitialFocusedControl => _fallbackBackBtn;

    public override void _Ready()
    {
        // Do NOT call base._Ready() — NSubmenu._Ready() explicitly throws when
        // GetType() != typeof(NSubmenu). Call ConnectSignals() ourselves instead.
        ConnectSignals();
    }

    protected override void ConnectSignals()
    {
        NBackButton? backButton = null;
        foreach (Node child in GetChildren())
        {
            if (child is NBackButton nb) { backButton = nb; break; }
        }

        DiagnosticLog.Write("Submenu",
            $"ConnectSignals: backButton={(backButton != null ? backButton.Name : "NULL")}, childCount={GetChildCount()}");

        Action closeMenu = () =>
        {
            if (PopAction != null) PopAction();
            else _stack?.Pop();
        };

        if (backButton != null)
        {
            backButton.Connect(NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ =>
                {
                    DiagnosticLog.Write("Submenu",
                        $"Back Released — PopAction={(PopAction != null ? "set" : "NULL")}");
                    closeMenu();
                }));

            backButton.Disable();

            Connect(CanvasItem.SignalName.VisibilityChanged, Callable.From(() =>
            {
                if (Visible)
                {
                    backButton.MoveToHidePosition();
                    backButton.Enable();
                }
                else
                {
                    backButton.Disable();
                }
            }));
        }

        // FullRect anchors are set by the caller before AddChild so that the
        // layout is resolved before this method runs. Do not set them here.

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        center.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(center);

        var panel = new PanelContainer();
        panel.CustomMinimumSize = new Vector2(1100, 650);
        center.AddChild(panel);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 8);
        panel.AddChild(outer);

        var title = new Label();
        title.Text = "Run Replays";
        title.HorizontalAlignment = HorizontalAlignment.Center;
        outer.AddChild(title);

        // ── Two-panel layout ────────────────────────────────────────────────────

        var hbox = new HBoxContainer();
        hbox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        hbox.AddThemeConstantOverride("separation", 0);
        outer.AddChild(hbox);

        // Left: tab buttons
        var tabs = new VBoxContainer();
        tabs.CustomMinimumSize = new Vector2(220, 0);
        tabs.AddThemeConstantOverride("separation", 2);
        hbox.AddChild(tabs);

        var myReplaysBtn = new Button { Text = "My Replays" };
        var samplesBtn   = new Button { Text = "Sample Runs" };
        var fightBtn     = new Button { Text = "Play a stream fight" };
        tabs.AddChild(myReplaysBtn);
        tabs.AddChild(samplesBtn);
        tabs.AddChild(fightBtn);

        // ── Tab button styling — mirrors NModListButton from BaseLib ────────────
        // Colors match BaseLib exactly.
        var bgNormal   = new Color(0f,    0f,    0f,    0.2f);
        var bgSelected = new Color(0.15f, 0.15f, 0.15f, 0.5f);
        var colorNormal   = new Color(0.7f, 0.7f, 0.7f);
        var colorSelected = StsColors.gold;

        Font? kreonFont = null;
        try { kreonFont = GD.Load<Font>("res://themes/kreon_regular_glyph_space_one.tres"); }
        catch { }

        // One StyleBoxFlat per button so they can be independently animated.
        StyleBoxFlat MakeTabBox(Color bg)
        {
            var s = new StyleBoxFlat();
            s.BgColor = bg;
            s.SetBorderWidthAll(0);
            s.BorderColor = colorSelected;
            s.CornerRadiusTopLeft    = 8;
            s.CornerRadiusTopRight   = 8;
            s.CornerRadiusBottomLeft  = 8;
            s.CornerRadiusBottomRight = 8;
            s.ContentMarginLeft   = 16;
            s.ContentMarginRight  = 8;
            s.ContentMarginTop    = 8;
            s.ContentMarginBottom = 8;
            return s;
        }

        var myBox  = MakeTabBox(bgNormal);
        var samBox = MakeTabBox(bgNormal);
        var figBox = MakeTabBox(bgNormal);

        string[] states = { "normal", "hover", "pressed", "focus" };
        foreach (var st in states) { myReplaysBtn.AddThemeStyleboxOverride(st, myBox); }
        foreach (var st in states) { samplesBtn.AddThemeStyleboxOverride(st, samBox); }
        foreach (var st in states) { fightBtn.AddThemeStyleboxOverride(st, figBox); }

        foreach (var btn in new[] { myReplaysBtn, samplesBtn, fightBtn })
        {
            if (kreonFont != null) btn.AddThemeFontOverride("font", kreonFont);
            btn.AddThemeFontSizeOverride("font_size", 24);
            btn.Alignment = HorizontalAlignment.Left;
        }

        hbox.AddChild(new VSeparator());

        // Right: two scroll containers, one per tab
        var userScroll = new ScrollContainer();
        userScroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        userScroll.SizeFlagsVertical   = Control.SizeFlags.ExpandFill;
        hbox.AddChild(userScroll);

        var userList = new VBoxContainer();
        userList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        userList.AddThemeConstantOverride("separation", 4);
        userScroll.AddChild(userList);

        var samplesScroll = new ScrollContainer();
        samplesScroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        samplesScroll.SizeFlagsVertical   = Control.SizeFlags.ExpandFill;
        hbox.AddChild(samplesScroll);

        var samplesList = new VBoxContainer();
        samplesList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        samplesList.AddThemeConstantOverride("separation", 4);
        samplesScroll.AddChild(samplesList);

        RunReplayMenu.PopulateSeparateDeferred(userList, samplesList, closeMenu);

        var fightPanel = BuildStreamFightPanel(kreonFont);
        hbox.AddChild(fightPanel);

        // Tab switching: swap bg + animate border width in over 200 ms (matching BaseLib).
        var tabBtns  = new[] { myReplaysBtn, samplesBtn, fightBtn };
        var tabBoxes = new[] { myBox, samBox, figBox };
        var tabViews = new Control[] { userScroll, samplesScroll, fightPanel };
        void SelectTab(int sel, bool animate = true)
        {
            for (int i = 0; i < tabBtns.Length; i++)
            {
                bool on = i == sel;
                tabViews[i].Visible = on;
                var box = tabBoxes[i];
                box.BgColor = on ? bgSelected : bgNormal;
                if (!on) box.BorderWidthLeft = 0;
                else if (animate)
                {
                    box.BorderWidthLeft = 0;
                    var tween = CreateTween();
                    tween.TweenProperty(box, "border_width_left", Variant.From(4), 0.2);
                }
                else box.BorderWidthLeft = 4;
                tabBtns[i].AddThemeColorOverride("font_color",         on ? colorSelected : colorNormal);
                tabBtns[i].AddThemeColorOverride("font_hover_color",   on ? colorSelected : Colors.White);
                tabBtns[i].AddThemeColorOverride("font_pressed_color", on ? colorSelected : Colors.White);
            }
        }

        myReplaysBtn.Pressed += () => SelectTab(0);
        samplesBtn.Pressed   += () => SelectTab(1);
        fightBtn.Pressed     += () => SelectTab(2);
        SelectTab(0, animate: false);

        // Fallback back button if no NBackButton was cloned
        if (backButton == null)
        {
            outer.AddChild(new HSeparator());
            _fallbackBackBtn = new Button { Text = "Back" };
            _fallbackBackBtn.Pressed += closeMenu;
            outer.AddChild(_fallbackBackBtn);
        }
    }

    /// <summary>The "Play a stream fight" tab (<see cref="StreamFight"/>): a code / link box with a Paste button (the
    /// clipboard, so a controller needs no keyboard), Load, and Restore for the player's own run.</summary>
    private Control BuildStreamFightPanel(Font? font)
    {
        var box = new VBoxContainer();
        box.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        box.SizeFlagsVertical   = Control.SizeFlags.ExpandFill;
        box.AddThemeConstantOverride("separation", 12);

        Label Text(string s, int size = 20)
        {
            var l = new Label { Text = s, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            if (font != null) l.AddThemeFontOverride("font", font);
            l.AddThemeFontSizeOverride("font_size", size);
            return l;
        }
        Button Btn(string s)
        {
            var b = new Button { Text = s, CustomMinimumSize = new Vector2(160, 48) };
            if (font != null) b.AddThemeFontOverride("font", font);
            b.AddThemeFontSizeOverride("font_size", 22);
            return b;
        }

        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top" }) margin.AddThemeConstantOverride("margin_" + side, 24);
        margin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        margin.AddChild(box);

        box.AddChild(Text("Play a fight from the stream in your own game. Copy its code or link from the stream's site, " +
                          "then Paste and Load. Your own saved run is backed up first."));
        var input = new LineEdit { PlaceholderText = "code or link, e.g. K7Q2XM", CustomMinimumSize = new Vector2(0, 48) };
        if (font != null) input.AddThemeFontOverride("font", font);
        input.AddThemeFontSizeOverride("font_size", 24);
        box.AddChild(input);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        var paste = Btn("Paste");
        var load  = Btn("Load");
        row.AddChild(paste);
        row.AddChild(load);
        box.AddChild(row);

        var status = Text("", 20);
        box.AddChild(status);

        box.AddChild(new HSeparator());
        var restoreRow = new HBoxContainer();
        restoreRow.AddThemeConstantOverride("separation", 12);
        var restore = Btn("Restore my run");
        var backupInfo = Text("", 18);
        backupInfo.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        restoreRow.AddChild(restore);
        restoreRow.AddChild(backupInfo);
        box.AddChild(restoreRow);

        void ShowBackup()
        {
            string? b = StreamFight.LatestBackup();
            backupInfo.Text = b == null ? "No backup yet." : "Backup: " + System.IO.Path.GetFileNameWithoutExtension(b).Replace("current_run.", "");
            restore.Disabled = b == null;
        }
        ShowBackup();

        paste.Pressed += () =>
        {
            input.Text = (DisplayServer.ClipboardGet() ?? "").Trim();
            status.Text = StreamFight.ParseCode(input.Text) is string c ? $"Code {c}. Press Load." : "The clipboard has no fight code in it.";
        };
        load.Pressed += async () =>
        {
            load.Disabled = true;
            status.Text = "Loading the fight…";
            string? err = await StreamFight.LoadAsync(input.Text);
            if (!GodotObject.IsInstanceValid(load)) return;
            load.Disabled = false;
            status.Text = err ?? "Loading…";
            ShowBackup();
        };
        restore.Pressed += () => { status.Text = StreamFight.RestoreLatest(); ShowBackup(); };

        return margin;
    }
}

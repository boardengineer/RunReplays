using System;
using Godot;
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
        foreach (Node child in GetChildren())
            DiagnosticLog.Write("Submenu", $"  child: {child.Name} ({child.GetType().Name})");

        if (backButton != null)
        {
            backButton.Connect(NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ =>
                {
                    DiagnosticLog.Write("Submenu",
                        $"Back Released — PopAction={(PopAction != null ? "set" : "NULL")}, _stack={(_stack != null ? _stack.Name : "NULL")}");
                    if (PopAction != null)
                        PopAction();
                    else
                        _stack?.Pop();
                }));

            backButton.Disable();

            Connect(CanvasItem.SignalName.VisibilityChanged, Callable.From(() =>
            {
                DiagnosticLog.Write("Submenu",
                    $"VisibilityChanged: Visible={Visible}, backButton valid={GodotObject.IsInstanceValid(backButton)}");
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
        // Pass events through so the NBackButton sibling (drawn below) can receive clicks.
        center.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(center);

        var panel = new PanelContainer();
        panel.CustomMinimumSize = new Vector2(960, 600);
        center.AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        panel.AddChild(vbox);

        var title = new Label();
        title.Text = "Run Replays";
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);

        var scroll = new ScrollContainer();
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        vbox.AddChild(scroll);

        var list = new VBoxContainer();
        list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        list.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(list);

        RunReplayMenu.PopulateList(list, () =>
        {
            if (PopAction != null)
                PopAction();
            else
                _stack?.Pop();
        });

        if (backButton == null)
        {
            vbox.AddChild(new HSeparator());
            _fallbackBackBtn = new Button { Text = "Back" };
            _fallbackBackBtn.Pressed += () =>
            {
                if (PopAction != null)
                    PopAction();
                else
                    _stack?.Pop();
            };
            vbox.AddChild(_fallbackBackBtn);
        }
    }
}

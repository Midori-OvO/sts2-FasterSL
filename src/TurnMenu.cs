using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;

namespace FasterSL;

/// <summary>顶栏地图按钮左边的“回合”按钮，只在战斗中、单人或多人房主那里显示；点开是一个下拉列表，列出本场战斗的回合。</summary>
internal static class TurnMenu
{
	private const string NodeName = "FasterSLTurns";

	private static Button? _button;

	private static CanvasLayer? _layer;

	public static bool IsOpen => _layer != null && GodotObject.IsInstanceValid(_layer);

	public static void Init()
	{
		TurnHistory.Changed += Refresh;
	}

	public static void Inject(NTopBar topBar)
	{
		Control map = topBar.Map;
		if (map.GetParent() is not Control parent || parent.HasNode(NodeName))
		{
			return;
		}
		Button button = Ui.MakeButton(Texts.Get(TextId.TurnButton), 22);
		button.Name = NodeName;
		button.TooltipText = Texts.Get(TextId.TurnTitle);
		button.Pressed += () => Toggle(button);
		parent.AddChild(button);
		if (parent is Container)
		{
			parent.MoveChild(button, map.GetIndex());
		}
		else
		{
			Callable.From(() =>
			{
				if (GodotObject.IsInstanceValid(button) && GodotObject.IsInstanceValid(map))
				{
					button.Position = new Vector2(map.Position.X - button.Size.X - 12f, map.Position.Y + (map.Size.Y - button.Size.Y) / 2f);
				}
			}).CallDeferred();
		}
		_button = button;
		Refresh();
	}

	public static void Refresh()
	{
		if (_button == null || !GodotObject.IsInstanceValid(_button))
		{
			return;
		}
		NetGameType? type = RunManager.Instance?.NetService?.Type;
		bool show = CombatManager.Instance.IsInProgress && (type == NetGameType.Singleplayer || type == NetGameType.Host);
		_button.Visible = show;
		if (!show)
		{
			Close();
		}
	}

	public static void Close()
	{
		if (IsOpen)
		{
			_layer!.QueueFree();
		}
		_layer = null;
	}

	private static void Toggle(Button anchor)
	{
		if (IsOpen)
		{
			Close();
			return;
		}
		if (TurnRewind.IsFastForwarding || !Reloader.CanStartReload())
		{
			return;
		}
		List<TurnMark> marks = TurnHistory.NewestFirst.ToList();
		if (marks.Count == 0 || !TurnHistory.IsCurrent(TurnHistory.CurrentReplay()))
		{
			Reloader.Toast(Texts.Get(TextId.TurnEmpty));
			return;
		}
		Open(anchor, marks);
	}

	private static void Open(Button anchor, List<TurnMark> marks)
	{
		_layer = new CanvasLayer { Layer = 121 };

		Control catcher = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
		catcher.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		catcher.GuiInput += e =>
		{
			if (e is InputEventMouseButton { Pressed: true })
			{
				Close();
			}
		};
		_layer.AddChild(catcher);

		PanelContainer panel = new PanelContainer();
		panel.AddThemeStyleboxOverride("panel", Ui.Panel(12f, 10f));
		_layer.AddChild(panel);

		VBoxContainer layout = new VBoxContainer();
		layout.AddThemeConstantOverride("separation", 8);
		panel.AddChild(layout);
		Label title = Ui.MakeLabel(Texts.Get(TextId.TurnTitle), 22, new Color(0.95f, 0.85f, 0.6f));
		title.AutowrapMode = TextServer.AutowrapMode.Off;
		layout.AddChild(title);

		ScrollContainer scroll = new ScrollContainer
		{
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
			CustomMinimumSize = new Vector2(300f, 0f)
		};
		layout.AddChild(scroll);

		VBoxContainer list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		list.AddThemeConstantOverride("separation", 6);
		scroll.AddChild(list);

		TurnMark current = marks[0];
		foreach (TurnMark mark in marks)
		{
			TurnMark target = mark;
			string text = Texts.Get(TextId.TurnLabel, mark.Turn);
			if (mark == current)
			{
				text += " " + Texts.Get(TextId.CurrentMark);
			}
			text += "\n" + Texts.Get(TextId.TurnHp, mark.Hp, mark.MaxHp);
			Button button = Ui.MakeButton(text, 20);
			button.Alignment = HorizontalAlignment.Left;
			button.CustomMinimumSize = new Vector2(0f, 64f);
			button.Pressed += () =>
			{
				Close();
				TurnRewind.Request(target);
			};
			list.AddChild(button);
		}

		NGame.Instance!.AddChildSafely(_layer);
		Callable.From(() =>
		{
			if (!GodotObject.IsInstanceValid(panel) || !GodotObject.IsInstanceValid(anchor))
			{
				return;
			}
			Vector2 viewport = panel.GetViewportRect().Size;
			float maxHeight = Math.Max(200f, viewport.Y * 0.6f);
			scroll.CustomMinimumSize = new Vector2(300f, Math.Min(list.GetCombinedMinimumSize().Y, maxHeight));
			panel.ResetSize();
			Vector2 pos = anchor.GetGlobalRect().Position + new Vector2(0f, anchor.Size.Y + 6f);
			pos.X = Math.Clamp(pos.X, 8f, Math.Max(8f, viewport.X - panel.Size.X - 8f));
			panel.Position = pos;
		}).CallDeferred();
	}
}

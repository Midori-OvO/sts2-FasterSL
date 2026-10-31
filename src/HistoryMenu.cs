using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FasterSL;

/// <summary>
/// 从 ESC 暂停菜单打开的节点列表：列出本局走过的节点（最新的在最上面），点一下就回退到那个节点的入口。
/// 界面全部用代码搭，不依赖游戏场景文件。
/// </summary>
internal static class HistoryMenu
{
	private static CanvasLayer? _layer;

	private static readonly Regex MarkupTag = new Regex(@"\[[^\]]*\]");

	public static bool IsOpen => _layer != null && GodotObject.IsInstanceValid(_layer);

	public static void Close()
	{
		if (IsOpen)
		{
			_layer!.QueueFree();
		}
		_layer = null;
	}

	public static void Open()
	{
		if (IsOpen || !Reloader.CanStartReload())
		{
			return;
		}
		List<NodeEntry> entries = NodeHistory.Entries.Reverse().ToList();
		if (entries.Count == 0)
		{
			Reloader.Toast(Texts.Get(TextId.HistoryEmpty));
			return;
		}
		NodeEntry? current = NodeHistory.FindCurrent();
		ulong localId = RunManager.Instance.NetService.NetId;

		_layer = new CanvasLayer { Layer = 120 };

		ColorRect dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.6f), MouseFilter = Control.MouseFilterEnum.Stop };
		dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_layer.AddChild(dim);

		CenterContainer center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_layer.AddChild(center);

		PanelContainer panel = new PanelContainer { CustomMinimumSize = new Vector2(820f, 680f) };
		panel.AddThemeStyleboxOverride("panel", Ui.Panel(28f, 22f));
		center.AddChild(panel);

		VBoxContainer layout = new VBoxContainer();
		layout.AddThemeConstantOverride("separation", 12);
		panel.AddChild(layout);

		layout.AddChild(Ui.MakeLabel(Texts.Get(TextId.HistoryTitle), 34, new Color(0.95f, 0.85f, 0.6f)));
		layout.AddChild(Ui.MakeLabel(Texts.Get(TextId.HistoryHint), 19, new Color(0.7f, 0.7f, 0.72f)));

		ScrollContainer scroll = new ScrollContainer
		{
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
		};
		layout.AddChild(scroll);

		VBoxContainer list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		list.AddThemeConstantOverride("separation", 6);
		scroll.AddChild(list);

		foreach (NodeEntry entry in entries)
		{
			NodeEntry target = entry;
			Button button = Ui.MakeButton(Describe(entry, entry == current, localId), 22);
			button.Alignment = HorizontalAlignment.Left;
			button.CustomMinimumSize = new Vector2(0f, 70f);
			button.Pressed += () =>
			{
				Close();
				Reloader.RequestReload(target.Save);
			};
			list.AddChild(button);
		}

		Button cancel = Ui.MakeButton(Texts.Get(TextId.Cancel), 22);
		cancel.CustomMinimumSize = new Vector2(0f, 52f);
		cancel.Pressed += Close;
		layout.AddChild(cancel);

		NGame.Instance!.AddChildSafely(_layer);
	}

	private static string Describe(NodeEntry entry, bool isCurrent, ulong localId)
	{
		string header = Texts.Get(TextId.NodeLabel, entry.Act + 1, entry.Floor);
		string room = RoomName(entry);
		string? model = ModelTitle(entry);
		string line1 = header;
		if (!string.IsNullOrEmpty(room))
		{
			line1 += "  ·  " + room;
		}
		if (!string.IsNullOrEmpty(model) && model != room)
		{
			line1 += (Texts.CurrentLanguage is "zhs" or "zht" or "jpn" ? "：" : ": ") + model;
		}
		if (isCurrent)
		{
			line1 += " " + Texts.Get(TextId.CurrentMark);
		}
		SerializablePlayer? player = entry.Save.Players.FirstOrDefault(p => p.NetId == localId) ?? entry.Save.Players.FirstOrDefault();
		if (player == null)
		{
			return line1;
		}
		return line1 + "\n" + Texts.Get(TextId.HpGold, player.CurrentHp, player.MaxHp, player.Gold);
	}

	/// <summary>用游戏顶栏房间图标悬浮提示的标题作为房间名，这样所有语言的叫法都和游戏一致。</summary>
	private static string RoomName(NodeEntry entry)
	{
		string? key = entry.PointType switch
		{
			MapPointType.Shop => "ROOM_MERCHANT",
			MapPointType.Treasure => "ROOM_TREASURE",
			MapPointType.RestSite => "ROOM_REST",
			MapPointType.Monster => "ROOM_ENEMY",
			MapPointType.Elite => "ROOM_ELITE",
			MapPointType.Boss => "ROOM_BOSS",
			MapPointType.Ancient => "ROOM_ANCIENT",
			MapPointType.Unknown => entry.RoomType switch
			{
				RoomType.Monster => "ROOM_UNKNOWN_ENEMY",
				RoomType.Treasure => "ROOM_UNKNOWN_TREASURE",
				RoomType.Shop => "ROOM_UNKNOWN_MERCHANT",
				RoomType.Event => "ROOM_UNKNOWN_EVENT",
				_ => null
			},
			_ => null
		};
		if (key == null)
		{
			return "";
		}
		try
		{
			return Clean(LocString.GetIfExists("static_hover_tips", key + ".title")?.GetFormattedText());
		}
		catch
		{
			return "";
		}
	}

	private static string? ModelTitle(NodeEntry entry)
	{
		if (entry.ModelId == null)
		{
			return null;
		}
		try
		{
			switch (entry.RoomType)
			{
			case RoomType.Monster:
			case RoomType.Elite:
			case RoomType.Boss:
				return Clean(ModelDb.GetByIdOrNull<EncounterModel>(entry.ModelId)?.Title.GetFormattedText());
			case RoomType.Event:
				return Clean(ModelDb.GetByIdOrNull<EventModel>(entry.ModelId)?.Title.GetFormattedText());
			default:
				return null;
			}
		}
		catch
		{
			return null;
		}
	}

	private static string Clean(string? text)
	{
		return text == null ? "" : MarkupTag.Replace(text, "").Trim();
	}

}

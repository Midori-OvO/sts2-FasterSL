using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;

namespace FasterSL;

/// <summary>
/// 在 ESC 暂停菜单里加入 Save&Load 和"回退到节点"按钮，沿用原版"保存并退出"的样式。
/// 多人局的客户端不显示，因为只有房主能回退。
/// </summary>
internal static class PauseMenuEntry
{
	private const string ReloadNodeName = "FasterSLReload";
	private const string HistoryNodeName = "FasterSLHistory";

	public static void Inject(NPauseMenu menu)
	{
		Control container = menu.GetNode<Control>("%ButtonContainer");
		NPauseMenuButton template = container.GetNode<NPauseMenuButton>("SaveAndQuit");
		AddButton(container, template, ReloadNodeName, "Save&Load", () => OnPressed(reload: true));
		AddButton(container, template, HistoryNodeName, Texts.Get(TextId.HistoryTitle), () => OnPressed(reload: false));
		UpdateNavigation(menu);
	}

	public static void UpdateNavigation(NPauseMenu menu)
	{
		NPauseMenuButton[] buttons = menu.GetNode<Control>("%ButtonContainer").GetChildren()
			.OfType<NPauseMenuButton>().Where(button => button.Visible).ToArray();
		for (int i = 0; i < buttons.Length; i++)
		{
			NPauseMenuButton button = buttons[i];
			button.FocusNeighborLeft = button.GetPath();
			button.FocusNeighborRight = button.GetPath();
			button.FocusNeighborTop = buttons[(i + buttons.Length - 1) % buttons.Length].GetPath();
			button.FocusNeighborBottom = buttons[(i + 1) % buttons.Length].GetPath();
			button.FocusPrevious = button.FocusNeighborTop;
			button.FocusNext = button.FocusNeighborBottom;
		}
	}

	private static void AddButton(Control container, NPauseMenuButton template, string nodeName, string text, Action onPressed)
	{
		if (container.HasNode(nodeName))
		{
			return;
		}
		// 不带 Signals 标志：原按钮上连着"保存并退出"的回调，不能一起复制过来
		const int flags = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation);
		NPauseMenuButton button = (NPauseMenuButton)template.Duplicate(flags);
		button.Name = nodeName;
		TextureRect image = button.GetNode<TextureRect>("ButtonImage");
		if (image.Material != null)
		{
			// 悬停效果是改 shader 参数，材质共用的话两个按钮会一起变亮
			image.Material = (Material)image.Material.Duplicate();
		}
		container.AddChild(button);
		container.MoveChild(button, template.GetIndex());
		if (container is not Container)
		{
			button.Position = template.Position;
			template.Position += new Vector2(0f, template.Size.Y);
		}
		button.GetNode<MegaLabel>("Label").SetTextAutoSize(text);
		button.Visible = RunManager.Instance.NetService.Type != NetGameType.Client;
		button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => onPressed()));
	}

	/// <summary>NPauseMenu.CloseToMenu 的 prefix：原版点“保存并退出”后会禁用所有按钮，我们的按钮也跟着禁用。</summary>
	public static void Disable(NPauseMenu menu)
	{
		foreach (string nodeName in new[] { ReloadNodeName, HistoryNodeName })
		{
			menu.GetNodeOrNull<NPauseMenuButton>("%ButtonContainer/" + nodeName)?.Disable();
		}
	}

	private static void OnPressed(bool reload)
	{
		if (Reloader.Quitting || Reloader.IsBusy)
		{
			return;
		}
		try
		{
			NCapstoneContainer.Instance?.Close();
			NRun.Instance?.GlobalUi.TopBar.Pause.ToggleAnimState();
			if (reload)
			{
				Reloader.RequestReload();
			}
			else
			{
				HistoryMenu.Open();
			}
		}
		catch (Exception ex)
		{
			Reloader.Info((reload ? "Save&Load 失败：" : "打开节点列表失败：") + ex);
		}
	}
}

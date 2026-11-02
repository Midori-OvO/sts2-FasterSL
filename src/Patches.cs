using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace FasterSL;

internal static class Patches
{
	public static void Apply(Harmony harmony)
	{
		Patch(harmony, AccessTools.DeclaredMethod(typeof(CombatReplayWriter), "RecordInitialState"), postfix: nameof(RecordInitialStatePostfix));
		Patch(harmony, AccessTools.DeclaredMethod(typeof(RunManager), "Launch"), postfix: nameof(LaunchPostfix));
		Patch(harmony, AccessTools.DeclaredMethod(typeof(NetHostGameService), "Disconnect"), prefix: nameof(DisconnectPrefix));
		Patch(harmony, AccessTools.DeclaredMethod(typeof(NetClientGameService), "Disconnect"), prefix: nameof(DisconnectPrefix));
		Patch(harmony, AccessTools.DeclaredMethod(typeof(NGame), "_Input"), prefix: nameof(InputPrefix));
		Patch(harmony, AccessTools.DeclaredMethod(typeof(NRun), "_Process"), prefix: nameof(RunProcessPrefix), postfix: nameof(RunProcessPostfix));
		Patch(harmony, AccessTools.DeclaredMethod(typeof(NetMessageBus), "SetBufferMessages"), prefix: nameof(SetBufferMessagesPrefix));
		// 游戏内部字段变了时只放弃入场同步超时保护，其他功能照常
		try
		{
			ReloadSyncGuard.Validate();
			harmony.Patch(AccessTools.DeclaredMethod(typeof(CombatStateSynchronizer), "WaitForSync"),
				postfix: new HarmonyMethod(typeof(ReloadSyncGuard), nameof(ReloadSyncGuard.WaitForSyncPostfix)));
		}
		catch (Exception ex)
		{
			Reloader.Info("[补丁失败] 入场同步超时保护不可用：" + ex.Message);
		}
		Patch(harmony, AccessTools.DeclaredMethod(typeof(RunManager), "EnterMapPointInternal"), prefix: nameof(EnterMapPointPrefix));
		Patch(harmony, AccessTools.DeclaredMethod(typeof(RunSaveManager), "SaveRun", new[] { typeof(SerializableRun), typeof(bool) }), prefix: nameof(SaveRunPrefix));
		Patch(harmony, AccessTools.DeclaredMethod(typeof(RunState), "AppendToMapPointHistory"), postfix: nameof(AppendToMapPointHistoryPostfix));
		Patch(harmony, AccessTools.DeclaredMethod(typeof(NPauseMenu), "_Ready"), postfix: nameof(PauseMenuReadyPostfix));
		Patch(harmony, AccessTools.DeclaredMethod(typeof(NPauseMenu), "Initialize"), postfix: nameof(PauseMenuInitializedPostfix));
		Patch(harmony, AccessTools.DeclaredMethod(typeof(NPauseMenu), "CloseToMenu"), prefix: nameof(CloseToMenuPrefix));
		Patch(harmony, AccessTools.DeclaredMethod(typeof(NTopBar), "_Ready"), postfix: nameof(TopBarReadyPostfix));
		Patch(harmony, AccessTools.DeclaredMethod(typeof(RunManager), "LoadIntoLatestMapCoord"), prefix: nameof(LoadIntoLatestMapCoordPrefix));
		try
		{
			TurnHistory.Init();
			TurnMenu.Init();
		}
		catch (Exception ex)
		{
			Reloader.Info("回合记录初始化失败：" + ex.Message);
		}
	}

	/// <summary>保存并退出期间不允许再发起重载，直到下一局开始。</summary>
	private static void CloseToMenuPrefix(NPauseMenu __instance)
	{
		Reloader.Quitting = true;
		try
		{
			PauseMenuEntry.Disable(__instance);
		}
		catch (Exception ex)
		{
			Reloader.Info("禁用暂停菜单按钮失败：" + ex.Message);
		}
	}

	private static void TopBarReadyPostfix(NTopBar __instance)
	{
		try
		{
			TurnMenu.Inject(__instance);
		}
		catch (Exception ex)
		{
			Reloader.Info("顶栏回合按钮添加失败：" + ex);
		}
	}

	private static void LoadIntoLatestMapCoordPrefix(RunManager __instance)
	{
		TurnRewind.BeforeRoomEntry(__instance);
	}

	private static bool RunProcessPrefix()
	{
		return !ReloadNetworkPump.ShouldSkipRunUpdate();
	}

	private static void RunProcessPostfix()
	{
		TurnHistory.Update();
	}

	private static bool SetBufferMessagesPrefix(NetMessageBus __instance, bool bufferMessages)
	{
		return bufferMessages || !Reloader.ShouldKeepBuffering(__instance);
	}

	private static void Patch(Harmony harmony, System.Reflection.MethodInfo? target, string? prefix = null, string? postfix = null)
	{
		if (target == null)
		{
			Reloader.Info($"[补丁失败] 找不到目标方法（prefix={prefix}, postfix={postfix}）");
			return;
		}
		try
		{
			harmony.Patch(target,
				prefix: prefix == null ? null : new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Patches), prefix)),
				postfix: postfix == null ? null : new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Patches), postfix)));
		}
		catch (Exception ex)
		{
			Reloader.Info($"[补丁失败] {target.DeclaringType?.Name}.{target.Name}：{ex.Message}");
		}
	}

	/// <summary>每次进入地图节点，原版都会先存档再调用这里，参数就是“房间入口”状态。</summary>
	private static void RecordInitialStatePostfix(SerializableRun serializableRun)
	{
		try
		{
			NodeHistory.Record(serializableRun);
		}
		catch (Exception ex)
		{
			Reloader.Info("记录节点历史失败：" + ex);
		}
	}

	/// <summary>saveGame=false 表示这是读档后重新进入节点（主菜单继续、本 mod 的回退），不是玩家走进新节点。</summary>
	private static void EnterMapPointPrefix(bool saveGame, AbstractRoom? preFinishedRoom)
	{
		NodeHistory.EnteringByLoad = !saveGame;
		NodeHistory.EnteringPreFinished = preFinishedRoom != null;
	}

	private static void SaveRunPrefix(SerializableRun save)
	{
		try
		{
			NodeHistory.RecordAfter(save);
		}
		catch (Exception ex)
		{
			Reloader.Info("记录战斗后存档失败：" + ex.Message);
		}
	}

	private static void AppendToMapPointHistoryPostfix(MapPointType mapPointType, RoomType initialRoomType, ModelId? roomModelId)
	{
		try
		{
			NodeHistory.OnRoomAppended(mapPointType, initialRoomType, roomModelId);
		}
		catch (Exception ex)
		{
			Reloader.Info("记录节点房间信息失败：" + ex.Message);
		}
	}

	private static void PauseMenuReadyPostfix(NPauseMenu __instance)
	{
		try
		{
			PauseMenuEntry.Inject(__instance);
		}
		catch (Exception ex)
		{
			Reloader.Info("暂停菜单按钮添加失败：" + ex);
		}
	}

	private static void PauseMenuInitializedPostfix(NPauseMenu __instance)
	{
		try
		{
			PauseMenuEntry.UpdateNavigation(__instance);
		}
		catch (Exception ex)
		{
			Reloader.Info("暂停菜单导航更新失败：" + ex);
		}
	}

	/// <summary>
	/// 列表打开时，键盘输入只给它用：Esc 关闭，其他按键吞掉，避免同时触发游戏的菜单或快捷键。
	/// 全部在 prefix 里处理：Harmony 的 postfix 即使 prefix 返回 false 也会执行。
	/// </summary>
	private static bool InputPrefix(NGame __instance, InputEvent inputEvent)
	{
		if (Reloader.IsBusy || TurnRewind.IsFastForwarding)
		{
			if (!TurnRewind.IsFastForwarding || inputEvent is InputEventKey or InputEventJoypadButton or InputEventJoypadMotion or InputEventAction)
			{
				__instance.GetViewport()?.SetInputAsHandled();
				return false;
			}
		}
		if (inputEvent is not InputEventKey key)
		{
			return true;
		}
		bool pressed = key.Pressed && !key.Echo;
		if (HistoryMenu.IsOpen || TurnMenu.IsOpen)
		{
			if (pressed && key.Keycode == Key.Escape)
			{
				HistoryMenu.Close();
				TurnMenu.Close();
			}
			__instance.GetViewport()?.SetInputAsHandled();
			return false;
		}
		return true;
	}

	private static void LaunchPostfix(RunManager __instance)
	{
		Reloader.Quitting = false;
		try
		{
			Reloader.EnsureHandlers(__instance.NetService);
		}
		catch (Exception ex)
		{
			Reloader.Info("注册消息处理器失败：" + ex.Message);
		}
	}

	/// <summary>重载时 RunManager.CleanUp 会顺手断开网络；我们要复用连接，所以这段时间拦掉。</summary>
	private static bool DisconnectPrefix()
	{
		return !Reloader.SuppressDisconnect;
	}
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace FasterSL;

/// <summary>
/// 让同一个 dll 同时兼容正式版（v0.107.1）和 Public Beta（v0.111.0）。
/// 两个版本的 LoadRunLobby 记录玩家的方式不同：
///   Beta：  public List&lt;LoadRunLobbyPlayer&gt; Players（结构体，含 id/isModded/isReady）
///   正式版：public HashSet&lt;ulong&gt; ConnectedPlayerIds + private HashSet&lt;ulong&gt; _readyPlayers
/// 这里全部用反射访问，不在编译期绑定任何一个版本独有的成员。
/// </summary>
internal static class LobbyCompat
{
	public static void AddReadyPlayers(LoadRunLobby lobby, IEnumerable<ulong> playerIds, INetGameService net)
	{
		Type lobbyType = typeof(LoadRunLobby);
		if (AccessTools.Property(lobbyType, "Players")?.GetValue(lobby) is IList players)
		{
			bool modded = IsLocalModded(net);
			Type playerType = players.GetType().GetGenericArguments()[0];
			foreach (ulong id in playerIds)
			{
				object player = Activator.CreateInstance(playerType)!;
				AccessTools.Field(playerType, "id").SetValue(player, id);
				AccessTools.Field(playerType, "isModded")?.SetValue(player, modded);
				AccessTools.Field(playerType, "isReady")?.SetValue(player, true);
				players.Add(player);
			}
			return;
		}
		if (AccessTools.Property(lobbyType, "ConnectedPlayerIds")?.GetValue(lobby) is HashSet<ulong> connected)
		{
			HashSet<ulong>? ready = AccessTools.Field(lobbyType, "_readyPlayers")?.GetValue(lobby) as HashSet<ulong>;
			foreach (ulong id in playerIds)
			{
				connected.Add(id);
				ready?.Add(id);
			}
			return;
		}
		throw new MissingMemberException("LoadRunLobby 既没有 Players 也没有 ConnectedPlayerIds，游戏版本不受支持");
	}

	/// <summary>Beta 版：等价于 net.LocalVersion.IsModded()（正式版的 INetGameService 没有 LocalVersion）。</summary>
	private static bool IsLocalModded(INetGameService net)
	{
		try
		{
			object? version = AccessTools.Property(net.GetType(), "LocalVersion")?.GetValue(net);
			if (version == null)
			{
				return true;
			}
			if (AccessTools.Method(version.GetType(), "IsModded", Type.EmptyTypes)?.Invoke(version, null) is bool modded)
			{
				return modded;
			}
		}
		catch (Exception ex)
		{
			Reloader.Info("[重载] 读取本机 mod 状态失败：" + ex.Message);
		}
		// 装了本 mod 就一定是 modded
		return true;
	}
}

/// <summary>
/// 重载时临时建的 LoadRunLobby 需要一个监听器；我们不走大厅界面，全部空实现。
/// 两个版本的 ILoadRunLobbyListener 方法签名不同（PlayerConnected 的参数类型变了），
/// 所以不静态实现接口，而是用 DispatchProxy 在运行时按当前游戏版本的接口生成实现。
/// </summary>
public class SilentLobbyListener : DispatchProxy
{
	public static ILoadRunLobbyListener Create()
	{
		return DispatchProxy.Create<ILoadRunLobbyListener, SilentLobbyListener>();
	}

	protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
	{
		switch (targetMethod?.Name)
		{
		case "ShouldAllowRunToBegin":
			return Task.FromResult(result: true);
		case "LocalPlayerDisconnected":
			Reloader.Info("[重载] 临时大厅收到本地断线：" + (args != null && args.Length > 0 ? args[0] : null));
			return null;
		default:
			return null;
		}
	}
}

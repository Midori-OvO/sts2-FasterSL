using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace FasterSL;

internal static class ReloadQuiescence
{
	private static readonly Harmony Patcher = new Harmony("sts2.FasterSL.Quiescence");
	private static readonly HashSet<MethodBase> Patched = new HashSet<MethodBase>();
	private static readonly Dictionary<Type, bool> BufferedTypes = new Dictionary<Type, bool>();
	private static readonly List<(MethodBase Method, object[] Args)> Pending = new List<(MethodBase, object[])>();
	private static object? _transport;
	private static ActionExecutor? _executor;
	private static bool _actionsWerePaused;
	private static bool _combatWasPaused;

	public static void Begin(INetGameService net)
	{
		if (_transport != null)
		{
			throw new InvalidOperationException("上一次重载握手尚未结束");
		}
		object transport = AccessTools.Field(net.GetType(), "_netHost")?.GetValue(net)
			?? AccessTools.Property(net.GetType(), "NetClient")?.GetValue(net)
			?? AccessTools.Field(net.GetType(), "_netClient")?.GetValue(net)
			?? throw new MissingMemberException("找不到联机传输服务");
		bool found = false;
		foreach (string name in new[] { "SendMessageToClient", "SendMessageToAll", "SendMessageToHost" })
		{
			MethodInfo? method = AccessTools.Method(transport.GetType(), name);
			if (method == null || method.IsAbstract)
			{
				continue;
			}
			found = true;
			if (!Patched.Contains(method))
			{
				Patcher.Patch(method, prefix: new HarmonyMethod(typeof(ReloadQuiescence), nameof(SendPrefix)));
				Patched.Add(method);
			}
		}
		if (!found)
		{
			throw new MissingMethodException("找不到联机传输的发送方法");
		}
		_executor = RunManager.Instance.ActionExecutor;
		_actionsWerePaused = _executor.IsPaused;
		_combatWasPaused = CombatManager.Instance.IsPaused;
		_transport = transport;
		_executor.Pause();
		CombatManager.Instance.Pause();
	}

	private static bool SendPrefix(object __instance, MethodBase __originalMethod, object[] __args)
	{
		if (!ReferenceEquals(__instance, _transport))
		{
			return true;
		}
		int index = Array.FindIndex(__args, arg => arg is byte[]);
		if (index < 0 || index + 1 >= __args.Length || __args[index] is not byte[] bytes || __args[index + 1] is not int length || length <= 0)
		{
			throw new InvalidOperationException("无法识别联机传输的数据包");
		}
		if (!MessageTypes.TryGetMessageType(bytes[0], out Type? type) || type == null)
		{
			return true;
		}
		if (!BufferedTypes.TryGetValue(type, out bool shouldBuffer))
		{
			shouldBuffer = ((INetMessage)Activator.CreateInstance(type)!).ShouldBuffer;
			BufferedTypes.Add(type, shouldBuffer);
		}
		if (!shouldBuffer)
		{
			return true;
		}
		object[] args = (object[])__args.Clone();
		byte[] copy = new byte[length];
		Array.Copy(bytes, copy, length);
		args[index] = copy;
		Pending.Add((__originalMethod, args));
		return false;
	}

	public static void Cancel()
	{
		object? transport = _transport;
		ActionExecutor? executor = _executor;
		bool actionsWerePaused = _actionsWerePaused;
		bool combatWasPaused = _combatWasPaused;
		var pending = Pending.ToArray();
		Clear();
		try
		{
			if (transport != null)
			{
				foreach (var packet in pending)
				{
					packet.Method.Invoke(transport, packet.Args);
				}
			}
		}
		finally
		{
			if (executor != null && ReferenceEquals(executor, RunManager.Instance.ActionExecutor))
			{
				if (!actionsWerePaused)
				{
					executor.Unpause();
				}
				if (!combatWasPaused)
				{
					CombatManager.Instance.Unpause();
				}
			}
		}
	}

	public static void Discard()
	{
		bool unpauseCombat = _transport != null;
		Clear();
		if (unpauseCombat)
		{
			CombatManager.Instance.Unpause();
		}
	}

	private static void Clear()
	{
		_transport = null;
		_executor = null;
		Pending.Clear();
	}
}

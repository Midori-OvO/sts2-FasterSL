using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace FasterSL;

internal static class ReloadNetworkPump
{
	private static SceneTree? _tree;
	private static INetGameService? _net;
	private static Action<Exception>? _onError;
	private static INetGameService? _lastNet;
	private static ulong _lastFrame;
	private static uint _generation;
	private static bool _hasPumped;
	private static bool _pumping;
	private static bool _errorReported;

	public static void Start(INetGameService net, Action<Exception> onError)
	{
		RequireMainThread();
		ArgumentNullException.ThrowIfNull(net);
		ArgumentNullException.ThrowIfNull(onError);
		SceneTree tree = Engine.GetMainLoop() as SceneTree
			?? throw new InvalidOperationException("找不到游戏场景树");
		Stop();
		tree.ProcessFrame += Pump;
		_tree = tree;
		_net = net;
		_onError = onError;
	}

	public static void Stop()
	{
		RequireMainThread();
		SceneTree? tree = _tree;
		_tree = null;
		_net = null;
		_onError = null;
		_errorReported = false;
		_generation++;
		if (tree != null && GodotObject.IsInstanceValid(tree))
		{
			tree.ProcessFrame -= Pump;
		}
	}

	public static bool ShouldSkipRunUpdate()
	{
		INetGameService? current = RunManager.Instance?.NetService;
		if (_net != null && (ReferenceEquals(current, _net) || current is NetReplayGameService))
		{
			return true;
		}
		// 本帧收包可能已经结束重载，仍不能让 NRun 再更新同一连接。
		return current != null && _hasPumped && ReferenceEquals(current, _lastNet)
			&& _lastFrame == Engine.GetProcessFrames();
	}

	private static void Pump()
	{
		INetGameService? net = _net;
		if (net == null || _pumping)
		{
			return;
		}
		ulong frame = Engine.GetProcessFrames();
		if (_hasPumped && _lastFrame == frame)
		{
			return;
		}
		uint generation = _generation;
		_pumping = true;
		_hasPumped = true;
		_lastFrame = frame;
		_lastNet = net;
		try
		{
			net.Update();
		}
		catch (Exception error)
		{
			if (generation == _generation && !_errorReported)
			{
				_errorReported = true;
				try
				{
					_onError?.Invoke(error);
				}
				catch (Exception callbackError)
				{
					Log.Error($"[FasterSL] 网络错误处理失败：{callbackError}（原始错误：{error.Message}）");
				}
			}
		}
		finally
		{
			_pumping = false;
		}
	}

	private static void RequireMainThread()
	{
		if (!NGame.IsMainThread())
		{
			throw new InvalidOperationException("重载网络轮询必须在游戏主线程管理");
		}
	}
}

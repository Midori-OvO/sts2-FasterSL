using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace FasterSL;

/// <summary>
/// 不断线重载：所有人在现有网络连接上，各自把当前局清掉，再用房主发来的房间入口存档重新载入。
///
/// 单人模式没有握手，直接本地重载（ReloadSingleplayer）。
///
/// 多人流程（房主点击 Save&Load / 在节点列表里选节点）：
///   1. 房主：暂停旧局、暂存出站消息、开启收包缓冲 → 广播 Prepare(存档)
///   2. 客户端：暂停旧局、暂存出站消息、开启收包缓冲 → 回复 Ready
///   3. 房主：收齐 Ready → 清空旧局消息 → 广播 Go
///   4. 所有人：清空缓冲 → 本地重载（CleanUp 时拦截断线）→ 原版 Launch() 会关闭缓冲并放行新局消息
/// </summary>
internal static class Reloader
{
	private const int ReadyTimeoutMs = 15000;

	private static readonly ConditionalWeakTable<INetGameService, object> RegisteredServices = new ConditionalWeakTable<INetGameService, object>();

	/// <summary>为 true 时拦截 NetService.Disconnect。只在我们调用 RunManager.CleanUp 期间为 true。</summary>
	public static bool SuppressDisconnect;

	private static bool _busy;

	public static bool IsBusy => _busy;

	/// <summary>玩家点了“保存并退出”，正在回主菜单。下一局 Launch 时清除。</summary>
	public static bool Quitting;

	private static bool _reloading;

	private static bool _cancellingHandshake;

	private static uint _seq;

	private static uint _activeSeq;

	private static SerializableRun? _pendingRun;

	private static RewindPlan? _pendingPlan;

	private static HashSet<ulong> _awaitingReady = new HashSet<ulong>();

	private static TaskCompletionSource<bool>? _allReady;

	/// <summary>回退回合时的真实联机服务：重放期间 RunManager.NetService 被换成了回放服务，同步屏障的消息要走这个。</summary>
	private static INetGameService? _rewindNet;

	private static bool _holdRewindMessages;

	private static bool _rewindReset;

	/// <summary>房主：还没报告重放完成的客户端。</summary>
	private static HashSet<ulong> _awaitingRewind = new HashSet<ulong>();

	private static bool _rewindFailed;

	/// <summary>房主：所有客户端都报告完成（或有人失败）；客户端：收到房主的 Resume，结果是是否重置。</summary>
	private static TaskCompletionSource<bool>? _rewindSignal;
	private static RewindStopState? _stopState;
	private static INetGameService? _reloadNet;
	private static TaskCompletionSource<Exception>? _reloadFailure;
	private static ulong _hostId;
	private static bool _resetCommitted;
	private static bool _failureSent;
	private static HashSet<ulong> _awaitingLoaded = new();
	private static TaskCompletionSource<bool>? _loadComplete;
	private static bool _resetPrepared;

	public static Task<Exception>? ReloadFailureTask => _reloading ? _reloadFailure?.Task : null;

	public static void ThrowIfReloadFailed()
	{
		if (_reloadFailure?.Task.IsCompleted == true)
		{
			throw _reloadFailure.Task.Result;
		}
	}

	public static void FailReload(Exception error)
	{
		if (_reloadFailure?.TrySetResult(error) != true)
		{
			return;
		}
		NotifyReloadFailure();
	}

	private static void NotifyReloadFailure()
	{
		if (_failureSent || _reloadNet == null)
		{
			return;
		}
		_failureSent = true;
		try
		{
			_reloadNet.SendMessage(new ReloadFailedMessage { seq = _activeSeq });
		}
		catch (Exception ex)
		{
			Info("通知其他玩家结束重载失败：" + ex.Message);
		}
	}

	private static void InitializeReload(INetGameService net, SerializableRun save, RewindPlan? plan)
	{
		_reloadNet = net;
		_reloadFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_failureSent = false;
		_resetCommitted = false;
		_resetPrepared = false;
		_rewindReset = false;
		_rewindFailed = false;
		_awaitingLoaded = save.Players.Select(p => p.NetId).Where(id => id != net.NetId).ToHashSet();
		_loadComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
		if (net is NetHostGameService && _awaitingLoaded.Count == 0)
		{
			_loadComplete.TrySetResult(true);
		}
		if (plan == null)
		{
			return;
		}
		_rewindNet = net;
		_holdRewindMessages = true;
		_awaitingRewind = save.Players.Select(p => p.NetId).Where(id => id != net.NetId).ToHashSet();
		_stopState = new RewindStopState(_activeSeq, net is NetHostGameService ? _awaitingRewind : Array.Empty<ulong>());
		_rewindSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
		if (net is NetHostGameService && _awaitingRewind.Count == 0)
		{
			_rewindSignal.TrySetResult(true);
		}
	}

	private static void StartNetworkPump(INetGameService net)
	{
		uint seq = _activeSeq;
		ReloadNetworkPump.Start(net, error =>
		{
			if (_activeSeq != seq || !_busy)
			{
				return;
			}
			if (_reloading)
			{
				FailReload(error);
			}
			else
			{
				TaskHelper.RunSafely(AbortHandshakeAsync(net, seq, TextId.NoResponse, error, net is NetHostGameService));
			}
		});
	}

	public static bool ShouldKeepBuffering(NetMessageBus bus)
	{
		return _holdRewindMessages && _rewindNet != null && ReferenceEquals(bus, GetBus(_rewindNet));
	}

	// 这些对象在 RunManager.CleanUp 里没有被 Dispose，但在构造时往 NetService 注册了消息处理器。
	// 原版换局会直接丢弃整个网络连接所以无所谓；我们复用连接，必须手动释放，否则旧局的处理器还会继续收消息。
	// 注意：InputSynchronizer 不在这里释放，它由 NGame.RemoteCursorContainer 持有，
	// 后面调用 RemoteCursorContainer.Initialize 时会先 Deinitialize（里面会 Dispose 旧的）。
	private static readonly string[] LeftoverDisposables =
	{
		"CombatStateSynchronizer",
		"EventSynchronizer",
		"OneOffSynchronizer",
		"MapSelectionSynchronizer",
		"TreasureRoomRelicSynchronizer",
		"HoveredModelTracker",
		"RunLocationTargetedBuffer",
		"ActChangeSynchronizer"
	};

	// ==================================================================
	// 消息处理器注册（每个 NetService 只注册一次；重载时连接不变，所以注册会一直有效）
	// ==================================================================

	public static void EnsureHandlers(INetGameService? net)
	{
		if (net == null || !net.Type.IsMultiplayer())
		{
			return;
		}
		if (RegisteredServices.TryGetValue(net, out _))
		{
			return;
		}
		RegisteredServices.Add(net, new object());
		net.RegisterMessageHandler<ReloadPrepareMessage>(OnPrepare);
		net.RegisterMessageHandler<ReloadReadyMessage>(OnReady);
		net.RegisterMessageHandler<ReloadGoMessage>(OnGo);
		net.RegisterMessageHandler<ReloadAbortMessage>(OnAbort);
		net.RegisterMessageHandler<RewindDoneMessage>(OnRewindDone);
		net.RegisterMessageHandler<RewindResumeMessage>(OnRewindResume);
		net.RegisterMessageHandler<RewindStopMessage>(OnRewindStop);
		net.RegisterMessageHandler<RewindStoppedMessage>(OnRewindStopped);
		net.RegisterMessageHandler<ReloadFailedMessage>(OnReloadFailed);
		net.RegisterMessageHandler<ReloadLoadedMessage>(OnReloadLoaded);
		net.RegisterMessageHandler<ReloadCompleteMessage>(OnReloadComplete);
	}

	// ==================================================================
	// 房主
	// ==================================================================

	/// <summary>检查当前能否发起重载（在对局中、单人或多人房主、没有正在进行的重载），不能就弹提示。</summary>
	public static bool CanStartReload()
	{
		if (Quitting)
		{
			return false;
		}
		RunManager run = RunManager.Instance;
		if (run == null || !run.IsInProgress)
		{
			Toast(Texts.Get(TextId.NotInRun));
			return false;
		}
		if (run.NetService is not NetSingleplayerGameService && run.NetService is not NetHostGameService)
		{
			Toast(Texts.Get(TextId.HostOnly));
			return false;
		}
		if (_busy)
		{
			Toast(Texts.Get(TextId.Busy));
			return false;
		}
		return true;
	}

	/// <param name="target">要回到的节点入口存档；为 null 时重载当前房间。</param>
	/// <param name="plan">多人局回退回合时的重放计划（单人的回退回合不走这里）。</param>
	public static void RequestReload(SerializableRun? target = null, RewindPlan? plan = null)
	{
		if (!CanStartReload())
		{
			return;
		}
		RunManager run = RunManager.Instance;
		bool singleplayer = run.NetService is NetSingleplayerGameService;
		SerializableRun? save = target ?? NodeHistory.FindCurrent()?.Save;
		if (save == null)
		{
			Toast(Texts.Get(TextId.NoSave));
			return;
		}
		// 存档里的绘画是进节点那一刻的；换成这一幕最新的，回档后地图上的线不会丢。多人局里这份存档会发给所有客户端。
		NodeHistory.ApplyLatestDrawings(save);
		save = NodeHistory.Clone(save);
		if (singleplayer)
		{
			_busy = true;
			Info("[单人] 重载到" + DescribeTarget(save));
			Toast(Texts.Get(TextId.Reloading));
			ReloadSingleplayer(save);
			return;
		}
		NetHostGameService host = (NetHostGameService)run.NetService;
		// ConnectedPeers 的返回类型两个版本不同（正式版 IReadOnlyList，Beta 版 List），直接调用会在另一个版本上 MissingMethodException
		HashSet<ulong> connected = ((AccessTools.Property(typeof(NetHostGameService), "ConnectedPeers")?.GetValue(host) as IEnumerable<NetClientData>) ?? Enumerable.Empty<NetClientData>())
			.Select(p => p.peerId).ToHashSet();
		List<ulong> clients = save.Players.Select(p => p.NetId).Where(id => id != host.NetId).ToList();
		List<ulong> missing = clients.Where(id => !connected.Contains(id)).ToList();
		if (missing.Count > 0)
		{
			Toast(Texts.Get(TextId.PlayersOffline, string.Join(",", missing)));
			return;
		}

		_activeSeq = ++_seq;
		try
		{
			ReloadQuiescence.Begin(host);
			_busy = true;
			_pendingRun = save;
			_pendingPlan = plan;
			_awaitingReady = clients.ToHashSet();
			_allReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			if (_awaitingReady.Count == 0)
			{
				_allReady.TrySetResult(true);
			}

			StartBuffering(host);
			StartNetworkPump(host);
			Info($"[房主] 重载到{DescribeTarget(save)}，等待 {_awaitingReady.Count} 名客户端就绪");
			Toast(Texts.Get(TextId.Reloading));
			host.SendMessage(new ReloadPrepareMessage { seq = _activeSeq, run = save, plan = plan });
			TaskHelper.RunSafely(HostWaitThenGo(host, _activeSeq));
		}
		catch (Exception ex)
		{
			TaskHelper.RunSafely(AbortHandshakeAsync(host, _activeSeq, TextId.NoResponse, ex, notifyClients: true));
		}
	}

	private static async Task HostWaitThenGo(NetHostGameService host, uint seq)
	{
		try
		{
			Task ready = _allReady!.Task;
			Task finished = await Task.WhenAny(ready, Task.Delay(ReadyTimeoutMs));
			if (seq != _activeSeq || _cancellingHandshake || _pendingRun == null)
			{
				return;
			}
			if (finished != ready)
			{
				Info($"[房主] 等待就绪超时，未就绪：{string.Join(",", _awaitingReady)}。取消本次重载");
				await AbortHandshakeAsync(host, seq, TextId.NoResponse, notifyClients: true);
				return;
			}
			InitializeReload(host, _pendingRun, _pendingPlan);
			await ReloadLocally(_pendingRun, _pendingPlan, sendGo: true);
		}
		catch (Exception ex)
		{
			await AbortHandshakeAsync(host, seq, TextId.NoResponse, ex, notifyClients: true);
		}
	}

	private static void OnReady(ReloadReadyMessage message, ulong senderId)
	{
		if (message.seq != _activeSeq || _allReady == null)
		{
			return;
		}
		_awaitingReady.Remove(senderId);
		if (_awaitingReady.Count == 0)
		{
			_allReady.TrySetResult(true);
		}
	}

	// ==================================================================
	// 客户端
	// ==================================================================

	private static void OnPrepare(ReloadPrepareMessage message, ulong senderId)
	{
		INetGameService? net = RunManager.Instance?.NetService;
		if (net is not NetClientGameService client || _busy || _cancellingHandshake)
		{
			return;
		}
		_activeSeq = message.seq;
		_hostId = senderId;
		try
		{
			ReloadQuiescence.Begin(client);
			_busy = true;
			_pendingRun = message.run;
			_pendingPlan = message.plan;
			StartBuffering(client);
			StartNetworkPump(client);
			Info("[客户端] 房主发起重载到" + DescribeTarget(message.run));
			Toast(Texts.Get(TextId.HostReloading));
			client.SendMessage(new ReloadReadyMessage { seq = message.seq });
			TaskHelper.RunSafely(ClientGoTimeout(client, message.seq));
		}
		catch (Exception ex)
		{
			TaskHelper.RunSafely(AbortHandshakeAsync(client, message.seq, TextId.HostCancelled, ex));
		}
	}

	/// <summary>房主迟迟不发 Go/Abort（比如房主掉线或报错）时，恢复正常处理消息，避免客户端永远卡在缓冲状态。</summary>
	private static async Task ClientGoTimeout(NetClientGameService client, uint seq)
	{
		await Task.Delay(ReadyTimeoutMs * 2);
		if (_activeSeq == seq && _pendingRun != null && _busy && !_reloading)
		{
			Info($"[客户端] 等待 Go 超时 seq={seq}，恢复处理消息");
			await AbortHandshakeAsync(client, seq, TextId.HostTimeout);
		}
	}

	private static void OnGo(ReloadGoMessage message, ulong senderId)
	{
		INetGameService? net = RunManager.Instance?.NetService;
		if (net is not NetClientGameService client || senderId != _hostId || message.seq != _activeSeq || _pendingRun == null || _reloading)
		{
			return;
		}
		ClearBufferedMessages(client);
		InitializeReload(client, _pendingRun, _pendingPlan);
		TaskHelper.RunSafely(ReloadLocally(_pendingRun, _pendingPlan));
	}

	private static void OnAbort(ReloadAbortMessage message, ulong senderId)
	{
		INetGameService? net = _reloadNet ?? RunManager.Instance?.NetService;
		if (net is not NetClientGameService client || senderId != _hostId || message.seq != _activeSeq)
		{
			return;
		}
		if (_reloading)
		{
			FailReload(new InvalidOperationException("房主已中止本次重载"));
			return;
		}
		Info("[客户端] 房主取消了重载");
		TaskHelper.RunSafely(AbortHandshakeAsync(client, message.seq, TextId.HostCancelled));
	}

	private static async Task AbortHandshakeAsync(INetGameService net, uint seq, TextId notice, Exception? error = null, bool notifyClients = false)
	{
		if (_activeSeq != seq || _reloading || _cancellingHandshake)
		{
			return;
		}
		_cancellingHandshake = true;
		_busy = true;
		_pendingRun = null;
		_pendingPlan = null;
		_allReady = null;
		if (error != null)
		{
			Log.Error($"{LogPrefix} 重载握手失败：{error}");
		}
		SceneTree tree = (SceneTree)Engine.GetMainLoop();
		await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
		try
		{
			if (notifyClients)
			{
				try
				{
					net.SendMessage(new ReloadAbortMessage { seq = seq });
				}
				catch (Exception ex)
				{
					Log.Error($"{LogPrefix} 通知客户端取消失败：{ex}");
				}
			}
			try
			{
				ReloadQuiescence.Cancel();
			}
			catch (Exception ex)
			{
				Log.Error($"{LogPrefix} 恢复旧局发送失败：{ex}");
			}
			_holdRewindMessages = false;
			try
			{
				StopBuffering(net);
			}
			catch (Exception ex)
			{
				Log.Error($"{LogPrefix} 恢复旧局消息处理失败：{ex}");
			}
			Toast(Texts.Get(notice));
		}
		finally
		{
			ReloadNetworkPump.Stop();
			_rewindNet = null;
			_rewindSignal = null;
			_rewindReset = false;
			_rewindFailed = false;
			_cancellingHandshake = false;
			_busy = false;
			_reloadNet = null;
			_reloadFailure = null;
			_stopState = null;
			_loadComplete = null;
			_resetPrepared = false;
			_resetCommitted = false;
		}
	}

	// ==================================================================
	// 本地重载（房主和客户端都走这里）
	// ==================================================================

	/// <param name="plan">回退回合时的重放计划：载入后先重放，再和其他人同步；需要重置时所有人用同一份存档再重载一次。</param>
	private static async Task ReloadLocally(SerializableRun save, RewindPlan? plan = null, bool sendGo = false)
	{
		RunManager runManager = RunManager.Instance;
		INetGameService net = runManager.NetService;
		_reloading = true;
		try
		{
			// 先让出一帧，避免在 NetService.Update() 派发消息的过程中拆掉整局
			SceneTree tree = (SceneTree)Engine.GetMainLoop();
			await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
			ThrowIfReloadFailed();
			await NGame.Instance!.Transition.FadeOut();
			ThrowIfReloadFailed();
			SerializableRun? retry = null;
			if (plan != null)
			{
				retry = NodeHistory.Clone(save);
				TurnRewind.Begin(plan, canReset: net is NetHostGameService);
				if (_rewindReset || _rewindFailed)
				{
					TurnRewind.RequestReset();
				}
			}
			while (true)
			{
				ThrowIfReloadFailed();
				await LoadMultiplayerRun(runManager, net, save, plan != null, sendGo);
				sendGo = false;
				ThrowIfReloadFailed();
				if (plan == null || !await TurnRewind.RunMultiplayer() || retry == null)
				{
					break;
				}
				plan = null;
				save = retry;
				Info("[多人] 所有人一起重置战斗");
				await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
			}
			await WaitForAllLoaded(net);
			await NGame.Instance.Transition.FadeIn();
			ThrowIfReloadFailed();
			Info($"[多人] 重载完成（{save.Players.Count} 名玩家）");
		}
		catch (Exception ex)
		{
			FailReload(ex);
			SceneTree tree = (SceneTree)Engine.GetMainLoop();
			await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
			Log.Error($"{LogPrefix} 重载失败：{ex}");
			SuppressDisconnect = false;
			_holdRewindMessages = false;
			try { TurnRewind.Abort(); }
			catch (Exception cleanupError) { Info("恢复回放状态失败：" + cleanupError.Message); }
			try { ReloadQuiescence.Discard(); }
			catch (Exception cleanupError) { Info("清理暂停状态失败：" + cleanupError.Message); }
			try { runManager.CleanUp(graceful: false); }
			catch (Exception cleanupError) { Info("清理旧局失败：" + cleanupError.Message); }
			try { net.Disconnect(NetError.Quit); }
			catch (Exception cleanupError) { Info("关闭重载连接失败：" + cleanupError.Message); }
			try
			{
				await NGame.Instance!.ReturnToMainMenuWithInternalError(ex);
				Toast(Texts.Get(TextId.ReloadStopped));
			}
			catch (Exception cleanupError)
			{
				Log.Error($"{LogPrefix} 返回主菜单失败：{cleanupError}");
			}
		}
		finally
		{
			ReloadNetworkPump.Stop();
			ReloadQuiescence.Discard();
			_reloading = false;
			_busy = false;
			_pendingRun = null;
			_pendingPlan = null;
			_rewindNet = null;
			_rewindSignal = null;
			_holdRewindMessages = false;
			_rewindReset = false;
			_rewindFailed = false;
			_stopState = null;
			_reloadNet = null;
			_reloadFailure = null;
			_loadComplete = null;
		}
	}

	private static async Task LoadMultiplayerRun(RunManager runManager, INetGameService net, SerializableRun save, bool replay, bool sendGo)
	{
		if (!_resetPrepared)
		{
			CleanUpForReload(runManager);
		}
		_resetPrepared = false;
		ReloadQuiescence.Discard();
		net.SetBufferMessages(true);
		if (!net.IsConnected)
		{
			throw new InvalidOperationException("重载期间连接已断开");
		}
		if (sendGo)
		{
			ClearBufferedMessages(net);
			net.SendMessage(new ReloadGoMessage { seq = _activeSeq });
		}

		LoadRunLobby lobby = new LoadRunLobby(net, SilentLobbyListener.Create(), save);
		try
		{
			List<ulong> playerIds = save.Players.Select(p => p.NetId).ToList();
			LobbyCompat.AddReadyPlayers(lobby, playerIds, net);
			NGame.Instance!.RemoteCursorContainer.Initialize(lobby.InputSynchronizer, playerIds);
			NGame.Instance.ReactionContainer.InitializeNetworking(net);
			RunState runState = RunState.FromSerializable(save);
			await runManager.SetUpSavedMultiplayer(runState, lobby);
			ThrowIfReloadFailed();
			if (!replay)
			{
				_holdRewindMessages = false;
			}
			await NGame.Instance.LoadRun(runState, save.PreFinishedRoom);
			ThrowIfReloadFailed();
		}
		finally
		{
			lobby.CleanUp(disconnectSession: false);
		}
	}

	private static void CleanUpForReload(RunManager runManager)
	{
		List<IDisposable> leftovers = CollectLeftovers(runManager);
		SuppressDisconnect = true;
		try
		{
			runManager.CleanUp(graceful: false);
		}
		finally
		{
			SuppressDisconnect = false;
		}
		foreach (IDisposable item in leftovers)
		{
			try
			{
				item.Dispose();
			}
			catch (Exception ex)
			{
				Info($"[重载] 释放 {item.GetType().Name} 失败：{ex.Message}");
			}
		}
	}

	// ==================================================================
	// 多人回退回合的同步屏障
	// ==================================================================

	/// <summary>
	/// 本地重放结束后调用。房主等所有客户端报告完成，再广播 Resume；客户端报告完成后等房主的 Resume。
	/// 任何一方失败或房主要求重置，都会让所有人一起重载到战斗入口。
	/// </summary>
	/// <returns>true 表示要重置战斗。</returns>
	public static async Task<bool> RewindBarrier(bool ok, bool resetRequested)
	{
		INetGameService? net = _rewindNet;
		TaskCompletionSource<bool>? signal = _rewindSignal;
		RewindStopState? stop = _stopState;
		if (net == null || signal == null || stop == null)
		{
			throw new InvalidOperationException("多人回退同步状态丢失");
		}
		ThrowIfReloadFailed();
		if (net is NetHostGameService host)
		{
			if (!ok || resetRequested || _rewindFailed || TurnRewind.ResetRequested)
			{
				RequestRewindStop();
			}
			if (!stop.IsRequested)
			{
				Task completed = Task.WhenAny(signal.Task, stop.Requested);
				try
				{
					await WaitForReloadStep(completed, ReadyTimeoutMs * 4, "等待客户端重放完成超时");
				}
				catch (TimeoutException) when (_reloadFailure?.Task.IsCompleted != true)
				{
					Info($"[房主] 等待客户端重放完成超时：{string.Join(",", _awaitingRewind)}，重置战斗");
					RequestRewindStop();
				}
			}
			if (stop.IsRequested)
			{
				await ConfirmRewindStopped(net, stop);
				await WaitForReloadStep(stop.AllStopped, ReadyTimeoutMs,
					$"等待玩家停止回退超时：{string.Join(",", stop.PendingPeers)}");
				_resetCommitted = true;
				ClearBufferedMessages(net);
				ReloadQuiescence.Discard();
				host.SendMessage(new RewindResumeMessage { seq = _activeSeq, reset = true });
				return true;
			}
			TurnRewind.RestoreService();
			_resetCommitted = true;
			host.SendMessage(new RewindResumeMessage { seq = _activeSeq, reset = false });
			_holdRewindMessages = false;
			StopBuffering(net);
			return false;
		}
		net.SendMessage(new RewindDoneMessage { seq = _activeSeq, ok = ok });
		await WaitForReloadStep(Task.WhenAny(signal.Task, stop.Requested), ReadyTimeoutMs * 8, "等待房主确认回退结果超时");
		if (stop.IsRequested)
		{
			await ConfirmRewindStopped(net, stop);
			await WaitForReloadStep(signal.Task, ReadyTimeoutMs * 2, "等待房主开始重置战斗超时");
			if (!signal.Task.Result)
			{
				throw new InvalidOperationException("停止回退后收到不一致的恢复指令");
			}
			return true;
		}
		SceneTree tree = (SceneTree)Engine.GetMainLoop();
		await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
		ThrowIfReloadFailed();
		_holdRewindMessages = false;
		StopBuffering(net);
		return false;
	}

	private static async Task WaitForReloadStep(Task task, int timeoutMs, string context)
	{
		Task<Exception> failed = _reloadFailure?.Task ?? throw new InvalidOperationException("重载状态丢失");
		Task finished = await Task.WhenAny(task, failed, Task.Delay(timeoutMs));
		ThrowIfReloadFailed();
		if (finished != task)
		{
			throw new TimeoutException(context);
		}
		await task;
	}

	public static void RequestRewindStop()
	{
		if (_rewindNet is not NetHostGameService host || _stopState == null || _resetCommitted
			|| _reloadFailure?.Task.IsCompleted == true || !_stopState.Request(_activeSeq))
		{
			return;
		}
		_rewindReset = true;
		_holdRewindMessages = true;
		try
		{
			host.SetBufferMessages(true);
			host.SendMessage(new RewindStopMessage { seq = _activeSeq });
			TurnRewind.RequestReset();
			TaskHelper.RunSafely(WatchRewindStop(_stopState, _activeSeq));
		}
		catch (Exception ex)
		{
			FailReload(ex);
		}
	}

	private static async Task WatchRewindStop(RewindStopState stop, uint seq)
	{
		await Task.WhenAny(stop.AllStopped, Task.Delay(ReadyTimeoutMs));
		if (_activeSeq == seq && ReferenceEquals(stop, _stopState) && !_resetCommitted && !stop.AllStopped.IsCompleted)
		{
			FailReload(new TimeoutException($"等待停止回退超时，未确认玩家：{string.Join(",", stop.PendingPeers)}"));
		}
	}

	private static async Task ConfirmRewindStopped(INetGameService net, RewindStopState stop)
	{
		SceneTree tree = (SceneTree)Engine.GetMainLoop();
		await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
		ThrowIfReloadFailed();
		ReloadQuiescence.Begin(net);
		net.SetBufferMessages(true);
		TurnRewind.RestoreService();
		CleanUpForReload(RunManager.Instance);
		NGame.Instance!.RemoteCursorContainer.Deinitialize();
		NGame.Instance.RootSceneContainer.SetCurrentScene(new Control());
		_resetPrepared = true;
		ClearBufferedMessages(net);
		stop.ConfirmLocalStopped();
		if (net is NetClientGameService)
		{
			net.SendMessage(new RewindStoppedMessage { seq = _activeSeq });
		}
	}

	private static void OnRewindDone(RewindDoneMessage message, ulong senderId)
	{
		if (message.seq != _activeSeq || _rewindSignal == null || _rewindNet is not NetHostGameService)
		{
			return;
		}
		if (!_awaitingRewind.Remove(senderId))
		{
			return;
		}
		if (!message.ok)
		{
			Info($"[房主] 客户端 {senderId} 重放失败");
			_rewindFailed = true;
			TurnRewind.RequestReset();
		}
		if (_awaitingRewind.Count == 0 || _rewindFailed)
		{
			_rewindSignal.TrySetResult(true);
		}
	}

	private static void OnRewindResume(RewindResumeMessage message, ulong senderId)
	{
		if (message.seq != _activeSeq || senderId != _hostId || _rewindSignal == null
			|| _rewindNet is not NetClientGameService || _rewindSignal.Task.IsCompleted)
		{
			return;
		}
		if (message.reset)
		{
			if (_stopState?.AllStopped.IsCompleted != true)
			{
				FailReload(new InvalidOperationException("本地尚未停止回退就收到重置指令"));
				return;
			}
			_resetCommitted = true;
			ClearBufferedMessages(_rewindNet);
		}
		else
		{
			if (_stopState?.IsRequested == true)
			{
				FailReload(new InvalidOperationException("停止回退后收到恢复回放指令"));
				return;
			}
			TurnRewind.RestoreService();
			_resetCommitted = true;
		}
		_rewindSignal.TrySetResult(message.reset);
	}

	private static void OnRewindStop(RewindStopMessage message, ulong senderId)
	{
		if (_rewindNet is not NetClientGameService || senderId != _hostId || _stopState == null
			|| _resetCommitted || !_stopState.Request(message.seq))
		{
			return;
		}
		_rewindReset = true;
		_holdRewindMessages = true;
		_rewindNet.SetBufferMessages(true);
		TurnRewind.RequestReset();
	}

	private static void OnRewindStopped(RewindStoppedMessage message, ulong senderId)
	{
		if (_rewindNet is NetHostGameService)
		{
			_stopState?.ConfirmPeer(message.seq, senderId);
		}
	}

	private static void OnReloadFailed(ReloadFailedMessage message, ulong senderId)
	{
		if (message.seq != _activeSeq || _reloadNet == null || !_reloading || _pendingRun == null
			|| (_reloadNet is NetClientGameService && senderId != _hostId)
			|| !_pendingRun.Players.Any(p => p.NetId == senderId))
		{
			return;
		}
		if (_reloadNet is NetClientGameService)
		{
			_failureSent = true;
		}
		FailReload(new InvalidOperationException($"玩家 {senderId} 已中止本次重载"));
	}

	private static async Task WaitForAllLoaded(INetGameService net)
	{
		Task completed = _loadComplete?.Task ?? throw new InvalidOperationException("加载确认状态丢失");
		if (net is NetClientGameService)
		{
			net.SendMessage(new ReloadLoadedMessage { seq = _activeSeq });
		}
		await WaitForReloadStep(completed, ReadyTimeoutMs * 3, "等待所有玩家完成重载超时");
		if (net is NetHostGameService)
		{
			net.SendMessage(new ReloadCompleteMessage { seq = _activeSeq });
		}
	}

	private static void OnReloadLoaded(ReloadLoadedMessage message, ulong senderId)
	{
		if (_reloadNet is NetHostGameService && message.seq == _activeSeq && _awaitingLoaded.Remove(senderId)
			&& _awaitingLoaded.Count == 0)
		{
			_loadComplete?.TrySetResult(true);
		}
	}

	private static void OnReloadComplete(ReloadCompleteMessage message, ulong senderId)
	{
		if (_reloadNet is NetClientGameService && message.seq == _activeSeq && senderId == _hostId)
		{
			_loadComplete?.TrySetResult(true);
		}
	}

	/// <summary>
	/// 单人模式：没有其他玩家，照原版“继续游戏”的流程直接本地重载。
	/// afterLoad 在载入完成、淡入之前执行；返回 true 时再用同一份存档原样重载一次（回合回退失败或被中止时回到战斗入口）。
	/// </summary>
	public static void ReloadSingleplayer(SerializableRun save, Func<Task<bool>>? afterLoad = null)
	{
		_busy = true;
		TaskHelper.RunSafely(ReloadSingleplayerAsync(save, afterLoad));
	}

	private static async Task ReloadSingleplayerAsync(SerializableRun save, Func<Task<bool>>? afterLoad)
	{
		RunManager runManager = RunManager.Instance;
		try
		{
			SceneTree tree = (SceneTree)Engine.GetMainLoop();
			await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

			await NGame.Instance!.Transition.FadeOut();
			// 读档会改动存档里的列表，第二次重载要用没动过的副本
			SerializableRun? retry = afterLoad != null ? NodeHistory.Clone(save) : null;
			while (true)
			{
				runManager.CleanUp();
				RunState runState = RunState.FromSerializable(save);
				await runManager.SetUpSavedSingleplayer(runState, save);
				NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
				await NGame.Instance.LoadRun(runState, save.PreFinishedRoom);
				if (afterLoad == null || !await afterLoad() || retry == null)
				{
					break;
				}
				afterLoad = null;
				save = retry;
			}
			Info("[单人] 重载完成");
			await NGame.Instance.Transition.FadeIn();
		}
		catch (Exception ex)
		{
			TurnRewind.Abort();
			Log.Error($"{LogPrefix} 单人重载失败：{ex}");
			try
			{
				await NGame.Instance!.ReturnToMainMenuWithInternalError(ex);
			}
			catch
			{
			}
		}
		finally
		{
			_busy = false;
		}
	}

	private static List<IDisposable> CollectLeftovers(RunManager runManager)
	{
		List<IDisposable> list = new List<IDisposable>();
		foreach (string name in LeftoverDisposables)
		{
			try
			{
				if (AccessTools.Property(typeof(RunManager), name)?.GetValue(runManager) is IDisposable disposable)
				{
					list.Add(disposable);
				}
			}
			catch
			{
			}
		}
		return list;
	}

	// ==================================================================
	// 消息总线工具
	// ==================================================================

	private static void StartBuffering(INetGameService net)
	{
		net.SetBufferMessages(true);
	}

	private static void StopBuffering(INetGameService net)
	{
		net.SetBufferMessages(false);
	}

	private static object? GetBus(INetGameService net)
	{
		return AccessTools.Field(net.GetType(), "_messageBus")?.GetValue(net);
	}

	private static void ClearBufferedMessages(INetGameService net)
	{
		try
		{
			object? bus = GetBus(net);
			if (bus != null && AccessTools.Field(bus.GetType(), "_bufferedMessages")?.GetValue(bus) is IList list)
			{
				list.Clear();
			}
		}
		catch (Exception ex)
		{
			Info($"清空缓冲失败：{ex.Message}");
		}
	}

	// ==================================================================
	// 杂项
	// ==================================================================

	public const string LogPrefix = "[FasterSL]";

	/// <summary>只用于关键事件和出错信息；屏幕提示、握手细节之类不写日志。</summary>
	public static void Info(string message)
	{
		Log.Info(LogPrefix + " " + message);
	}

	private static string DescribeTarget(SerializableRun save)
	{
		return $"第 {save.CurrentActIndex + 1} 幕第 {save.FloorReached + 1} 层";
	}

	public static void Toast(string text)
	{
		try
		{
			NFullscreenTextVfx? vfx = NFullscreenTextVfx.Create(text);
			if (vfx != null)
			{
				NGame.Instance?.AddChildSafely(vfx);
			}
		}
		catch
		{
		}
	}
}

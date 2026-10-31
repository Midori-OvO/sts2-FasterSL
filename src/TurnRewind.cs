using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace FasterSL;

/// <summary>
/// 回退到本场战斗的某个回合：先重载到战斗入口，再用游戏自带的战斗回放机制把之前的操作快进重放到那个回合开头。
///
/// 做法和原版调试用的 NMultiplayerTest.RunReplay 一样：进房间前把各种 id 拨到录制时的值，
/// 进房间时把网络服务换成 NetReplayGameService（Type = Replay），这样游戏不会自己生成钩子动作、不弹选择界面、不发联机消息，
/// 全部由录下来的事件驱动；重放到目标回合开始后再换回原来的服务。
/// 只换对象不打补丁：NetService.Type 这种小 getter 会被 JIT 内联，Harmony 补丁不一定生效。
///
/// 多人局：房主把重放计划随 Prepare 发给所有人，每个人各自在本地重放；全部重放完之后由房主统一发 Resume，
/// 大家再同时换回联机服务，避免先恢复的人往还在重放的人那里发消息。
/// </summary>
internal static class TurnRewind
{
	private const float FastForwardTimeScale = 10f;

	private const int TurnTimeoutMs = 30000;

	private static RewindPlan? _plan;

	private static int _replayedTurn;

	/// <summary>放弃重放、改为重载到战斗入口（“重置战斗”按钮，或多人局里房主要求重置）。</summary>
	private static TaskCompletionSource<bool>? _reset;

	private static readonly TaskCompletionSource<bool> NeverReset = new TaskCompletionSource<bool>();

	private static INetGameService? _realService;

	private static NetReplayGameService? _replayService;

	public static bool IsFastForwarding => _plan != null;

	public static Task ResetTask => _reset?.Task ?? NeverReset.Task;

	public static bool ResetRequested => _reset?.Task.IsCompleted == true;

	public static void Request(TurnMark mark)
	{
		if (!Reloader.CanStartReload())
		{
			return;
		}
		RunManager run = RunManager.Instance;
		CombatReplay? replay = TurnHistory.CurrentReplay();
		bool singleplayer = run.NetService is NetSingleplayerGameService;
		if ((!singleplayer && run.NetService is not NetHostGameService) || !CombatManager.Instance.IsInProgress || !TurnHistory.IsCurrent(replay) || !TurnHistory.Contains(mark) || mark.EventIndex > replay!.events.Count)
		{
			Reloader.Toast(Texts.Get(TextId.TurnEmpty));
			return;
		}
		RewindPlan plan = RewindPlan.From(replay, mark, TurnHistory.Starts);
		Reloader.Info($"回退到本场战斗第 {mark.Turn} 回合（重放 {plan.Events.Count} 个事件）");
		if (singleplayer)
		{
			SerializableRun save = replay.serializableRun;
			NodeHistory.ApplyLatestDrawings(save);
			Begin(plan, canReset: true);
			Reloader.ReloadSingleplayer(NodeHistory.Clone(save), RunSingleplayer);
		}
		else
		{
			Reloader.RequestReload(replay.serializableRun, plan);
		}
	}

	/// <summary>开始一次重放：记下计划、盖上遮罩。之后的重载会在进房间前（BeforeRoomEntry）切到回放模式。</summary>
	public static void Begin(RewindPlan plan, bool canReset)
	{
		_plan = plan;
		_replayedTurn = 0;
		_reset = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		ShowOverlay(canReset);
	}

	/// <summary>多人局里收到房主的重置要求：中止还在进行的重放。</summary>
	public static void RequestReset()
	{
		_reset?.TrySetResult(true);
		if (_progress != null && GodotObject.IsInstanceValid(_progress))
		{
			_progress.Text = Texts.Get(TextId.TurnStopping);
		}
		Reloader.RequestRewindStop();
	}

	private static async Task<bool> RunSingleplayer()
	{
		bool ok = await FastForward();
		Finish();
		return !ok;
	}

	/// <summary>多人局的 afterLoad：本地重放完成后和其他人同步，返回 true 表示所有人一起重载到战斗入口。</summary>
	public static async Task<bool> RunMultiplayer()
	{
		bool ok = await FastForward();
		bool reset = await Reloader.RewindBarrier(ok, ResetRequested);
		Finish();
		return reset;
	}

	// ==================================================================
	// 遮罩
	// ==================================================================

	private static CanvasLayer? _overlay;

	private static Label? _progress;

	private static bool _muted;

	/// <summary>重放期间盖在最上层的黑屏，挡住加速播放的动画、声音和输入，并显示快进到了第几回合。</summary>
	private static void ShowOverlay(bool canReset)
	{
		HideOverlay();
		_overlay = new CanvasLayer { Layer = 200 };
		ColorRect black = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Stop };
		black.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_overlay.AddChild(black);
		CenterContainer center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_overlay.AddChild(center);
		VBoxContainer box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		box.AddThemeConstantOverride("separation", 16);
		center.AddChild(box);
		Label title = Ui.MakeLabel(Texts.Get(TextId.TurnRewinding, _plan?.TargetTurn ?? 0), 40, new Color(0.95f, 0.85f, 0.6f));
		title.AutowrapMode = TextServer.AutowrapMode.Off;
		title.HorizontalAlignment = HorizontalAlignment.Center;
		box.AddChild(title);
		_progress = Ui.MakeLabel("", 28, new Color(0.8f, 0.8f, 0.82f));
		_progress.AutowrapMode = TextServer.AutowrapMode.Off;
		_progress.HorizontalAlignment = HorizontalAlignment.Center;
		box.AddChild(_progress);
		if (canReset)
		{
			Button reset = Ui.MakeButton(Texts.Get(TextId.TurnReset), 24);
			reset.CustomMinimumSize = new Vector2(240f, 56f);
			reset.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
			reset.Pressed += () =>
			{
				reset.Disabled = true;
				RequestReset();
			};
			box.AddChild(new Control { CustomMinimumSize = new Vector2(0f, 24f) });
			box.AddChild(reset);
		}
		NGame.Instance?.AddChildSafely(_overlay);
		SetMuted(true);
	}

	private static void HideOverlay()
	{
		if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
		{
			_overlay.QueueFree();
		}
		_overlay = null;
		_progress = null;
		SetMuted(false);
	}

	/// <summary>和游戏“切到后台静音”用同一套接口（FMOD 和 Godot 音频），另外把 Godot 主总线也静音，盖住其他 mod 自己播的语音。</summary>
	private static void SetMuted(bool mute)
	{
		if (_muted == mute)
		{
			return;
		}
		_muted = mute;
		try
		{
			float volume = mute ? 0f : SaveManager.Instance.SettingsSave.VolumeMaster;
			NGame.Instance?.AudioManager?.SetMasterVol(volume);
			NGame.Instance?.DebugAudio?.SetMasterAudioVolume(volume);
			AudioServer.SetBusMute(0, mute);
		}
		catch (Exception ex)
		{
			Reloader.Info("切换静音失败：" + ex.Message);
		}
	}

	// ==================================================================
	// 重放
	// ==================================================================

	/// <summary>RunManager.LoadIntoLatestMapCoord 的 prefix：进房间之前切到回放模式。</summary>
	public static void BeforeRoomEntry(RunManager run)
	{
		if (_plan == null || _replayService != null)
		{
			return;
		}
		run.ActionQueueSet.FastForwardNextActionId(_plan.NextActionId);
		run.ActionQueueSynchronizer.FastForwardHookId(_plan.NextHookId);
		run.PlayerChoiceSynchronizer.FastForwardChoiceIds(_plan.ChoiceIds.ToList());
		run.RewardsSetSynchronizer.FastForwardRewardIds(_plan.RewardIds.ToList());
		_realService = run.NetService;
		_replayService = new NetReplayGameService(_realService.NetId);
		SwapService(run, _realService, _replayService);
		run.CombatStateSynchronizer.IsDisabled = true;
		Engine.TimeScale = FastForwardTimeScale;
	}

	public static void OnTurnStarted(int turn)
	{
		if (_plan == null)
		{
			return;
		}
		_replayedTurn = turn;
		if (_progress != null && GodotObject.IsInstanceValid(_progress))
		{
			_progress.Text = Texts.Get(TextId.TurnProgress, turn, _plan.TargetTurn);
		}
	}

	private static void ThrowIfReset()
	{
		Reloader.ThrowIfReloadFailed();
		if (ResetRequested)
		{
			throw new OperationCanceledException();
		}
	}

	private static async Task NextFrame(SceneTree tree)
	{
		await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
		ThrowIfReset();
	}

	/// <summary>等 task 完成；中途要求重置就立刻放弃等待（重放可能已经卡死，task 永远不会完成）。</summary>
	private static async Task<bool> WaitOrReset(Task task, int timeoutMs)
	{
		Task finished = await Task.WhenAny(task, ResetTask,
			(Task?)Reloader.ReloadFailureTask ?? NeverReset.Task, Task.Delay(timeoutMs));
		ThrowIfReset();
		if (finished != task)
		{
			return false;
		}
		await task;
		return true;
	}

	private static async Task WaitUntil(SceneTree tree, Func<bool> ready, string context)
	{
		ulong deadline = Time.GetTicksMsec() + TurnTimeoutMs;
		while (!ready())
		{
			ThrowIfReset();
			if (Time.GetTicksMsec() >= deadline)
			{
				throw new TimeoutException(context);
			}
			await NextFrame(tree);
		}
		ThrowIfReset();
	}

	/// <returns>true 表示已经重放到目标回合开始。</returns>
	private static async Task<bool> FastForward()
	{
		RewindPlan? plan = _plan;
		if (plan == null)
		{
			return false;
		}
		try
		{
			ThrowIfReset();
			if (_replayService == null)
			{
				throw new InvalidOperationException("没有进入回放模式");
			}
			RunManager run = RunManager.Instance;
			RunState state = run.DebugOnlyGetState()!;
			SceneTree tree = (SceneTree)Engine.GetMainLoop();
			await WaitUntil(tree, () => !run.ActionExecutor.IsPaused, "等待战斗出牌阶段超时");
			int nextStart = 0;
			for (int i = 0; i < plan.Events.Count; i++)
			{
				ThrowIfReset();
				// 原版重放只靠 CurrentSide 判断时机，敌人回合还没真正开始时就会把下一回合的操作塞进去；
				// 所以每个回合起点的事件，要等重放出来的这个回合真正开始了再继续
				while (nextStart < plan.TurnStarts.Count && plan.TurnStarts[nextStart].EventIndex <= i)
				{
					int turn = plan.TurnStarts[nextStart].Turn;
					await WaitUntil(tree, () => _replayedTurn >= turn, $"重放到第 {i} 个事件时，没有等到第 {turn} 回合开始");
					nextStart++;
				}
				CombatReplayEvent e = plan.Events[i];
				switch (e.eventType)
				{
				case CombatReplayEventType.GameAction:
				{
					await WaitUntil(tree, () => !CombatManager.Instance.EndingPlayerTurnPhaseOne && !CombatManager.Instance.EndingPlayerTurnPhaseTwo,
						$"重放到第 {i} 个事件时，等待回合结算超时");
					GameAction action = e.action!.ToGameAction(state.GetPlayer(e.playerId!.Value)!);
					if (action.ActionType == GameActionType.CombatPlayPhaseOnly)
					{
						await WaitUntil(tree, () => CombatManager.Instance.DebugOnlyGetState()?.CurrentSide == CombatSide.Player,
							$"重放到第 {i} 个事件时，等待玩家回合超时");
					}
					run.ActionQueueSet.EnqueueWithoutSynchronizing(action);
					if (action is EndPlayerTurnAction || action is ReadyToBeginEnemyTurnAction)
					{
						if (!await WaitOrReset(run.ActionExecutor.FinishedExecutingActions(), TurnTimeoutMs))
						{
							throw new TimeoutException($"重放到第 {i} 个事件时，动作一直没有执行完");
						}
					}
					break;
				}
				case CombatReplayEventType.HookAction:
					run.ActionQueueSet.EnqueueWithoutSynchronizing(run.ActionQueueSynchronizer.GetHookActionForId(e.hookId!.Value, e.playerId!.Value, e.gameActionType!.Value));
					break;
				case CombatReplayEventType.ResumeAction:
					run.ActionQueueSet.ResumeActionWithoutSynchronizing(e.actionId!.Value);
					break;
				case CombatReplayEventType.PlayerChoice:
					run.PlayerChoiceSynchronizer.ReceiveReplayChoice(state.GetPlayer(e.playerId!.Value)!, e.choiceId!.Value, e.playerChoiceResult!.Value);
					break;
				default:
					throw new InvalidEnumArgumentException();
				}
			}
			await WaitUntil(tree, () => _replayedTurn >= plan.TargetTurn && TurnHistory.IsSafeBoundary(_replayedTurn),
				$"重放完成后没有等到第 {plan.TargetTurn} 回合的起手选择和动作完成");
			if (_replayedTurn != plan.TargetTurn)
			{
				throw new InvalidOperationException($"重放越过目标回合：{_replayedTurn} / {plan.TargetTurn}");
			}
			TurnHistory.Update();
			Reloader.Info($"已重放到第 {plan.TargetTurn} 回合");
			return true;
		}
		catch (OperationCanceledException)
		{
			Reloader.Info("回退中止，重置战斗");
			return false;
		}
		catch (Exception ex) when (Reloader.ReloadFailureTask?.IsCompleted != true)
		{
			Log.Error($"{Reloader.LogPrefix} 回退回合失败，重置战斗：{ex}");
			Reloader.Toast(Texts.Get(TextId.TurnFailed));
			return false;
		}
	}

	/// <summary>
	/// 换回原来的网络服务。多人局的客户端在收到房主 Resume 的消息处理函数里就要立刻调用：
	/// 同一次网络更新里紧跟着的房主消息会马上被处理，那时必须已经不在回放模式了。
	/// </summary>
	public static void RestoreService()
	{
		RunManager? run = RunManager.Instance;
		if (run != null && _replayService != null && _realService != null)
		{
			SwapService(run, _replayService, _realService);
			if (run.CombatStateSynchronizer != null)
			{
				run.CombatStateSynchronizer.IsDisabled = false;
			}
		}
		_replayService = null;
		_realService = null;
	}

	/// <summary>结束回放模式：恢复速度、换回原来的网络服务、撤掉遮罩。</summary>
	private static void Finish()
	{
		Engine.TimeScale = 1f;
		HideOverlay();
		RestoreService();
		_plan = null;
		_reset = null;
		TurnMenu.Refresh();
	}

	/// <summary>重载失败、握手被取消等情况下调用，保证不会一直停在回放模式和加速状态。</summary>
	public static void Abort()
	{
		if (_plan != null)
		{
			Finish();
		}
	}

	/// <summary>把 RunManager 及其各个同步器里引用 from 的字段换成 to。</summary>
	private static void SwapService(RunManager run, INetGameService from, INetGameService to)
	{
		HashSet<object> visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
		Replace(run, from, to, visited, depth: 0);
	}

	private static void Replace(object owner, object from, object to, HashSet<object> visited, int depth)
	{
		if (!visited.Add(owner))
		{
			return;
		}
		for (Type? type = owner.GetType(); type != null && type != typeof(object); type = type.BaseType)
		{
			foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
			{
				if (field.FieldType.IsValueType || field.FieldType == typeof(string))
				{
					continue;
				}
				object? value = field.GetValue(owner);
				if (value == null)
				{
					continue;
				}
				if (ReferenceEquals(value, from))
				{
					if (field.FieldType.IsInstanceOfType(to))
					{
						field.SetValue(owner, to);
					}
					continue;
				}
				if (depth == 0 && value.GetType().Namespace?.StartsWith("MegaCrit.Sts2.Core") == true && value is not GodotObject)
				{
					Replace(value, from, to, visited, depth + 1);
				}
			}
		}
	}
}

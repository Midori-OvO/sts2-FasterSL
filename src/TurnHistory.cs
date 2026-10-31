using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FasterSL;

/// <summary>本场战斗里一个玩家回合的起点：回放事件列表里的位置，以及显示用的信息。</summary>
public sealed class TurnMark
{
	public int Turn;

	/// <summary>起手选择和自动动作完成后的回放位置。</summary>
	public int EventIndex;

	public int Hp;

	public int MaxHp;
}

/// <summary>
/// 记录当前战斗每个玩家回合的起点。游戏自己的 CombatReplayWriter 会录下整场战斗的所有操作，
/// 起手选择完成且尚未主动操作时记录安全起点，回退时重放到该位置。
/// </summary>
internal static class TurnHistory
{
	private static readonly List<TurnMark> _marks = new List<TurnMark>();

	private static CombatReplay? _replay;

	private static readonly List<(int Turn, int EventIndex)> _starts = new List<(int, int)>();

	private static ActionExecutor? _executor;

	private static CombatState? _state;

	private static int _turn;

	private static bool _pending;

	public static event Action? Changed;

	public static void Init()
	{
		CombatManager.Instance.TurnStarted += OnTurnStarted;
		CombatManager.Instance.CombatEnded += OnCombatEnded;
	}

	/// <summary>游戏正在录制的本场战斗回放；不在战斗里或没在录制时为 null。</summary>
	public static CombatReplay? CurrentReplay()
	{
		CombatReplayWriter? writer = RunManager.Instance?.CombatReplayWriter;
		return writer == null ? null : AccessTools.Field(typeof(CombatReplayWriter), "_replay")?.GetValue(writer) as CombatReplay;
	}

	/// <summary>当前记录是否属于正在进行的这场战斗。</summary>
	public static bool IsCurrent(CombatReplay? replay)
	{
		return replay != null && ReferenceEquals(replay, _replay);
	}

	public static bool Contains(TurnMark mark) => _marks.Contains(mark);

	public static IEnumerable<(int Turn, int EventIndex)> Starts => _starts;

	private static void OnTurnStarted(CombatState state)
	{
		try
		{
			if (state.CurrentSide != CombatSide.Player)
			{
				_pending = false;
				return;
			}
			CombatReplay? replay = CurrentReplay();
			if (replay == null)
			{
				return;
			}
			if (!ReferenceEquals(replay, _replay))
			{
				_marks.Clear();
				_starts.Clear();
				_turn = 0;
				_replay = replay;
				if (_executor != null)
				{
					_executor.BeforeActionExecuted -= OnActionExecuting;
				}
				_executor = RunManager.Instance.ActionExecutor;
				_executor.BeforeActionExecuted += OnActionExecuting;
			}
			_state = state;
			// 额外回合只增加部分玩家的个人回合数，这里按全局玩家阶段计数。
			_turn++;
			_starts.Add((_turn, replay.events.Count));
			_pending = _executor?.CurrentlyRunningAction is null or GenericHookGameAction or ReadyToBeginEnemyTurnAction;
			TurnRewind.OnTurnStarted(_turn);
			Update();
		}
		catch (Exception ex)
		{
			Reloader.Info("记录回合失败：" + ex.Message);
		}
	}

	private static void OnActionExecuting(GameAction action)
	{
		if (action is not GenericHookGameAction and not ReadyToBeginEnemyTurnAction)
		{
			_pending = false;
		}
	}

	public static bool IsSafeBoundary(int turn)
	{
		RunManager run = RunManager.Instance;
		CombatManager combat = CombatManager.Instance;
		if (turn != _turn || _state == null || !ReferenceEquals(_state, combat.DebugOnlyGetState()) ||
			_state.CurrentSide != CombatSide.Player || !combat.IsInProgress ||
			combat.EndingPlayerTurnPhaseOne || combat.EndingPlayerTurnPhaseTwo ||
			run.ActionExecutor.IsPaused || run.ActionExecutor.IsRunning || !run.ActionQueueSet.IsEmpty)
		{
			return false;
		}
		IReadOnlyList<Player> extra = combat.PlayersTakingExtraTurn;
		return _state.Players.All(p => p.Creature.IsDead || (extra.Count > 0 && !extra.Contains(p)) ||
			p.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
	}

	public static void Update()
	{
		if (!_pending)
		{
			return;
		}
		try
		{
			if (_replay == null || !IsCurrent(CurrentReplay()) || !IsSafeBoundary(_turn))
			{
				return;
			}
			_pending = false;
			Player? me = LocalContext.GetMe(_state!.Players);
			_marks.Add(new TurnMark
			{
				Turn = _turn,
				EventIndex = _replay.events.Count,
				Hp = me?.Creature.CurrentHp ?? 0,
				MaxHp = me?.Creature.MaxHp ?? 0
			});
			Changed?.Invoke();
		}
		catch (Exception ex)
		{
			_pending = false;
			Reloader.Info("记录安全回合起点失败：" + ex.Message);
		}
	}

	private static void OnCombatEnded(CombatRoom _)
	{
		_marks.Clear();
		_starts.Clear();
		_replay = null;
		_state = null;
		_pending = false;
		_turn = 0;
		if (_executor != null)
		{
			_executor.BeforeActionExecuted -= OnActionExecuting;
			_executor = null;
		}
		Changed?.Invoke();
	}

	public static IEnumerable<TurnMark> NewestFirst => Enumerable.Reverse(_marks);
}

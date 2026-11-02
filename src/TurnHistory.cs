using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
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

	/// <summary>
	/// 有人在其他玩家完成起手选择前就出牌时不为空：EventIndex 之后只保留这些事件（还在起手阶段的玩家的钩子、选择和恢复），
	/// 先出的牌不重放，回退后就是“所有人起手完成、还没人出牌”的状态。
	/// </summary>
	public List<int>? Kept;

	/// <summary>和 Kept 一一对应：原局里该事件发生时的下一个动作 id 和钩子 id，重放时拨到这个值，跳过被略去的动作占用的 id。</summary>
	public List<(uint Action, uint Hook)>? KeptIds;
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

	private static ActionQueueSet? _queues;

	private static ActionQueueSynchronizer? _sync;

	private static PlayerChoiceSynchronizer? _choices;

	/// <summary>本回合已分类到的回放事件位置。</summary>
	private static int _seen;

	/// <summary>本回合第一次主动操作在回放事件里的位置，没有为 -1。</summary>
	private static int _cut = -1;

	private static readonly List<int> _kept = new List<int>();

	private static readonly List<(uint Action, uint Hook)> _keptIds = new List<(uint, uint)>();

	/// <summary>本回合已经主动操作过（起手阶段已结束）的玩家。</summary>
	private static readonly HashSet<ulong> _acted = new HashSet<ulong>();

	/// <summary>动作 id 到所属玩家，用来判断 ResumeAction 属于谁（恢复时动作会换新 id）。</summary>
	private static readonly Dictionary<uint, ulong> _owners = new Dictionary<uint, ulong>();

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
				_owners.Clear();
				_turn = 0;
				_replay = replay;
				Subscribe(RunManager.Instance);
			}
			_state = state;
			// 额外回合只增加部分玩家的个人回合数，这里按全局玩家阶段计数。
			_turn++;
			_starts.Add((_turn, replay.events.Count));
			_seen = replay.events.Count;
			_cut = -1;
			_kept.Clear();
			_keptIds.Clear();
			_acted.Clear();
			_pending = _executor?.CurrentlyRunningAction is null or GenericHookGameAction or ReadyToBeginEnemyTurnAction;
			TurnRewind.OnTurnStarted(_turn);
			Update();
		}
		catch (Exception ex)
		{
			Reloader.Info("记录回合失败：" + ex.Message);
		}
	}

	/// <summary>在 CombatReplayWriter 之后订阅（它在 RunManager 初始化时就订阅了），所以回调时事件已经追加进回放。</summary>
	private static void Subscribe(RunManager run)
	{
		Unsubscribe();
		_executor = run.ActionExecutor;
		_queues = run.ActionQueueSet;
		_sync = run.ActionQueueSynchronizer;
		_choices = run.PlayerChoiceSynchronizer;
		_queues.ActionEnqueued += OnEnqueued;
		_queues.ActionResumed += OnResumed;
		_choices.PlayerChoiceReceived += OnChoice;
	}

	private static void Unsubscribe()
	{
		if (_queues != null)
		{
			_queues.ActionEnqueued -= OnEnqueued;
			_queues.ActionResumed -= OnResumed;
		}
		if (_choices != null)
		{
			_choices.PlayerChoiceReceived -= OnChoice;
		}
		_executor = null;
		_queues = null;
		_sync = null;
		_choices = null;
	}

	private static void OnEnqueued(GameAction action)
	{
		if (action.Id is uint id)
		{
			_owners[id] = action.OwnerId;
		}
		Classify(action.Id ?? uint.MaxValue);
	}

	/// <summary>原版先触发 ActionResumed 再分配新 id，所以此时的 NextActionId 就是动作恢复后的新 id。</summary>
	private static void OnResumed(uint id)
	{
		uint next = _queues?.NextActionId ?? uint.MaxValue;
		if (_owners.TryGetValue(id, out ulong owner))
		{
			_owners[next] = owner;
		}
		Classify(next);
	}

	private static void OnChoice(Player player, uint choiceId, NetPlayerChoiceResult result)
	{
		Classify(uint.MaxValue);
	}

	/// <summary>
	/// 回合开始后、记录回退点之前，给新追加的回放事件分类。第一次主动操作（打牌、喝药、结束回合）之后，
	/// 只保留还没主动操作过的玩家的钩子、选择和恢复事件，也就是他们的起手阶段。
	/// </summary>
	private static void Classify(uint actionId)
	{
		CombatReplay? replay = _replay;
		if (replay == null)
		{
			return;
		}
		List<CombatReplayEvent> events = replay.events;
		if (!_pending)
		{
			_seen = events.Count;
			return;
		}
		for (; _seen < events.Count; _seen++)
		{
			CombatReplayEvent e = events[_seen];
			ulong? owner = e.eventType == CombatReplayEventType.ResumeAction
				? (_owners.TryGetValue(e.actionId!.Value, out ulong o) ? o : null)
				: e.playerId;
			if (e.eventType == CombatReplayEventType.GameAction)
			{
				if (_cut < 0)
				{
					_cut = _seen;
				}
				if (owner != null)
				{
					_acted.Add(owner.Value);
				}
				continue;
			}
			if (_cut < 0 || owner == null || _acted.Contains(owner.Value))
			{
				continue;
			}
			bool latest = _seen == events.Count - 1;
			_kept.Add(_seen);
			_keptIds.Add((latest && e.eventType != CombatReplayEventType.PlayerChoice ? actionId : uint.MaxValue, _sync?.NextHookId ?? uint.MaxValue));
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
			Classify(uint.MaxValue);
			_pending = false;
			Player? me = LocalContext.GetMe(_state!.Players);
			bool filtered = _cut >= 0;
			_marks.Add(new TurnMark
			{
				Turn = _turn,
				EventIndex = filtered ? _cut : _replay.events.Count,
				Hp = me?.Creature.CurrentHp ?? 0,
				MaxHp = me?.Creature.MaxHp ?? 0,
				Kept = filtered ? _kept.ToList() : null,
				KeptIds = filtered ? _keptIds.ToList() : null
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
		_owners.Clear();
		Unsubscribe();
		Changed?.Invoke();
	}

	public static IEnumerable<TurnMark> NewestFirst => Enumerable.Reverse(_marks);
}

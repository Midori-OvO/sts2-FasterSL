using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace FasterSL;

/// <summary>回退到某个回合需要的全部数据：战斗入口时的各种 id、要重放的事件、每个回合的起点。多人局里由房主发给所有人。</summary>
public sealed class RewindPlan
{
	public List<uint> ChoiceIds = new List<uint>();

	public List<int> RewardIds = new List<int>();

	public uint NextActionId;

	public uint NextHookId;

	public List<CombatReplayEvent> Events = new List<CombatReplayEvent>();

	public int TargetTurn;

	public List<(int Turn, int EventIndex)> TurnStarts = new List<(int, int)>();

	/// <summary>和 Events 一一对应（可以为空）：重放这个事件前要拨到的动作 id 和钩子 id，uint.MaxValue 表示不拨。</summary>
	public List<(uint Action, uint Hook)> Ids = new List<(uint, uint)>();

	public static RewindPlan From(CombatReplay replay, TurnMark target, IEnumerable<(int Turn, int EventIndex)> starts)
	{
		RewindPlan plan = new RewindPlan
		{
			ChoiceIds = replay.choiceIds.ToList(),
			RewardIds = replay.rewardIds.ToList(),
			NextActionId = replay.nextActionId,
			NextHookId = replay.nextHookId,
			Events = replay.events.Take(target.EventIndex).ToList(),
			TargetTurn = target.Turn,
			TurnStarts = starts.Where(m => m.Turn <= target.Turn).OrderBy(m => m.EventIndex).ToList()
		};
		if (target.Kept != null && target.KeptIds != null)
		{
			plan.Ids.AddRange(Enumerable.Repeat((uint.MaxValue, uint.MaxValue), plan.Events.Count));
			plan.Events.AddRange(target.Kept.Select(i => replay.events[i]));
			plan.Ids.AddRange(target.KeptIds);
		}
		return plan;
	}

	public void Serialize(PacketWriter writer)
	{
		writer.WriteInt(ChoiceIds.Count);
		foreach (uint id in ChoiceIds)
		{
			writer.WriteUInt(id);
		}
		writer.WriteInt(RewardIds.Count);
		foreach (int id in RewardIds)
		{
			writer.WriteInt(id);
		}
		writer.WriteUInt(NextActionId);
		writer.WriteUInt(NextHookId);
		writer.WriteList(Events);
		writer.WriteInt(TargetTurn);
		writer.WriteInt(TurnStarts.Count);
		foreach ((int turn, int index) in TurnStarts)
		{
			writer.WriteInt(turn);
			writer.WriteInt(index);
		}
		writer.WriteInt(Ids.Count);
		foreach ((uint action, uint hook) in Ids)
		{
			writer.WriteUInt(action);
			writer.WriteUInt(hook);
		}
	}

	public static RewindPlan Deserialize(PacketReader reader)
	{
		RewindPlan plan = new RewindPlan();
		int choices = reader.ReadInt();
		for (int i = 0; i < choices; i++)
		{
			plan.ChoiceIds.Add(reader.ReadUInt());
		}
		int rewards = reader.ReadInt();
		for (int i = 0; i < rewards; i++)
		{
			plan.RewardIds.Add(reader.ReadInt());
		}
		plan.NextActionId = reader.ReadUInt();
		plan.NextHookId = reader.ReadUInt();
		plan.Events = reader.ReadList<CombatReplayEvent>();
		plan.TargetTurn = reader.ReadInt();
		int starts = reader.ReadInt();
		for (int i = 0; i < starts; i++)
		{
			int turn = reader.ReadInt();
			plan.TurnStarts.Add((turn, reader.ReadInt()));
		}
		int ids = reader.ReadInt();
		for (int i = 0; i < ids; i++)
		{
			uint action = reader.ReadUInt();
			plan.Ids.Add((action, reader.ReadUInt()));
		}
		return plan;
	}
}

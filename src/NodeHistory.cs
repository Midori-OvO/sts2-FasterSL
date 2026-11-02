using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.MapDrawing;

namespace FasterSL;

/// <summary>本局走过的一个节点：进入该节点那一刻的存档、战斗胜利后（领奖励前）的存档，以及用来显示的信息。</summary>
internal sealed class NodeEntry
{
	/// <summary>进入节点那一刻。从主菜单继续到领奖励界面时没有这份。</summary>
	public SerializableRun? Before;

	/// <summary>战斗胜利、原版写盘时的存档（和“保存并退出”再继续是同一个状态）。</summary>
	public SerializableRun? After;

	public SerializableRun? Latest => After ?? Before;

	/// <summary>第几幕（从 0 开始）。</summary>
	public int Act;

	/// <summary>本幕已访问的地图坐标数（含当前节点）。和 Act 一起唯一确定一个节点。</summary>
	public int VisitedCount;

	/// <summary>总层数（显示用）。</summary>
	public int Floor;

	public MapPointType PointType;

	/// <summary>节点实际生成的房间类型。问号节点要进去之后才知道，所以可能为空。</summary>
	public RoomType? RoomType;

	/// <summary>遭遇或事件的模型 id，用来显示怪物/事件名。</summary>
	public ModelId? ModelId;

	public int CompareTo(int act, int visitedCount)
	{
		return Act != act ? Act.CompareTo(act) : VisitedCount.CompareTo(visitedCount);
	}
}

/// <summary>
/// 记录本局每个节点的入口存档，供“回退到任意节点”使用。只保存在内存里，重启游戏后清空。
///
/// 规则（回退是单向的，不能再“前进”回去）：
///   - 进入一个节点后，删掉它之后的所有记录。
///   - 正常走进节点：替换掉这个位置原有的记录。
///   - 读档重新进入（主菜单继续、本 mod 的回退）：已有记录就原样保留，没有才追加。
///   - 换了一局（开局时间不同）：清空。
/// </summary>
internal static class NodeHistory
{
	private const int MaxEntries = 200;

	private static readonly List<NodeEntry> _entries = new List<NodeEntry>();

	private static long _runStartTime = long.MinValue;

	/// <summary>由 RunManager.EnterMapPointInternal 的 prefix 设置：本次进入节点是否来自读档（saveGame=false）。</summary>
	public static bool EnteringByLoad;

	/// <summary>同上：本次进入的是已经打完的房间（读档回到领奖励界面）。</summary>
	public static bool EnteringPreFinished;

	/// <summary>
	/// 每一幕最新的地图绘画。绘画是按幕的（进入新一幕时游戏会清空地图上的线），
	/// 回档时把目标幕最新的绘画塞进要载入的存档，否则会退回到进节点那一刻的绘画。
	/// </summary>
	private static readonly Dictionary<int, SerializableMapDrawings> _latestDrawings = new Dictionary<int, SerializableMapDrawings>();

	public static IReadOnlyList<NodeEntry> Entries => _entries;

	public static void Record(SerializableRun save)
	{
		bool byLoad = EnteringByLoad;
		bool preFinished = EnteringPreFinished;
		EnteringByLoad = false;
		EnteringPreFinished = false;
		SyncRun(save);
		RememberDrawings(save, byLoad);
		int act = save.CurrentActIndex;
		int visited = save.VisitedMapCoords.Count;
		_entries.RemoveAll(e => e.CompareTo(act, visited) > 0);
		int existing = _entries.FindIndex(e => e.CompareTo(act, visited) == 0);
		if (preFinished && existing >= 0)
		{
			// 读档进已打完的房间时，这里拿到的是战斗后的状态，不能当入口存档
			return;
		}
		if (existing >= 0)
		{
			NodeEntry old = _entries[existing];
			if (byLoad)
			{
				// 回到战斗前，战斗后的记录作废（回退单向）
				old.After = null;
				if (old.Before != null)
				{
					return;
				}
			}
			_entries.RemoveAt(existing);
		}
		save = Clone(save);
		RunState? state = RunManager.Instance?.DebugOnlyGetState();
		Add(new NodeEntry
		{
			Before = save,
			Act = act,
			VisitedCount = visited,
			Floor = save.FloorReached + 1,
			PointType = state?.CurrentMapPoint?.PointType ?? MapPointType.Unassigned
		});
	}

	/// <summary>
	/// RunSaveManager.SaveRun(SerializableRun, bool) 的 prefix：战斗胜利后原版会带着打完的房间写盘，记下这份。
	/// 读档回到领奖励界面时 IncrementNumReloads 也会把读入的存档原样写一次，同样走这里。
	/// </summary>
	public static void RecordAfter(SerializableRun save)
	{
		if (save.PreFinishedRoom is not { IsPreFinished: true } room
			|| room.RoomType is not (RoomType.Monster or RoomType.Elite or RoomType.Boss))
		{
			return;
		}
		SyncRun(save);
		int act = save.CurrentActIndex;
		int visited = save.VisitedMapCoords.Count;
		_entries.RemoveAll(e => e.CompareTo(act, visited) > 0);
		NodeEntry? entry = _entries.Find(e => e.CompareTo(act, visited) == 0);
		if (entry == null)
		{
			RunState? state = RunManager.Instance?.DebugOnlyGetState();
			entry = new NodeEntry
			{
				Act = act,
				VisitedCount = visited,
				Floor = state?.TotalFloor ?? save.FloorReached,
				PointType = state?.CurrentMapPoint?.PointType ?? MapPointType.Unassigned,
				RoomType = room.RoomType,
				ModelId = room.EncounterId
			};
			Add(entry);
		}
		entry.After = Clone(save);
	}

	private static void SyncRun(SerializableRun save)
	{
		if (save.StartTime != _runStartTime)
		{
			_entries.Clear();
			_latestDrawings.Clear();
			_runStartTime = save.StartTime;
		}
	}

	private static void Add(NodeEntry entry)
	{
		_entries.Add(entry);
		if (_entries.Count > MaxEntries)
		{
			_entries.RemoveRange(0, _entries.Count - MaxEntries);
		}
	}

	/// <summary>
	/// 深拷贝存档。RunState.FromSerializable 会直接复用存档里的列表（比如 MapPointHistory），
	/// 读档后游戏往里追加记录就会改掉我们保存的那份，导致再回到这个节点时层数 +1。
	/// </summary>
	public static SerializableRun Clone(SerializableRun save)
	{
		JsonTypeInfo<SerializableRun> info = JsonSerializationUtility.GetTypeInfo<SerializableRun>();
		return JsonSerializer.Deserialize(JsonSerializer.Serialize(save, info), info)!;
	}

	/// <summary>RunState.AppendToMapPointHistory 之后调用：这时才知道节点实际是什么房间。</summary>
	public static void OnRoomAppended(MapPointType pointType, RoomType roomType, ModelId? modelId)
	{
		NodeEntry? entry = FindCurrent();
		if (entry == null)
		{
			return;
		}
		entry.PointType = pointType;
		entry.RoomType = roomType;
		entry.ModelId = modelId;
		RunState? state = RunManager.Instance?.DebugOnlyGetState();
		if (state != null)
		{
			entry.Floor = state.TotalFloor;
		}
	}

	/// <summary>
	/// 正常进节点时，存档里的绘画就是地图上当时的绘画。
	/// 读档进节点时，游戏要等房间进入之后才把绘画画回地图（NGame.LoadRun 末尾的 LoadDrawings），
	/// 所以这时存档里的绘画是空的，真正的绘画在 RunManager.MapDrawingsToLoad 里；顺便补进存档，
	/// 否则之后再重载这个节点会把绘画弄丢。
	/// </summary>
	private static void RememberDrawings(SerializableRun save, bool byLoad)
	{
		SerializableMapDrawings? drawings = save.MapDrawings;
		if (byLoad)
		{
			drawings = RunManager.Instance?.MapDrawingsToLoad;
			if (drawings != null)
			{
				save.MapDrawings = drawings;
			}
		}
		if (drawings != null)
		{
			_latestDrawings[save.CurrentActIndex] = drawings;
		}
	}

	/// <summary>回档前调用：记下当前地图上的绘画，并把目标幕最新的绘画写进要载入的存档。</summary>
	public static void ApplyLatestDrawings(SerializableRun target)
	{
		try
		{
			RunState? state = RunManager.Instance?.DebugOnlyGetState();
			SerializableMapDrawings? live = NRun.Instance?.GlobalUi?.MapScreen?.Drawings?.GetSerializableMapDrawings();
			if (state != null && live != null && target.StartTime == _runStartTime)
			{
				_latestDrawings[state.CurrentActIndex] = live;
			}
			if (target.StartTime == _runStartTime && _latestDrawings.TryGetValue(target.CurrentActIndex, out SerializableMapDrawings? latest))
			{
				target.MapDrawings = latest;
			}
		}
		catch (Exception ex)
		{
			Reloader.Info("保留地图绘画失败：" + ex.Message);
		}
	}

	/// <summary>当前所在节点对应的记录。</summary>
	public static NodeEntry? FindCurrent()
	{
		RunState? state = RunManager.Instance?.DebugOnlyGetState();
		if (state == null)
		{
			return null;
		}
		int act = state.CurrentActIndex;
		int visited = state.VisitedMapCoords.Count;
		return _entries.Find(e => e.CompareTo(act, visited) == 0);
	}
}

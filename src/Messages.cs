using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Saves;

namespace FasterSL;

// 游戏会通过 ReflectionHelper.GetSubtypesInMods<INetMessage>() 自动注册这些类型，
// 所以所有玩家都必须装同一版本的本 mod，否则消息 ID 对不上。
// ShouldBuffer = false：重载期间消息总线处于缓冲状态，我们自己的握手消息必须能穿过去。
// LogLevel.Debug：和游戏自己的消息一样，收发时不写进普通日志。

/// <summary>房主 → 所有人：准备重载，附带要载入的存档；回退回合时还附带重放计划。</summary>
public struct ReloadPrepareMessage : INetMessage
{
	public uint seq;

	public SerializableRun run;

	public RewindPlan? plan;

	public bool ShouldBroadcast => false;

	public NetTransferMode Mode => NetTransferMode.Reliable;

	public LogLevel LogLevel => LogLevel.Debug;

	public bool ShouldBuffer => false;

	public void Serialize(PacketWriter writer)
	{
		writer.WriteUInt(seq);
		writer.Write(run);
		writer.WriteBool(plan != null);
		plan?.Serialize(writer);
	}

	public void Deserialize(PacketReader reader)
	{
		seq = reader.ReadUInt();
		run = reader.Read<SerializableRun>();
		plan = reader.ReadBool() ? RewindPlan.Deserialize(reader) : null;
	}
}

/// <summary>客户端 → 房主：本地重放结束（ok=false 表示失败）。</summary>
public struct RewindDoneMessage : INetMessage
{
	public uint seq;

	public bool ok;

	public bool ShouldBroadcast => false;

	public NetTransferMode Mode => NetTransferMode.Reliable;

	public LogLevel LogLevel => LogLevel.Debug;

	public bool ShouldBuffer => false;

	public void Serialize(PacketWriter writer)
	{
		writer.WriteUInt(seq);
		writer.WriteBool(ok);
	}

	public void Deserialize(PacketReader reader)
	{
		seq = reader.ReadUInt();
		ok = reader.ReadBool();
	}
}

/// <summary>房主 → 所有人：所有人都重放完了，一起恢复联机；reset=true 表示改为一起重载到战斗入口。</summary>
public struct RewindResumeMessage : INetMessage
{
	public uint seq;

	public bool reset;

	public bool ShouldBroadcast => false;

	public NetTransferMode Mode => NetTransferMode.Reliable;

	public LogLevel LogLevel => LogLevel.Debug;

	public bool ShouldBuffer => false;

	public void Serialize(PacketWriter writer)
	{
		writer.WriteUInt(seq);
		writer.WriteBool(reset);
	}

	public void Deserialize(PacketReader reader)
	{
		seq = reader.ReadUInt();
		reset = reader.ReadBool();
	}
}

public struct RewindStopMessage : INetMessage
{
	public uint seq;
	public bool ShouldBroadcast => false;
	public NetTransferMode Mode => NetTransferMode.Reliable;
	public LogLevel LogLevel => LogLevel.Debug;
	public bool ShouldBuffer => false;
	public void Serialize(PacketWriter writer) => writer.WriteUInt(seq);
	public void Deserialize(PacketReader reader) => seq = reader.ReadUInt();
}

public struct RewindStoppedMessage : INetMessage
{
	public uint seq;
	public bool ShouldBroadcast => false;
	public NetTransferMode Mode => NetTransferMode.Reliable;
	public LogLevel LogLevel => LogLevel.Debug;
	public bool ShouldBuffer => false;
	public void Serialize(PacketWriter writer) => writer.WriteUInt(seq);
	public void Deserialize(PacketReader reader) => seq = reader.ReadUInt();
}

public struct ReloadFailedMessage : INetMessage
{
	public uint seq;
	public bool ShouldBroadcast => false;
	public NetTransferMode Mode => NetTransferMode.Reliable;
	public LogLevel LogLevel => LogLevel.Debug;
	public bool ShouldBuffer => false;
	public void Serialize(PacketWriter writer) => writer.WriteUInt(seq);
	public void Deserialize(PacketReader reader) => seq = reader.ReadUInt();
}

public struct ReloadLoadedMessage : INetMessage
{
	public uint seq;
	public bool ShouldBroadcast => false;
	public NetTransferMode Mode => NetTransferMode.Reliable;
	public LogLevel LogLevel => LogLevel.Debug;
	public bool ShouldBuffer => false;
	public void Serialize(PacketWriter writer) => writer.WriteUInt(seq);
	public void Deserialize(PacketReader reader) => seq = reader.ReadUInt();
}

public struct ReloadCompleteMessage : INetMessage
{
	public uint seq;
	public bool ShouldBroadcast => false;
	public NetTransferMode Mode => NetTransferMode.Reliable;
	public LogLevel LogLevel => LogLevel.Debug;
	public bool ShouldBuffer => false;
	public void Serialize(PacketWriter writer) => writer.WriteUInt(seq);
	public void Deserialize(PacketReader reader) => seq = reader.ReadUInt();
}

/// <summary>客户端 → 房主：已停止处理旧局消息，可以开始。</summary>
public struct ReloadReadyMessage : INetMessage
{
	public uint seq;

	public bool ShouldBroadcast => false;

	public NetTransferMode Mode => NetTransferMode.Reliable;

	public LogLevel LogLevel => LogLevel.Debug;

	public bool ShouldBuffer => false;

	public void Serialize(PacketWriter writer)
	{
		writer.WriteUInt(seq);
	}

	public void Deserialize(PacketReader reader)
	{
		seq = reader.ReadUInt();
	}
}

/// <summary>房主 → 所有人：所有人都已就绪，开始本地重载。</summary>
public struct ReloadGoMessage : INetMessage
{
	public uint seq;

	public bool ShouldBroadcast => false;

	public NetTransferMode Mode => NetTransferMode.Reliable;

	public LogLevel LogLevel => LogLevel.Debug;

	public bool ShouldBuffer => false;

	public void Serialize(PacketWriter writer)
	{
		writer.WriteUInt(seq);
	}

	public void Deserialize(PacketReader reader)
	{
		seq = reader.ReadUInt();
	}
}

/// <summary>房主 → 所有人：有人没就绪，取消本次重载。</summary>
public struct ReloadAbortMessage : INetMessage
{
	public uint seq;

	public bool ShouldBroadcast => false;

	public NetTransferMode Mode => NetTransferMode.Reliable;

	public LogLevel LogLevel => LogLevel.Debug;

	public bool ShouldBuffer => false;

	public void Serialize(PacketWriter writer)
	{
		writer.WriteUInt(seq);
	}

	public void Deserialize(PacketReader reader)
	{
		seq = reader.ReadUInt();
	}
}

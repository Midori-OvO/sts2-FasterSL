using System.Collections.Generic;
using System.Threading.Tasks;

namespace FasterSL;

internal sealed class RewindStopState
{
	private readonly uint _sequence;
	private readonly HashSet<ulong> _pending;
	private readonly TaskCompletionSource<bool> _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private bool _localStopped;

	public RewindStopState(uint sequence, IEnumerable<ulong> peers)
	{
		_sequence = sequence;
		_pending = new HashSet<ulong>(peers);
	}

	public bool IsRequested => _requested.Task.IsCompleted;
	public Task Requested => _requested.Task;
	public Task AllStopped => _stopped.Task;
	public IEnumerable<ulong> PendingPeers => _pending;

	public bool Request(uint sequence)
	{
		if (sequence != _sequence || IsRequested)
		{
			return false;
		}
		_requested.TrySetResult(true);
		CheckStopped();
		return true;
	}

	public void ConfirmLocalStopped()
	{
		_localStopped = true;
		CheckStopped();
	}

	public void ConfirmPeer(uint sequence, ulong peer)
	{
		if (sequence == _sequence && IsRequested)
		{
			_pending.Remove(peer);
			CheckStopped();
		}
	}

	private void CheckStopped()
	{
		if (IsRequested && _localStopped && _pending.Count == 0)
		{
			_stopped.TrySetResult(true);
		}
	}
}

using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;

namespace FasterSL;

internal static class ReloadSyncGuard
{
	private const int SyncTimeoutMs = 30000;

	public static void Validate()
	{
		Type type = typeof(MegaCrit.Sts2.Core.Multiplayer.CombatStateSynchronizer);
		FieldInfo? field = AccessTools.Field(type, "_syncCompletionSource");
		if (field == null || field.IsStatic || field.IsInitOnly || field.FieldType != typeof(TaskCompletionSource))
		{
			throw new MissingFieldException(type.FullName, "_syncCompletionSource: TaskCompletionSource");
		}
	}

	public static void WaitForSyncPostfix(object __instance, ref Task __result)
	{
		Task<Exception>? failure = Reloader.ReloadFailureTask;
		if (failure == null || __result.IsCompleted)
		{
			return;
		}
		Task original = __result;
		try
		{
			FieldInfo field = AccessTools.Field(__instance.GetType(), "_syncCompletionSource")
				?? throw new MissingFieldException(__instance.GetType().FullName, "_syncCompletionSource");
			if (field.GetValue(__instance) is not TaskCompletionSource source)
			{
				throw new InvalidOperationException("找不到正在等待的战斗同步任务");
			}
			__result = WaitForSync(__instance, field, source, original, failure);
		}
		catch (Exception ex)
		{
			__result = FailClosed(original, ex);
		}
	}

	private static async Task WaitForSync(object synchronizer, FieldInfo field, TaskCompletionSource source,
		Task original, Task<Exception> failure)
	{
		Task finished = await Task.WhenAny(original, failure, Task.Delay(SyncTimeoutMs));
		if (original.IsCompleted)
		{
			await original;
			return;
		}
		Exception error;
		try
		{
			error = finished == failure ? await failure : new TimeoutException("等待所有玩家的战斗入场同步超时");
		}
		catch (Exception ex)
		{
			error = ex;
		}
		try
		{
			if (ReferenceEquals(field.GetValue(synchronizer), source))
			{
				field.SetValue(synchronizer, null);
			}
		}
		catch (Exception ex)
		{
			error = new InvalidOperationException("清理战斗同步等待失败", ex);
		}
		source.TrySetException(error);
		Reloader.FailReload(error);
		await original;
	}

	private static async Task FailClosed(Task original, Exception error)
	{
		Reloader.FailReload(error);
		await original;
		throw error;
	}
}

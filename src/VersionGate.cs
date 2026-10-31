using System;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Debug;

namespace FasterSL;

/// <summary>
/// 只在验证过的游戏版本上启用。遇到其他版本时不打任何补丁、不注册任何东西，
/// 游戏照常运行，等 mod 更新后再启用。
/// </summary>
internal static class VersionGate
{
	private static readonly string[] Supported = { "v0.107.1", "v0.111.0" };

	public static bool IsSupported(out string version)
	{
		try
		{
			version = ReadVersion() ?? "unknown";
		}
		catch (Exception ex)
		{
			// 游戏改了版本信息的接口，本身就说明是没验证过的版本
			version = "unknown (" + ex.GetType().Name + ")";
			return false;
		}
		return Array.IndexOf(Supported, version) >= 0;
	}

	public static string SupportedList => string.Join(", ", Supported);

	// 单独成方法：游戏接口变了时，异常发生在 JIT 编译这个方法的时候，外层才接得住
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static string? ReadVersion()
	{
		return ReleaseInfoManager.Instance.ReleaseInfo?.Version;
	}
}

using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace FasterSL;

[ModInitializer("Initialize")]
public static class MainFile
{
	public static void Initialize()
	{
		if (!VersionGate.IsSupported(out string version))
		{
			// 用 Godot 自己的输出，不依赖可能已经变化的游戏日志接口
			GD.Print($"[FasterSL] 游戏版本 {version} 未经验证（支持：{VersionGate.SupportedList}），FasterSL 已停用全部功能。请等待 mod 更新。");
			return;
		}
		try
		{
			Patches.Apply(new Harmony("sts2.FasterSL"));
			Reloader.Info($"已加载（游戏版本 {version}）：ESC 菜单 Save&Load 重载当前房间、回退到任意节点");
		}
		catch (Exception ex)
		{
			Reloader.Info("初始化失败：" + ex);
		}
	}
}

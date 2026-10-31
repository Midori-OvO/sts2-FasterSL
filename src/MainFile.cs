using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace FasterSL;

[ModInitializer("Initialize")]
public static class MainFile
{
	public static void Initialize()
	{
		try
		{
			Patches.Apply(new Harmony("sts2.FasterSL"));
			Reloader.Info("已加载：ESC 菜单 Save&Load 重载当前房间、回退到任意节点");
		}
		catch (Exception ex)
		{
			Reloader.Info("初始化失败：" + ex);
		}
	}
}

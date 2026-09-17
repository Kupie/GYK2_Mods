using BepInEx;
using GK2.FlowCanvasNodes;
using HarmonyLib;
using UnityEngine;

namespace AvailableInDemoPatcher
{
	[BepInPlugin("kupie.gk2.AvailableInDemoPatcher", "Unlocked Tech Tabs", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		private Harmony harmony;

		private void Awake()
		{
			harmony = new Harmony("kupie.gk2.AvailableInDemoPatcher");
			harmony.PatchAll();
		}

		private void OnDestroy()
		{
			harmony?.UnpatchSelf();
		}
	}

	// Forces every tech tab to read as unlocked, ignoring lockedTechTabs entirely.
	[HarmonyPatch(typeof(KnowledgeSystem), nameof(KnowledgeSystem.IsTechTabLocked))]
	internal static class KnowledgeSystem_IsTechTabLocked_Patch
	{
		private static bool Prefix(ref bool __result)
		{
			__result = false;
			return false;
		}
	}


	[HarmonyPatch(typeof(Flow_IsDemo), "RegisterPorts")]
	internal static class Flow_IsDemo_RegisterPorts_Patch
	{
		private static void Postfix(Flow_IsDemo __instance)
		{
			var yesField = AccessTools.Field(typeof(Flow_IsDemo), "yes");
			var noField = AccessTools.Field(typeof(Flow_IsDemo), "no");
			object yesValue = yesField.GetValue(__instance);
			object noValue = noField.GetValue(__instance);
			yesField.SetValue(__instance, noValue);
			noField.SetValue(__instance, yesValue);
		}
	}

	[HarmonyPatch(typeof(QuestSystemData), nameof(QuestSystemData.StartQuest))]
	internal static class QuestSystemData_StartQuest_Patch
	{
		private const string DemoSuffix = "_demo";

		// StartQuest does zero branching of its own - it just starts whatever id it's given, so
		// this is the one place every "_demo" quest start passes through regardless of which
		// global script or trigger decided to call it. Only redirects when the non-demo sibling
		// id actually exists as a real quest, so a coincidental "_demo"-ending id that isn't
		// actually part of a demo/full pair is left alone rather than redirected to nothing.
		private static void Prefix(QuestSystemData __instance, ref string id)
		{
			if (string.IsNullOrEmpty(id) || !id.EndsWith(DemoSuffix))
			{
				return;
			}

			string nonDemoId = id.Substring(0, id.Length - DemoSuffix.Length);
			if (__instance.questCollection.questsCache.ContainsKey(nonDemoId))
			{
				Debug.Log($"QuestSystemData_StartQuest_Patch: redirecting '{id}' -> '{nonDemoId}'.");
				id = nonDemoId;
			}
		}
	}

	[HarmonyPatch(typeof(TechTreePageWidget), nameof(TechTreePageWidget.IsTechAvailableInCurrentBuild))]
	internal static class IsTechAvailableInCurrentBuild_Patch
	{
		private static void Postfix(ref bool __result)
		{
			__result = true;
		}
	}

	// Flips isAvailableInDemo before Unlock() checks it, so the rest of Unlock() runs normally.
	[HarmonyPatch(typeof(TechDef), nameof(TechDef.Unlock))]
	internal static class TechDef_Unlock_Patch
	{
		private static void Prefix(TechDef __instance)
		{
			__instance.isAvailableInDemo = true;
		}
	}
}

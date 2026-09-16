using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace AvailableInDemoPatcher
{
	[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
	public class Plugin : BaseUnityPlugin
	{
		private Harmony harmony;

		private void Awake()
		{
			harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
			harmony.PatchAll();
		}

		private void OnDestroy()
		{
			harmony?.UnpatchSelf();
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

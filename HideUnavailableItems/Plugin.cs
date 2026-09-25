using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace HideUnavailableItems
{
	[BepInPlugin("kupie.gk2.hideunavailableitems", "Hide Unavailable Items", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ManualLogSource Log;

		internal static ConfigEntry<bool> HideUnavailable;

		private Harmony harmony;

		private void Awake()
		{
			Log = Logger;

			HideUnavailable = Config.Bind(
				"General",
				"Hide Unavailable Items",
				true,
				"Hide items the open window won't accept (can't be sold to this vendor, can't go in this bag) from the player's inventory/bag panels entirely, instead of just graying them out.");

			harmony = new Harmony("kupie.gk2.hideunavailableitems");
			harmony.PatchAll();

			Log.LogInfo("Plugin kupie.gk2.hideunavailableitems is loaded!");
		}

		private void OnDestroy()
		{
			harmony?.UnpatchSelf();
		}
	}
}

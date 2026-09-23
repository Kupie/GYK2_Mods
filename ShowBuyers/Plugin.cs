using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace ShowBuyers
{
	[BepInPlugin("kupie.gk2.showbuyers", "Show Buyers", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ConfigEntry<bool> ShowBuyersEnabled;

		private Harmony harmony;

		private void Awake()
		{
			ShowBuyersEnabled = Config.Bind(
				"General",
				"ShowBuyers",
				true,
				"Adds a 'Buyers' line to item tooltips listing the NPC vendors who will currently buy that item. Turn off if it conflicts with another tooltip mod.");

			// The vendor->item mapping can change mid-game (a vendor levels up and
			// unlocks new products), so the cache is rebuilt on every new game/loaded
			// save and invalidated again whenever a vendor's tier changes - see
			// BuyerCache and the ForceLevelUp patch in TooltipPatch.cs.
			MainGame.OnGameStarted += BuyerCache.Invalidate;

			harmony = new Harmony("kupie.gk2.showbuyers");
			harmony.PatchAll();

			Logger.LogInfo("Plugin kupie.gk2.showbuyers is loaded!");
		}

		private void OnDestroy()
		{
			MainGame.OnGameStarted -= BuyerCache.Invalidate;
			harmony?.UnpatchSelf();
		}
	}
}

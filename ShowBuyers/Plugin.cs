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
				"Adds a 'Buyer' section to item tooltips listing the NPC vendors who buy that item, the tier each needs to reach first (if not the base tier), and the base price they pay. Turn off if it conflicts with another tooltip mod.");

			// Which vendor buys what at which tier comes straight from balance data
			// (VendorDef.tierDataList) rather than any per-save state, so the cache
			// only needs rebuilding when a new/loaded save can hand BuyerCache a
			// different vendor list to work from - see BuyerCache.
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

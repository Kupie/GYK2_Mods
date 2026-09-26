using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ShowBuyers
{
	[BepInPlugin("kupie.gk2.showbuyers", "Show Buyers", "1.1.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ManualLogSource Log;
		internal static ConfigEntry<bool> ShowBuyersEnabled;
		internal static ConfigEntry<bool> LogVendorData;

		private Harmony harmony;

		private void Awake()
		{
			Log = Logger;

			ShowBuyersEnabled = Config.Bind(
				"General",
				"ShowBuyers",
				true,
				"Adds a 'Buyer' line to item tooltips listing the NPC vendors who buy that item and the tier each needs to reach first, followed by the item's base price. Turn off if it conflicts with another tooltip mod.");

			LogVendorData = Config.Bind(
				"Debug",
				"LogVendorData",
				false,
				"Writes every vendor's per-tier product and notBuying lists to the BepInEx log when the buyer list is built, and the buyers found for each item the first time its tooltip is shown. For troubleshooting only.");

			// Which vendor buys what at which tier comes straight from balance data
			// (VendorDef.tierDataList) rather than any per-save state, so the cache
			// only needs rebuilding when a new/loaded save can hand BuyerCache a
			// different vendor list to work from - see BuyerCache.
			MainGame.OnGameStarted += BuyerCache.Invalidate;
			LogVendorData.SettingChanged += (sender, args) => BuyerCache.Invalidate();

			harmony = new Harmony("kupie.gk2.showbuyers");
			harmony.PatchAll();

			Log.LogInfo("Plugin kupie.gk2.showbuyers 1.1.0 is loaded!");
		}

		private void OnDestroy()
		{
			MainGame.OnGameStarted -= BuyerCache.Invalidate;
			harmony?.UnpatchSelf();
		}
	}
}

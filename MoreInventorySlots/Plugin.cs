using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace MoreInventorySlots
{
	[BepInPlugin("kupie.gk2.moreinventoryslots", "More Inventory Slots", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ManualLogSource Log;
		internal static ConfigEntry<int> PlayerInventoryBonus;
		internal static ConfigEntry<int> ToolBeltBonus;

		private void Awake()
		{
			Log = Logger;

			PlayerInventoryBonus = Config.Bind(
				"General",
				"Player Inventory Bonus Slots",
				10,
				"Extra slots added on top of the player's base inventory (vanilla default is 20). 0 disables the bonus.");

			ToolBeltBonus = Config.Bind(
				"General",
				"Tool Belt Bonus Slots",
				0,
				"Extra slots added on top of the tool belt. 0 disables the bonus.");

			// Re-apply immediately if either value is edited in-game (e.g.
			// via the Configuration Manager plugin) rather than only on the
			// next save load.
			PlayerInventoryBonus.SettingChanged += (sender, args) => InventoryBonus.Apply();
			ToolBeltBonus.SettingChanged += (sender, args) => InventoryBonus.Apply();

			// MainGame.OnGameStarted fires once the gameplay scene has
			// finished loading, for a brand-new game and a continued/loaded
			// save alike - unlike PlayerData.Init, which only ever runs for a
			// new character.
			MainGame.OnGameStarted += InventoryBonus.Apply;

			Log.LogInfo("Plugin kupie.gk2.moreinventoryslots is loaded!");
		}

		private void OnDestroy()
		{
			MainGame.OnGameStarted -= InventoryBonus.Apply;
		}
	}
}

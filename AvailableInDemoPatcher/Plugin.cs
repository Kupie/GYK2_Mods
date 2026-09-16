using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AvailableInDemoPatcher
{
	[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
	public class Plugin : BaseUnityPlugin
	{
		internal static ConfigEntry<KeyboardShortcut> GiveItemsKey;
		internal static ConfigEntry<string> PolishedBronzeItemId;
		internal static ConfigEntry<int> PolishedBronzeCount;
		internal static ConfigEntry<string> PaperItemId;
		internal static ConfigEntry<int> PaperCount;

		private Harmony harmony;

		private void Awake()
		{
			GiveItemsKey = Config.Bind(
				"F9 Items",
				"GiveItemsKey",
				new KeyboardShortcut(KeyCode.F9),
				"Key that gives the items configured below.");

			// Not confirmed against the game's own item data - "polished_bronze" doesn't
			// appear anywhere in the game's code, items are defined in data tables this mod
			// can't see. If F9 doesn't actually give Polished Bronze, check the BepInEx log
			// for an error and correct the id here.
			PolishedBronzeItemId = Config.Bind(
				"F9 Items",
				"PolishedBronzeItemId",
				"polished_bronze",
				"Internal item id for Polished Bronze. Unconfirmed guess - see the note in this mod's source.");

			PolishedBronzeCount = Config.Bind("F9 Items", "PolishedBronzeCount", 10, "How many Polished Bronze to give per press.");

			// Confirmed from the game's own code - UIResourceBasedCraftWindow's debug test
			// method gives the player 15 of this exact id.
			PaperItemId = Config.Bind(
				"F9 Items",
				"PaperItemId",
				"clean_paper",
				"Internal item id for Paper. Confirmed from the game's own code.");

			PaperCount = Config.Bind("F9 Items", "PaperCount", 10, "How many Paper to give per press.");

			harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
			harmony.PatchAll();
		}

		private void OnDestroy()
		{
			harmony?.UnpatchSelf();
		}

		private void Update()
		{
			if (GiveItemsKey.Value.IsDown())
			{
				GiveItem(PolishedBronzeItemId.Value, PolishedBronzeCount.Value, "Polished Bronze");
				GiveItem(PaperItemId.Value, PaperCount.Value, "Paper");
			}
		}

		private void GiveItem(string itemId, int count, string label)
		{
			if (string.IsNullOrWhiteSpace(itemId) || count <= 0)
			{
				return;
			}

			Inventory inventory = MainGame.PlayerData?.Inventory;
			if (inventory == null)
			{
				Logger.LogWarning($"F9: couldn't give {label} - no active PlayerData/Inventory yet.");
				return;
			}

			// new Item(itemId, count) throws if itemId doesn't match any real item
			// definition - since PolishedBronzeItemId is an unverified guess, this is what
			// turns a bad id into a clear log line instead of a silent failure or a crash.
			try
			{
				bool added = inventory.AddItemToInventory(new Item(itemId, count), null, false);
				if (added)
				{
					Logger.LogInfo($"F9: gave {count}x {label} ('{itemId}').");
				}
				else
				{
					Logger.LogWarning($"F9: inventory couldn't fit {count}x {label} ('{itemId}') - is it full?");
				}
			}
			catch (Exception ex)
			{
				Logger.LogError($"F9: failed to give {label} - '{itemId}' may not be a real item id. {ex.Message}");
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
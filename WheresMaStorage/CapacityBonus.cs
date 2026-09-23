using System.Collections.Generic;
using HarmonyLib;

namespace WheresMaStorage
{
	// Same approach as MoreInventorySlots/InventoryBonus.cs, extended to cover
	// every placed container in the save (chests etc.), not just the player and
	// tool belt. Item.InventorySize has a public setter, but several of Item's
	// own capacity checks (CanAddItemToInventory and friends) read the private
	// backing field directly, so the field has to be written via the setter -
	// a Harmony postfix on the getter alone would not actually let more items
	// be stored. See MoreInventorySlots/README.md for the full writeup.
	internal static class CapacityBonus
	{
		private static int appliedPlayerBonus;
		private static int appliedToolBeltBonus;
		private static readonly Dictionary<SGuid, int> AppliedContainerBonus = new Dictionary<SGuid, int>();

		internal static void ApplyPlayerAndToolBelt()
		{
			PlayerData playerData = MainGame.PlayerData;
			if (playerData == null)
			{
				return;
			}

			ApplyBonus("player inventory", playerData.inventory?.Data, Plugin.PlayerInventoryBonus.Value, ref appliedPlayerBonus);
			ApplyBonus("tool belt", playerData.toolBeltInventory?.Data, Plugin.PlayerInventoryBonus.Value, ref appliedToolBeltBonus);
		}

		internal static void ApplyAllContainers()
		{
			WorldData worldData = MainGame.WorldData;
			if (worldData == null)
			{
				return;
			}

			foreach (GameSceneData sceneData in worldData.gameSceneDataList)
			{
				foreach (WgoData wgoData in sceneData.wgoDataList)
				{
					ApplyContainerBonus(wgoData);
				}
			}
		}

		internal static void ApplyContainerBonus(WgoData wgoData)
		{
			if (wgoData?.Definition == null || wgoData.Definition.inventorySize <= 0 || !wgoData.Definition.OpenInMultiInventory)
			{
				return;
			}

			int bonus = Plugin.ContainerInventoryBonus.Value;
			AppliedContainerBonus.TryGetValue(wgoData.UniqueId, out int applied);
			ApplyBonus($"container {wgoData.id}", wgoData.Inventory?.Data, bonus, ref applied);
			AppliedContainerBonus[wgoData.UniqueId] = applied;
		}

		private static void ApplyBonus(string label, Item item, int newBonus, ref int appliedBonus)
		{
			if (item == null)
			{
				return;
			}

			// Recover the underlying vanilla base size by undoing whatever bonus
			// was last applied, so re-running this never compounds on top of an
			// earlier bonus (config edit, repeated OnGameStarted, etc).
			int baseSize = item.InventorySize - appliedBonus;
			int desired = baseSize + newBonus;

			if (desired == item.InventorySize)
			{
				return;
			}

			if (desired < item.InventoryFillSize)
			{
				Plugin.Log.LogWarning($"Skipping {label} bonus of {newBonus}: it would shrink the container below the {item.InventoryFillSize} slots already in use.");
				return;
			}

			item.InventorySize = desired;
			appliedBonus = newBonus;
		}
	}

	// Applies the container bonus the moment a chest is placed/spawned during
	// play, rather than only at the next OnGameStarted.
	[HarmonyPatch(typeof(WorldData), nameof(WorldData.AddWgoData), typeof(WgoData), typeof(bool))]
	internal static class WorldData_AddWgoData_Patch
	{
		private static void Postfix(WgoData data)
		{
			CapacityBonus.ApplyContainerBonus(data);
		}
	}
}

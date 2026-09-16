namespace MoreInventorySlots
{
	// Item.InventorySize looks like the obvious thing to Harmony-postfix (add
	// the bonus on every read), but that doesn't actually work: several of
	// Item's own capacity checks (CanAddItemToInventory and friends) read the
	// private backing field directly rather than going through this
	// property, so a getter patch would make the number look bigger in the
	// UI without ever letting you hold more items. The only way to change
	// the number everywhere - including those internal field reads - is to
	// write the field itself, via the public setter, which is what this does.
	internal static class InventoryBonus
	{
		private static int appliedPlayerBonus;
		private static int appliedToolBeltBonus;

		internal static void Apply()
		{
			PlayerData playerData = MainGame.PlayerData;
			if (playerData == null)
			{
				return;
			}

			ApplyBonus("player inventory", playerData.inventory?.Data, Plugin.PlayerInventoryBonus.Value, ref appliedPlayerBonus);
			ApplyBonus("tool belt", playerData.toolBeltInventory?.Data, Plugin.ToolBeltBonus.Value, ref appliedToolBeltBonus);
		}

		private static void ApplyBonus(string label, Item item, int newBonus, ref int appliedBonus)
		{
			if (item == null)
			{
				return;
			}

			// Recover the underlying base size by undoing whatever bonus we
			// last applied, so re-running this (a config edit, a second
			// OnGameStarted, a different save with a different base size)
			// never compounds on top of an earlier bonus.
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
			Plugin.Log.LogInfo($"Set {label} size to {desired} (base {baseSize} + bonus {newBonus}).");
		}
	}
}

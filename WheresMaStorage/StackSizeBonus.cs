using System.Collections.Generic;

namespace WheresMaStorage
{
	// ItemDef instances are shared, balance-data singletons (one per item id, in
	// GameBalance.Me.itemDefs), so raising ItemDef.stackCount here affects every
	// copy of that item everywhere - unlike CapacityBonus, there's no per-Item
	// state to iterate at runtime.
	//
	// Category membership is read off ItemDef.type (an ItemType enum) and
	// ItemDef.isTool, confirmed against the decomp. GK1's "pen/paper/ink" and
	// "chisel" stacking toggles have no equivalent here yet - decomp doesn't
	// expose an item-id list or ItemType value for either, and stackCount is a
	// flat int per item rather than a "category" concept GK2 tracks itself, so
	// grepping the runtime item id list is the only way to find them. See
	// TASKS.md.
	internal static class StackSizeBonus
	{
		private static readonly Dictionary<string, int> OriginalStackCount = new Dictionary<string, int>();

		internal static void Apply()
		{
			GameBalance balance = GameBalance.Me;
			if (balance == null)
			{
				return;
			}

			foreach (ItemDef itemDef in balance.itemDefs)
			{
				if (!OriginalStackCount.TryGetValue(itemDef.id, out int baseStackCount))
				{
					baseStackCount = itemDef.stackCount;
					OriginalStackCount[itemDef.id] = baseStackCount;
				}

				int desired = Plugin.ModifyStackSize.Value && IsCategoryEnabled(itemDef)
					? Plugin.StackSizeForStackables.Value
					: baseStackCount;

				// Only ever raise a stack size - never shrink one a vanilla
				// definition (or another mod) set higher than our target.
				itemDef.stackCount = desired > baseStackCount ? desired : baseStackCount;
			}
		}

		private static bool IsCategoryEnabled(ItemDef itemDef)
		{
			if (itemDef.stackCount > 1)
			{
				// Already a vanilla stackable (general resources/consumables) -
				// always eligible, this is GK1's ungated "general stackables"
				// category.
				return true;
			}

			if (itemDef.isTool)
			{
				return Plugin.EnableToolStacking.Value;
			}

			switch (itemDef.type)
			{
				case ItemType.Sword:
				case ItemType.Bow:
				case ItemType.Pike:
					return Plugin.EnableWeaponStacking.Value;
				case ItemType.BodyArmor:
				case ItemType.Collar:
					return Plugin.EnableEquipmentStacking.Value;
				case ItemType.Preach:
					return Plugin.EnablePrayerStacking.Value;
				case ItemType.Brain:
				case ItemType.Heart:
				case ItemType.Flesh:
				case ItemType.Bones:
				case ItemType.Skull:
				case ItemType.Guts:
				case ItemType.Skin:
				case ItemType.Embalm:
					return Plugin.EnableGraveItemStacking.Value;
				default:
					return false;
			}
		}
	}
}

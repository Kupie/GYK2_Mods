using System.Collections.Generic;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;

namespace WheresMaStorage
{
	// Tier 3 QoL: append used-space and (for chests) world-zone-name to every
	// inventory panel's title. InventoryHeaderWidget.UpdateHeader (confirmed
	// via decomp, private but Harmony-patchable) is the single method every
	// panel header goes through - player inventory, tool belt, chests, bags -
	// so one postfix there covers all of them, rather than patching each
	// window type separately.
	internal static class InventoryTitles
	{
		// Inventory has no owner backreference (confirmed Phase 2 research),
		// so the header postfix alone can't get from "this is the inventory
		// being drawn" to "this is the zone it's in". The zone *is* known at
		// the point a chest window opens though (see the ctor patch below),
		// so that's recorded here and consulted by the header postfix.
		// Keyed by reference identity, which is stable for a WgoData's
		// lifetime - a removed chest's entry just goes unused rather than
		// being cleaned up, since Inventory itself gives no removal signal to
		// hook.
		internal static readonly Dictionary<Inventory, string> ZoneNameByInventory = new Dictionary<Inventory, string>();
	}

	[HarmonyPatch(typeof(InventoryHeaderWidget), "UpdateHeader")]
	internal static class InventoryHeaderWidget_UpdateHeader_Patch
	{
		// ___header/___data: Harmony's private-field injection, reaching
		// InventoryHeaderWidget's own private `header` field and the
		// protected `data` field it inherits from LazyWidget<T> (confirmed
		// field names via decomp). Runs after vanilla's UpdateHeader already
		// set (or didn't set) header.text, so this only has to append, not
		// reimplement the localization/visibility logic above it.
		private static void Postfix(InventoryHeaderWidgetData ___data, TextMeshProUGUI ___header)
		{
			if (___header == null || string.IsNullOrEmpty(___header.text) || ___data?.Inventory?.Data == null)
			{
				return;
			}

			string suffix = string.Empty;

			if (Plugin.ShowUsedSpaceInTitles.Value)
			{
				Item item = ___data.Inventory.Data;
				suffix += $" ({item.InventoryFillSize}/{item.InventorySize})";
			}

			if (Plugin.ShowWorldZoneInTitles.Value && InventoryTitles.ZoneNameByInventory.TryGetValue(___data.Inventory, out string zoneNameLocId))
			{
				suffix += $" - {LLBase.L(zoneNameLocId)}";
			}

			if (suffix.Length > 0)
			{
				___header.text += suffix;
			}
		}
	}

	[HarmonyPatch(typeof(UIBaseChestWindowData), MethodType.Constructor, typeof(Inventory), typeof(MultiInventory), typeof(WgoData))]
	internal static class UIBaseChestWindowData_Ctor_Patch
	{
		// "wz_" + zone id is the same display-name loc key vanilla itself
		// uses for zone names (confirmed in WorldZoneWidget.cs, the map's own
		// zone label).
		private static void Postfix(WgoData wgoData)
		{
			WorldZoneData zone = wgoData?.WorldZoneData;
			if (zone == null || wgoData.Inventory == null)
			{
				return;
			}

			InventoryTitles.ZoneNameByInventory[wgoData.Inventory] = "wz_" + zone.id;
		}
	}
}

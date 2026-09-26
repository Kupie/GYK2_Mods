using System.Collections.Generic;
using HarmonyLib;

namespace HideUnavailableItems
{
	// GK1's "hide invalid selections instead of graying them out": in the
	// player's own inventory/bag panels, an item the open window won't accept
	// (can't be sold to this vendor, can't go in this bag, isn't a prayer for
	// the prayer slot...) is hidden instead of just greyed.
	//
	// InventoryWidget.Redraw (decomp: Assembly-CSharp/InventoryWidget.cs) greys
	// cells via data.CustomItemsAvailableCondition and ends with its own hide
	// pass for data.CustomItemsNotShowCondition. This postfix reuses that same
	// SetActive(false) for cells the availability condition rejected.
	//
	// The vendor trade window is the one place an NPC's inventory is drawn
	// through InventoryWidget with an availability condition, and it must never
	// be hidden. Trading.FillVendorWindowData puts every vendor-side panel into
	// UIVendorWindowData.VendorMultiInventoryWidgetData: the vendor's stock, plus
	// "Tier N" previews for the next two tiers (the TryFormFakeInventoryForTier
	// local function - only visible in the IL, the decompiled .cs drops its
	// body). Each preview is a fake Inventory whose availability condition is a
	// lambda that always returns false and has no not-show condition, so
	// without this exclusion every preview item got hidden. Method-name and
	// not-show-condition heuristics can't identify those previews; membership
	// in VendorMultiInventoryWidgetData can, since the window draws exactly
	// those data objects.
	internal static class TradeWindowPanels
	{
		internal static MultiInventoryWidgetData VendorSide;
		internal static MultiInventoryWidgetData PlayerSide;

		internal static bool IsVendorSide(InventoryWidgetDataBase data)
		{
			return VendorSide != null && VendorSide.inventoriesData.Contains(data);
		}

		internal static bool IsPlayerSide(InventoryWidgetDataBase data)
		{
			return PlayerSide != null && PlayerSide.inventoriesData.Contains(data);
		}

		// Item ids the vendor's side actually shows (greyed or not): its stock
		// minus what vanilla's own not-show condition hides, plus the tier
		// previews. Read from data rather than UI cells so it doesn't depend on
		// which panel redrew first.
		internal static HashSet<string> VendorVisibleItemIds()
		{
			HashSet<string> ids = new HashSet<string>();
			if (VendorSide == null)
			{
				return ids;
			}

			foreach (InventoryWidgetDataBase widgetData in VendorSide.inventoriesData)
			{
				List<Item> items = widgetData?.Inventory?.Data?.Inventory;
				if (items == null)
				{
					continue;
				}

				foreach (Item item in items)
				{
					if (item == null || item.IsEmpty)
					{
						continue;
					}

					if (widgetData.CustomItemsNotShowCondition != null && widgetData.CustomItemsNotShowCondition(item))
					{
						continue;
					}

					ids.Add(item.id);
				}
			}

			return ids;
		}
	}

	[HarmonyPatch(typeof(Trading), nameof(Trading.FillVendorWindowData))]
	internal static class Trading_FillVendorWindowData_Patch
	{
		private static void Postfix(UIVendorWindowData vendorWindowData)
		{
			if (vendorWindowData?.VendorMultiInventoryWidgetData == null)
			{
				return;
			}

			TradeWindowPanels.VendorSide = vendorWindowData.VendorMultiInventoryWidgetData;
			TradeWindowPanels.PlayerSide = vendorWindowData.PlayerMultiInventoryWidgetData;
		}
	}

	[HarmonyPatch(typeof(InventoryWidget), nameof(InventoryWidget.Redraw))]
	internal static class InventoryWidget_Redraw_Patch
	{
		private static void Postfix(InventoryWidgetDataBase ___data, List<UIItemCell> ___uiItemCells)
		{
			if (!Plugin.HideUnavailable.Value || ___data?.CustomItemsAvailableCondition == null)
			{
				return;
			}

			if (TradeWindowPanels.IsVendorSide(___data))
			{
				return;
			}

			// On the player's side of a trade, an item the vendor still shows
			// (e.g. iron bars greyed in its "Tier 2" preview) stays greyed here
			// too - "the vendor deals in this, just not at this tier".
			HashSet<string> keepGreyedIds = TradeWindowPanels.IsPlayerSide(___data)
				? TradeWindowPanels.VendorVisibleItemIds()
				: null;

			foreach (UIItemCell cell in ___uiItemCells)
			{
				Item item = cell.DisplayingItem;
				if (!cell.gameObject.activeSelf || item == null || item.IsEmpty)
				{
					continue;
				}

				if (___data.CustomItemsAvailableCondition(item))
				{
					continue;
				}

				if (keepGreyedIds != null && keepGreyedIds.Contains(item.id))
				{
					continue;
				}

				cell.gameObject.SetActive(false);
			}
		}
	}
}

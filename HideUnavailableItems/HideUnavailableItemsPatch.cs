using System.Collections.Generic;
using HarmonyLib;

namespace HideUnavailableItems
{
	// GK1's "hide invalid selections instead of graying them out", applied to
	// the player's own inventory/bag panels: an item that can't be sold to the
	// open vendor, or can't go into the open bag, doesn't just show grayed out
	// (vanilla's normal behavior) - it's hidden entirely.
	//
	// Confirmed via decomp (InventoryWidget.Redraw, Assembly-CSharp/InventoryWidget.cs):
	// every inventory panel already has two independent predicates -
	// CustomItemsAvailableCondition (drives ItemRelatedWidgetState.Disabled,
	// i.e. the gray-out) and CustomItemsNotShowCondition (a separate hide-cell
	// pass vanilla itself uses, but leaves unset almost everywhere - e.g.
	// Trading.cs's vendor window wires PlayerItemsAvailableCondition [can this
	// be sold] for graying but never sets a not-show condition for the
	// player's own panel). Rather than trying to inject our own predicate into
	// CustomItemsNotShowCondition (a protected auto-property with no public
	// setter), this patches the same place vanilla's own hide pass runs: a
	// postfix that hides any cell whose CustomItemsAvailableCondition already
	// said no, reusing the exact SetActive(false) vanilla's hide pass already
	// does for whatever CustomItemsNotShowCondition covers.
	//
	// InventoryWidget is the base for the player's main inventory panel and
	// BagInventoryWidget (an open bag's contents) - both go through this one
	// Redraw override, so one patch covers both. ToolBeltInventoryWidget,
	// BodyOrgansInventoryWidget/BodyPocketInventoryWidget and
	// VendorDealInventoryWidget each have their own separate Redraw
	// implementation with the same two-predicate shape and are not covered
	// here.
	[HarmonyPatch(typeof(InventoryWidget), nameof(InventoryWidget.Redraw))]
	internal static class InventoryWidget_Redraw_Patch
	{
		private static void Postfix(InventoryWidgetDataBase ___data, List<UIItemCell> ___uiItemCells)
		{
			if (!Plugin.HideUnavailable.Value || ___data?.CustomItemsAvailableCondition == null)
			{
				return;
			}

			foreach (UIItemCell cell in ___uiItemCells)
			{
				if (!cell.gameObject.activeSelf || cell.DisplayingItem == null || cell.DisplayingItem.IsEmpty)
				{
					continue;
				}

				if (!___data.CustomItemsAvailableCondition(cell.DisplayingItem))
				{
					cell.gameObject.SetActive(false);
				}
			}
		}
	}
}

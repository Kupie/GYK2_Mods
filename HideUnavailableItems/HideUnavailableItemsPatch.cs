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
	//
	// CustomItemsAvailableCondition isn't unique to the player's own panel -
	// the same trade window also wires it up on the vendor's side (its
	// reverse-direction check, Vendor.CanSellItemToPlayer), so hiding on the
	// predicate alone hid the vendor's items too. Restricting to the two
	// confirmed player-side callbacks - Trading.cs's PlayerItemsAvailableCondition
	// (can this be sold to the open vendor) and
	// PlayerInventoryUIItemOpHandler.PlayerItemsAvailabilityCondition (can this
	// go in the open bag) - keys off the delegate's own method name rather than
	// which panel it's attached to, so the vendor's reverse-direction check
	// (a different method) never matches.
	[HarmonyPatch(typeof(InventoryWidget), nameof(InventoryWidget.Redraw))]
	internal static class InventoryWidget_Redraw_Patch
	{
		private static readonly HashSet<string> PlayerSideConditionMethodNames = new HashSet<string>
		{
			"PlayerItemsAvailableCondition",
			"PlayerItemsAvailabilityCondition",
		};

		private static void Postfix(InventoryWidgetDataBase ___data, List<UIItemCell> ___uiItemCells)
		{
			if (!Plugin.HideUnavailable.Value || ___data?.CustomItemsAvailableCondition == null)
			{
				return;
			}

			string conditionMethodName = ___data.CustomItemsAvailableCondition.Method?.Name;
			if (conditionMethodName == null || !PlayerSideConditionMethodNames.Contains(conditionMethodName))
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

using System;
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
	// the vendor trade window also wires it up on the vendor's side (its
	// reverse-direction check, Vendor.CanSellItemToPlayer), so hiding on the
	// predicate alone hid the vendor's items too. This is patched by excluding
	// only conditions declared on a "Vendor"-named type (matching the one
	// confirmed reverse-direction check) rather than allow-listing specific
	// player-side method names - every other filtered picker (bag-insert,
	// prayer slot, organ slot, etc.) each wires its own differently-named
	// condition method, and a narrower allow-list previously hid nothing in
	// those windows at all.
	//
	// Once past that check, also optionally hides the panel's blank
	// (unoccupied) slots - same SetActive(false) mechanism, just for cells
	// with no item at all rather than an unavailable one.
	//
	// Tier-gated vendors (e.g. a smithy that only trades bronze bars at rep
	// level 1, with iron/steel bars shown greyed rather than hidden on its own
	// panel until level 2) shouldn't have the matching sell-side rejection
	// hidden either - "greyed on the vendor's side" means the vendor still
	// deals in that item, just not at the player's current tier, same meaning
	// as vanilla's own grey-out.
	//
	// ShowBuyers/BuyerCache.cs already confirmed (via decomp) the real API for
	// this: Vendor.CurrentTierData.vendorProducts (List<VendorProductData>,
	// each with an itemId) is a superset that includes items the vendor deals
	// in even when Vendor.CurrentTierData.IsBuyingProduct(itemId) is currently
	// false for them (tier not met yet) - exactly the grey-vs-hide signal we
	// need, as a direct membership check instead of inferring it from whatever
	// happens to be rendered. The one missing piece was identifying *which*
	// vendor is currently open: nothing in this codebase tracks that
	// separately, but we don't need it to - Vendor.CanSellItemToPlayer (the
	// vendor-side condition excluded above) is an instance method, so the
	// delegate bound to it on that pass has the Vendor itself as its Target.
	// Caching that gives a direct Vendor reference, no extra tracking patch
	// needed. Only applies to the sell-to-vendor condition (identified by its
	// own confirmed method name); every other filtered picker has no vendor to
	// check against.
	[HarmonyPatch(typeof(InventoryWidget), nameof(InventoryWidget.Redraw))]
	internal static class InventoryWidget_Redraw_Patch
	{
		private const string SellToVendorConditionMethodName = "PlayerItemsAvailableCondition";

		private static Vendor lastVendorInstance;

		private static void Postfix(InventoryWidgetDataBase ___data, List<UIItemCell> ___uiItemCells)
		{
			if (___data?.CustomItemsAvailableCondition == null)
			{
				return;
			}

			var conditionMethod = ___data.CustomItemsAvailableCondition.Method;
			string declaringTypeName = conditionMethod?.DeclaringType?.Name;

			if (conditionMethod?.Name == "CanSellItemToPlayer" ||
				(declaringTypeName != null && declaringTypeName.IndexOf("Vendor", StringComparison.OrdinalIgnoreCase) >= 0))
			{
				// This is the vendor's own side of a trade window - cache the
				// vendor instance itself for the tier cross-check below and
				// never hide anything here; vendor's own panel stays exactly as
				// vanilla renders it (greyed, never hidden).
				lastVendorInstance = ___data.CustomItemsAvailableCondition.Target as Vendor;
				return;
			}

			if (!Plugin.HideUnavailable.Value)
			{
				return;
			}

			bool isSellToVendor = conditionMethod?.Name == SellToVendorConditionMethodName;

			foreach (UIItemCell cell in ___uiItemCells)
			{
				if (!cell.gameObject.activeSelf)
				{
					continue;
				}

				bool isBlank = cell.DisplayingItem == null || cell.DisplayingItem.IsEmpty;
				if (isBlank)
				{
					if (Plugin.HideBlankSlots.Value)
					{
						cell.gameObject.SetActive(false);
					}

					continue;
				}

				if (___data.CustomItemsAvailableCondition(cell.DisplayingItem))
				{
					continue;
				}

				if (isSellToVendor && VendorDealsInItem(cell.DisplayingItem))
				{
					// The vendor deals in this item's category at some tier
					// (just not buying it right now) - leave it at vanilla's
					// own grey-out here too instead of hiding it.
					continue;
				}

				cell.gameObject.SetActive(false);
			}
		}

		private static bool VendorDealsInItem(Item item)
		{
			string itemId = item?.Definition?.id;
			List<VendorProductData> vendorProducts = lastVendorInstance?.CurrentTierData?.vendorProducts;
			if (string.IsNullOrEmpty(itemId) || vendorProducts == null)
			{
				return false;
			}

			foreach (VendorProductData product in vendorProducts)
			{
				if (product.itemId == itemId)
				{
					return true;
				}
			}

			return false;
		}
	}
}

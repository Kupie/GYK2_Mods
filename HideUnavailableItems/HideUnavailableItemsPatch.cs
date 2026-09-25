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
	// pass vanilla itself uses at the end of Redraw, but leaves unset for the
	// player's own panel - InventoryWidgetDataHelper.GetWidgetsDataForInventory,
	// which Trading.cs uses to build it, has no parameter for one). This patches
	// the same place vanilla's own hide pass runs: a postfix that hides any cell
	// whose CustomItemsAvailableCondition already said no, reusing the exact
	// SetActive(false) vanilla's hide pass already does for whatever
	// CustomItemsNotShowCondition covers elsewhere.
	//
	// InventoryWidget is the base for the player's main inventory panel and
	// BagInventoryWidget (an open bag's contents) - both go through this one
	// Redraw override, so one patch covers both. ToolBeltInventoryWidget,
	// BodyOrgansInventoryWidget/BodyPocketInventoryWidget and
	// VendorDealInventoryWidget each have their own separate Redraw
	// implementation and are not covered here (VendorDealInventoryWidget in
	// particular overrides Redraw entirely, so it never runs through this
	// patch regardless).
	//
	// CustomItemsAvailableCondition isn't unique to the player's own panel -
	// confirmed in Trading.cs's FillVendorWindowData: the vendor's own buy-panel
	// is also a plain InventoryWidgetData (bound to vendor.Inventory), with its
	// own condition Trading.VendorItemsAvailableCondition (-> vendor.CanSellItemToPlayer)
	// for graying, and - unlike the player's panel - an explicit
	// CustomItemsNotShowCondition, Trading.VendorItemsNotShowCondition
	// (-> !vendor.CurrentTierData.HasProduct(item.id)), which vanilla's own hide
	// pass already applies. So the vendor's panel is fully handled by vanilla on
	// its own. Rather than matching the vendor condition's method name (which
	// didn't reliably catch it in practice), this patch detects the vendor's
	// panel structurally: InventoryWidgetDataHelper.GetWidgetsDataForInventory,
	// which Trading.cs uses to build the player's own panel, has no parameter
	// for a CustomItemsNotShowCondition at all, so it's always null there - the
	// vendor's panel is the only one routed through this Redraw that sets one.
	// A non-null CustomItemsNotShowCondition is therefore treated as "leave
	// this panel alone entirely."
	//
	// Tier-gated vendors (e.g. a smithy that only trades bronze bars at rep
	// level 1, with iron/steel bars shown greyed rather than hidden on its own
	// panel until level 2) shouldn't have the matching sell-side rejection
	// hidden either. Vendor.CurrentTierData.vendorProducts is scoped to only the
	// current tier's own product list (confirmed in Vendor.cs -
	// AddMissingCurrentTierProductsToInventory adds each tier's own products on
	// level-up, so tiers don't accumulate into one shared list), so it can't see
	// a next-tier item at all - not a usable signal. Instead this mirrors
	// vanilla's own VendorItemsNotShowCondition directly:
	// vendor.CurrentTierData.HasProduct(itemId). Trading.FillVendorWindowData
	// seeds vendor.Inventory with placeholder items for the next tier or two
	// before drawing, which is why HasProduct (and thus vanilla's own panel)
	// still shows those items greyed rather than absent - reusing that same
	// check means we don't need to reconstruct that seeding logic ourselves.
	//
	// Getting the Vendor instance needs no cross-widget caching: the player's
	// own condition (Trading.PlayerItemsAvailableCondition) is an instance
	// method bound to the same Trading object that owns the trade window, and
	// Trading has a private field cachedWindowData (type UIVendorWindowData,
	// confirmed in Trading.cs) whose public Vendor property is exactly what's
	// needed - read once per rejected item via reflection on the condition
	// delegate's own Target.
	[HarmonyPatch(typeof(InventoryWidget), nameof(InventoryWidget.Redraw))]
	internal static class InventoryWidget_Redraw_Patch
	{
		private const string SellToVendorConditionMethodName = "PlayerItemsAvailableCondition";

		private static readonly System.Reflection.FieldInfo TradingCachedWindowDataField =
			AccessTools.Field(typeof(Trading), "cachedWindowData");

		private static void Postfix(InventoryWidgetDataBase ___data, List<UIItemCell> ___uiItemCells)
		{
			if (___data?.CustomItemsAvailableCondition == null)
			{
				return;
			}

			if (___data.CustomItemsNotShowCondition != null)
			{
				// A panel with its own not-show condition already wired up -
				// the vendor's own panel, per Trading.cs. Vanilla already grays
				// (CustomItemsAvailableCondition) and hides
				// (CustomItemsNotShowCondition) it correctly on its own; leave
				// it completely alone.
				return;
			}

			string conditionMethodName = ___data.CustomItemsAvailableCondition.Method?.Name;

			if (!Plugin.HideUnavailable.Value)
			{
				return;
			}

			bool isSellToVendor = conditionMethodName == SellToVendorConditionMethodName;
			Vendor vendor = isSellToVendor ? GetVendorFromCondition(___data.CustomItemsAvailableCondition) : null;

			foreach (UIItemCell cell in ___uiItemCells)
			{
				if (!cell.gameObject.activeSelf)
				{
					continue;
				}

				if (cell.DisplayingItem == null || cell.DisplayingItem.IsEmpty)
				{
					continue;
				}

				if (___data.CustomItemsAvailableCondition(cell.DisplayingItem))
				{
					continue;
				}

				if (vendor != null && VendorStillShowsItem(vendor, cell.DisplayingItem))
				{
					// Mirrors vanilla's own VendorItemsNotShowCondition: the
					// vendor's own panel wouldn't hide this item id either
					// (just grey it), so match that here instead of hiding it.
					continue;
				}

				cell.gameObject.SetActive(false);
			}
		}

		private static Vendor GetVendorFromCondition(Func<Item, bool> condition)
		{
			Trading trading = condition.Target as Trading;
			if (trading == null || TradingCachedWindowDataField == null)
			{
				return null;
			}

			UIVendorWindowData windowData = TradingCachedWindowDataField.GetValue(trading) as UIVendorWindowData;
			return windowData?.Vendor;
		}

		private static bool VendorStillShowsItem(Vendor vendor, Item item)
		{
			string itemId = item?.Definition?.id;
			if (string.IsNullOrEmpty(itemId))
			{
				return false;
			}

			return vendor.CurrentTierData != null && vendor.CurrentTierData.HasProduct(itemId);
		}
	}
}

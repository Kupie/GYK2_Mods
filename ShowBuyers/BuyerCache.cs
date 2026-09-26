using System;
using System.Collections.Generic;

namespace ShowBuyers
{
	// One vendor who buys a given item, and the lowest tier (1-based, the same
	// numbering the vendor window's "Tier N" headers use) that vendor needs to
	// reach before they will.
	internal sealed class BuyerInfo
	{
		internal readonly Vendor Vendor;
		internal readonly int Tier;

		internal BuyerInfo(Vendor vendor, int tier)
		{
			Vendor = vendor;
			Tier = tier;
		}
	}

	// Maps item id -> every vendor that buys that item at any tier, not just
	// the tier each vendor is currently at. Vendor.CurrentTierData can't answer
	// this: it only ever holds the current tier's products, so a next-tier item
	// is never in it (the gap the Hide Unavailable Items mod ran into too).
	//
	// The vendor window's own "Tier N" previews (Trading.FillVendorWindowData's
	// local TryFormFakeInventoryForTier, whose body is only in the IL) read
	// VendorDef.tierDataList[N - 1] directly, which is balance data rather than
	// save state. Each tier's vendorProducts is the full list for that tier and
	// its notBuying is also per tier. Vendor.CanBuyItemFromPlayer is exactly
	// "in vendorProducts and not in notBuying" for the current tier, so this
	// applies that same check to every tier. VendorTierData.newProducts (the
	// list the previews show) isn't enough on its own: an item can be refused
	// at the tier it's introduced and bought from a later one, e.g. the
	// blacksmith lists hammer_1 from tier 1 but only drops it from notBuying
	// at tier 2.
	internal static class BuyerCache
	{
		private static readonly List<BuyerInfo> EmptyBuyers = new List<BuyerInfo>();

		private static Dictionary<string, List<BuyerInfo>> buyersByItemId;

		internal static void Invalidate()
		{
			buyersByItemId = null;
		}

		internal static IReadOnlyList<BuyerInfo> GetBuyers(string itemId)
		{
			if (buyersByItemId == null)
			{
				Rebuild();
			}

			List<BuyerInfo> buyers;
			return buyersByItemId.TryGetValue(itemId, out buyers) ? buyers : EmptyBuyers;
		}

		private static void Rebuild()
		{
			buyersByItemId = new Dictionary<string, List<BuyerInfo>>();

			List<Vendor> vendors = MainGame.Instance?.GameSave?.vendorSystem?.vendors;
			if (vendors == null)
			{
				return;
			}

			foreach (Vendor vendor in vendors)
			{
				VendorDef definition = vendor.Definition;
				List<VendorTierData> tierDataList = definition?.tierDataList;
				if (tierDataList == null || IsTestVendor(vendor.id))
				{
					continue;
				}

				HashSet<string> itemsAlreadyRecordedForVendor = new HashSet<string>();

				// Tiers below startTier are never reached (Vendor's constructor sets
				// curTier = startTier), and walking low to high means the first tier
				// that buys an item is the lowest one.
				for (int tierIndex = Math.Max(0, definition.startTier - 1); tierIndex < tierDataList.Count; tierIndex++)
				{
					VendorTierData tierData = tierDataList[tierIndex];
					List<VendorProductData> products = tierData?.vendorProducts;
					if (products == null)
					{
						continue;
					}

					foreach (VendorProductData product in products)
					{
						if (string.IsNullOrEmpty(product.itemId) || itemsAlreadyRecordedForVendor.Contains(product.itemId))
						{
							continue;
						}

						if (!tierData.IsBuyingProduct(product.itemId))
						{
							continue;
						}

						itemsAlreadyRecordedForVendor.Add(product.itemId);

						List<BuyerInfo> buyers;
						if (!buyersByItemId.TryGetValue(product.itemId, out buyers))
						{
							buyers = new List<BuyerInfo>();
							buyersByItemId[product.itemId] = buyers;
						}

						buyers.Add(new BuyerInfo(vendor, tierIndex + 1));
					}
				}
			}
		}

		// GameBalance ships test vendors ("test2", "test_town_vendor") alongside
		// the real ones, and VendorSystem.PrepareForGame creates a Vendor for
		// every VendorDef, so they'd otherwise show up as buyers of beer,
		// firewood and so on.
		private static bool IsTestVendor(string vendorId)
		{
			return vendorId != null && vendorId.StartsWith("test");
		}
	}
}

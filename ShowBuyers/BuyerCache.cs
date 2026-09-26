using System;
using System.Collections.Generic;
using System.Linq;
using LazyBearTechnology;

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
				buyersByItemId = Build();
				if (buyersByItemId == null)
				{
					return EmptyBuyers;
				}
			}

			List<BuyerInfo> buyers;
			return buyersByItemId.TryGetValue(itemId, out buyers) ? buyers : EmptyBuyers;
		}

		// Returns null while no save's vendors exist yet, so a tooltip shown that
		// early doesn't cache an empty list for the rest of the session.
		private static Dictionary<string, List<BuyerInfo>> Build()
		{
			List<Vendor> vendors = MainGame.Instance?.GameSave?.vendorSystem?.vendors;
			if (vendors == null || vendors.Count == 0)
			{
				return null;
			}

			bool logVendorData = Plugin.LogVendorData.Value;
			Dictionary<string, List<BuyerInfo>> result = new Dictionary<string, List<BuyerInfo>>();
			int upperTierEntries = 0;

			foreach (Vendor vendor in vendors)
			{
				VendorDef definition = vendor.Definition;
				List<VendorTierData> tierDataList = definition?.tierDataList;
				bool skip = tierDataList == null || IsTestVendor(vendor.id);

				if (logVendorData)
				{
					Plugin.Log.LogInfo(string.Format(
						"Vendor {0} ({1}): startTier {2}, curTier {3}, {4} tiers{5}",
						vendor.id,
						LLBase.L(vendor.id),
						definition?.startTier,
						vendor.CurTier,
						tierDataList?.Count,
						skip ? ", skipped" : string.Empty));
				}

				if (skip)
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

					if (logVendorData)
					{
						Plugin.Log.LogInfo(string.Format(
							"  Tier {0}: products [{1}] notBuying [{2}]",
							tierIndex + 1,
							string.Join(", ", products.Select(p => p.itemId)),
							string.Join(", ", tierData.notBuying)));
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
						if (!result.TryGetValue(product.itemId, out buyers))
						{
							buyers = new List<BuyerInfo>();
							result[product.itemId] = buyers;
						}

						buyers.Add(new BuyerInfo(vendor, tierIndex + 1));
						if (tierIndex + 1 > definition.startTier)
						{
							upperTierEntries++;
						}
					}
				}
			}

			Plugin.Log.LogInfo(string.Format(
				"Buyer list built from {0} vendors: {1} items have buyers, {2} buyer entries start above the vendor's first tier.",
				vendors.Count,
				result.Count,
				upperTierEntries));

			return result;
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

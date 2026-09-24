using System.Collections.Generic;

namespace ShowBuyers
{
	// One vendor who buys a given item, and the lowest tier (1-based, matching
	// VendorDef.tierDataList's index + 1 - confirmed via decomp, e.g.
	// UIVendorOrderWidget indexes tierIcons[VendorOrderData.Tier - 1]) that
	// vendor needs to reach before they will.
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

	// Maps item id -> the vendors (town NPCs, confirmed via decomp - Vendor.id/
	// Vendor.Definition.id are the same "npc_xxx" strings UIVendorWindow feeds
	// straight into LLBase.L for the shop header) that buy that item, at any
	// tier - not just the tier each vendor currently happens to be at, so the
	// tooltip can still tell the player "the smithy will buy this once they
	// reach tier II" before that's true. Built once and reused across every
	// tooltip hover instead of rescanning every vendor's tier list per frame.
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
				List<VendorTierData> tierDataList = vendor.Definition?.tierDataList;
				if (tierDataList == null)
				{
					continue;
				}

				// A vendor's product list only grows/changes as they level up, and
				// once they buy an item at one tier they keep buying it at every
				// tier after - so the lowest tier where notBuying doesn't exclude it
				// is the one that matters, and tiers are walked low to high to find
				// it directly instead of picking the minimum out of several hits.
				HashSet<string> itemsAlreadyRecordedForVendor = new HashSet<string>();

				for (int tierIndex = 0; tierIndex < tierDataList.Count; tierIndex++)
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
	}
}

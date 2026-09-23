using System.Collections.Generic;

namespace ShowBuyers
{
	// Maps item id -> the ids of the Vendors (town NPCs, confirmed via decomp -
	// Vendor.id/Vendor.Definition.id are the same "npc_xxx" strings UIVendorWindow
	// feeds straight into LLBase.L for the shop header) currently willing to buy
	// that item from the player. Built once and reused across every tooltip hover
	// instead of rescanning every vendor's product list per frame.
	internal static class BuyerCache
	{
		private static readonly List<string> EmptyBuyerIds = new List<string>();

		private static Dictionary<string, List<string>> buyerIdsByItemId;

		internal static void Invalidate()
		{
			buyerIdsByItemId = null;
		}

		internal static IReadOnlyList<string> GetBuyerNpcIds(string itemId)
		{
			if (buyerIdsByItemId == null)
			{
				Rebuild();
			}

			List<string> buyerIds;
			return buyerIdsByItemId.TryGetValue(itemId, out buyerIds) ? buyerIds : EmptyBuyerIds;
		}

		private static void Rebuild()
		{
			buyerIdsByItemId = new Dictionary<string, List<string>>();

			List<Vendor> vendors = MainGame.Instance?.GameSave?.vendorSystem?.vendors;
			if (vendors == null)
			{
				return;
			}

			foreach (Vendor vendor in vendors)
			{
				List<VendorProductData> products = vendor.CurrentTierData?.vendorProducts;
				if (products == null)
				{
					continue;
				}

				foreach (VendorProductData product in products)
				{
					if (string.IsNullOrEmpty(product.itemId) || !vendor.CurrentTierData.IsBuyingProduct(product.itemId))
					{
						continue;
					}

					List<string> buyerIds;
					if (!buyerIdsByItemId.TryGetValue(product.itemId, out buyerIds))
					{
						buyerIds = new List<string>();
						buyerIdsByItemId[product.itemId] = buyerIds;
					}

					if (!buyerIds.Contains(vendor.id))
					{
						buyerIds.Add(vendor.id);
					}
				}
			}
		}
	}
}

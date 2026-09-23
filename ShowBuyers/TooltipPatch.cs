using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;

namespace ShowBuyers
{
	// UITooltip.AddItemWidgets (confirmed via decomp) is the single method every
	// item tooltip - inventory, containers, vendor windows, craft results - builds
	// its widget list through, so one postfix here covers all of them instead of
	// patching each tooltip source separately.
	[HarmonyPatch(typeof(UITooltip), "AddItemWidgets")]
	internal static class UITooltip_AddItemWidgets_Patch
	{
		private static void Postfix(List<LazyWidgetDataBase> widgetData, ItemDef itemDef)
		{
			if (!Plugin.ShowBuyersEnabled.Value || itemDef == null)
			{
				return;
			}

			IReadOnlyList<string> buyerIds = BuyerCache.GetBuyerNpcIds(itemDef.id);
			if (buyerIds.Count == 0)
			{
				return;
			}

			StringBuilder names = new StringBuilder();
			for (int i = 0; i < buyerIds.Count; i++)
			{
				if (i > 0)
				{
					names.Append(", ");
				}

				names.Append(LLBase.L(buyerIds[i]));
			}

			widgetData.Add(new UITooltipSeparatorWidgetData());
			widgetData.Add(new UITooltipTextWidgetData(
				"Buyers: " + names,
				TextAlignmentOptions.Center,
				GetSmallDescriptionTextStyle()));
		}

		// smallDescriptionTextStyle is a private instance field on the private static
		// UITooltip.instance singleton (confirmed via decomp) - Harmony's ___field
		// injection can't reach it from a postfix on a static method, so it's pulled
		// through Traverse instead.
		private static TextStyle GetSmallDescriptionTextStyle()
		{
			UITooltip instance = Traverse.Create(typeof(UITooltip)).Field("instance").GetValue<UITooltip>();
			if (instance == null)
			{
				return null;
			}

			return Traverse.Create(instance).Field("smallDescriptionTextStyle").GetValue<TextStyle>();
		}
	}

	// Vendor.CurrentTierData (and so which items it buys) changes the moment a
	// vendor levels up, so the cache built from it has to be thrown away right
	// then rather than waiting for the next game load.
	[HarmonyPatch(typeof(Vendor), nameof(Vendor.ForceLevelUp))]
	internal static class Vendor_ForceLevelUp_Patch
	{
		private static void Postfix()
		{
			BuyerCache.Invalidate();
		}
	}
}

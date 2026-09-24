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
		private static readonly int[] RomanValues = { 10, 9, 5, 4, 1 };
		private static readonly string[] RomanNumerals = { "X", "IX", "V", "IV", "I" };

		private static void Postfix(List<LazyWidgetDataBase> widgetData, ItemDef itemDef)
		{
			if (!Plugin.ShowBuyersEnabled.Value || itemDef == null)
			{
				return;
			}

			IReadOnlyList<BuyerInfo> buyers = BuyerCache.GetBuyers(itemDef.id);
			if (buyers.Count == 0)
			{
				return;
			}

			StringBuilder text = new StringBuilder();
			for (int i = 0; i < buyers.Count; i++)
			{
				if (i > 0)
				{
					text.Append('\n');
				}

				BuyerInfo buyer = buyers[i];
				string buyerName = LLBase.L(buyer.Vendor.id);

				text.Append("Buyer: ").Append(buyerName);
				if (buyer.Tier > 1)
				{
					text.Append(" (").Append(ToRomanNumeral(buyer.Tier)).Append(')');
				}

				text.Append('\n');
				text.Append(Trading.FormatMoney(buyer.Vendor.CurBasePrice(buyer.Product), false, " ", null));
			}

			widgetData.Add(new UITooltipSeparatorWidgetData());
			widgetData.Add(new UITooltipTextWidgetData(
				text.ToString(),
				TextAlignmentOptions.Center,
				GetSmallDescriptionTextStyle()));
		}

		private static string ToRomanNumeral(int number)
		{
			StringBuilder roman = new StringBuilder();
			for (int i = 0; i < RomanValues.Length; i++)
			{
				while (number >= RomanValues[i])
				{
					roman.Append(RomanNumerals[i]);
					number -= RomanValues[i];
				}
			}

			return roman.ToString();
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
}

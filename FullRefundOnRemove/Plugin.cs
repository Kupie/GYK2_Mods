using System.Collections.Generic;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace FullRefundOnRemove
{
	// Vanilla's Remove tool refunds whatever a building's *removableWgos* BuildingDef
	// (buildingDef2 in the decomp) lists in its own outputItems - a separate, usually much
	// smaller "parts/pieces" list (e.g. a handful of planks) than what the building actually
	// cost to place. The real placement cost lives on the *buildableWgos* BuildingDef
	// (buildingDef in the decomp) as needItems, and is never consulted by removal at all.
	//
	// This mod makes Remove refund needItems (the full build cost) instead, for any Wgo that
	// has one - i.e. anything that was actually built by the player. Wgos that are
	// remove-only (decorative clutter, trees, rubble - a removableWgos entry with no matching
	// buildableWgos entry) are untouched, since there's no build cost to refund in the first
	// place; they keep vanilla's own outputItems behaviour.
	[BepInPlugin("kupie.gk2.fullrefundonremove", "Full Refund On Remove", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		private Harmony harmony;

		private void Awake()
		{
			harmony = new Harmony("kupie.gk2.fullrefundonremove");
			harmony.PatchAll();
		}

		private void OnDestroy()
		{
			harmony?.UnpatchSelf();
		}
	}

	// Targets Wgo.DoBuildRemove() directly rather than OutputItems.MakePreOutput() (which
	// buildingDef2.outputItems.MakePreOutput() ultimately calls) - MakePreOutput is shared
	// with ordinary crafting output all over the game, so patching it would be far too broad
	// a blast radius for what's meant to be a removal-only change.
	//
	// A full Prefix that skips the original (rather than a Postfix correcting its result
	// afterwards) is required because DoBuildRemove doesn't return the refund list - it
	// consumes it internally (dropping items immediately, or baking it into a CraftElement
	// for the deconstruct-over-time path) with no seam left afterwards to intercept.
	//
	// Deliberately defers to the original method (returns true) for every case this mod
	// doesn't need to change: no buildableWgos BuildingDef at all, no needItems on it, or the
	// buildingDef.deleteInstantly branch (which already drops needItems in full - see
	// Wgo.DoBuildRemove's own first branch in the decomp). Only the two buildingDef2-driven
	// refund branches (instant remove, and starting the deconstruct-over-time craft) are
	// replaced - both mirror the vanilla logic byte-for-byte except for where the refund list
	// itself comes from.
	[HarmonyPatch(typeof(Wgo), nameof(Wgo.DoBuildRemove))]
	internal static class Wgo_DoBuildRemove_Patch
	{
		private static bool Prefix(Wgo __instance, ref bool __result)
		{
			BuildingDef buildingDef;
			GameBalance.Me.buildableWgos.TryGetValue(__instance.WGOId, out buildingDef);

			if (buildingDef == null || buildingDef.needItems == null || buildingDef.needItems.Count == 0)
			{
				// Nothing was ever paid to place this Wgo (or it isn't placeable at all) -
				// nothing for this mod to do, let vanilla run unmodified.
				return true;
			}

			if (buildingDef.deleteInstantly)
			{
				// Vanilla's own first branch already refunds buildingDef.needItems in full
				// here - nothing to change.
				return true;
			}

			BuildingDef buildingDef2;
			GameBalance.Me.removableWgos.TryGetValue(__instance.WGOId, out buildingDef2);
			if (buildingDef2 == null)
			{
				// Shouldn't happen - IsBuildRemovable() already required buildingDef2 or
				// buildingDef.deleteInstantly before TryDoBuildAction() ever called this -
				// but fall back safely rather than assume.
				return true;
			}

			if (buildingDef2.deleteInstantly)
			{
				if (buildingDef.buildingMode == BuildingDef.BuildingMode.FightingPlace)
				{
					MainGame.Instance.GameSave.militaryBaseData.RemoveFightBuilding(__instance.data);
					MainGame.Instance.GameSave.worldData.RemoveWgoDataFromGameScene(__instance.data, true);
					__result = true;
					return false;
				}

				if (buildingDef.buildingMode == BuildingDef.BuildingMode.FightBuilding)
				{
					MainGame.Instance.GameSave.militaryBaseData.RemoveBaseBuilding(__instance.data);
				}

				__instance.DropItemsFromBuild(BuildFullRefundItems(buildingDef, __instance.data));
				MainGame.Instance.GameSave.worldData.RemoveWgoDataFromGameScene(__instance.data, true);
				if (__instance.data.Inventory.Data != null)
				{
					__instance.DropItemsFromInventory();
				}

				__result = true;
				return false;
			}

			CraftComponent craftComponent = __instance.data.CraftComponent;
			if (craftComponent.CurrentCraftElement != null && craftComponent.CurrentCraftElement.CraftId == buildingDef2.startCraft)
			{
				// A deconstruct-over-time craft is already running (this click is cancelling
				// it) - no refund list involved either way, let vanilla handle it.
				return true;
			}

			// Starts the deconstruct-over-time craft, same as vanilla, except the craft's
			// baked-in output is the full build cost instead of buildingDef2.outputItems.
			var craftElement = new CraftElement(buildingDef2.startCraft, 1, new CraftParamsData(buildingDef2.startCraft, __instance.data, CraftParamsData.CraftParamsType.Common, -1));
			List<Item> list = OutputItems.MakeOutput(BuildFullRefundItems(buildingDef, __instance.data));

			CraftDefBase fuelCraft = null;
			foreach (CraftDefBase craftDef in __instance.data.CraftComponent.AvailableCrafts)
			{
				if (craftDef.isFuelCraft)
				{
					fuelCraft = craftDef;
					break;
				}
			}

			if (__instance.data.Inventory.Data != null)
			{
				foreach (Item item in __instance.data.Inventory.Data.Inventory)
				{
					if (!item.IsEmpty && (fuelCraft == null || !item.Definition.isFuel))
					{
						list.Add(item);
					}
				}
			}

			if (fuelCraft != null)
			{
				NeedItemData fuelNeedItem = fuelCraft.needItems.Count > 0 ? fuelCraft.needItems[0] : null;
				List<ItemCount> fuelAddOnFinish = fuelCraft.addItemsToWgoOnFinish.MakePreOutput(__instance.data, 0f);
				int fuelAddCount = fuelAddOnFinish.Count > 0 ? fuelAddOnFinish[0].count : 0;
				if (fuelNeedItem != null && fuelAddCount > 0 && __instance.data.Inventory.Data != null)
				{
					foreach (Item item in __instance.data.Inventory.Data.Inventory)
					{
						if (!item.IsEmpty && item.Definition.isFuel)
						{
							list.Add(new Item(fuelNeedItem.id, Mathf.Clamp(item.Count / fuelAddCount * fuelNeedItem.GetCount(null), 1, 999)));
						}
					}
				}
			}

			craftElement.SetCustomOutputItems(list);
			__instance.data.CraftComponent.AddDestroyCraft(craftElement);
			__instance.interactionHandler = __instance.GetNewInteractionHandler();

			__result = false;
			return false;
		}

		// Mirrors the full-refund list vanilla's own buildingDef.deleteInstantly branch
		// already builds from needItems (Wgo.DoBuildRemove's first branch in the decomp) -
		// same simplification of not resolving item-group needItems to a concrete item,
		// since vanilla's own equivalent code doesn't either. Evaluates each NeedItemData's
		// count expression against the actual Wgo (data), not null, so a cost that scales
		// with the built object's own state (e.g. an upgrade tier) refunds what this
		// particular instance actually cost, not just the definition's baseline.
		private static List<ItemCount> BuildFullRefundItems(BuildingDef buildingDef, WgoData data)
		{
			var refundItems = new List<ItemCount>();
			foreach (NeedItemData needItem in buildingDef.needItems)
			{
				refundItems.Add(new ItemCount(needItem.id, needItem.GetCount(data)));
			}

			return refundItems;
		}
	}
}

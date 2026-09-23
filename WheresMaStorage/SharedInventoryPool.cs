using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WheresMaStorage
{
	// Tier 1 - the mod's actual reason to exist. Decomp research (TASKS.md) found
	// that GK2 already pools every eligible container in a zone for chests, craft
	// desks and the Builder/town-building desks - it all funnels through one of
	// two constructors: MultiInventory(WorldZoneData, WgoData, bool), or
	// WgoData.GetCraftableMultiInventory/MultiInventory(PlayerData, bool), which
	// both call the first one internally. So this file doesn't build a pool - it
	// restricts and reorders the one vanilla already builds.
	//
	// Patching that single WorldZoneData-taking constructor covers exclusion for
	// chests, craft desks AND building in one place. Distance sort can't be done
	// there for craft desks/building though: GetCraftableMultiInventory and the
	// PlayerData constructor both call Inventory-returning MultiInventory.Add()
	// afterward, and Add() always re-runs the private Sort() (fuel-container
	// priority, not distance), which would silently undo any ordering applied
	// inside the WorldZoneData constructor. So distance sort for those two paths
	// is applied again, later, after all Adds are done - see the two dedicated
	// patches below. For chests, nothing is added after the WorldZoneData
	// constructor returns (UIBaseChestWindowData just reads the list), so a
	// single sort there is enough.
	internal static class SharedInventoryPool
	{
		// Confirmed via DataDumper_Output/worldZoneDefs.json against the live
		// balance data (105 zone defs). GK1's third exclusion category, the
		// "zombie mill", has no matching zone id in that dump and no id string
		// appears anywhere in the decompiled code either - GK2 may not have a
		// dedicated zone for it, or it's identified some other way (a WGO tag,
		// not a zone). Deliberately not implemented rather than guessed at -
		// see TASKS.md.
		private const string WellZoneId = "well";
		private const string QuarryZoneId = "mine";

		internal static void ApplyZoneRules(MultiInventory instance, WorldZoneData worldZoneData, Vector3? sortReferencePosition)
		{
			if (instance == null || worldZoneData?.Definition == null)
			{
				return;
			}

			Dictionary<Inventory, Vector3> zonePositions = BuildZonePositionMap(worldZoneData);

			if (!Plugin.SharedInventory.Value || IsZoneExcluded(worldZoneData.Definition.id))
			{
				RemoveEntries(instance, zonePositions.Keys);
				return;
			}

			if (sortReferencePosition.HasValue && Plugin.SortByDistanceFromCrafter.Value)
			{
				SortByDistance(instance.inventoryList, sortReferencePosition.Value, zonePositions);
			}
		}

		// GetCraftableMultiInventory (craft desks) has already merged in the
		// worker's inventory, the desk's own craft buffer and the interacting
		// player's inventory by the time this runs - all after the exclusion
		// pass above already stripped any excluded zone's containers out of the
		// nested WorldZoneData MultiInventory it built internally. This only
		// needs to (a) strip the zone's containers entirely when a zombie is
		// working the desk and the config says not to give zombies pool access,
		// and (b) re-sort by distance from the desk, since the Adds since the
		// exclusion pass re-ran the vanilla fuel-priority Sort().
		internal static void FinalizeCraftableMultiInventory(WgoData wgoData, MultiInventory multiInventory)
		{
			if (wgoData == null || multiInventory == null)
			{
				return;
			}

			WorldZoneData zone = wgoData.WorldZoneData;
			if (zone == null)
			{
				return;
			}

			Dictionary<Inventory, Vector3> zonePositions = BuildZonePositionMap(zone);

			if (!Plugin.AllowZombiesAccessToSharedInventory.Value && wgoData.Worker is ZombieWgoData)
			{
				RemoveEntries(multiInventory, zonePositions.Keys);
				return;
			}

			if (Plugin.SortByDistanceFromCrafter.Value)
			{
				SortByDistance(multiInventory.inventoryList, wgoData.Position, zonePositions);
			}
		}

		// BuildManager/UIBuildingWindow/UITownBuildingWindow all build their
		// MultiInventory from new MultiInventory(playerData, true), which itself
		// calls the WorldZoneData constructor already patched above (so
		// exclusion already ran) then Insert(0, playerData.inventory) - no
		// further Add() calls after that, but Insert doesn't re-sort, so a
		// distance pass here is still needed for a correct order relative to
		// the player, who is the crafter for every one of these call sites.
		internal static void FinalizeBuildMultiInventory(PlayerData playerData, MultiInventory multiInventory)
		{
			if (playerData?.CurrentWorldZoneData == null || multiInventory == null || !Plugin.SortByDistanceFromCrafter.Value)
			{
				return;
			}

			Dictionary<Inventory, Vector3> zonePositions = BuildZonePositionMap(playerData.CurrentWorldZoneData);
			SortByDistance(multiInventory.inventoryList, playerData.position.Value, zonePositions);
		}

		private static bool IsZoneExcluded(string zoneDefId)
		{
			if (zoneDefId == WellZoneId)
			{
				return Plugin.ExcludeWellsFromSharedInventory.Value;
			}

			if (zoneDefId == QuarryZoneId)
			{
				return Plugin.ExcludeQuarryFromSharedInventory.Value;
			}

			return false;
		}

		private static Dictionary<Inventory, Vector3> BuildZonePositionMap(WorldZoneData zone)
		{
			var map = new Dictionary<Inventory, Vector3>();
			WorldData worldData = MainGame.WorldData;
			if (worldData == null)
			{
				return map;
			}

			foreach (SGuid guid in zone.wgoDataList)
			{
				WgoData wgoData = worldData.GetWgoData(guid);
				if (wgoData?.Inventory != null)
				{
					map[wgoData.Inventory] = wgoData.Position;
				}
			}

			return map;
		}

		private static void RemoveEntries(MultiInventory instance, IEnumerable<Inventory> entries)
		{
			foreach (Inventory inventory in entries)
			{
				instance.inventoryList.Remove(inventory);
			}
		}

		// Entries with no known position (the desk's own craft buffer, the
		// worker's carried inventory, the interacting player's own inventory)
		// sort as distance 0 - i.e. first, ahead of every pooled zone container.
		// That's the right default even without a real position for them: GK1's
		// intent was "closest/most-personal storage first", and personal/
		// immediate storage is by definition closer than anything pooled from
		// elsewhere in the zone.
		private static void SortByDistance(List<Inventory> inventoryList, Vector3 referencePosition, Dictionary<Inventory, Vector3> positions)
		{
			inventoryList.Sort((a, b) =>
			{
				float distA = positions.TryGetValue(a, out Vector3 posA) ? Vector3.Distance(referencePosition, posA) : 0f;
				float distB = positions.TryGetValue(b, out Vector3 posB) ? Vector3.Distance(referencePosition, posB) : 0f;
				return distA.CompareTo(distB);
			});
		}
	}

	[HarmonyPatch(typeof(MultiInventory), MethodType.Constructor, typeof(WorldZoneData), typeof(WgoData), typeof(bool))]
	internal static class MultiInventory_ZoneCtor_Patch
	{
		// excludeWgoData is the chest itself in ChestInteractionHandler's call
		// (the only confirmed caller of this exact overload with a non-null
		// second argument) - reusing it as the distance-sort reference point
		// means chests are fully handled by this one patch, with no need for a
		// separate "finalize" pass the way craft desks and building need.
		private static void Postfix(MultiInventory __instance, WorldZoneData worldZoneData, WgoData excludeWgoData)
		{
			Vector3? referencePosition = excludeWgoData != null ? excludeWgoData.Position : (Vector3?)null;
			SharedInventoryPool.ApplyZoneRules(__instance, worldZoneData, referencePosition);
		}
	}

	// ZombieWgoData and ConveyorWgoData each override GetCraftableMultiInventory
	// with their own implementation (confirmed in decomp) - patching the base
	// WgoData method here does not reach either override, since Harmony patches
	// the specific MethodInfo it's given, not every virtual dispatch target.
	// Ordinary craft desks (CraftInteractionHandler's WgoData) go through the
	// base implementation and are covered; zombie-specific and conveyor-belt
	// crafting are not yet, and are unrestricted/unsorted rather than broken -
	// see TASKS.md.
	[HarmonyPatch(typeof(WgoData), nameof(WgoData.GetCraftableMultiInventory))]
	internal static class WgoData_GetCraftableMultiInventory_Patch
	{
		private static void Postfix(WgoData __instance, MultiInventory __result)
		{
			SharedInventoryPool.FinalizeCraftableMultiInventory(__instance, __result);
		}
	}

	[HarmonyPatch(typeof(MultiInventory), MethodType.Constructor, typeof(PlayerData), typeof(bool))]
	internal static class MultiInventory_PlayerCtor_Patch
	{
		private static void Postfix(MultiInventory __instance, PlayerData playerData)
		{
			SharedInventoryPool.FinalizeBuildMultiInventory(playerData, __instance);
		}
	}
}

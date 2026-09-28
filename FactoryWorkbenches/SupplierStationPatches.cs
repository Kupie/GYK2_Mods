using System.Collections.Generic;
using HarmonyLib;

namespace FactoryWorkbenches
{
	// The conveyor desk's build list comes from BuildingDef.GetBuildingsInBuilder, which reads
	// GameBalance.buildDefsInBuilder (filled once from each BuildingDef.buildsIn). The station's
	// BuildingDef does not list builder_conveyor in buildsIn and must not be edited, so this
	// appends it (and, when enabled, the three everyday chests) to the returned list instead.
	// Nothing downstream re-checks buildsIn: the only reader of that field in the whole game is
	// GameBalance.CreateBuildCache.
	//
	// Postfix: the list is built and returned in one go, so adding to it afterwards is enough.
	//
	// Placement and removal need no patch. A def with no customBuildAreaId (the station) is
	// placed by WgoBuildPointer's "Soft" rule, which only needs each cell to overlap a BuildArea
	// (any id) inside the desk's world zone, and the conveyor zone is covered by conveyor_place
	// build areas. Removal is keyed by wgo id (GameBalance.removableWgos) and does not look at
	// the current desk at all.
	[HarmonyPatch(typeof(BuildingDef), nameof(BuildingDef.GetBuildingsInBuilder))]
	internal static class BuildingDef_GetBuildingsInBuilder_Patch
	{
		private const string StationWgoId = "zombie_supplier_station";

		// The desk entries of the everyday chests are the "_place" defs (chest_rough_place_p,
		// chest_place_p, chest_good_place_p, 20 / 30 / 40 slots): a construction site that turns into
		// the real chest wgo after three hits, exactly like conveyor_chest_t1_place. The real chest
		// defs (chest_rough_p, chest_p, chest_good_p) have an empty buildsIn and are never offered.
		private static readonly string[] ChestPlaceWgoIds = { "chest_rough_place", "chest_place", "chest_good_place" };

		private static void Postfix(Wgo builder, ref List<BuildData> __result)
		{
			if (!Plugin.Enabled.Value)
			{
				return;
			}
			if (builder == null || __result == null || builder.Id != Plugin.ConveyorDeskId)
			{
				return;
			}

			TryAdd(StationWgoId, __result);

			if (Plugin.AddNormalChests.Value)
			{
				foreach (string wgoId in ChestPlaceWgoIds)
				{
					TryAdd(wgoId, __result);
				}
			}
		}

		// Same filter as the vanilla loop in GetBuildingsInBuilder. The fight-builder barricade
		// and tower filters are left out: they only apply to the fight builder desks.
		private static bool TryAdd(string wgoId, List<BuildData> list)
		{
			GameBalance balance = GameBalance.Me;
			BuildingDef def;
			if (balance == null || !balance.buildableWgos.TryGetValue(wgoId, out def))
			{
				return false;
			}

			BuildingDef.BuildingMode mode = def.buildingMode;
			if (mode == BuildingDef.BuildingMode.None || mode == BuildingDef.BuildingMode.Remove)
			{
				return false;
			}

			KnowledgeSystem knowledge = MainGame.Instance.GameSave.knowledgeSystem;
			if (def.isNeedsUnlock && !knowledge.unlockedBuildings.Contains(def.id))
			{
				return false;
			}
			if (knowledge.lockedBuildings.Contains(def.id))
			{
				return false;
			}

			foreach (BuildData existing in list)
			{
				if (existing != null && existing.Definition == def)
				{
					return true;
				}
			}

			list.Add(BuildData.GetDataForBuild(def));
			Plugin.Verbose("Added " + def.id + " to the build list of " + Plugin.ConveyorDeskId);
			return true;
		}
	}
}

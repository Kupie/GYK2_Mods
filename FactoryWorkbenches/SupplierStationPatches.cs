using System.Collections.Generic;
using HarmonyLib;

namespace FactoryWorkbenches
{
	// The conveyor desk's build list comes from BuildingDef.GetBuildingsInBuilder, which reads
	// GameBalance.buildDefsInBuilder (filled once from each BuildingDef.buildsIn). The station's
	// BuildingDef does not list builder_conveyor in buildsIn and must not be edited, so this
	// appends it to the returned list instead. Nothing downstream re-checks buildsIn: the only
	// reader of that field in the whole game is GameBalance.CreateBuildCache.
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
		private const string MiniStationWgoId = "zombie_supplier_station_mini";

		private static bool loggedMissingMini;

		private static void Postfix(Wgo builder, ref List<BuildData> __result)
		{
			if (!Plugin.Enabled.Value || !Plugin.SupplierStationOnConveyorDesk.Value)
			{
				return;
			}
			if (builder == null || __result == null || builder.Id != Plugin.ConveyorDeskId.Value)
			{
				return;
			}

			TryAdd(StationWgoId, __result);
			if (Plugin.SupplierStationMiniOnConveyorDesk.Value)
			{
				if (!TryAdd(MiniStationWgoId, __result) && !loggedMissingMini)
				{
					loggedMissingMini = true;
					Plugin.Log.LogInfo("No BuildingDef exists for " + MiniStationWgoId + ", so it cannot be offered on the conveyor desk.");
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
			Plugin.Verbose("Added " + def.id + " to the build list of " + Plugin.ConveyorDeskId.Value);
			return true;
		}
	}
}

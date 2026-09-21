using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace BuildAnywhere
{
	// Port of p1xel8ted's GK1 mod "I Build Where I Want!" (IBuildWhereIWant) onto GK2's very
	// different building system. GK1's BuildModeLogics/WorldGameObject/FlowGridCell pipeline
	// doesn't exist here - GK2 places objects through BuildManager -> BuildController ->
	// BuildPointer -> WgoBuildPointer, and the actual "can I place this here" decision lives in
	// WgoBuildPointer.UpdateSelectionCellsState(), not in the visual ground-grid overlay
	// (BuildGridData/BuildCellData), which only draws the tinted tiles you see while moving the
	// cursor and doesn't gate placement on its own. See README.md for how this was traced.
	[BepInPlugin("kupie.gk2.buildanywhere", "Build Anywhere", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ConfigEntry<bool> AllowBuildAnywhere;
		internal static ConfigEntry<KeyboardShortcut> OpenBuildMenuKey;
		internal static ConfigEntry<bool> ShowEveryBuildingOnHotkeyOpen;
		internal static ConfigEntry<bool> Debug;

		// Set for the duration of the hotkey's own TryEnable call so the FormBuildData patch
		// below can tell "opened via this mod's hotkey" apart from a normal desk interaction -
		// only the former should get every building instead of that desk's own recipe list.
		internal static bool HotkeyOpenInProgress;

		private Harmony harmony;

		private void Awake()
		{
			AllowBuildAnywhere = Config.Bind(
				"General",
				"AllowBuildAnywhere",
				true,
				"Lets placement succeed anywhere in view, ignoring the current build zone's boundary, other objects in the way, and the placement-blocking layer some terrain uses. Resource cost still applies - this doesn't give items for free, it only removes the positional restrictions.");

			OpenBuildMenuKey = Config.Bind(
				"General",
				"OpenBuildMenuKey",
				new KeyboardShortcut(KeyCode.B, KeyCode.LeftControl),
				"Opens the build menu from whichever Builder-type desk is currently loaded nearest to you, regardless of distance or which zone it belongs to. Most useful together with AllowBuildAnywhere so you don't have to walk back to a specific desk first.");

			ShowEveryBuildingOnHotkeyOpen = Config.Bind(
				"General",
				"ShowEveryBuildingOnHotkeyOpen",
				true,
				"Like GK1's craft-anywhere menu, opening the build menu with OpenBuildMenuKey shows every unlocked building from every desk in the game, not just whatever the nearest desk normally offers. Only affects menus opened via that hotkey - interacting with a desk normally still shows just that desk's own list.");

			Debug = Config.Bind("General", "Debug", false, "Extra logging for troubleshooting.");

			harmony = new Harmony("kupie.gk2.buildanywhere");
			harmony.PatchAll();
		}

		private void OnDestroy()
		{
			harmony?.UnpatchSelf();
		}

		private void Update()
		{
			if (!OpenBuildMenuKey.Value.IsDown())
			{
				return;
			}

			Wgo desk = FindNearestBuilderDesk();
			if (desk == null)
			{
				Logger.LogWarning("BuildAnywhere: no Builder-type desk is currently loaded nearby - can't open the build menu.");
				return;
			}

			HotkeyOpenInProgress = true;
			bool opened;
			try
			{
				opened = LazySingleton<BuildManager>.Instance.TryEnable(desk, null);
			}
			finally
			{
				HotkeyOpenInProgress = false;
			}

			if (Debug.Value)
			{
				Logger.LogInfo($"BuildAnywhere: TryEnable on '{desk.Id}' returned {opened}.");
			}
		}

		// Picks whichever loaded Wgo has a plain Builder interaction (a normal crafting desk -
		// not a FightBuilder military-base desk, which needs its own inventory callback and
		// isn't handled here) and is physically closest to the player. Only finds desks that
		// are actually spawned in the currently loaded scene, the same limitation
		// FindObjectsByType has everywhere else in this codebase.
		private static Wgo FindNearestBuilderDesk()
		{
			PlayerController player = MainGame.PlayerController;
			if (player == null)
			{
				return null;
			}

			Vector3 playerPos = player.transform.position;
			Wgo nearest = null;
			float bestSqrDist = float.PositiveInfinity;

			foreach (Wgo wgo in UnityEngine.Object.FindObjectsByType<Wgo>(FindObjectsSortMode.None))
			{
				if (wgo == null || wgo.Data?.Definition == null)
				{
					continue;
				}

				if (wgo.Data.Definition.interactionType != WGODef.InteractionType.Builder)
				{
					continue;
				}

				float sqrDist = (wgo.transform.position - playerPos).sqrMagnitude;
				if (sqrDist < bestSqrDist)
				{
					bestSqrDist = sqrDist;
					nearest = wgo;
				}
			}

			return nearest;
		}
	}

	// The real placement gate. UpdateSelectionCellsState() runs its own physics query per
	// selection cell (separate from the ground-grid overlay in BuildGridData/BuildCellData) and
	// folds three unrelated things into one bool, shownAsActive:
	//   1. Can you afford it right now (canTakeResources - inventory + limitMax check)
	//   2. Is every cell still inside a WorldZone matching the zone you opened build mode in
	//   3. Is every cell clear of blocking areas, other Wgos, and the hard-block layer (29)
	// TryDoBuildAction() (the method that actually spawns the object) only checks shownAsActive,
	// so overriding it here is sufficient - no need to also patch TryDoBuildAction directly.
	// This postfix re-derives shownAsActive from just #1, discarding whatever #2/#3 decided, so
	// resource cost is still enforced but the positional restrictions are not. It has to redo
	// the same cells/moduleSlotAreas propagation the original method's tail does, since the
	// original already wrote the old value to both before this postfix runs.
	//
	// canTakeResources, shownAsActive, cells, and moduleSlotAreas are all private/protected in
	// the game's own source (shownAsActive/cells on the base BuildPointerObject, the other two
	// on WgoBuildPointer itself) - accessed here directly assuming the game assembly is
	// publicized at build time (Krafs.Publicizer, PublicizeAll - see the .csproj), same as
	// AvailableInDemoPatcher already does for its own private-field pokes.
	[HarmonyPatch(typeof(WgoBuildPointer), nameof(WgoBuildPointer.UpdateSelectionCellsState))]
	internal static class WgoBuildPointer_UpdateSelectionCellsState_Patch
	{
		private static void Postfix(WgoBuildPointer __instance)
		{
			if (!Plugin.AllowBuildAnywhere.Value)
			{
				return;
			}

			Func<bool> canTakeResources = __instance.canTakeResources;
			bool affordable = canTakeResources == null || canTakeResources();

			__instance.shownAsActive = affordable;

			List<BuildSelectionCell> cells = __instance.cells;
			if (cells != null)
			{
				foreach (BuildSelectionCell cell in cells)
				{
					if (cell != null && !(cell is BuffCell))
					{
						cell.IsAvailableForBuild = affordable;
					}
				}
			}

			List<ModuleSlotArea> moduleSlotAreas = __instance.moduleSlotAreas;
			if (moduleSlotAreas != null)
			{
				foreach (ModuleSlotArea area in moduleSlotAreas)
				{
					if (area != null)
					{
						area.ApplyVisibility(affordable);
					}
				}
			}
		}
	}

	// TryEnable's only hard failure mode is not finding a WorldZone whose builderId matches
	// this desk within 10 units (WgoExtensions.TryGetNearestBuilderWorldZone). Without this,
	// the hotkey above would open the menu on a distant desk, find no matching zone, and the
	// build window would silently never appear. Falls back to whichever loaded WorldZone is
	// physically nearest the desk, so the grid still centers somewhere sensible instead of
	// picking an arbitrary one.
	[HarmonyPatch(typeof(WgoExtensions), nameof(WgoExtensions.TryGetNearestBuilderWorldZone))]
	internal static class WgoExtensions_TryGetNearestBuilderWorldZone_Patch
	{
		private static void Postfix(Wgo builderWgo, ref bool __result, ref WorldZone worldZone)
		{
			if (__result || !Plugin.AllowBuildAnywhere.Value || builderWgo == null)
			{
				return;
			}

			WorldZone[] allZones = UnityEngine.Object.FindObjectsByType<WorldZone>(FindObjectsSortMode.None);
			if (allZones.Length == 0)
			{
				return;
			}

			Vector3 pos = builderWgo.transform.position;
			WorldZone nearest = allZones[0];
			float bestSqrDist = float.PositiveInfinity;

			foreach (WorldZone zone in allZones)
			{
				if (zone == null || zone.ZoneCollider == null)
				{
					continue;
				}

				float sqrDist = zone.ZoneCollider.bounds.SqrDistance(pos);
				if (sqrDist < bestSqrDist)
				{
					bestSqrDist = sqrDist;
					nearest = zone;
				}
			}

			worldZone = nearest;
			__result = true;
		}
	}

	// GK1's craft-anywhere menu showed every craft the player had unlocked, aggregated across
	// every desk in the game, not just what the desk you interacted with offers - GameBalance's
	// craft_obj_data list, filtered by MainGame.me.save.IsCraftVisible(d). GK2's equivalent
	// data is per-desk: GameBalance.Me.buildDefsInBuilder[deskId] gives one desk's list, and
	// BuildingDef.GetBuildingsInBuilder(desk) is what normally turns that into the BuildData
	// list FormBuildData assigns to its buildDataList field. This postfix only runs for menus
	// this mod's hotkey opened (HotkeyOpenInProgress) and replaces that field with every
	// building across every desk's list combined, using the exact same per-building unlock
	// checks GetBuildingsInBuilder itself uses (isNeedsUnlock/unlockedBuildings/lockedBuildings)
	// so it still respects what the player has actually unlocked rather than showing everything
	// in the game regardless of progress. A normal desk interaction never sets that flag, so it
	// keeps showing just that desk's own list, same as vanilla.
	//
	// FormBuildData and buildDataList are both private on BuildManager in the game's own
	// source - accessed here directly assuming the game assembly is publicized at build time
	// (Krafs.Publicizer, PublicizeAll - see the .csproj).
	[HarmonyPatch(typeof(BuildManager), nameof(BuildManager.FormBuildData))]
	internal static class BuildManager_FormBuildData_Patch
	{
		private static void Postfix(BuildManager __instance, ref bool __result)
		{
			if (!__result || !Plugin.HotkeyOpenInProgress || !Plugin.ShowEveryBuildingOnHotkeyOpen.Value)
			{
				return;
			}

			var seen = new HashSet<BuildingDef>();
			var allBuildings = new List<BuildData>();

			foreach (List<BuildingDef> deskBuildings in GameBalance.Me.buildDefsInBuilder.Values)
			{
				foreach (BuildingDef buildingDef in deskBuildings)
				{
					if (buildingDef == null || !seen.Add(buildingDef))
					{
						continue;
					}

					if (buildingDef.buildingMode == BuildingDef.BuildingMode.None || buildingDef.buildingMode == BuildingDef.BuildingMode.Remove)
					{
						continue;
					}

					if (buildingDef.isNeedsUnlock && !MainGame.Instance.GameSave.knowledgeSystem.unlockedBuildings.Contains(buildingDef.id))
					{
						continue;
					}

					if (MainGame.Instance.GameSave.knowledgeSystem.lockedBuildings.Contains(buildingDef.id))
					{
						continue;
					}

					allBuildings.Add(BuildData.GetDataForBuild(buildingDef));
				}
			}

			__instance.buildDataList = allBuildings;

			if (Plugin.Debug.Value)
			{
				UnityEngine.Debug.Log($"BuildAnywhere: hotkey menu showing {allBuildings.Count} buildings aggregated across {GameBalance.Me.buildDefsInBuilder.Count} desks.");
			}
		}
	}
}

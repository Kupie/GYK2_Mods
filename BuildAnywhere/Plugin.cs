using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
		internal static ConfigEntry<KeyboardShortcut> DumpZonesKey;

		// The real Builder-type desk found by the most recent OpenBuildMenuKey press - searched
		// fresh every press, never cached, so the resolved WorldZone (and therefore where the
		// build camera ends up once an item is actually placed) always tracks wherever the
		// player currently is, not wherever the first desk this mod ever found happened to be.
		//
		// The FormBuildData patch below is keyed on reference equality to this field, not a
		// flag, so it stays valid across BuildManager.Disable()'s reopen-the-browse-window path
		// and Move Stations' ReopenBuildMenu(), both of which just re-call TryEnable on whatever
		// Wgo they captured without going through this mod's Update() again.
		//
		// This is the same desk instance used for normal interactions too, so left set forever
		// it would cause a bug: hotkey a desk, close the menu, then later walk up and interact
		// with that exact same desk normally (before hotkeying any other desk) - that normal
		// interaction would still match this field and incorrectly show the aggregated "every
		// building" list instead of just that desk's own list. UIBuildingWindow_Close_Patch
		// below clears this field when the build browse-list window actually closes, which
		// fixes that without reintroducing the problem the old flag-based version had (losing
		// the aggregated list on every Disable()-triggered reopen mid-session, i.e. every time
		// the player places one item and the list reopens for the next one) - see that patch's
		// doc comment for why Close() only fires on a true exit, not that mid-session reopen.
		internal static Wgo LastHotkeyDesk;

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

			DumpZonesKey = Config.Bind(
				"General",
				"DumpZonesKey",
				new KeyboardShortcut(KeyCode.F11),
				"Dumps every WorldZone in the entire game - not just whatever's currently loaded - to a CSV file, so you can see up front where a WorldZone does and doesn't exist before building there with AllowBuildAnywhere. Written to BepInEx/BuildAnywhere_Output/worldZones.csv.");

			harmony = new Harmony("kupie.gk2.buildanywhere");
			harmony.PatchAll();
		}

		private void OnDestroy()
		{
			harmony?.UnpatchSelf();
		}

		private void Update()
		{
			if (OpenBuildMenuKey.Value.IsDown())
			{
				OpenBuildMenu();
			}

			if (DumpZonesKey.Value.IsDown())
			{
				DumpZones();
			}
		}

		private void OpenBuildMenu()
		{
			Wgo desk = FindNearestBuilderDesk();
			if (desk == null)
			{
				Logger.LogWarning("BuildAnywhere: no Builder-type desk is currently loaded nearby - can't open the build menu.");
				return;
			}

			// Set before TryEnable, not after - FormBuildData (and therefore the postfix below
			// that reads this field) runs synchronously inside that call.
			LastHotkeyDesk = desk;

			bool opened = LazySingleton<BuildManager>.Instance.TryEnable(desk, null);

			if (Debug.Value)
			{
				Logger.LogInfo($"BuildAnywhere: TryEnable on '{desk.Id}' returned {opened}.");
			}
		}

		// Every WorldZone in the entire game, not just whatever's currently loaded.
		// MainGame.WorldData.gameSceneDataList holds one GameSceneData per scene in the game,
		// fully populated from the save file the moment the player is in-game - independent of
		// which Unity scene is actually loaded/active. Each GameSceneData.worldZones carries
		// already-baked, absolute-world-space geography (pos/wholeZoneRect), so this needs no
		// live WorldZone GameObject or scene streaming, unlike the FindObjectsByType<WorldZone>
		// pattern the patches above use (which would silently under-report to just the current
		// scene - the opposite of what "see the whole game's zone layout" needs here).
		private void DumpZones()
		{
			string outputDir = Path.Combine(Paths.BepInExRootPath, "BuildAnywhere_Output");

			try
			{
				Directory.CreateDirectory(outputDir);
			}
			catch (Exception ex)
			{
				Logger.LogError($"BuildAnywhere: couldn't create output folder '{outputDir}': {ex.Message}");
				return;
			}

			var zones = new List<WorldZoneData>();

			foreach (GameSceneData sceneData in MainGame.WorldData.gameSceneDataList)
			{
				if (sceneData?.worldZones == null)
				{
					continue;
				}

				zones.AddRange(sceneData.worldZones.Where(zone => zone != null));
			}

			var sb = new StringBuilder();
			sb.AppendLine("Id,GameSceneId,WorldZoneType,BuilderId,PosX,PosY,PosZ,RectXMin,RectZMin,RectXMax,RectZMax");

			// wholeZoneRect is a Rect over the XZ ground plane - its xMin/xMax are world X, but
			// its yMin/yMax are world Z despite the struct's own "y" naming. Labelled RectZMin/
			// RectZMax in the header above so the CSV isn't mistaken for a literal Y (height) axis.
			// gameSceneId (not a GameSceneData-level id) is read off the zone itself, since every
			// WorldZoneData already carries which scene it belongs to.
			foreach (WorldZoneData zone in zones.OrderBy(z => z.gameSceneId, StringComparer.Ordinal).ThenBy(z => z.id, StringComparer.Ordinal))
			{
				Vector3 pos = zone.pos;
				Rect rect = zone.wholeZoneRect;
				// Definition can legitimately be null for a zone whose id doesn't resolve to a
				// known WorldZoneDef (e.g. leftover dev/test zones) - leave BuilderId blank for
				// those rather than skipping the row, so they're still visible in the dump.
				string builderId = zone.Definition?.builderId ?? string.Empty;

				sb.Append(CsvField(zone.id)).Append(',');
				sb.Append(CsvField(zone.gameSceneId)).Append(',');
				sb.Append(CsvField(zone.worldZoneType.ToString())).Append(',');
				sb.Append(CsvField(builderId)).Append(',');
				sb.Append(pos.x.ToString(CultureInfo.InvariantCulture)).Append(',');
				sb.Append(pos.y.ToString(CultureInfo.InvariantCulture)).Append(',');
				sb.Append(pos.z.ToString(CultureInfo.InvariantCulture)).Append(',');
				sb.Append(rect.xMin.ToString(CultureInfo.InvariantCulture)).Append(',');
				sb.Append(rect.yMin.ToString(CultureInfo.InvariantCulture)).Append(',');
				sb.Append(rect.xMax.ToString(CultureInfo.InvariantCulture)).Append(',');
				sb.Append(rect.yMax.ToString(CultureInfo.InvariantCulture));
				sb.AppendLine();
			}

			string outputPath = Path.Combine(outputDir, "worldZones.csv");

			try
			{
				File.WriteAllText(outputPath, sb.ToString());
			}
			catch (Exception ex)
			{
				Logger.LogError($"BuildAnywhere: couldn't write '{outputPath}': {ex.Message}");
				return;
			}

			Logger.LogInfo($"BuildAnywhere: dumped {zones.Count} zone(s) across {MainGame.WorldData.gameSceneDataList.Count} scene(s) to '{outputPath}'.");
		}

		// Quotes a CSV field and doubles any embedded quotes if it contains a comma, quote, or
		// newline. Zone/scene ids are realistically always plain identifiers, but this is cheap
		// defensive hygiene against a CSV that silently misparses if that ever isn't true.
		private static string CsvField(string value)
		{
			if (value == null)
			{
				return string.Empty;
			}

			if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
			{
				return value;
			}

			return "\"" + value.Replace("\"", "\"\"") + "\"";
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
	// list FormBuildData assigns to its buildDataList field. This postfix only runs for the
	// desk Plugin.LastHotkeyDesk was last set to by an OpenBuildMenuKey press - checked by
	// reference equality, not a flag, and cleared by UIBuildingWindow_Close_Patch below once
	// the browse-list window actually closes (see the doc comment on LastHotkeyDesk for why
	// that's needed). It replaces buildDataList with every building across every desk's list
	// combined, using the exact same per-building unlock checks
	// GetBuildingsInBuilder itself uses (isNeedsUnlock/unlockedBuildings/lockedBuildings) so it
	// still respects what the player has actually unlocked rather than showing everything in the
	// game regardless of progress.
	//
	// FormBuildData and buildDataList are both private on BuildManager in the game's own
	// source - accessed here directly assuming the game assembly is publicized at build time
	// (Krafs.Publicizer, PublicizeAll - see the .csproj).
	[HarmonyPatch(typeof(BuildManager), nameof(BuildManager.FormBuildData))]
	internal static class BuildManager_FormBuildData_Patch
	{
		private static void Postfix(BuildManager __instance, Wgo buildDesk, ref bool __result)
		{
			if (!__result || buildDesk != Plugin.LastHotkeyDesk || !Plugin.ShowEveryBuildingOnHotkeyOpen.Value)
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

	// UIBuildingWindow doesn't override Close() - it inherits
	// LazyWindow<UIBuildingWindowData>.Close() unmodified, so the attribute below targets
	// LazyWindow<UIBuildingWindowData> directly, not UIBuildingWindow. This isn't just style:
	// BepInEx's bundled Harmony is the HarmonyX fork, whose [HarmonyPatch(Type, string)]
	// resolves via AccessTools.DeclaredMethod, which only finds methods declared directly on
	// the given type - unlike plain Harmony's AccessTools.Method, it does not walk up the
	// base-class chain, so targeting UIBuildingWindow itself throws
	// "Could not find method for type UIBuildingWindow and name Close" at PatchAll() time.
	// Closing a generic type parameter doesn't move a member to a different level of the
	// inheritance chain - Close() declared in the body of LazyWindow<T> is a declared member of
	// every closed instantiation, including LazyWindow<UIBuildingWindowData> - and since
	// UIBuildingWindow doesn't override it, that's the exact MethodInfo invoked for
	// UIBuildingWindow instances via virtual dispatch, so patching it here still correctly
	// intercepts calls made through a UIBuildingWindow reference. It doesn't fire for any other
	// LazyWindow<T> subclass in the game that also leaves Close() unoverridden, since those are
	// distinct closed-generic MethodInfos.
	//
	// Close() only runs when the player actually leaves the build browse-list window -
	// backing/right-clicking out of it (OnPressedBack) or clicking its own close button - not
	// during mid-session placement (place one item, cancel back to the list, place another).
	// That path goes through BuildManager.Disable() -> OpenBuildingWindow() -> Open() ->
	// ShowWindow(), which just redraws the window because its isShown flag was never cleared by
	// entering placement mode, without ever calling Close()/HideWindow(). Confirmed via
	// FightingGameController, which explicitly treats BuildController.IsBuildModeActive and
	// UIBuildingWindow.IsShown as two independent states that can both be true at once - i.e.
	// placement mode alone never closes the window. UNVERIFIED: the exact compiler-generated
	// local function that runs when the player selects an item to place couldn't be read
	// directly (this decompile strips compiler-generated display-class bodies repo-wide), so
	// this rests on that independent evidence rather than reading that callback's body.
	//
	// The clear itself is unconditional, not gated on AllowBuildAnywhere/
	// ShowEveryBuildingOnHotkeyOpen - it's cheap lifecycle cleanup of a field that could have
	// been set while a toggle was on and then read after it was flipped, so it has to run
	// regardless of either toggle's current value to avoid a stale reference surviving that.
	[HarmonyPatch(typeof(LazyWindow<UIBuildingWindowData>), nameof(LazyWindow<UIBuildingWindowData>.Close))]
	internal static class UIBuildingWindow_Close_Patch
	{
		private static void Postfix()
		{
			Plugin.LastHotkeyDesk = null;
		}
	}
}

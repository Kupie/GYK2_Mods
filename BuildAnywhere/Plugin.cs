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
using UnityEngine.Rendering;

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
		internal static ConfigEntry<KeyboardShortcut> ToggleZoneVisualsKey;

		// How often (seconds, real time - Time.unscaledTime so a paused game doesn't stall
		// this) RefreshZoneVisuals() re-scans for zones while the toggle is on. Not every
		// frame, and not event-driven - there's no scene-load/unload event to hook (GameScene
		// Manager's load/unload calls take one-shot per-call callbacks, not events), so this
		// mirrors this mod's own existing convention of re-scanning FindObjectsByType fresh
		// each time rather than caching indefinitely, just done periodically instead of once
		// per hotkey press.
		private const float ZoneVisualsRefreshInterval = 1.5f;

		// How far above the player's current head height each marker floats. Anchoring to the
		// player's own position, not the zone's ground level, is deliberate - a marker sitting
		// at ground level is easily hidden behind buildings/terrain/decorations, and there's no
		// way to make it render through solid geometry (ZTest isn't script-configurable on the
		// Standard shader, and a mod can't ship a custom always-on-top shader asset without
		// Unity's asset-bundle tooling). Floating markers near the player instead keeps them out
		// of ground clutter and roughly in the player's own sightline as they move around.
		private const float ZoneVisualHeightOffset = 1.5f;

		// Border thickness and how far above the fill quad the border sits, both in world
		// units. The height bias is tiny - both submeshes render at essentially the same world
		// height via the marker's shared transform, so without it the border and fill would
		// z-fight along the shared edge.
		private const float ZoneVisualBorderThickness = 0.2f;
		private const float ZoneVisualBorderHeightBias = 0.02f;

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

		private bool zoneVisualsEnabled;
		private float nextZoneVisualsRefreshTime;

		// One marker GameObject per currently-visualized WorldZone. Keyed on the WorldZone
		// component itself - UnityEngine.Object overrides Equals/GetHashCode to key off the
		// underlying instance ID, so dictionary lookups/removal here stay correct even for a
		// zone that's since been destroyed (the "!zone" checks in RefreshZoneVisuals only
		// affect the implicit bool conversion, not dictionary hashing/equality) - the same
		// idiom Unity code relies on everywhere for a "fake null" reference.
		private readonly Dictionary<WorldZone, GameObject> zoneVisualMarkers = new Dictionary<WorldZone, GameObject>();

		// Shared across every marker - all markers render the same two colors, so these are
		// built once and reused via MeshRenderer.sharedMaterials rather than rebuilt per zone.
		private static Material zoneVisualFillMaterial;
		private static Material zoneVisualBorderMaterial;

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

			ToggleZoneVisualsKey = Config.Bind(
				"General",
				"ToggleZoneVisualsKey",
				new KeyboardShortcut(KeyCode.F10),
				"Toggles a visible fill and border around every WorldZone currently loaded, floating near your own height so it's not hidden by ground clutter, so you can see up front where a WorldZone does and doesn't exist before building there with AllowBuildAnywhere. Only shows zones that are actually loaded right now (unlike DumpZonesKey, which covers the whole game) - re-scans every couple seconds while on.");

			harmony = new Harmony("kupie.gk2.buildanywhere");
			harmony.PatchAll();
		}

		private void OnDestroy()
		{
			ClearZoneVisuals();

			if (zoneVisualFillMaterial != null)
			{
				UnityEngine.Object.Destroy(zoneVisualFillMaterial);
				zoneVisualFillMaterial = null;
			}

			if (zoneVisualBorderMaterial != null)
			{
				UnityEngine.Object.Destroy(zoneVisualBorderMaterial);
				zoneVisualBorderMaterial = null;
			}

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

			if (ToggleZoneVisualsKey.Value.IsDown())
			{
				ToggleZoneVisuals();
			}

			if (zoneVisualsEnabled && Time.unscaledTime >= nextZoneVisualsRefreshTime)
			{
				nextZoneVisualsRefreshTime = Time.unscaledTime + ZoneVisualsRefreshInterval;
				RefreshZoneVisuals();
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

		private void ToggleZoneVisuals()
		{
			zoneVisualsEnabled = !zoneVisualsEnabled;

			if (zoneVisualsEnabled)
			{
				// Refresh immediately instead of waiting up to ZoneVisualsRefreshInterval for
				// the first markers to appear.
				nextZoneVisualsRefreshTime = 0f;
				Logger.LogInfo("BuildAnywhere: zone visuals ON.");
			}
			else
			{
				ClearZoneVisuals();
				Logger.LogInfo("BuildAnywhere: zone visuals OFF.");
			}
		}

		// Scans only whatever's currently loaded, the same FindObjectsByType<WorldZone> pattern
		// this mod's own patches already use elsewhere (WgoExtensions_TryGetNearestBuilderWorldZone_Patch,
		// FindNearestBuilderDesk) - deliberately not DumpZones()'s MainGame.WorldData.gameSceneDataList
		// whole-game read, since that would mean eagerly spawning a marker for every WorldZone in
		// the entire game simultaneously (low hundreds to low thousands, order-of-magnitude) rather
		// than just what's actually on screen right now.
		private void RefreshZoneVisuals()
		{
			PlayerController player = MainGame.PlayerController;
			float playerY = player != null ? player.transform.position.y : 0f;

			var seen = new HashSet<WorldZone>();

			foreach (WorldZone zone in UnityEngine.Object.FindObjectsByType<WorldZone>(FindObjectsSortMode.None))
			{
				if (zone == null || zone.Data == null)
				{
					continue;
				}

				seen.Add(zone);

				if (!zoneVisualMarkers.ContainsKey(zone))
				{
					zoneVisualMarkers[zone] = CreateZoneVisualMarker(zone, playerY);
				}
			}

			// Prune markers for zones that are gone. No scene-load/unload event exists to hook
			// instead (GameSceneManager.LoadScene/UnloadScene take one-shot per-call completion
			// callbacks, not subscribable events) - this periodic re-scan-and-diff is the same
			// shape this mod already uses for "what's loaded right now" elsewhere, just repeated
			// on a timer instead of once per hotkey press.
			List<WorldZone> stale = null;

			foreach (KeyValuePair<WorldZone, GameObject> entry in zoneVisualMarkers)
			{
				if (!entry.Key || !seen.Contains(entry.Key))
				{
					if (stale == null)
					{
						stale = new List<WorldZone>();
					}

					stale.Add(entry.Key);
				}
			}

			if (stale != null)
			{
				foreach (WorldZone zone in stale)
				{
					DestroyMarker(zoneVisualMarkers[zone]);
					zoneVisualMarkers.Remove(zone);
				}
			}

			// Every surviving marker floats to the player's current height, not just
			// newly-created ones - re-applied every refresh so markers keep tracking the
			// player up/down stairs, hills, and basements rather than freezing at whatever
			// height they were created at.
			foreach (GameObject marker in zoneVisualMarkers.Values)
			{
				Vector3 pos = marker.transform.position;
				pos.y = playerY + ZoneVisualHeightOffset;
				marker.transform.position = pos;
			}
		}

		private void ClearZoneVisuals()
		{
			foreach (GameObject marker in zoneVisualMarkers.Values)
			{
				DestroyMarker(marker);
			}

			zoneVisualMarkers.Clear();
		}

		private static GameObject CreateZoneVisualMarker(WorldZone zone, float playerY)
		{
			Rect rect = zone.Data.wholeZoneRect;

			var markerObject = new GameObject($"BuildAnywhere_ZoneVisual_{zone.Id}");
			markerObject.transform.position = new Vector3(rect.center.x, playerY + ZoneVisualHeightOffset, rect.center.y);

			MeshFilter meshFilter = markerObject.AddComponent<MeshFilter>();
			MeshRenderer meshRenderer = markerObject.AddComponent<MeshRenderer>();

			meshFilter.sharedMesh = BuildZoneMarkerMesh(rect, ZoneVisualBorderThickness);
			meshRenderer.sharedMaterials = new[] { GetOrCreateZoneVisualFillMaterial(), GetOrCreateZoneVisualBorderMaterial() };
			meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
			meshRenderer.receiveShadows = false;

			return markerObject;
		}

		// Each marker's mesh is unique to that zone's size, so it has to be destroyed
		// explicitly here - Destroy(gameObject) does not destroy a Mesh asset referenced by
		// its MeshFilter, the same Unity gotcha ElevationGridQuad.OnDestroy() already handles
		// for its own runtime-created Texture2D/Material instances.
		private static void DestroyMarker(GameObject marker)
		{
			if (marker == null)
			{
				return;
			}

			if (marker.TryGetComponent(out MeshFilter meshFilter) && meshFilter.sharedMesh != null)
			{
				UnityEngine.Object.Destroy(meshFilter.sharedMesh);
			}

			UnityEngine.Object.Destroy(marker);
		}

		// Builds one zone's flat marker mesh: two submeshes sharing one vertex/triangle
		// buffer, each edge/fill quad using the same 4-vertex/2-triangle/up-normal shape
		// ElevationGridQuad.GetSharedMesh() uses for the game's own build-mode grid overlay,
		// just parameterized per quad instead of a fixed unit square. Submesh 0 is a single
		// quad spanning the whole rect (the translucent fill); submesh 1 is four independent
		// edge quads - not mitred at the corners, a harmless simplification for a debug
		// overlay - forming a border around it, nudged up by ZoneVisualBorderHeightBias in
		// local Y so it doesn't z-fight with the fill sitting at the same world height.
		// Vertices are in local space relative to rect.center - the marker's own transform is
		// positioned at that world-space center (see CreateZoneVisualMarker), so these stay
		// small, precise numbers regardless of where the zone actually is in the world.
		private static Mesh BuildZoneMarkerMesh(Rect rect, float thickness)
		{
			float cx = rect.center.x;
			float cz = rect.center.y; // wholeZoneRect's "y" axis is world Z, not height - see DumpZones' own comment on this same quirk.

			float xMin = rect.xMin - cx;
			float xMax = rect.xMax - cx;
			float zMin = rect.yMin - cz;
			float zMax = rect.yMax - cz;

			var vertices = new List<Vector3>(20);
			var fillTriangles = new List<int>(6);
			var borderTriangles = new List<int>(24);

			AddQuad(vertices, fillTriangles, xMin, zMin, xMax, zMax, 0f);

			float by = ZoneVisualBorderHeightBias;
			AddQuad(vertices, borderTriangles, xMin, zMax - thickness, xMax, zMax, by); // north
			AddQuad(vertices, borderTriangles, xMin, zMin, xMax, zMin + thickness, by); // south
			AddQuad(vertices, borderTriangles, xMin, zMin, xMin + thickness, zMax, by); // west
			AddQuad(vertices, borderTriangles, xMax - thickness, zMin, xMax, zMax, by); // east

			var mesh = new Mesh { name = "BuildAnywhere_ZoneMarker" };
			mesh.SetVertices(vertices);
			mesh.subMeshCount = 2;
			mesh.SetTriangles(fillTriangles, 0);
			mesh.SetTriangles(borderTriangles, 1);

			var normals = new Vector3[vertices.Count];
			for (int i = 0; i < normals.Length; i++)
			{
				normals[i] = Vector3.up;
			}

			mesh.normals = normals;
			mesh.RecalculateBounds();

			return mesh;
		}

		// One quad: 4 vertices in SW/SE/NE/NW order and the same {0,2,1,0,3,2} triangle
		// winding ElevationGridQuad.GetSharedMesh() uses for its own unit square, offset by
		// whatever vertices already exist in the combined mesh - a direct parameterization of
		// that proven shape rather than new winding logic.
		private static void AddQuad(List<Vector3> vertices, List<int> triangles, float x0, float z0, float x1, float z1, float y)
		{
			int baseIndex = vertices.Count;

			vertices.Add(new Vector3(x0, y, z0)); // SW
			vertices.Add(new Vector3(x1, y, z0)); // SE
			vertices.Add(new Vector3(x1, y, z1)); // NE
			vertices.Add(new Vector3(x0, y, z1)); // NW

			triangles.Add(baseIndex + 0);
			triangles.Add(baseIndex + 2);
			triangles.Add(baseIndex + 1);
			triangles.Add(baseIndex + 0);
			triangles.Add(baseIndex + 3);
			triangles.Add(baseIndex + 2);
		}

		private static Material GetOrCreateZoneVisualFillMaterial()
		{
			if (zoneVisualFillMaterial == null)
			{
				zoneVisualFillMaterial = CreateTransparentMaterial(new Color(0f, 1f, 1f, 0.25f));
			}

			return zoneVisualFillMaterial;
		}

		private static Material GetOrCreateZoneVisualBorderMaterial()
		{
			if (zoneVisualBorderMaterial == null)
			{
				zoneVisualBorderMaterial = CreateTransparentMaterial(new Color(0f, 1f, 1f, 0.9f));
			}

			return zoneVisualBorderMaterial;
		}

		// Shader.Find("Standard") + new Material(...) is the one runtime shader pattern
		// already proven working in this exact game (LazyTerrainSurfaceUtility.cs uses it
		// identically). Standard defaults to Opaque, so this switches it to Transparent
		// (Fade) mode via the standard runtime recipe for that shader, the only way to get
		// real alpha blending out of it from script - there's no way to also make it ignore
		// depth testing (render "through" walls) this way, since ZTest isn't a
		// script-settable property on Standard; occlusion by solid geometry between the
		// camera and a marker is an accepted limitation, not something this recipe can fix.
		private static Material CreateTransparentMaterial(Color color)
		{
			var material = new Material(Shader.Find("Standard"));

			material.SetFloat("_Mode", 3f);
			material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
			material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
			material.SetInt("_ZWrite", 0);
			material.DisableKeyword("_ALPHATEST_ON");
			material.EnableKeyword("_ALPHABLEND_ON");
			material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
			material.renderQueue = 3000;
			material.color = color;

			return material;
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

					// Don't show test buildings that are only in the game for dev purposes.
					if (buildingDef.id.StartsWith("test_", StringComparison.OrdinalIgnoreCase))
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

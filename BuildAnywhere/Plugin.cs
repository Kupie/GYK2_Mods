using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

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
		internal static ConfigEntry<string> ZoneSizeOverrides;

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

		// The id of this mod's own dedicated "build anywhere" desk - never a real desk's id,
		// on purpose. Earlier versions of this feature tried to make a real desk (or a
		// disposable clone sharing a real desk's id) carry the "show every building" identity,
		// which kept breaking in different ways: keying off a real Wgo meant every normal
		// interaction and every legitimate menu-reopen path could ambiguously match it too, and
		// writing an aggregated list into GameBalance.Me.buildDefsInBuilder under a borrowed id
		// would corrupt that real desk's own normal list, since that dictionary is shared,
		// global data keyed purely by id string (confirmed via GameBalance.CreateBuildCache()).
		// A brand-new id sidesteps both problems at once - nothing else in the game will ever
		// use it, so there's no ambiguity to resolve and nothing shared to corrupt.
		private const string AggregateDeskId = "buildanywhere_desk";

		// This mod's own dedicated desk, spawned once (lazily, on first hotkey press) and
		// simply moved to wherever it's needed on every later press - not destroyed and
		// respawned like the real-desk clones earlier designs used, since its id never needs
		// to change between presses. Always shows every unlocked building, permanently: its
		// GameBalance.Me.buildDefsInBuilder[AggregateDeskId] entry is seeded once at startup
		// (see RegisterAggregateDesk) with every real building in the game, and vanilla
		// BuildingDef.GetBuildingsInBuilder already re-derives what's actually unlocked on
		// every call - no Harmony patch on FormBuildData needed at all.
		//
		// Left inactive after spawning (gameObject.SetActive(false), i.e. never calling
		// UpdateChunkVisibility(true)) and with WgoData.IsInteractable = false set explicitly -
		// two independently-sufficient guarantees this desk can never be walked up to and
		// interacted with normally: the game's interaction detection is a live
		// Physics.OverlapBox sweep every frame (PlayerInteractionComponent.Update) that cannot
		// return colliders on an inactive GameObject at all, and IsInteractable is the exact
		// per-instance field that same sweep checks even if it somehow were active.
		internal static Wgo AggregateDesk;

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

		// Built lazily on first need (see EnsureCurrentZoneOverlay), not in Awake() - it's only
		// needed once Zone Visuals is actually toggled on, and there's no reason to pay for a
		// Canvas/font asset otherwise.
		private GameObject currentZoneOverlayObject;
		private TextMeshProUGUI currentZoneOverlayText;

		// Parsed once from ZoneSizeOverrides at startup (see ParseZoneSizeOverrides) - plain
		// string parsing against config text, nothing needs to be "ready" first the way
		// GameBalance/the loc table do, so no lazy-retry needed here.
		private static Dictionary<string, ZoneEdgeOverride> zoneSizeOverrides;

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

			Debug = Config.Bind("Debug", "Debug Logs", false, "Extra logging for troubleshooting.");

			DumpZonesKey = Config.Bind(
				"Debug",
				"DumpZonesKey",
				new KeyboardShortcut(KeyCode.None),
				"Dumps every Build Zone to a CSV file,  Written to BepInEx/BuildAnywhere_Output/worldZones.csv.");

			ToggleZoneVisualsKey = Config.Bind(
				"General",
				"ToggleZoneVisualsKey",
				new KeyboardShortcut(KeyCode.F10),
				"Toggles a visible fill and border around every WorldZone currently loaded, floating near your own height so it's not hidden by ground clutter, so you can see up front where a WorldZone does and doesn't exist before building there with AllowBuildAnywhere. Only shows zones that are actually loaded right now (unlike DumpZonesKey, which covers the whole game) - re-scans every couple seconds while on.");

			ZoneSizeOverrides = Config.Bind(
				"General",
				"ZoneSizeOverrides",
				"",
				"Set a zone's West/East/North/South edges to exact world coordinates - one override per zone, semicolon-separated, format 'zoneId,west,east,north,south' (West/East are X coordinates, North/South are Z coordinates). Leave any field blank, or put 'n', to leave that edge exactly where it already is - you don't have to specify all four. Example: 'yard,,,,10' sets only yard's south edge to Z=10, leaving west/east/north untouched; 'home,-30,30,,' sets home's west and east edges, leaving north/south alone. Run DumpZonesKey first to find a zone's id and its current edges (the on-screen overlay while standing in a zone shows the same numbers too). This changes more than just where you can build there - it can also affect that zone's navmesh, worker task assignment, storage/delivery network membership, and quality/achievement scoring, since all of those are keyed off the same zone bounds. Malformed entries are skipped with a warning, not an error.");

			// Best-effort only - GameBalance.Me usually isn't populated this early (see
			// RegisterAggregateDesk's own doc comment). The real guarantee comes from
			// OpenBuildMenu() retrying this on-demand right before first use.
			RegisterAggregateDesk();
			RegisterAggregateDeskDisplayName();
			ParseZoneSizeOverrides();

			harmony = new Harmony("kupie.gk2.buildanywhere");
			harmony.PatchAll();

			PatchMoveStationsMoveButtonSuppression();
		}

		// Registers this mod's own dedicated WGODef (AggregateDeskId, every field left at its
		// C# default besides id) via the same GameBalanceBase.AddData/InitCache pattern this
		// game already uses for its own balance data - confirmed safe by tracing the full
		// Wgo.Spawn/WgoData construction pipeline: every field a minimal WGODef would leave at
		// default is read through a TryGetValue, a string.IsNullOrEmpty guard, or a
		// sentinel-returning cache lookup, never a direct dereference that would null-ref. No
		// Addressables/prefab asset is ever touched for this id either, since
		// InitVisualBindings() - the one method that would touch one - is only reachable
		// through chunk registration, which GetOrMoveAggregateDesk's Wgo.Spawn call always
		// skips via ignoreChunkRegistration: true.
		//
		// GameBalance.Me is NOT reliably available in Awake() - it's only guaranteed populated
		// once MainGame.Start() has run (confirmed via decomp: GameBalance.LoadGameBalance is
		// only ever called from GameBalance.Me's own lazy getter and from MainGame.Start()), and
		// a BepInEx plugin's Awake() runs earlier than that with no ordering guarantee either
		// way. DataDumper (this repo's own data-dumping mod) already works around exactly this
		// by polling for GameBalance.Me in Update() instead of touching it in Awake() - the
		// same reasoning applies here. Returns false (and logs, doesn't seed anything) if
		// GameBalance.Me still isn't ready; callers must retry rather than assume Awake()'s one
		// call was enough. It's cheap and safe to call repeatedly - guarded so re-registering an
		// already-registered id is a no-op.
		//
		// Also seeds GameBalance.Me.buildDefsInBuilder[AggregateDeskId] - not optional.
		// BuildingDef.GetBuildingsInBuilder indexes that dictionary with a bare [], not
		// TryGetValue, so an unseeded id throws KeyNotFoundException the moment this desk is
		// ever opened. Seeded once with the deduplicated, test_-excluded union of every real
		// desk's own building list (the same logic this mod's now-removed FormBuildData patch
		// used to run on every open) - seeding it once is sufficient forever, since unlock
		// status is re-derived fresh by GetBuildingsInBuilder on every call, not baked into
		// this list. Confirmed via decomp that nothing in normal gameplay calls
		// GameBalance.InitCache()/CreateBuildCache() again after MainGame.Start() (no save-load,
		// scene-transition, or unlock path re-triggers it), so this entry can't get silently
		// cleared out later by the game's own code once it's been seeded.
		private bool RegisterAggregateDesk()
		{
			if (GameBalance.Me == null)
			{
				Logger.LogWarning("BuildAnywhere: GameBalance.Me isn't ready yet - can't register the aggregate desk this time.");
				return false;
			}

			if (GameBalance.Me.GetDataOrNull<WGODef>(AggregateDeskId) == null)
			{
				GameBalance.Me.AddData(new WGODef { id = AggregateDeskId });
				GameBalance.Me.InitCache();
			}

			if (!GameBalance.Me.buildDefsInBuilder.ContainsKey(AggregateDeskId))
			{
				var seen = new HashSet<BuildingDef>();
				var everyBuilding = new List<BuildingDef>();

				foreach (List<BuildingDef> deskBuildings in GameBalance.Me.buildDefsInBuilder.Values)
				{
					foreach (BuildingDef buildingDef in deskBuildings)
					{
						if (buildingDef == null || !seen.Add(buildingDef))
						{
							continue;
						}

						if (buildingDef.id.StartsWith("test_", StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}

						everyBuilding.Add(buildingDef);
					}
				}

				GameBalance.Me.buildDefsInBuilder[AggregateDeskId] = everyBuilding;
			}

			return true;
		}

		// WGODef has no display-name field at all - it only inherits id from
		// BalanceBaseObject (confirmed via decomp). This game's own convention instead has a
		// Wgo's id double as a localization *key*: UIBuildingWindow.Redraw() draws the build
		// window's header via UIInfoWidgetData.Header, which calls LLBase.L(wgoData.id)
		// directly - so without a real loc entry for AggregateDeskId, L() falls back to
		// returning the raw id string verbatim (its documented behavior on a dictionary miss),
		// which is exactly the "buildanywhere_desk" text showing up in the menu today.
		//
		// There's no field to set on WGODef to fix this - the fix has to register a real entry
		// in the live loc table for that key. LL (the concrete loc-table class, LLBase's only
		// subclass) is fully public, and LLBase.L() reads straight from its public,
		// non-serialized dictionary/idsToMetaInfo instance fields - so writing directly into
		// those is enough, and lighter than going through AddLangString()/InitHashDictionary()
		// (which clears and rebuilds the *entire* table from separate txtIds/txts lists - more
		// than this needs, and risks wiping anything a language-mod-loader added there that
		// hasn't made it into those lists yet). NestedLocalesMetaInfo's default constructor
		// leaves hasMetaInfo false, which is exactly what skips L()'s nested-locale-insertion
		// branch for a plain literal string like this.
		//
		// The one obstacle: the loaded table itself lives in LLBase's protected static
		// currentLang field - no public static accessor returns the LL instance itself
		// (LLBase.CurrentLang only exposes the language id string). Reflection is needed to
		// reach it; everything after that is ordinary public API. Same lazy/retried shape as
		// RegisterAggregateDesk - the loc table's load timing isn't something this mod controls
		// either, so this is called from the same two places for the same reason.
		private static bool RegisterAggregateDeskDisplayName()
		{
			FieldInfo currentLangField = typeof(LLBase).GetField("currentLang", BindingFlags.NonPublic | BindingFlags.Static);
			LL currentLang = currentLangField?.GetValue(null) as LL;
			if (currentLang == null)
			{
				return false;
			}

			if (!currentLang.dictionary.ContainsKey(AggregateDeskId))
			{
				currentLang.dictionary[AggregateDeskId] = "Build Anywhere";
				currentLang.idsToMetaInfo[AggregateDeskId] = new NestedLocalesMetaInfo();
			}

			return true;
		}

		// Move Stations (github.com/Kupie/GYK2_DECOMP/tree/main/GK2MoveStations) has no
		// Harmony patches or per-desk gate of its own - it injects its "Move" row into the
		// build browsing list via a Canvas.willRenderCanvases poll (TryInjectMoveMenuRow(),
		// private, void, no params) that clones the vanilla "Remove" entry whenever no row is
		// already present. Clicking it doesn't work correctly on AggregateDesk (fails to close
		// the menu before opening the move-picker) - rather than debug that, this prevents the
		// row from ever being created while AggregateDesk's menu is open, so nothing broken is
		// ever clickable.
		//
		// No compile-time dependency on Move Stations' assembly - resolved and patched manually
		// at runtime via AccessTools, both calls null-safe (return null on a miss instead of
		// throwing), so this is a clean no-op with no Harmony error if Move Stations isn't
		// installed, or if a future version renames/removes this method. Called once from
		// Awake() only - unlike GameBalance/the loc table, if Move Stations' assembly isn't
		// loaded by the time every plugin's Awake() has run, it never will be this session.
		private void PatchMoveStationsMoveButtonSuppression()
		{
			Type moveStationsPluginType = AccessTools.TypeByName("GK2MoveStations.MoveStationsPlugin");
			if (moveStationsPluginType == null)
			{
				return;
			}

			MethodInfo tryInjectMoveMenuRow = AccessTools.Method(moveStationsPluginType, "TryInjectMoveMenuRow");
			if (tryInjectMoveMenuRow == null)
			{
				Logger.LogWarning("BuildAnywhere: found Move Stations but not its TryInjectMoveMenuRow method - its Move button may appear on the Build Anywhere desk.");
				return;
			}

			harmony.Patch(tryInjectMoveMenuRow, prefix: new HarmonyMethod(typeof(MoveStationsCompat_Patch), nameof(MoveStationsCompat_Patch.Prefix)));
		}

		// One override per entry of ZoneSizeOverrides, semicolon-separated, format
		// "zoneId,west,east,north,south" - each field an ABSOLUTE world coordinate for that edge
		// (West/East are X, North/South are Z), not a delta. Reverted from an earlier
		// delta-based design ("push this edge out by N units") back to absolute coordinates -
		// deltas were harder to reason about once you wanted to line an edge up with a specific
		// spot (e.g. "push the south edge out to Z=10"), since that meant first reading the
		// zone's current edge and doing the subtraction by hand. Any field can be left blank, or
		// set to the literal "n" (case-insensitive, for "no change"), to leave that edge exactly
		// where it already is - not every override needs to touch all four edges. Because a blank
		// field is meaningful here (it's how an edge is skipped), splitting a single entry's
		// fields does NOT use RemoveEmptyEntries the way splitting entries themselves does below -
		// "yard,,,,10" has to stay five fields (id + 4, with 3 blank), not collapse down to two.
		//
		// Semicolons, not newlines, separate multiple entries - confirmed against a real BepInEx
		// .cfg file that this has to be a single physical line: BepInEx writes a ConfigEntry<string>
		// as one "Key = Value" line, and a second raw line after it isn't a continuation, it's
		// outside the entry entirely (an earlier version of this mod assumed newline-separated
		// entries would work here; that assumption was wrong). Newlines are still accepted as an
		// extra separator on top of semicolons (not instead of them), in case some other editing
		// surface - e.g. a config-manager plugin's multi-line text field - does preserve real
		// newlines; that costs nothing and only helps.
		//
		// Parsed once at startup, not lazily/retried like RegisterAggregateDesk or the loc table -
		// this is plain string parsing against config text already loaded by Config.Bind, nothing
		// needs to be "ready" first. Malformed entries (wrong column count, unparsable coordinate,
		// no edges specified at all) are skipped with a warning rather than aborting the whole
		// list or crashing.
		private void ParseZoneSizeOverrides()
		{
			zoneSizeOverrides = new Dictionary<string, ZoneEdgeOverride>();

			string raw = ZoneSizeOverrides.Value;
			if (string.IsNullOrWhiteSpace(raw))
			{
				return;
			}

			foreach (string rawEntry in raw.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
			{
				string entry = rawEntry.Trim();
				if (entry.Length == 0)
				{
					continue;
				}

				string[] parts = entry.Split(',');
				if (parts.Length != 5)
				{
					Logger.LogWarning($"BuildAnywhere: skipping malformed ZoneSizeOverrides entry (expected 'zoneId,west,east,north,south'): '{entry}'");
					continue;
				}

				string id = parts[0].Trim();
				if (id.Length == 0)
				{
					Logger.LogWarning($"BuildAnywhere: skipping malformed ZoneSizeOverrides entry (missing zone id): '{entry}'");
					continue;
				}

				if (!TryParseZoneEdgeField(parts[1], out float? west)
					|| !TryParseZoneEdgeField(parts[2], out float? east)
					|| !TryParseZoneEdgeField(parts[3], out float? north)
					|| !TryParseZoneEdgeField(parts[4], out float? south))
				{
					Logger.LogWarning($"BuildAnywhere: skipping malformed ZoneSizeOverrides entry (unparsable coordinate - use a number, blank, or 'n'): '{entry}'");
					continue;
				}

				if (west == null && east == null && north == null && south == null)
				{
					Logger.LogWarning($"BuildAnywhere: skipping ZoneSizeOverrides entry with no edges specified: '{entry}'");
					continue;
				}

				zoneSizeOverrides[id] = new ZoneEdgeOverride(west, east, north, south);
			}

			if (zoneSizeOverrides.Count > 0)
			{
				Logger.LogInfo($"BuildAnywhere: loaded {zoneSizeOverrides.Count} zone size override(s).");
			}
		}

		// A blank field, or the literal "n" (case-insensitive - short for "no change"), means
		// this edge is left alone; anything else has to parse as an absolute world coordinate.
		private static bool TryParseZoneEdgeField(string field, out float? value)
		{
			string trimmed = field.Trim();
			if (trimmed.Length == 0 || trimmed.Equals("n", StringComparison.OrdinalIgnoreCase))
			{
				value = null;
				return true;
			}

			if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
			{
				value = parsed;
				return true;
			}

			value = null;
			return false;
		}

		// internal, not private - WorldZoneData_Init_Patch and WorldZoneData_PrepareForGame_Patch
		// (separate top-level classes below) call this directly, which needs compile-time
		// accessibility from outside Plugin - same reason MoveStationsCompat_Patch.Prefix is
		// internal rather than private.
		internal static bool TryGetZoneOverride(string id, out ZoneEdgeOverride zoneOverride)
		{
			if (zoneSizeOverrides != null && id != null)
			{
				return zoneSizeOverrides.TryGetValue(id, out zoneOverride);
			}

			zoneOverride = default;
			return false;
		}

		// Reverted from a self-built runtime font (Font.CreateDynamicFontFromOSFont +
		// TMP_FontAsset.CreateFontAsset) back to copying a live TextMeshProUGUI's font - the
		// self-built version broke text rendering game-wide, not just in this overlay, including
		// BepInEx's own Configuration Manager UI. Root cause understood but not deeply verified
		// (no build toolchain here to instrument it): Unity's dynamic-font system keeps its font
		// atlas texture in native/OS font-rendering state shared process-wide, and
		// CreateDynamicFontFromOSFont requesting a name already in use elsewhere in the process
		// (this list included "Arial", a near-universal default other mods/tools reach for too)
		// can collide with and corrupt that shared state rather than getting an isolated font of
		// its own. Copying an existing live TextMeshProUGUI's already-working font/material next
		// to it never touches that creation path at all, so it can't cause this class of bug -
		// confirmed safe in this exact form earlier this session, before the runtime-font attempt.
		//
		// This does bring back the two bugs that copying approach originally had, both still
		// worked around the same way as before: the found instance can be scene-scoped (worked
		// around by deferring the search until MainGame.PlayerData is populated, i.e. actually
		// in-game rather than still at the main menu/loading), and an arbitrary found instance's
		// own material can carry unusual styling (worked around by using the font asset's own
		// default material, templateText.font.material, rather than the found instance's
		// fontSharedMaterial).
		//
		// Lazy, retried each frame from Update() while Zone Visuals is on and this hasn't
		// succeeded yet - same shape as RegisterAggregateDesk's GameBalance.Me retry. Returns
		// true once the overlay exists, regardless of whether this particular call built it or
		// a previous one already did.
		//
		// The Canvas setup (ScreenSpaceOverlay + CanvasScaler, no GraphicRaycaster since this is
		// display-only) mirrors UISteamWorkshopCreatorWindow.CreateInstance()'s own proven
		// recipe. Parented under this.transform so it persists across scene loads the same way
		// this Plugin's own GameObject already does - general BepInEx platform behavior (every
		// plugin sits under a persisted root), not itself something traced in the decomp.
		//
		// A solid background panel behind the text, not a shader-based outline, is what makes
		// this readable against an arbitrary 3D game background - a screen-space overlay has no
		// contrast guarantee of its own otherwise. Deliberately not using TMP's built-in
		// outline (a real option on the Distance Field shader the copied font asset's material
		// may use, via _OutlineWidth/_OutlineColor), since that's gated behind a shader keyword
		// that could just as easily be stripped from this specific build as the one that broke
		// Zone Visuals' fill material earlier this session - a plain Image using Unity's default
		// UI material needs no keyword at all, and that material's presence is already confirmed
		// safe via this game's extensive uGUI usage (see the Zone Visuals fix for the same
		// reasoning).
		private bool EnsureCurrentZoneOverlay()
		{
			if (currentZoneOverlayText != null)
			{
				return true;
			}

			if (MainGame.PlayerData == null)
			{
				return false;
			}

			// FindObjectsByType only returns components on active GameObjects unless told
			// otherwise - explicitly including inactive ones here, since whether anything
			// happens to be active at the exact moment this first runs isn't something this mod
			// controls, and a template that's merely inactive still has a perfectly usable
			// .font to copy (disabling a component doesn't clear its data).
			TextMeshProUGUI templateText = null;
			foreach (TextMeshProUGUI candidate in UnityEngine.Object.FindObjectsByType<TextMeshProUGUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
			{
				if (candidate != null && candidate.font != null)
				{
					templateText = candidate;
					break;
				}
			}

			if (templateText == null)
			{
				if (Debug.Value)
				{
					Logger.LogWarning("BuildAnywhere: current-zone overlay couldn't find any live TextMeshProUGUI yet to copy a font from - will keep retrying.");
				}

				return false;
			}

			TMP_FontAsset fontAsset = templateText.font;

			var canvasObject = new GameObject("BuildAnywhere_CurrentZoneOverlay");
			canvasObject.transform.SetParent(transform, false);

			Canvas canvas = canvasObject.AddComponent<Canvas>();
			canvas.renderMode = RenderMode.ScreenSpaceOverlay;
			canvas.overrideSorting = true;
			// Comfortably above anything this game's own UI is likely to use, so this can't end
			// up hidden behind a HUD/menu Canvas with a higher sortingOrder of its own.
			canvas.sortingOrder = 32000;

			CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
			scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

			var backgroundObject = new GameObject("BuildAnywhere_CurrentZoneOverlayBackground");
			backgroundObject.transform.SetParent(canvasObject.transform, false);

			Image background = backgroundObject.AddComponent<Image>();
			background.color = new Color(0f, 0f, 0f, 0.6f);

			RectTransform backgroundRect = background.rectTransform;
			backgroundRect.anchorMin = new Vector2(0.5f, 1f);
			backgroundRect.anchorMax = new Vector2(0.5f, 1f);
			backgroundRect.pivot = new Vector2(0.5f, 1f);
			backgroundRect.anchoredPosition = new Vector2(0f, -10f);
			backgroundRect.sizeDelta = new Vector2(1500f, 70f);

			var textObject = new GameObject("BuildAnywhere_CurrentZoneOverlayText");
			textObject.transform.SetParent(canvasObject.transform, false);

			currentZoneOverlayText = textObject.AddComponent<TextMeshProUGUI>();
			currentZoneOverlayText.font = fontAsset;
			currentZoneOverlayText.fontSharedMaterial = fontAsset.material;
			currentZoneOverlayText.fontStyle = FontStyles.Bold;
			currentZoneOverlayText.color = Color.white;
			currentZoneOverlayText.alignment = TextAlignmentOptions.Center;
			currentZoneOverlayText.textWrappingMode = TextWrappingModes.NoWrap;
			// Auto-shrinks instead of a fixed 36pt - the line now carries the zone id, its full
			// bounds, AND the player's own position, which can run long enough to overflow a
			// fixed size on a narrower screen. NoWrap plus a shrink range keeps it one line and
			// always fully covered by the background panel, at whatever resolution this runs at.
			currentZoneOverlayText.enableAutoSizing = true;
			currentZoneOverlayText.fontSizeMin = 18f;
			currentZoneOverlayText.fontSizeMax = 36f;

			RectTransform rectTransform = currentZoneOverlayText.rectTransform;
			rectTransform.anchorMin = new Vector2(0.5f, 1f);
			rectTransform.anchorMax = new Vector2(0.5f, 1f);
			rectTransform.pivot = new Vector2(0.5f, 1f);
			rectTransform.anchoredPosition = new Vector2(0f, -10f);
			rectTransform.sizeDelta = new Vector2(1480f, 60f);

			currentZoneOverlayObject = canvasObject;

			if (Debug.Value)
			{
				Logger.LogInfo($"BuildAnywhere: current-zone overlay created, using font copied from '{templateText.name}'.");
			}

			return true;
		}

		// Cheap enough (one property chain read plus one string format) to run every frame while
		// the overlay is on, unlike Zone Visuals' periodic scene-wide FindObjectsByType rescan -
		// no refresh-interval timer needed here.
		//
		// Includes the player's own live X/Z alongside the zone's edges - the whole point of this
		// overlay is tuning ZoneSizeOverrides, and having your current position on the same line
		// as the edges you're editing means you don't need a second tool (or DumpZonesKey) just
		// to see how far you are from a boundary you're trying to move to meet you. Labelled
		// West/East/North/South (matching ZoneSizeOverrides' own field order/names) rather than
		// raw min/max pairs, since that's what you actually type into the config - no translating
		// "xMin" into "which edge is that again" in your head while you're trying to read numbers
		// off the screen and match them to a config line.
		private void UpdateCurrentZoneOverlayText()
		{
			PlayerController player = MainGame.PlayerController;
			// pos.z is world Z, matching wholeZoneRect's yMin/yMax below despite the struct's own
			// "y" naming - same quirk DumpZones() already documents.
			Vector3 pos = player != null ? player.transform.position : Vector3.zero;
			string playerText = $"Player: {pos.x:F1}, {pos.z:F1}";

			WorldZoneData zone = MainGame.PlayerData?.CurrentWorldZoneData;

			if (zone == null)
			{
				currentZoneOverlayText.text = $"No Zone  |  {playerText}";
				return;
			}

			// West/East are the rect's X bounds; North/South are its Z bounds (rect.yMin/yMax
			// despite the struct's own "y" naming) - West/South are the smaller coordinate on
			// each axis, East/North the larger, matching WorldZoneData_Init_Patch's own
			// West/East/North/South -> xMin/xMax/zMax/zMin mapping.
			Rect rect = zone.wholeZoneRect;
			currentZoneOverlayText.text = $"{zone.id}  |  West: {rect.xMin:F1}, East: {rect.xMax:F1}, North: {rect.yMax:F1}, South: {rect.yMin:F1}.  {playerText}";
		}

		private void OnDestroy()
		{
			if (AggregateDesk)
			{
				UnityEngine.Object.Destroy(AggregateDesk.gameObject);
				AggregateDesk = null;
			}

			if (currentZoneOverlayObject != null)
			{
				UnityEngine.Object.Destroy(currentZoneOverlayObject);
				currentZoneOverlayObject = null;
				currentZoneOverlayText = null;
			}

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

			// Tied to Zone Visuals' own toggle rather than a separate config - showing which
			// zone you're actually in reads as part of the same "see the zone layout" feature
			// as the boundary fill/border, not an independent always-on overlay.
			if (zoneVisualsEnabled)
			{
				if (EnsureCurrentZoneOverlay())
				{
					currentZoneOverlayObject.SetActive(true);
					UpdateCurrentZoneOverlayText();
				}
			}
			else if (currentZoneOverlayObject != null)
			{
				currentZoneOverlayObject.SetActive(false);
			}
		}

		private void OpenBuildMenu()
		{
			Wgo nearestDesk = FindNearestBuilderDesk();
			if (nearestDesk == null)
			{
				Logger.LogWarning("BuildAnywhere: no Builder-type desk is currently loaded nearby - can't open the build menu.");
				return;
			}

			// ShowEveryBuildingOnHotkeyOpen now picks which desk gets opened, not a data
			// override on top of one desk - AggregateDesk always shows every building
			// (permanently, via its own buildDefsInBuilder entry), so when the toggle is off
			// this just opens the real nearest desk directly instead, matching a normal
			// interaction's own list with no patch involved either way.
			//
			// RegisterAggregateDesk() is called again here, not just once in Awake() - by the
			// time a player can press this hotkey they're already in an active session, so
			// GameBalance.Me is guaranteed ready (see RegisterAggregateDesk's doc comment for
			// why Awake() alone isn't). If it still somehow fails, fall back to the real desk
			// for this one press rather than walking into a guaranteed KeyNotFoundException -
			// self-healing, since the next press just retries.
			Wgo deskToOpen = nearestDesk;

			if (ShowEveryBuildingOnHotkeyOpen.Value)
			{
				if (RegisterAggregateDesk())
				{
					// Best-effort, same as in Awake() - a failure here only means the menu
					// header shows the raw id instead of "Build Anywhere" this time, not
					// something worth falling back to the real desk over.
					RegisterAggregateDeskDisplayName();
					deskToOpen = GetOrMoveAggregateDesk(nearestDesk);
				}
				else
				{
					Logger.LogWarning("BuildAnywhere: aggregate desk isn't ready yet - opening the normal desk menu this time instead.");
				}
			}

			bool opened = LazySingleton<BuildManager>.Instance.TryEnable(deskToOpen, null);

			if (Debug.Value)
			{
				Logger.LogInfo($"BuildAnywhere: TryEnable on '{deskToOpen.Id}' (nearest real desk '{nearestDesk.Id}') returned {opened}.");
			}
		}

		// Spawns AggregateDesk once, lazily, then just moves it on every later call - matching
		// AggregateDeskId's own doc comment on why this desk never needs to be destroyed and
		// respawned (its id is always the same, unlike the real-desk clones earlier designs
		// used). anchor is wherever FindNearestBuilderDesk() found, so the resolved WorldZone
		// (and therefore where the camera ends up once an item is placed) keeps tracking
		// wherever the player currently is, exactly as it did before this desk existed.
		private static Wgo GetOrMoveAggregateDesk(Wgo anchor)
		{
			if (!AggregateDesk)
			{
				WgoData deskData = new WgoData(AggregateDeskId, anchor.transform.position, anchor.Data.WorldId)
				{
					isTempObject = true,
					IsInteractable = false,
				};

				AggregateDesk = Wgo.Spawn(deskData, anchor.transform.parent, true, true, true, false);
				return AggregateDesk;
			}

			AggregateDesk.transform.SetParent(anchor.transform.parent);
			AggregateDesk.transform.position = anchor.transform.position;
			AggregateDesk.Data.WorldId = anchor.Data.WorldId;

			return AggregateDesk;
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
				zoneVisualFillMaterial = CreateTransparentMaterial(new Color(0f, 1f, 1f, 0.10f));
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

		// Originally built from Shader.Find("Standard"), manually forced into Transparent
		// (Fade) mode via the standard runtime recipe (_Mode/_SrcBlend/_DstBlend/_ZWrite/the
		// _ALPHABLEND_ON keyword/renderQueue) - that rendered fully opaque in-game regardless
		// of alpha. Root cause, confirmed via decomp: the doc comment that recipe used to cite
		// as proof (LazyTerrainSurfaceUtility.cs also using Shader.Find("Standard")) was wrong
		// - that class only ever uses Standard's default Opaque mode and its MeshRenderer is
		// explicitly disabled (it exists purely to host a MeshCollider), so it never proved
		// Transparent mode renders correctly here at all. Nothing else in this game's own code
		// uses Standard's Transparent mode either, and there's no shader-variant-preservation
		// mechanism (ShaderVariantCollection, Shader.WarmupAllShaders) anywhere - consistent
		// with Unity's build-time shader stripping simply not having kept the _ALPHABLEND_ON
		// variant, since nothing in the shipped game needs it, so toggling that keyword at
		// runtime silently no-ops and the shader falls back to its always-present Opaque variant.
		//
		// Sprites/Default sidesteps this instead of working around it: it's alpha-blended
		// unconditionally, baked directly into the shader with no keyword gate to get stripped,
		// and confirmed present in this build via this game's own extensive uGUI usage (264
		// files use UnityEngine.UI) - Unity's shader stripper always retains it alongside
		// UI/Default, since Image/Graphic rendering depends on one of them. Its _MainTex
		// defaults to a built-in white texture when unset (this mesh has no UVs/texture, same
		// as before), so setting just .color applies the fill/border color and alpha exactly
		// as intended. Same accepted limitation as before either way: there's no way to also
		// make this render "through" walls (ignore depth testing) from script - occlusion by
		// solid geometry between the camera and a marker isn't something a shader swap changes.
		private static Material CreateTransparentMaterial(Color color)
		{
			var material = new Material(Shader.Find("Sprites/Default"));
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

	// One ZoneSizeOverrides config line parses into one of these - the new absolute world
	// coordinate for each compass edge of a WorldZone, or null to leave that edge exactly where
	// it already is. West/East are X coordinates, North/South are Z coordinates (world Z, not
	// height - the same "Rect.y is actually world Z" convention this mod already documents
	// elsewhere). Nullable rather than a plain float specifically so "unspecified" (a blank
	// config field, or "n") is representable distinctly from "set to 0" - a real, valid
	// coordinate for a zone centered near the origin.
	internal readonly struct ZoneEdgeOverride
	{
		internal ZoneEdgeOverride(float? west, float? east, float? north, float? south)
		{
			West = west;
			East = east;
			North = north;
			South = south;
		}

		internal float? West { get; }
		internal float? East { get; }
		internal float? North { get; }
		internal float? South { get; }
	}

	// Manually patched (not attribute-discovered) from Plugin.PatchMoveStationsMoveButtonSuppression -
	// see that method's doc comment for why. TryInjectMoveMenuRow is void and parameterless, so this
	// prefix has to independently determine whether AggregateDesk's menu is the one currently open.
	internal static class MoveStationsCompat_Patch
	{
		// internal, not private - Plugin.PatchMoveStationsMoveButtonSuppression references
		// this via nameof(), which needs compile-time accessibility from that class. The other
		// patch classes in this file don't hit this, since HarmonyLib.PatchAll() discovers
		// their Prefix/Postfix methods purely by reflection, not by a C#-checked nameof().
		internal static bool Prefix()
		{
			UIBuildingWindow window = UnityEngine.Object.FindFirstObjectByType<UIBuildingWindow>();
			if (window != null && window.IsShown && window.data?.AssignedWgo == Plugin.AggregateDesk)
			{
				return false;
			}

			return true;
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
	// this desk within 10 units (WgoExtensions.TryGetNearestBuilderWorldZone). This used to be
	// a rare-case defensive fallback (for a distant real desk hitting a physics-streaming
	// corner case); it's now the ONLY way AggregateDesk's zone match ever succeeds at all -
	// AggregateDeskId is a brand-new id that will never naturally match any real
	// WorldZoneDef.builderId (confirmed: that check is pure string equality), so without this
	// patch the build window would silently never appear for it. Falls back to whichever
	// loaded WorldZone is physically nearest the desk, so the grid still centers somewhere
	// sensible instead of picking an arbitrary one.
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

	// Applies ZoneSizeOverrides to the real physics collider a zone actually uses for build
	// placement. Confirmed via decomp that wholeZoneRect is NOT the source of truth for
	// placement checks - WgoExtensions.TryGetNearestBuilderWorldZone's OverlapBox and
	// WgoBuildPointer.UpdateSelectionCellsState's per-cell check both query the real
	// BoxCollider, never wholeZoneRect directly - so overriding wholeZoneRect alone would do
	// nothing for this mod's actual use case. Instead this mutates the collider itself, before
	// WorldZoneData.Init's own body derives wholeZoneRect from it (Init's exact formula, read
	// directly: wholeZoneRect = new Rect(new Vector2(vector.x - size.x/2f, vector.z -
	// size.z/2f), new Vector2(size.x, size.z)), where vector =
	// zoneCollider.transform.TransformPoint(zoneCollider.center) - so size.x/size.z map
	// directly to world extents with no lossyScale factor, matching how vanilla content never
	// scales these colliders). This patch reuses that exact same math, substituting the
	// configured absolute West/East/North/South coordinate for whichever edges were specified
	// (an unspecified edge falls back to the collider's own current value via `??`), and only
	// ever touches the XZ footprint - the collider's existing world-space Y center/size is
	// preserved, since a zone override is about ground footprint, not height.
	//
	// Absolute coordinates, unlike the delta-based design this replaced, are naturally
	// idempotent: applying the same override twice in a row produces the same result both
	// times, so there's no "relative to what baseline" question to reason about, and no way for
	// repeated application (every time the zone's scene streams in) to compound or drift.
	[HarmonyPatch(typeof(WorldZoneData), nameof(WorldZoneData.Init))]
	internal static class WorldZoneData_Init_Patch
	{
		private static void Prefix(WorldZoneData __instance, BoxCollider zoneCollider)
		{
			if (zoneCollider == null || !Plugin.TryGetZoneOverride(__instance.id, out ZoneEdgeOverride zoneOverride))
			{
				return;
			}

			Vector3 worldCenter = zoneCollider.transform.TransformPoint(zoneCollider.center);
			Vector3 size = zoneCollider.size;

			float xMin = zoneOverride.West ?? worldCenter.x - size.x / 2f;
			float xMax = zoneOverride.East ?? worldCenter.x + size.x / 2f;
			float zMin = zoneOverride.South ?? worldCenter.z - size.z / 2f;
			float zMax = zoneOverride.North ?? worldCenter.z + size.z / 2f;

			if (xMax <= xMin || zMax <= zMin)
			{
				UnityEngine.Debug.LogWarning($"BuildAnywhere: zone size override for '{__instance.id}' would collapse or invert the zone - skipping.");
				return;
			}

			Vector3 desiredWorldCenter = new Vector3((xMin + xMax) / 2f, worldCenter.y, (zMin + zMax) / 2f);
			zoneCollider.center = zoneCollider.transform.InverseTransformPoint(desiredWorldCenter);
			zoneCollider.size = new Vector3(xMax - xMin, size.y, zMax - zMin);
		}
	}

	// Applies the same ZoneSizeOverrides edges to the data-layer wholeZoneRect - not redundant
	// with WorldZoneData_Init_Patch above, since PrepareForGame() (the once-per-boot method that
	// bakes this zone's navmesh region, among other things, off wholeZoneRect) runs before any
	// WorldZone GameObject/collider for that zone's scene necessarily exists yet (scenes stream
	// in later). Without this second patch, a zone whose scene hasn't loaded at boot would get
	// its navmesh baked at the OLD size, only for the collider to catch up later when the scene
	// streams in - leaving the physics collider and the navmesh out of sync.
	//
	// Also idempotent, same as the Init patch above and for the same reason (absolute
	// coordinates, not deltas): whether wholeZoneRect going in already reflects this override
	// (e.g. persisted from a previous session) or not, substituting the configured edges again
	// produces the same result either way - unlike the delta-based design this replaced, there's
	// no scenario where re-applying this compounds or drifts across sessions.
	[HarmonyPatch(typeof(WorldZoneData), nameof(WorldZoneData.PrepareForGame))]
	internal static class WorldZoneData_PrepareForGame_Patch
	{
		private static void Prefix(WorldZoneData __instance)
		{
			if (!Plugin.TryGetZoneOverride(__instance.id, out ZoneEdgeOverride zoneOverride))
			{
				return;
			}

			Rect rect = __instance.wholeZoneRect;
			float xMin = zoneOverride.West ?? rect.xMin;
			float xMax = zoneOverride.East ?? rect.xMax;
			float zMin = zoneOverride.South ?? rect.yMin;
			float zMax = zoneOverride.North ?? rect.yMax;

			if (xMax <= xMin || zMax <= zMin)
			{
				UnityEngine.Debug.LogWarning($"BuildAnywhere: zone size override for '{__instance.id}' would collapse or invert the zone - skipping.");
				return;
			}

			__instance.wholeZoneRect = new Rect(xMin, zMin, xMax - xMin, zMax - zMin);
		}
	}

}

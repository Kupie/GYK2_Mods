using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace WheresMaStorage
{
	// Ported from the GK1 mod of the same name (p1xel8ted/Graveyard-Keeper-Mods).
	// GK2's world-object and inventory systems are architecturally different from
	// GK1's, so this is a from-scratch reimplementation of the GK1 mod's behavior
	// against GK2-native APIs rather than a transliterated Harmony patch set. See
	// TASKS.md for the phased plan and the decomp grounding behind each phase.
	//
	// Phase 1: Tier 2 - extra inventory/container capacity and configurable
	// per-category stack sizes.
	// Phase 2: Tier 1 - the shared inventory pool. GK2 already pools every
	// eligible container in a zone for chests, craft desks and building (see
	// SharedInventoryPool.cs); this restricts and reorders that existing pool
	// rather than building a new one.
	// Phase 3 (this build, partial): Tier 4 - hand tool destroy only so far.
	// Drop collection and the loot magnet range are researched in TASKS.md
	// but not implemented yet - both have an open question a code-only
	// decomp can't resolve. Tier 3 (QoL/UI) is not implemented yet either.
	[BepInPlugin("kupie.gk2.wheresmastorage", "Where's Ma Storage", "0.3.0")]
	public class Plugin : BaseUnityPlugin
	{
		private const string CapacitySection = "Capacity";
		private const string StackingSection = "Item Stacking";
		private const string SharedInventorySection = "Shared Inventory";
		private const string GameplaySection = "Gameplay";

		internal static ManualLogSource Log;

		internal static ConfigEntry<int> PlayerInventoryBonus;
		internal static ConfigEntry<int> ContainerInventoryBonus;

		internal static ConfigEntry<bool> ModifyStackSize;
		internal static ConfigEntry<int> StackSizeForStackables;
		internal static ConfigEntry<bool> EnableToolStacking;
		internal static ConfigEntry<bool> EnableWeaponStacking;
		internal static ConfigEntry<bool> EnableEquipmentStacking;
		internal static ConfigEntry<bool> EnablePrayerStacking;
		internal static ConfigEntry<bool> EnableGraveItemStacking;

		internal static ConfigEntry<bool> SharedInventory;
		internal static ConfigEntry<bool> SortByDistanceFromCrafter;
		internal static ConfigEntry<bool> ExcludeWellsFromSharedInventory;
		internal static ConfigEntry<bool> ExcludeQuarryFromSharedInventory;
		internal static ConfigEntry<bool> AllowZombiesAccessToSharedInventory;

		internal static ConfigEntry<bool> AllowHandToolDestroy;

		private Harmony harmony;

		private void Awake()
		{
			Log = Logger;

			PlayerInventoryBonus = Config.Bind(
				CapacitySection,
				"Player Inventory Bonus Slots",
				20,
				"Extra slots added on top of the player's base inventory and tool belt. 0 disables the bonus.");

			ContainerInventoryBonus = Config.Bind(
				CapacitySection,
				"Container Inventory Bonus Slots",
				20,
				"Extra slots added on top of every placed container's (chest, etc.) vanilla base size. 0 disables the bonus.");

			ModifyStackSize = Config.Bind(
				StackingSection,
				"Modify Stack Size",
				true,
				"Master toggle for all stack size changes below.");

			StackSizeForStackables = Config.Bind(
				StackingSection,
				"Stack Size For Stackables",
				999,
				new ConfigDescription(
					"Target stack size applied to every enabled category below. Only ever raises a stack size, never lowers it.",
					new AcceptableValueRange<int>(1, 999)));

			EnableToolStacking = Config.Bind(StackingSection, "Tool Stacking", true, "Let tools (axe, shovel, pickaxe, hammer, fishing rod) stack.");
			EnableWeaponStacking = Config.Bind(StackingSection, "Weapon Stacking", true, "Let weapons (sword, bow, pike) stack.");
			EnableEquipmentStacking = Config.Bind(StackingSection, "Equipment Stacking", true, "Let equipment (body armor, collar) stack.");
			EnablePrayerStacking = Config.Bind(StackingSection, "Prayer Stacking", true, "Let preach/prayer items stack.");
			EnableGraveItemStacking = Config.Bind(StackingSection, "Grave Item Stacking", false, "Let grave/autopsy items (organs, bones, skull, embalm) stack. Off by default - matches the GK1 mod's default.");

			// GK2 already pools every eligible container in a zone for chests,
			// craft desks and building - these toggles restrict/reorder that
			// existing pool rather than turning pooling on from scratch. Read
			// live at every pool construction, so no re-apply plumbing is
			// needed (unlike the capacity/stacking config above).
			SharedInventory = Config.Bind(SharedInventorySection, "Shared Inventory", true, "Master toggle for the shared inventory pool. Off restores vanilla's per-container-only behavior at chests, craft desks and building.");
			SortByDistanceFromCrafter = Config.Bind(SharedInventorySection, "Sort By Distance From Crafter", true, "Order the pool's containers by distance from the crafter (nearest first) instead of vanilla's fuel-container-priority order.");
			ExcludeWellsFromSharedInventory = Config.Bind(SharedInventorySection, "Exclude Wells From Shared Inventory", true, "Don't pool a zone's containers when the zone is a well.");
			ExcludeQuarryFromSharedInventory = Config.Bind(SharedInventorySection, "Exclude Quarry From Shared Inventory", true, "Don't pool a zone's containers when the zone is the mine/quarry.");
			AllowZombiesAccessToSharedInventory = Config.Bind(SharedInventorySection, "Allow Zombies Access To Shared Inventory", true, "Off restricts a zombie worker at a craft desk to its own carried inventory instead of the zone's pooled containers.");

			// Also read live at the point of use (ItemDef.CanNotBeDestroyed's
			// getter) - no re-apply plumbing needed, same as the Shared
			// Inventory section above.
			AllowHandToolDestroy = Config.Bind(GameplaySection, "Allow Hand Tool Destroy", true, "Let hand tools (axe, shovel, pickaxe, hammer, fishing rod) be destroyed from the inventory context menu. Vanilla blocks this the same way it blocks destroying any other canNotBeDestroyed item.");

			PlayerInventoryBonus.SettingChanged += (_, _) => CapacityBonus.ApplyPlayerAndToolBelt();
			ContainerInventoryBonus.SettingChanged += (_, _) => CapacityBonus.ApplyAllContainers();
			ModifyStackSize.SettingChanged += (_, _) => StackSizeBonus.Apply();
			StackSizeForStackables.SettingChanged += (_, _) => StackSizeBonus.Apply();
			EnableToolStacking.SettingChanged += (_, _) => StackSizeBonus.Apply();
			EnableWeaponStacking.SettingChanged += (_, _) => StackSizeBonus.Apply();
			EnableEquipmentStacking.SettingChanged += (_, _) => StackSizeBonus.Apply();
			EnablePrayerStacking.SettingChanged += (_, _) => StackSizeBonus.Apply();
			EnableGraveItemStacking.SettingChanged += (_, _) => StackSizeBonus.Apply();

			// MainGame.OnGameStarted fires once the gameplay scene has finished
			// loading, for a brand-new game and a continued/loaded save alike.
			MainGame.OnGameStarted += CapacityBonus.ApplyPlayerAndToolBelt;
			MainGame.OnGameStarted += CapacityBonus.ApplyAllContainers;
			MainGame.OnGameStarted += StackSizeBonus.Apply;

			harmony = new Harmony("kupie.gk2.wheresmastorage");
			harmony.PatchAll();

			Log.LogInfo("Plugin kupie.gk2.wheresmastorage is loaded!");
		}

		private void OnDestroy()
		{
			MainGame.OnGameStarted -= CapacityBonus.ApplyPlayerAndToolBelt;
			MainGame.OnGameStarted -= CapacityBonus.ApplyAllContainers;
			MainGame.OnGameStarted -= StackSizeBonus.Apply;
			harmony?.UnpatchSelf();
		}
	}
}

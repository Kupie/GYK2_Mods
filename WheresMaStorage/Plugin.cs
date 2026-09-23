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
	// Phase 1 (this build): Tier 2 only - extra inventory/container capacity and
	// configurable per-category stack sizes. Tier 1 (the shared-pool feature that
	// is the mod's actual reason to exist), Tier 3 (QoL/UI) and Tier 4 (gameplay
	// conveniences) are not implemented yet - see TASKS.md.
	[BepInPlugin("kupie.gk2.wheresmastorage", "Where's Ma Storage", "0.1.0")]
	public class Plugin : BaseUnityPlugin
	{
		private const string CapacitySection = "Capacity";
		private const string StackingSection = "Item Stacking";

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

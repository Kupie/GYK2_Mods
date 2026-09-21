using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SpeedupThings
{
	[BepInPlugin("kupie.gk2.speedupthings", "Speedup Things", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ConfigEntry<float> CraftingSpeedMult;
		internal static ConfigEntry<bool> AutoCraftEnabled;
		internal static ConfigEntry<float> AutoCraftSpeedMult;
		internal static ConfigEntry<float> HpActivitySpeedMult;

		private Harmony harmony;

		private void Awake()
		{
			CraftingSpeedMult = Config.Bind(
				"General",
				"CraftingSpeedMult",
				1f,
				new ConfigDescription(
					"Speeds up manual crafting (hammer-on-anvil style interactions where you hold a tool and hit a workbench). 0.5 = 2x faster, 2 = 2x slower. Only scales how fast the swing animation itself plays - it does not change how many progress bars each hit fills, that still depends on skill like normal.",
					new AcceptableValueRange<float>(0.1f, 3f)));

			AutoCraftEnabled = Config.Bind(
				"General",
				"AutoCraftEnabled",
				false,
				new ConfigDescription(
					"Enables the automatic, worker-less crafting speedup below (smelting, cooking, and similar - anything that just takes in-game time to complete, not manual hammer/anvil crafting). Off by default, since other mods already cover auto-craft speed - turn this on only if you want SpeedupThings handling it instead. Takes effect immediately, no restart needed."));

			AutoCraftSpeedMult = Config.Bind(
				"General",
				"AutoCraftSpeedMult",
				1f,
				new ConfigDescription(
					"How much faster automatic, worker-less crafting runs (smelting, cooking, and similar) once AutoCraftEnabled is on above. 2 = 2x faster, 200 = 200x faster (max). Has no effect while AutoCraftEnabled is off. Does not affect manual hammer/anvil-style crafting - see CraftingSpeedMult for that. Takes effect immediately, no restart needed.",
					new AcceptableValueRange<float>(1f, 200f)));

			HpActivitySpeedMult = Config.Bind(
				"General",
				"HpActivitySpeedMult",
				1f,
				new ConfigDescription(
					"Speeds up manual labor on world objects - digging graves, filling graves, mining, and similar repeated-hit interactions (anything the game drives through PlayerHPActivity). 0.5 = 2x faster, 2 = 2x slower. Separate from CraftingSpeedMult, which only covers actual crafting.",
					new AcceptableValueRange<float>(0.1f, 3f)));

			harmony = new Harmony("kupie.gk2.speedupthings");
			harmony.PatchAll();
		}

		private void OnDestroy()
		{
			harmony?.UnpatchSelf();
		}
	}

	// AutoCraftSpeedMult: CraftSystem.CustomUpdate only calls CraftComponent.Update(deltaTime)
	// for craft components where IsAutoCraftable is true (smelting, cooking, and similar) -
	// manual, hammer-swung crafts advance through a completely different method,
	// UpdateManual(int), so scaling deltaTime here can't touch them.
	//
	// Gated behind AutoCraftEnabled (off by default) since other mods already cover
	// auto-craft speedup - this is a single bool check and returns immediately when off,
	// so the patch is effectively a no-op unless the user explicitly opts in.
	[HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.Update))]
	internal static class CraftComponent_Update_Patch
	{
		private static void Prefix(CraftComponent __instance, ref float deltaTime)
		{
			if (!Plugin.AutoCraftEnabled.Value)
			{
				return;
			}

			if (__instance.IsAutoCraftable)
			{
				deltaTime *= Plugin.AutoCraftSpeedMult.Value;
			}
		}
	}

	// CraftingSpeedMult / HpActivitySpeedMult: manual crafting's (and digging/mining's)
	// ~1-second-per-bar cadence comes from an Animation Event embedded in the tool-swing clip
	// itself (ToolComponent.OnUseToolActionAnimationEvent), not a duration field in code, so
	// there's nothing to multiply directly. Scaling the player's own Animator.speed while a
	// swing is active is the actual lever - it speeds up the swing itself without touching
	// GetActionDamage's mastery-based tick math, so "N bars/HP per hit depending on skill"
	// stays exactly as it is normally.
	//
	// ToolComponent is only ever constructed once, for the player (PlayerController.cs), so
	// every TryStartInteraction/ResetData call here is already the player's own. The type
	// check below picks which multiplier applies: PlayerCraftActivity is crafting proper,
	// PlayerHPActivity is the game's general "repeatedly hit this object" mechanic (digging
	// graves, filling graves, mining, and similar) - anything else (e.g. combat, if it also
	// routes through here) is left untouched.
	[HarmonyPatch(typeof(ToolComponent), nameof(ToolComponent.TryStartInteraction))]
	internal static class ToolComponent_TryStartInteraction_Patch
	{
		private static void Postfix(IWorkActivity toolActor, bool __result)
		{
			if (!__result)
			{
				return;
			}

			float? mult = null;
			if (toolActor is PlayerCraftActivity)
			{
				mult = Plugin.CraftingSpeedMult.Value;
			}
			else if (toolActor is PlayerHPActivity)
			{
				mult = Plugin.HpActivitySpeedMult.Value;
			}

			if (mult == null)
			{
				return;
			}

			Animator animator = MainGame.PlayerController?.View?.PlayerAnimation?.Animator;
			if (animator != null)
			{
				animator.speed = 1f / mult.Value;
			}
		}
	}

	// ResetData is private, so it's patched by name - it's the common endpoint for both the
	// animation-driven stop path and the immediate one, so it's the reliable place to put the
	// player's Animator back to normal speed once a tool interaction (crafting or otherwise)
	// ends. Resetting unconditionally is safe: this only ever gets sped up in the first place
	// during a crafting swing, so putting it back to 1x after any tool use is a no-op the rest
	// of the time.
	[HarmonyPatch(typeof(ToolComponent), "ResetData")]
	internal static class ToolComponent_ResetData_Patch
	{
		private static void Postfix()
		{
			Animator animator = MainGame.PlayerController?.View?.PlayerAnimation?.Animator;
			if (animator != null)
			{
				animator.speed = 1f;
			}
		}
	}
}
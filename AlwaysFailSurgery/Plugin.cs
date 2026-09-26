using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace AlwaysFailSurgery
{
	[BepInPlugin("kupie.gk2.alwaysfailsurgery", "Always Fail Surgery", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ConfigEntry<bool> AlwaysFail;

		private Harmony harmony;

		private void Awake()
		{
			AlwaysFail = Config.Bind(
				"General",
				"Always Fail",
				true,
				"Every hit on an autopsy fills a red (failed) cell instead of green, so the surgery always fails - for getting the \"Oops, surgeon\" achievement on purpose.");

			harmony = new Harmony("kupie.gk2.alwaysfailsurgery");
			harmony.PatchAll();
		}

		private void OnDestroy()
		{
			harmony?.UnpatchSelf();
		}
	}

	// PlayerCraftActivity.GetActionDamage is the player's per-hit roll: for autopsy (and star)
	// crafts it returns 0 when Random.Range misses the mastery chance, or 1+ on success. The
	// return value goes straight into CraftComponent.UpdateManual -> CraftElementBase.Update,
	// where 0 on an autopsy craft adds a red (failed) cell and anything above 0 adds green
	// cells. Forcing 0 makes every hit red, so the organ ends below its silverLevel and
	// WgoData.DefineResultFor*OrganCraft takes its "failed result" (surgeon mistake) branch.
	[HarmonyPatch(typeof(PlayerCraftActivity), nameof(PlayerCraftActivity.GetActionDamage))]
	internal static class PlayerCraftActivity_GetActionDamage_Patch
	{
		private static void Postfix(PlayerCraftActivity __instance, ref int __result)
		{
			if (!Plugin.AlwaysFail.Value)
			{
				return;
			}

			CraftElementBase craftElement = __instance.CraftComponent?.CurrentCraftElement;
			if (craftElement == null || !craftElement.Def.isAutopsyCraft)
			{
				return;
			}

			__result = 0;
		}
	}
}

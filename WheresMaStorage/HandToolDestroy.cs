using HarmonyLib;

namespace WheresMaStorage
{
	// Vanilla gates "can this item be destroyed" purely by ItemDef.CanNotBeDestroyed,
	// a flat per-item-definition flag (confirmed the only check in
	// PlayerInventoryUIItemOpHandler.TryDestroyItem, no separate "is this tool
	// currently equipped" runtime check anywhere in that path) - hand tools are
	// just data-flagged canNotBeDestroyed = true, the same way any other
	// undiscardable item would be. So unlike Phase 1's InventorySize (where a
	// getter patch doesn't work because other code reads the private field
	// directly), a getter patch here is sufficient: TryDestroyItem is the only
	// confirmed reader of this property, and it goes through the property.
	[HarmonyPatch(typeof(ItemDef), nameof(ItemDef.CanNotBeDestroyed), MethodType.Getter)]
	internal static class ItemDef_CanNotBeDestroyed_Patch
	{
		private static void Postfix(ItemDef __instance, ref bool __result)
		{
			if (__result && Plugin.AllowHandToolDestroy.Value && __instance.isTool)
			{
				__result = false;
			}
		}
	}
}

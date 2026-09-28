using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace FactoryWorkbenches
{
	// A factory bench takes materials from the belts by pulling: ConveyorWorkbenchComponent
	// .GetItemFromConveyor asks the neighbouring belt element "CanGiveItem(me)" and only moves an
	// item when the answer is yes. (The bench's own CanGiveItem is always false, and it only pushes
	// onto a belt while its status is WaitingForOutputDrop, which only a ConveyorCrafter zombie ever
	// causes.) With Belt Inputs Allowed off, every belt element answers "no" to a converted bench, so the
	// belts neither feed nor drain it.
	//
	// Postfix on every CanGiveItem override of the belt element classes (cell, splitter, underground
	// cell, station cell, chest, chest out, pallet): the answer is just forced to false afterwards.
	// A bench still holding a legacy ConveyorCrafter is not "regular" (Factory.IsRegularMode), so
	// its belts keep working.
	[HarmonyPatch]
	internal static class ConveyorComponent_CanGiveItem_Patch
	{
		private static IEnumerable<MethodBase> TargetMethods()
		{
			foreach (Type type in typeof(ConveyorComponent).Assembly.GetTypes())
			{
				if (!type.IsSubclassOf(typeof(ConveyorComponent)) || type == typeof(ConveyorWorkbenchComponent))
				{
					continue;
				}
				MethodInfo method = AccessTools.DeclaredMethod(type, "CanGiveItem", new[] { typeof(ConveyorComponent) });
				if (method != null)
				{
					yield return method;
				}
			}
		}

		private static void Postfix(ConveyorComponent conveyorComponent, ref bool __result)
		{
			if (!__result || Plugin.BeltInputsAllowed.Value)
			{
				return;
			}

			ConveyorWorkbenchComponent bench = conveyorComponent as ConveyorWorkbenchComponent;
			if (bench != null && Factory.IsRegularMode(bench.WgoData))
			{
				__result = false;
			}
		}
	}
}

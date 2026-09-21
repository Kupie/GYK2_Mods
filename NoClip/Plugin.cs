using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace NoclipMod
{
	// Movement in this game is pathfinding-driven (PlayerLocalAreaMovement/MovementComponent,
	// built on the A* Pathfinding Project) rather than Rigidbody/Collider based - walls are
	// simply absent nodes in the nav graph, not colliders a normal move "hits". That means
	// disabling colliders wouldn't do anything useful for the *pathfinding* side; asking the
	// pathfinder to route through a wall just fails to find a path. So this bypasses
	// pathfinding entirely while active and drives the player's transform directly instead.
	//
	// The player also has a real physical Rigidbody/MeshCollider (PlayerController.PhysicalBody)
	// independent of pathfinding. detectCollisions=false stops it being depenetrated out of
	// walls, but gravity is a physics force, not a collision response, so the rigidbody kept
	// falling anyway with nothing to land on. Making it kinematic while noclip is active stops
	// gravity (and all other physics forces) from touching it at all - it then only moves when
	// we explicitly move its transform, which is exactly what happens every frame below.
	[BepInPlugin("kupie.gk2.noclipmod", "Noclip Mod", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ConfigEntry<KeyboardShortcut> ToggleKey;
		internal static ConfigEntry<float> FlySpeed;

		private bool noclipActive;
		private bool originalIsKinematic;
		private Camera mainCamera;

		private void Awake()
		{
			ToggleKey = Config.Bind(
				"General",
				"ToggleKey",
				new KeyboardShortcut(KeyCode.N, KeyCode.LeftControl),
				"Toggles noclip (free movement through walls) on/off.");

			FlySpeed = Config.Bind(
				"General",
				"FlySpeed",
				8f,
				"Movement speed while noclip is active, in units/second.");
		}

		private void Update()
		{
			if (ToggleKey.Value.IsDown())
			{
				noclipActive = !noclipActive;
				SetNoclipState(noclipActive);
				Logger.LogInfo($"Noclip {(noclipActive ? "enabled" : "disabled")}.");
			}

			if (!noclipActive)
			{
				return;
			}

			PlayerController playerController = MainGame.PlayerController;
			if (playerController == null)
			{
				return;
			}

			// Keep cancelling any path the normal click-to-move system might start while
			// noclip is active (e.g. from a stray click) - it would otherwise fight our
			// direct transform moves every frame it ran.
			if (playerController.PlayerLocalAreaMovement.IsMovementStarted)
			{
				playerController.PlayerLocalAreaMovement.StopMovement(true);
			}

			if (mainCamera == null)
			{
				mainCamera = Camera.main;
			}

			Vector3 forward;
			Vector3 right;
			if (mainCamera != null)
			{
				forward = mainCamera.transform.forward;
				right = mainCamera.transform.right;
			}
			else
			{
				forward = Vector3.forward;
				right = Vector3.right;
			}

			forward.y = 0f;
			right.y = 0f;
			forward.Normalize();
			right.Normalize();

			Vector3 moveDir = Vector3.zero;
			if (Input.GetKey(KeyCode.W))
			{
				moveDir += forward;
			}
			if (Input.GetKey(KeyCode.S))
			{
				moveDir -= forward;
			}
			if (Input.GetKey(KeyCode.D))
			{
				moveDir += right;
			}
			if (Input.GetKey(KeyCode.A))
			{
				moveDir -= right;
			}
			if (Input.GetKey(KeyCode.Space))
			{
				moveDir += Vector3.up;
			}
			if (Input.GetKey(KeyCode.LeftShift))
			{
				moveDir -= Vector3.up;
			}

			if (moveDir.sqrMagnitude > 0f)
			{
				playerController.transform.position += moveDir.normalized * FlySpeed.Value * Time.deltaTime;
			}
		}

		private void SetNoclipState(bool enabled)
		{
			PlayerController playerController = MainGame.PlayerController;
			if (playerController == null)
			{
				return;
			}

			// Cancel any path in progress so the normal movement system doesn't try to keep
			// following it once we start moving the transform ourselves.
			playerController.PlayerLocalAreaMovement.StopMovement(true);

			PlayerPhysicalBody physicalBody = playerController.PhysicalBody;
			if (physicalBody == null)
			{
				return;
			}

			if (physicalBody.Rb != null)
			{
				if (enabled)
				{
					originalIsKinematic = physicalBody.Rb.isKinematic;
					physicalBody.Rb.isKinematic = true;
				}
				else
				{
					physicalBody.Rb.isKinematic = originalIsKinematic;
				}

				physicalBody.Rb.detectCollisions = !enabled;
			}

			if (physicalBody.MeshCollider != null)
			{
				physicalBody.MeshCollider.enabled = !enabled;
			}
		}
	}
}
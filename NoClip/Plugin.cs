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
		internal static ConfigEntry<float> FlySpeedStep;
		internal static ConfigEntry<KeyboardShortcut> SpeedUpKey;
		internal static ConfigEntry<KeyboardShortcut> SpeedDownKey;
		internal static ConfigEntry<float> Smoothing;

		private const float MinFlySpeed = 0.5f;
		private const float MaxFlySpeed = 100f;

		private bool noclipActive;
		private Vector3 velocity;
		private Vector3 velocityRef;
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
				new ConfigDescription(
					"Movement speed while noclip is active, in units/second. Can also be changed in-game with the speed keys below.",
					new AcceptableValueRange<float>(MinFlySpeed, MaxFlySpeed)));

			FlySpeedStep = Config.Bind(
				"General",
				"FlySpeedStep",
				2f,
				"How much FlySpeed changes per press of the speed up/down keys.");

			SpeedUpKey = Config.Bind(
				"General",
				"SpeedUpKey",
				new KeyboardShortcut(KeyCode.PageUp),
				"Increases noclip speed while noclip is active.");

			SpeedDownKey = Config.Bind(
				"General",
				"SpeedDownKey",
				new KeyboardShortcut(KeyCode.PageDown),
				"Decreases noclip speed while noclip is active.");

			Smoothing = Config.Bind(
				"General",
				"Smoothing",
				0.12f,
				new ConfigDescription(
					"How long (seconds) movement takes to ease in/out. 0 = instant start/stop, higher = floatier.",
					new AcceptableValueRange<float>(0f, 1f)));
		}

		private void Update()
		{
			if (ToggleKey.Value.IsDown())
			{
				noclipActive = !noclipActive;
				velocity = Vector3.zero;
				velocityRef = Vector3.zero;
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

			if (SpeedUpKey.Value.IsDown())
			{
				AdjustSpeed(FlySpeedStep.Value);
			}
			if (SpeedDownKey.Value.IsDown())
			{
				AdjustSpeed(-FlySpeedStep.Value);
			}

			PlayerPhysicalBody physicalBody = playerController.PhysicalBody;
			if (physicalBody == null || physicalBody.Rb == null || MainGame.IsGamePaused)
			{
				return;
			}

			// The game can flip the rigidbody back to dynamic (its own kinematic multi-flag) at
			// any time; keep it kinematic so gravity/physics can't fight the movement below.
			if (!physicalBody.Rb.isKinematic)
			{
				physicalBody.Rb.isKinematic = true;
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
			if (Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
			{
				moveDir += Vector3.up;
			}
			if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
			{
				moveDir -= Vector3.up;
			}

			// Ease toward the target velocity instead of snapping to it, so starting/stopping and
			// changing direction don't produce hard per-frame jumps.
			Vector3 targetVelocity = moveDir.sqrMagnitude > 0f ? moveDir.normalized * FlySpeed.Value : Vector3.zero;
			if (Smoothing.Value > 0f)
			{
				velocity = Vector3.SmoothDamp(velocity, targetVelocity, ref velocityRef, Smoothing.Value, Mathf.Infinity, Time.deltaTime);
			}
			else
			{
				velocity = targetVelocity;
			}

			if (velocity.sqrMagnitude < 0.0001f)
			{
				velocity = Vector3.zero;
				physicalBody.StopMoving();
				return;
			}

			// Move the way the game's own path-following does: through the kinematic rigidbody
			// (MoveByPosition also keeps PlayerData.position, and so world chunking, in sync). The
			// transform is set every frame as well - the rigidbody only pushes its position to the
			// transform on the 50Hz physics step, which is what made movement look jittery.
			Vector3 newPosition = physicalBody.transform.position + velocity * Time.deltaTime;
			physicalBody.transform.position = newPosition;
			physicalBody.MoveByPosition(newPosition, new Vector2(velocity.x, velocity.z), false);
		}

		private void AdjustSpeed(float delta)
		{
			FlySpeed.Value = Mathf.Clamp(FlySpeed.Value + delta, MinFlySpeed, MaxFlySpeed);
			Logger.LogInfo($"Noclip speed: {FlySpeed.Value:0.#}");
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
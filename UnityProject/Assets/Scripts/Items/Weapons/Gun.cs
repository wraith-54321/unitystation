﻿using System.Collections.Generic;
using Mirror;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;
using Weapons.Projectiles;

namespace Weapons
{
	/// <summary>
	///  Allows an object to behave like a gun and fire shots. Server authoritative with client prediction.
	/// </summary>
	[RequireComponent(typeof(Pickupable))]
	[RequireComponent(typeof(ItemStorage))]
	[RequireComponent(typeof(GunTrigger))]
	public class Gun : NetworkBehaviour, IPredictedCheckedInteractable<AimApply>, IClientInteractable<HandActivate>,
		IClientInteractable<InventoryApply>, IServerInventoryMove, IServerSpawn, IExaminable
	{
		//constants for calculating screen shake due to recoil. Currently unused.
		/*
	private static readonly float MAX_PROJECTILE_VELOCITY = 48f;
	private static readonly float MAX_SHAKE_INTENSITY = 1f;
	private static readonly float MIN_SHAKE_INTENSITY = 0.01f;
	*/

		/// <summary>
		/// Prefab to be spawned within on roundstart
		/// </summary>
		[SerializeField, Tooltip("The prefab to be spawned within the weapon on roundstart")]
		protected GameObject ammoPrefab = null;

		/// <summary>
		/// Optional ejected casing override, will default to the standard casing if left null and will only be used if SpawnsCasing is true
		/// </summary>
		[SerializeField, Tooltip("Optional casing override, defaults to standard casing when null")]
		private GameObject casingPrefabOverride = null;

		/// <summary>
		/// If false prevents players from removing the magazine from their weapon.
		/// </summary>
		[SerializeField, Tooltip("If the player is allowed to remove a loaded mag")]
		private bool allowMagazineRemoval = true;

		/// <summary>
		/// The type of ammo this weapon will allow, this is a string and not an enum for diversity
		/// </summary>
		[FormerlySerializedAs("AmmoType"), Tooltip("The type of ammo this weapon will use")]
		public AmmoType ammoType;

		//server-side object indicating the player holding the weapon (null if none)
		protected GameObject serverHolder;
		private RegisterTile shooterRegisterTile;


		/// <summary>
		/// The current magazine for this weapon, null means empty
		/// </summary>
		public MagazineBehaviour CurrentMagazine =>
			magSlot.Item != null ? magSlot.Item.GetComponent<MagazineBehaviour>() : null;

		/// <summary>
		/// Checks if the weapon should spawn weapon casings
		/// </summary>
		[Tooltip("If the weapon should spawn casings ")]
		public bool SpawnsCasing = true;

		/// <summary>
		/// Whether the gun uses an internal magazine.
		/// </summary>
		[HideInInspector, Tooltip("Effects if the gun will use an internal mag")] // will be shown by the code at the very end, if appropriate
		public bool MagInternal = false;

		/// <summary>
		/// The cooldown between full bursts in seconds
		/// </summary>
		[HideInInspector, Tooltip("The cooldown between full a burst in seconds")] // will be shown by the code at the very end, if appropriate
		public double burstCooldown = 3;

		/// <summary>
		/// The number of allowed shots in a burst
		/// </summary>
		[HideInInspector, Tooltip("The number of bullets in a full burst")] // will be shown by the code at the very end, if appropriate
		public double burstCount = 3;
		private double currentBurstCount = 0;

		/// <summary>
		/// If the gun should eject it's magazine automatically (external-magazine-specific)
		/// </summary>
		[HideInInspector, Tooltip("If the gun should eject an empty mag automatically")] // will be shown by the code at the very end, if appropriate
		public bool SmartGun = false;

		/// <summary>
		/// The the current recoil variance this weapon has reached
		/// </summary>
		[HideInInspector]
		public float CurrentRecoilVariance;

		/// <summary>
		/// The countdown untill we can shoot again (seconds)
		/// </summary>
		[HideInInspector]
		public double FireCountDown;

		/// <summary>
		/// The name of the sound this gun makes when shooting
		/// </summary>
		[FormerlySerializedAs("FireingSound"), Tooltip("The name of the sound the gun uses when shooting (must be in soundmanager")]
		public string FiringSound;

		/// <summary>
		/// The amount of times per second this weapon can fire
		/// </summary>
		[Tooltip("The amount of times per second this weapon can fire")]
		public double FireRate;

		/// <summary>
		/// If suicide shooting should be prevented (for when user inadvertently drags over themselves during a burst)
		/// </summary>
		[Tooltip("If suicide shots should be prevented (if the user accidentally mouses over themselves during a shot")]
		protected bool AllowSuicide;

		//TODO connect these with the actual shooting of a projectile
		/// <summary>
		/// The max recoil angle this weapon can reach with sustained fire
		/// </summary>
		public float MaxRecoilVariance;

		/// <summary>
		/// The traveling speed for this weapons projectile
		/// </summary>
		[Tooltip("The speed of the projectile")]
		public int ProjectileVelocity;

		/// <summary>
		/// Describes the recoil behavior of the camera when this gun is fired
		/// </summary>
		[Tooltip("Describes the recoil behavior of the camera when this gun is fired")]
		public CameraRecoilConfig CameraRecoilConfig;

		/// <summary>
		/// The firemode this weapon will use (burst,semi,auto)
		/// </summary>
		[Tooltip("The firemode this weapon will use")]
		public WeaponType WeaponType;

		/// <summary>
		/// Used only in server, the queued up shots that need to be performed when the weapon FireCountDown hits
		/// 0.
		/// </summary>
		private Queue<QueuedShot> queuedShots;

		/// <summary>
		/// We don't want to eject the magazine or reload as soon as the client says its time to do those things - if the client is
		/// firing a burst, they might shoot the whole magazine then eject it and reload before server is done processing the last shot.
		/// Instead, these vars are set when the client says to eject or load a new magazine, and the server will only process
		/// the actual unload / load (updating MagNetID) once the shot queue is empty.
		/// </summary>
		private bool queuedUnload = false;
		private uint queuedLoadMagNetID = NetId.Invalid;

		private RegisterTile registerTile;
		private ItemStorage itemStorage;
		public ItemSlot magSlot;

		private GunTrigger gunTrigger;

		public bool isSuppressed;

		#region Init Logic

		private void Awake()
		{
			//init weapon with missing settings
			GetComponent<ItemAttributesV2>().AddTrait(CommonTraits.Instance.Gun);
			itemStorage = GetComponent<ItemStorage>();
			magSlot = itemStorage.GetIndexedItemSlot(0);
			registerTile = GetComponent<RegisterTile>();
			gunTrigger = GetComponent<GunTrigger>();
			queuedShots = new Queue<QueuedShot>();
			if (gunTrigger == null || magSlot == null || itemStorage == null)
			{
				Debug.LogWarning($"{gameObject.name} missing components, may cause issues");
			}
		}

		private void OnEnable()
		{
			UpdateManager.Add(CallbackType.UPDATE, UpdateMe);
		}

		private void OnDisable()
		{
			UpdateManager.Remove(CallbackType.UPDATE, UpdateMe);
		}

		public virtual void OnSpawnServer(SpawnInfo info)
		{
			Init();
		}

		private void Init()
		{
			if (MagInternal)
			{
				//automatic ejection always disabled
				SmartGun = false;
				//ejecting an internal mag should never be allowed
				allowMagazineRemoval = false;
				//populate with a full internal mag on spawn
			}

			if (ammoPrefab == null)
			{
				Debug.LogError($"{gameObject.name} magazine prefab was null, cannot auto-populate.");
				return;
			}

			//populate with a full external mag on spawn
			Logger.LogTraceFormat("Auto-populate external magazine for {0}", Category.Inventory, name);
			Inventory.ServerAdd(Spawn.ServerPrefab(ammoPrefab).GameObject, magSlot);
		}

		public void OnInventoryMoveServer(InventoryMove info)
		{
			if (info.ToPlayer != null)
			{
				serverHolder = info.ToPlayer.gameObject;
				shooterRegisterTile = serverHolder.GetComponent<RegisterTile>();
			}
			else
			{
				serverHolder = null;
				shooterRegisterTile = null;
			}
		}

		#endregion

		#region Interaction

		public virtual bool WillInteract(AimApply interaction, NetworkSide side)
		{
			if (!DefaultWillInteract.Default(interaction, side)) return false;



			if (CurrentMagazine == null)
			{
				PlayEmptySFX();
				if (side == NetworkSide.Server)
				{
					Logger.LogTrace("Server rejected shot - No magazine being loaded", Category.Firearms);
				}
				return false;
			}

			//note: fire count down is only checked local player side, as server processes all the shots in a queue
			//anyway so client cannot exceed that firing rate no matter what. If we validate firing rate server
			//side at the moment of interaction, it will reject client's shots because of lag between server / client
			//firing countdown
			if (side == NetworkSide.Server || FireCountDown <= 0)
			{
				if (CurrentMagazine.ClientAmmoRemains <= 0)
				{
					if (SmartGun && allowMagazineRemoval) // smartGun is forced off when using an internal magazine
					{
						RequestUnload(CurrentMagazine);
						OutOfAmmoSFX();
					}
					else
					{
						PlayEmptySFX();
					}
					if (side == NetworkSide.Server)
					{
						Logger.LogTrace("Server rejected shot - out of ammo", Category.Firearms);
					}
					return false;
				}

				if (CurrentMagazine.containedBullets[0] != null)
				{
					if (WeaponType == WeaponType.Burst)
					{
						//being held and is a burst weapon, check how many shots have been fired our the current burst
						if (currentBurstCount < burstCount)
						{
							//we have shot less then the max allowed shots in our current burst, increase and fire again
							currentBurstCount++;
							return true;
						}
						else if (currentBurstCount >= burstCount)
						{
							//we have shot the max allowed shots in our current burst, start cooldown and then fire the first shot
							WaitFor.Seconds((float)burstCooldown);
							currentBurstCount = 1;
							return true;
						}
					}
					else if (interaction.MouseButtonState == MouseButtonState.PRESS)
					{
						currentBurstCount = 0;
						return true;
					}
					else
					{
						//being held, only can shoot if this is an automatic
						return WeaponType == WeaponType.FullyAutomatic;
					}
				}
			}

			if (side == NetworkSide.Server)
			{
				Logger.LogTraceFormat("Server rejected shot - unknown reason. MouseButtonState {0} ammo remains {1} weapon type {2}", Category.Firearms,
					interaction.MouseButtonState, CurrentMagazine.ClientAmmoRemains, WeaponType);
			}
			return false;
		}

		public virtual void ClientPredictInteraction(AimApply interaction)
		{
			//do we need to check if this is a suicide (want to avoid the check because it involves a raycast).
			//case 1 - we are beginning a new shot, need to see if we are shooting ourselves
			//case 2 - we are firing an automatic and are currently shooting ourselves, need to see if we moused off
			//	ourselves.
			var isSuicide = false;
			if (interaction.MouseButtonState == MouseButtonState.PRESS ||
			    (WeaponType != WeaponType.SemiAutomatic && AllowSuicide))
			{
				isSuicide = interaction.IsAimingAtSelf;
				AllowSuicide = isSuicide;
			}

			var dir = ApplyRecoil(interaction.TargetVector.normalized);
			DisplayShot(PlayerManager.LocalPlayer, dir, UIManager.DamageZone, isSuicide, CurrentMagazine.containedBullets[0].name, CurrentMagazine.containedProjectilesFired[0]);
		}

		//nothing to rollback
		public void ServerRollbackClient(AimApply interaction) { }

		public virtual void ServerPerformInteraction(AimApply interaction)
		{
			//do we need to check if this is a suicide (want to avoid the check because it involves a raycast).
			//case 1 - we are beginning a new shot, need to see if we are shooting ourselves
			//case 2 - we are firing an automatic and are currently shooting ourselves, need to see if we moused off
			//	ourselves.
			var isSuicide = false;
			if (interaction.MouseButtonState == MouseButtonState.PRESS ||
			    (WeaponType != WeaponType.SemiAutomatic && AllowSuicide))
			{
				isSuicide = interaction.IsAimingAtSelf;
				AllowSuicide = isSuicide;
			}

			//enqueue the shot (will be processed in Update)
			gunTrigger.TriggerPull(interaction.Performer, interaction.TargetVector.normalized, UIManager.DamageZone, isSuicide);
		}



		public virtual bool Interact(HandActivate interaction)
		{
			//try ejecting the mag if external
			if (CurrentMagazine != null && allowMagazineRemoval && !MagInternal)
			{
				RequestUnload(CurrentMagazine);
				return true;
			}

			return false;
		}

		public bool Interact(InventoryApply interaction)
		{
			//only reload if the gun is the target and mag/clip is in hand slot
			if (interaction.TargetObject == gameObject && interaction.IsFromHandSlot)
			{
				if (interaction.UsedObject != null)
				{
					MagazineBehaviour mag = interaction.UsedObject.GetComponent<MagazineBehaviour>();
					if (mag)
					{
						TryReload(mag.gameObject);
						return true;
					}
				}

			}
			return false;
		}

		public virtual string Examine(Vector3 pos)
		{
			return WeaponType + " - Fires " + ammoType + " ammunition (" + (CurrentMagazine != null ? (CurrentMagazine.ServerAmmoRemains.ToString() + " rounds loaded in magazine") : "It's empty!") + ")";
		}

		#endregion


		#region Weapon Firing Mechanism

		private void UpdateMe()
		{
			//don't process if we are server and the gun is not held by anyone
			if (isServer && serverHolder == null) return;

			//if we are client, make sure we've initialized
			if (!isServer && !PlayerManager.LocalPlayer) return;

			//if we are client, only process this if we are holding it
			if (!isServer)
			{
				if (UIManager.Hands == null || UIManager.Hands.CurrentSlot == null) return;
				var heldItem = UIManager.Hands.CurrentSlot.ItemObject;
				if (gameObject != heldItem) return;
			}

			//update the time until the next shot can happen
			if (FireCountDown > 0)
			{
				FireCountDown -= Time.deltaTime;
				//prevents the next projectile taking miliseconds longer than it should
				if (FireCountDown < 0)
				{
					FireCountDown = 0;
				}
			}

			//remaining logic is server side only

			//this will only be executed on the server since only the server
			//maintains the queued actions
			if (queuedShots.Count > 0 && FireCountDown <= 0)
			{
				//fire the next shot in the queue
				DequeueAndProcessServerShot();
			}

			if (queuedUnload && queuedShots.Count == 0 && allowMagazineRemoval && !MagInternal)
			{
				// done processing shot queue,
				// perform the queued unload action, causing all clients and server to update their version of this Weapon
				// due to the syncvar hook
				// there should not be an unload action for internal magazines
				Inventory.ServerDrop(magSlot);
				queuedUnload = false;

			}
			if (queuedLoadMagNetID != NetId.Invalid && queuedShots.Count == 0)
			{
				if (CurrentMagazine == null)
				{
					Logger.LogWarning($"Why is {nameof(CurrentMagazine)} null for {this}?");
				}

				//done processing shot queue, perform the reload, causing all clients and server to update their version of this Weapon
				//due to the syncvar hook
				if (MagInternal)
				{
					var clip = NetworkIdentity.spawned[queuedLoadMagNetID];
					MagazineBehaviour clipComp = clip.GetComponent<MagazineBehaviour>();
					string message = CurrentMagazine.LoadFromClip(clipComp);
					Chat.AddExamineMsg(serverHolder, message);
					queuedLoadMagNetID = NetId.Invalid;
				}
				else
				{
					var magazine = NetworkIdentity.spawned[queuedLoadMagNetID];
					var fromSlot = magazine.GetComponent<Pickupable>().ItemSlot;
					Inventory.ServerTransfer(fromSlot, magSlot);
					queuedLoadMagNetID = NetId.Invalid;
				}
			}
		}

		/// <summary>
		/// attempt to reload the weapon with the item given
		/// </summary>
		private void TryReload(GameObject ammo)
		{
			MagazineBehaviour magazine = ammo.GetComponent<MagazineBehaviour>();
			if (CurrentMagazine == null || (MagInternal && magazine.isClip))
			{
				//RELOAD
				// If the item used on the gun is a magazine, check type and reload
				AmmoType ammoType = magazine.ammoType;
				if (this.ammoType == ammoType)
				{
					var hand = UIManager.Hands.CurrentSlot.NamedSlot;
					RequestReload(magazine.gameObject, hand);
				}
				if (this.ammoType != ammoType)
				{
					Chat.AddExamineMsgToClient("You try to load the wrong ammo into your weapon");
				}
			}
			else if (ammoType == magazine.ammoType)
			{
				Chat.AddExamineMsgToClient("You weapon is already loaded, you can't fit more Magazines in it, silly!");
			}
		}

		/// <summary>
		/// Perform an actual shot on the server, putting the requesst to shoot into our queue
		/// and informing all clients of the shot once the shot is processed
		/// </summary>
		/// <param name="shotBy">gameobject of the player performing the shot</param>
		/// <param name="target">normalized target vector(actual trajectory will differ due to accuracy)</param>
		/// <param name="damageZone">targeted body part</param>
		/// <param name="isSuicideShot">if this is a suicide shot</param>
		[Server]
		public void ServerShoot(GameObject shotBy, Vector2 target,
			BodyPartType damageZone, bool isSuicideShot)
		{
			var finalDirection = ApplyRecoil(target);
			//don't enqueue the shot if the player is no longer able to shoot
			PlayerScript shooter = shotBy.GetComponent<PlayerScript>();
			if (!Validations.CanInteract(shooter, NetworkSide.Server))
			{
				Logger.LogTrace("Server rejected shot: shooter cannot interact", Category.Firearms);
				return;
			}
			//simply enqueue the shot
			//but only enqueue the shot if we have not yet queued up all the shots in the magazine
			if (CurrentMagazine != null && queuedShots.Count < CurrentMagazine.ServerAmmoRemains)
			{
				queuedShots.Enqueue(new QueuedShot(shotBy, finalDirection, damageZone, isSuicideShot));
			}
		}

		/// <summary>
		/// Gets the next shot from the queue (if there is one) and performs the server-side shot (which will
		/// perform damage calculation when the bullet hits stuff), informing all
		/// clients to display the shot.
		/// </summary>
		[Server]
		private void DequeueAndProcessServerShot()
		{
			if (queuedShots.Count > 0)
			{
				QueuedShot nextShot = queuedShots.Dequeue();

				// check if we can still shoot
				PlayerMove shooter = nextShot.shooter.GetComponent<PlayerMove>();
				PlayerScript shooterScript = nextShot.shooter.GetComponent<PlayerScript>();
				if (!shooter.allowInput || shooterScript.IsGhost)
				{
					Logger.Log("A player tried to shoot when not allowed or when they were a ghost.", Category.Exploits);
					Logger.LogWarning("A shot was attempted when shooter is a ghost or is not allowed to shoot.", Category.Firearms);
					return;
				}


				if (CurrentMagazine == null || CurrentMagazine.ServerAmmoRemains <= 0 || CurrentMagazine.containedBullets[0] == null)
				{
					Logger.LogTrace("Player tried to shoot when there was no ammo.", Category.Exploits);
					Logger.LogWarning("A shot was attempted when there is no ammo.", Category.Firearms);
					return;
				}

				if (FireCountDown > 0)
				{
					Logger.LogTrace("Player tried to shoot too fast.", Category.Exploits);
					Logger.LogWarning("Shot was attempted to be dequeued when the fire count down is not yet at 0.", Category.Exploits);
					return;
				}

				GameObject toShoot = CurrentMagazine.containedBullets[0];
				int quantity = CurrentMagazine.containedProjectilesFired[0];

				if (toShoot == null)
				{
					Logger.LogError("Shot was attempted but no projectile or quantity was found to use", Category.Firearms);
					return;
				}

				//perform the actual server side shooting, creating the bullet that does actual damage
				DisplayShot(nextShot.shooter, nextShot.finalDirection, nextShot.damageZone, nextShot.isSuicide, toShoot.name, quantity);

				//trigger a hotspot caused by gun firing
				shooterRegisterTile.Matrix.ReactionManager.ExposeHotspotWorldPosition(nextShot.shooter.TileWorldPosition(), 3200, 0.005f);

				//tell all the clients to display the shot
				ShootMessage.SendToAll(nextShot.finalDirection, nextShot.damageZone, nextShot.shooter, this.gameObject, nextShot.isSuicide, toShoot.name, quantity);

				if (isSuppressed == false && nextShot.isSuicide == false)
				{
					Chat.AddActionMsgToChat(
					serverHolder,
					$"You fire your {gameObject.ExpensiveName()}",
					$"{serverHolder.ExpensiveName()} fires their {gameObject.ExpensiveName()}");
				}


				//kickback
				shooterScript.pushPull.Pushable.NewtonianMove((-nextShot.finalDirection).NormalizeToInt());

				if (SpawnsCasing)
				{
					if (casingPrefabOverride == null)
					{
						//no casing override set, use normal casing prefab
						casingPrefabOverride = Resources.Load("BulletCasing") as GameObject;
					}

					Spawn.ServerPrefab(casingPrefabOverride, nextShot.shooter.transform.position, nextShot.shooter.transform.parent);
				}
			}
		}

		/// <summary>
		/// Perform and display the shot locally (i.e. only on this instance of the game). Does not
		/// communicate anything to other players (unless this is the server, in which case the server
		/// will determine the effects of the bullet). Does not do any validation. This should only be invoked
		/// when displaying the results of a shot (i.e. after receiving a ShootMessage or after this client performs a shot)
		/// or when server is determining the outcome of the shot.
		/// </summary>
		/// <param name="shooter">gameobject of the shooter</param>
		/// <param name="finalDirection">direction the shot should travel (accuracy deviation should already be factored into this)</param>
		/// <param name="damageZone">targeted damage zone</param>
		/// <param name="isSuicideShot">if this is a suicide shot (aimed at shooter)</param>
		public void DisplayShot(GameObject shooter, Vector2 finalDirection,
			BodyPartType damageZone, bool isSuicideShot, string projectileName, int quantity)
		{
			if (!MatrixManager.IsInitialized) return;

			//if this is our gun (or server), last check to ensure we really can shoot
			if (isServer || PlayerManager.LocalPlayer == shooter)
			{
				if (CurrentMagazine.ClientAmmoRemains <= 0)
				{
					if (isServer)
					{
						Logger.LogTrace("Server rejected shot - out of ammo", Category.Firearms);
					}
					return;
				}
				CurrentMagazine.ExpendAmmo();
			}
			//TODO: If this is not our gun, simply display the shot, don't run any other logic
			if (shooter == PlayerManager.LocalPlayer)
			{
				//this is our gun so we need to update our predictions
				FireCountDown += 1.0 / FireRate;
				//add additional recoil after shooting for the next round
				AppendRecoil();

				//Default camera recoil params until each gun is configured separately
				if (CameraRecoilConfig == null || CameraRecoilConfig.Distance == 0f)
				{
					CameraRecoilConfig = new CameraRecoilConfig
					{
						Distance = 0.2f,
						RecoilDuration = 0.05f,
						RecoveryDuration = 0.6f
					};
				}
				Camera2DFollow.followControl.Recoil(-finalDirection, CameraRecoilConfig);
			}

			if (isSuicideShot)
			{
				GameObject bullet = Spawn.ClientPrefab(projectileName,
					shooter.transform.position, parent: shooter.transform.parent).GameObject;
				var b = bullet.GetComponent<Projectile>();
				b.Suicide(shooter, this, damageZone);
			}
			else
			{
				for (int n = 0; n < quantity; n++)
				{
					GameObject Abullet = Spawn.ClientPrefab(projectileName,
						shooter.transform.position, parent: shooter.transform.parent).GameObject;
					var A = Abullet.GetComponent<Projectile>();
					var finalDirectionOverride = CalcDirection(finalDirection, n);
					A.Shoot(finalDirectionOverride, shooter, this, damageZone);
				}
			}
			SoundManager.PlayAtPosition(FiringSound, shooter.transform.position, shooter);
			shooter.GetComponent<PlayerSprites>().ShowMuzzleFlash();
		}

		private Vector2 CalcDirection(Vector2 direction, int iteration)
		{
			float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
			float angleVariance = iteration/1f;
			float angleDeviation = Random.Range(-angleVariance, angleVariance);
			float newAngle = (angle+ angleDeviation) * Mathf.Deg2Rad;
			Vector2 vec2 = new Vector2(Mathf.Cos(newAngle), Mathf.Sin(newAngle)).normalized;
			return vec2;
		}

		#endregion

		#region Weapon Loading and Unloading

		/// <summary>
		/// Tells the server we want to reload and what magazine we want to use to do it and clears our hands. Does
		/// no other logic - the server takes care of the next step to handle the reload.
		/// </summary>
		/// <param name="m"></param>
		/// <param name="hand"></param>
		/// <param name="current"></param>
		private void RequestReload(GameObject m, NamedSlot hand)
		{
			//TODO: Handle this using IF2 instead of Cmd

			Logger.LogTrace("Reloading", Category.Firearms);
			PlayerManager.LocalPlayerScript.weaponNetworkActions.CmdLoadMagazine(gameObject, m, hand);
		}

		/// <summary>
		/// Invoked when server receives a request to load a new magazine. Queues up the reload to occur
		/// when the shot queue is empty.
		/// </summary>
		/// <param name="newMagazineID">id of the new magazine</param>
		[Server]
		public void ServerHandleReloadRequest(uint newMagazineID)
		{
			if (queuedLoadMagNetID != NetId.Invalid)
			{
				//can happen if client is spamming CmdLoadWeapon
				Logger.LogWarning("Player tried to queue a load action while a load action was already queued, ignoring the" +
				                  " second load.", Category.Firearms);
			}
			else
			{
				queuedLoadMagNetID = newMagazineID;
			}
		}

		/// <summary>
		/// Invoked when server recieves a request to unload the current magazine. Queues up the unload to occur
		/// when the shot queue is empty.
		/// </summary>
		public void ServerHandleUnloadRequest()
		{
			if (queuedUnload)
			{
				//this can happen if client is spamming CmdUnloadWeapon
				Logger.LogWarning("Player tried to queue an unload action while an unload action was already queued. Ignoring the" +
				                  " second unload.", Category.Firearms);
			}
			else if (queuedLoadMagNetID != NetId.Invalid)
			{
				Logger.LogWarning("Player tried to queue an unload action while a load action was already queued. Ignoring the unload.", Category.Firearms);
			}
			else
			{
				queuedUnload = true;
			}
		}

		//atm unload with shortcut 'e'
		//TODO dev right click unloading so it goes into the opposite hand if it is selected
		/// <summary>
		/// Tells the server we want to unload and does nothing else. Server takes care of the next step to handle the unload.
		/// </summary>
		/// <param name="m"></param>
		private void RequestUnload(MagazineBehaviour m)
		{
			Logger.LogTrace("Unloading", Category.Firearms);
			if (m != null)
			{
				//PlayerManager.LocalPlayerScript.playerNetworkActions.CmdDropItemNotInUISlot(m.gameObject);
				PlayerManager.LocalPlayerScript.weaponNetworkActions.CmdUnloadWeapon(gameObject);
			}
		}

		#endregion


		#region Weapon Sounds

		private void OutOfAmmoSFX()
		{
			SoundManager.PlayNetworkedAtPos("OutOfAmmoAlarm", transform.position, sourceObj: serverHolder);
		}

		public void PlayEmptySFX()
		{
			SoundManager.PlayNetworkedAtPos("EmptyGunClick", transform.position, sourceObj: serverHolder);
		}

		#endregion

		#region Weapon Network Supporting Methods

		/// <summary>
		/// Applies recoil to calcuate the final direction of the shot
		/// </summary>
		/// <param name="target">normalized target vector</param>
		/// <returns>final position after applying recoil</returns>
		private Vector2 ApplyRecoil(Vector2 target)
		{
			float angle = Mathf.Atan2(target.y, target.x) * Mathf.Rad2Deg;
			float angleVariance = MagSyncedRandomFloat(-CurrentRecoilVariance, CurrentRecoilVariance);
			Logger.LogTraceFormat("angleVariance {0}", Category.Firearms, angleVariance);
			float newAngle = angle * Mathf.Deg2Rad + angleVariance;
			Vector2 vec2 = new Vector2(Mathf.Cos(newAngle), Mathf.Sin(newAngle)).normalized;
			return vec2;
		}

		private float MagSyncedRandomFloat(float min, float max)
		{
			return (float)(CurrentMagazine.CurrentRNG() * (max - min) + min);
		}

		private void AppendRecoil()
		{
			if (CurrentRecoilVariance < MaxRecoilVariance)
			{
				//get a random recoil
				float randRecoil = MagSyncedRandomFloat(CurrentRecoilVariance, MaxRecoilVariance);
				Logger.LogTraceFormat("randRecoil {0}", Category.Firearms, randRecoil);
				CurrentRecoilVariance += randRecoil;
				//make sure the recoil is not too high
				if (CurrentRecoilVariance > MaxRecoilVariance)
				{
					CurrentRecoilVariance = MaxRecoilVariance;
				}
			}
		}

		#endregion
	}

	/// <summary>
	/// Represents a shot that has been queued up to fire when the weapon is next able to. Only used on server side.
	/// </summary>
	struct QueuedShot
	{
		public readonly GameObject shooter;
		public readonly Vector2 finalDirection;
		public readonly BodyPartType damageZone;
		public readonly bool isSuicide;

		public QueuedShot(GameObject shooter, Vector2 finalDirection, BodyPartType damageZone, bool isSuicide)
		{
			this.shooter = shooter;
			this.finalDirection = finalDirection;
			this.damageZone = damageZone;
			this.isSuicide = isSuicide;
		}
	}

	/// <summary>
	/// Generic weapon types
	/// </summary>
	public enum WeaponType
	{
		SemiAutomatic = 0,
		FullyAutomatic = 1,
		Burst = 2
	}

#if UNITY_EDITOR
	[CustomEditor(typeof(Gun), true)]
	public class GunEditor : Editor
	{
		public override void OnInspectorGUI()
		{
			DrawDefaultInspector(); // for other non-HideInInspector fields

			Gun script = (Gun)target;

			script.MagInternal = EditorGUILayout.Toggle("Magazine Internal", script.MagInternal);

			if (!script.MagInternal) // show exclusive fields depending on whether magazine is internal
			{
				script.SmartGun = EditorGUILayout.Toggle("Smart Gun", script.SmartGun);
			}

			if (script.WeaponType == WeaponType.Burst)
			{
				script.burstCooldown = EditorGUILayout.DoubleField("Burst Cooldown", script.burstCooldown);
				script.burstCount = EditorGUILayout.DoubleField("Burst Count", script.burstCount);
			}
		}
	}
#endif
}

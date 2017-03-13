﻿using UnityEngine;
using System.Collections;
using PlayGroup;
using Matrix;
using Sprites;

public class DoorController: Photon.PunBehaviour
{
	public AudioSource openSFX;
	public AudioSource closeSFX;
	private Animator animator;
	private BoxCollider2D boxColl;
	private RegisterTile registerTile;
	public bool usingAnimator = true;
	public bool isWindowedDoor = false;
	[HideInInspector]
	public bool isPerformingAction = false;
	public DoorType doorType;
	private bool isOpened = false;
	public float maxTimeOpen = 5;
	private float timeOpen = 0;
	private HorizontalDoorAnimator horizontalAnim;

	void Start()
	{
		animator = gameObject.GetComponent<Animator>();
		boxColl = gameObject.GetComponent<BoxCollider2D>();
		registerTile = gameObject.GetComponent<RegisterTile>();
		if (!usingAnimator) {
			//TODO later change usingAnimator to horizontal checker (when vertical doors are done)
			horizontalAnim = gameObject.AddComponent<HorizontalDoorAnimator>();
		}
	}
		
	public void BoxCollToggleOn()
	{
		registerTile.UpdateTileType(TileType.Door);
		boxColl.enabled = true;
	}

	public void BoxCollToggleOff()
	{
		registerTile.UpdateTileType(TileType.None);
		boxColl.enabled = false;
	}

	private IEnumerator _WaitUntilClose()
	{
		// After the door opens, wait until it's supposed to close.
		yield return new WaitForSeconds(maxTimeOpen);
		TryClose();
	}

	//3d sounds
	public void PlayOpenSound()
	{
		if (openSFX != null)
			openSFX.Play();
	}

	public void PlayCloseSound()
	{
		if (closeSFX != null)
			closeSFX.Play();
	}

	public void PlayCloseSFXshort()
	{
		if (closeSFX != null) {
			closeSFX.time = 0.6f;
			closeSFX.Play();
		}
	}

	void OnMouseDown()
	{
		if (PlayerManager.PlayerInReach(transform)) {
			if (isOpened) {
				photonView.RPC("Close", PhotonTargets.All);
			} else {
				photonView.RPC("Open", PhotonTargets.All);
			}
		}
	}

	public void TryOpen()
	{
		if (PhotonNetwork.connectedAndReady) {
			photonView.RPC("Open", PhotonTargets.All, null);
		} else {
			Open();
		}
	}

	public void TryClose()
	{
		if (PhotonNetwork.connectedAndReady) {
			photonView.RPC("Close", PhotonTargets.All, null);
		} else {
			Close();
		}
	}

	[PunRPC]
	public void Open()
	{
		isOpened = true;
		StartCoroutine(_WaitUntilClose());

		if (usingAnimator) {
			animator.SetBool("open", true);
		} else {
			if (!isPerformingAction) {
				horizontalAnim.OpenDoor();
			}
		}
	}

	[PunRPC]
	public void Close()
	{
		isOpened = false;
		if (usingAnimator) {
			animator.SetBool("open", false);
		} else {
			if (!isPerformingAction) {
				horizontalAnim.CloseDoor();
			}
		}
	}
}

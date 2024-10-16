using System.Collections;
using System.Collections.Generic;
using HealthV2;
using UI.Action;
using UnityEngine;

public class BodyPartControllableLight : BodyPartFunctionality
{

	private ItemLightControl ItemLightControl;
	private ItemActionButton actionButton;

	[SerializeField] private SpriteDataSO baseEyeSprite;
	[SerializeField] private SpriteDataSO litEyeSprite;


	private void Awake()
	{
		ItemLightControl = GetComponent<ItemLightControl>();
		actionButton = GetComponent<ItemActionButton>();
	}

	public List<ColourAndSize> IntensityColours = new List<ColourAndSize>();

	[System.Serializable]
	public struct ColourAndSize
	{
		public Color Color;
		public int Size;
	}

	public int Index = 0;

	private void OnEnable()
	{
		if (actionButton)
		{
			actionButton.ServerActionClicked += ToggleLight;
		}
	}

	private void OnDisable()
	{
		if (actionButton)
		{
			actionButton.ServerActionClicked -= ToggleLight;
		}
	}


	private void ToggleLight()
	{
		Index++;
		var ColourIndex = Index - 1;

		if (ColourIndex < IntensityColours.Count)
		{
			ItemLightControl.Toggle(true);
			ItemLightControl.SetColor(IntensityColours[ColourIndex].Color);
			ItemLightControl.SetSize(IntensityColours[ColourIndex].Size);
			RelatedPart.SpritePrefab.UpdateSpritesForImplant(RelatedPart, Systems.Clothing.ClothingHideFlags.HIDE_NONE, litEyeSprite, RelatedPart.SpritePrefab.SpriteOrder);
			
		}
		else
		{
			Index = 0;
			ItemLightControl.Toggle(false);
			RelatedPart.SpritePrefab.UpdateSpritesForImplant(RelatedPart, Systems.Clothing.ClothingHideFlags.HIDE_NONE, baseEyeSprite, RelatedPart.SpritePrefab.SpriteOrder);
		}
	}

}

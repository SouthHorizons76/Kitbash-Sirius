// Stubs for the MonoBehaviours whose serialised data we read out of game bundles.
//
// AssetBundles resolve MonoBehaviours by assembly + namespace + class name, so
// these must stay in the GLOBAL namespace inside Assembly-CSharp. Do not add an
// .asmdef under Assets/ and do not wrap these in a namespace, or the bundle data
// silently deserialises to defaults.
//
// Field names and types mirror the originals exactly. Do not rename.

using System;
using UnityEngine;

public class ModPlacer : MonoBehaviour
{
    public Vector3 ModPosition = Vector3.zero;
    public Vector3 ModRotation = Vector3.zero;
    public Vector3 ModScale = Vector3.one;
}

// The weapon container's reference to its in-game model, its animated hands rig
// and that rig's controller; the rest of the original's fields are ignored on load.
public class WeaponPrefab : MonoBehaviour
{
    public GameObject _weaponObject;
    public GameObject _objectInstance;
    public RuntimeAnimatorController _originalAnimatorController;
    public Avatar _avatar;
}

// Where a hand holds a weapon or a part: the left hand goes to the build's grip.
public class GripPose : MonoBehaviour
{
    public enum EGripType { Common, Alternative, UnderbarrelWeapon }
    public enum EHand { Left, Right }

    public EHand Hand;
    public EGripType GripType;
}

// The character's named bones: weapon roots, palms, and where each carried
// weapon hangs.
public class PlayerBones : MonoBehaviour
{
    public Transform Weapon_Root_Third;
    public Transform Weapon_Root_Anim;
    public Transform Neck;
    public Transform[] Shoulders;
    public Transform[] Upperarms;
    public Transform[] Forearms;
    public Transform LeftPalm;
    public Transform RightPalm;
    public Transform RootJoint;
    public Transform WeaponMeleeRoot;
    public Transform WeaponPistolRoot;
    public Transform WeaponRifleRoot;
    public Transform HolsterPrimary;
    public Transform HolsterPrimaryAlternative;
    public Transform HolsterSecondary;
    public Transform HolsterSecondaryAlternative;
    public Transform ScabbardTagillaMelee;
    public Transform HolsterPistol;
    public Transform LeftLegHolsterPistol;
    public Transform[] BendGoals;
    public EFT.BifacialTransform WeaponRoot;
    public EFT.BifacialTransform Ribcage;
    public EFT.BifacialTransform Head;
    public EFT.BifacialTransform LeftShoulder;
    public EFT.BifacialTransform RightShoulder;
    public EFT.BifacialTransform Pelvis;
    public EFT.BifacialTransform Spine3;
    // Where a third-person weapon root sits: Weapon_Root_Third turned by DeltaRotation,
    // moved by Offset in its frame.
    public Vector3 Offset;
    public Quaternion DeltaRotation;
}

namespace EFT
{
    [Serializable]
    public class BifacialTransform
    {
        public Transform Original;
    }
}

public enum EAutoFoldableSightMode
{
    On,
    Off
}

public class AutoFoldableSight : MonoBehaviour
{
    public EAutoFoldableSightMode Mode;
}

namespace EFT.Visual
{
    // Folds a stock on a non-animated item (icons, loot): each bone's localRotation
    // is FoldedRotation while the item's FoldableComponent is folded.
    public class FoldableStockView : MonoBehaviour
    {
        [Serializable]
        public class BonePosition
        {
            public string BoneName;
            public Transform Bone;
            public Quaternion FoldedRotation;
            public Quaternion UnfoldedRotation;
        }

        [SerializeField] public BonePosition[] _bonePositions;
    }

    // As FoldableStockView, but each bone's localPosition is RetractedPosition.
    public class RetractableStockView : MonoBehaviour
    {
        [Serializable]
        public struct BonePosition
        {
            public string BoneName;
            public Transform Bone;
            public Vector3 RetractedPosition;
            public Vector3 NormalPosition;
        }

        [SerializeField] public BonePosition[] _bonePositions;
    }
}

public class PreviewPivot : MonoBehaviour
{
    [Serializable]
    public class IconSettings
    {
        public Vector3 position;
        public bool hasOffset;
        public Quaternion rotation;
        public float boundsScale = 1f;
        public float perspective = 15f;
        public bool orthographic;
        public float orthographicSize = 10f;
        public Sprite overrideIcon;
    }

    public Vector3 pivotPosition = Vector3.zero;
    public Quaternion pivotRotation = Quaternion.identity;
    public Vector3 scale = Vector3.one;
    public Vector3 SpawnPosition = Vector3.zero;
    public IconSettings Icon = new IconSettings();
}

// A worn item's prefab: the icon shows LootPrefab, DressPrefab off
// (ObjectsFactory.CreateCleanLootPrefab -> DressItem.EnableLoot(true)).
public class DressItem : MonoBehaviour
{
    public GameObject LootPrefab;
    public GameObject DressPrefab;
}

// HelmetDress's hinge drive. Set(on, initial: true) snaps to Forward at its end
// (on) or Backward at 0 (off).
public class HysteresisFilter : MonoBehaviour
{
    public AnimationCurve Forward;
    public AnimationCurve Backward;
}

// NightVisionMount's hinge: Set(on, initial: true) snaps RotatedTransform's
// localRotation to OnRotation or OffRotation.
public class CurveRotator : MonoBehaviour
{
    public Transform RotatedTransform;
    public float RotationSpeed;
    public Quaternion OffRotation;
    public Quaternion OnRotation;
    public AnimationCurve AnimationCurve;
}

namespace EFT.Visual
{
    // A helmet's face shield hinge: HingeTransform.localRotation is
    // Lerp(OffRotation, OnRotation, Filter value), on unless a face shield in the
    // helmet is toggled off.
    public class HelmetDress : MonoBehaviour
    {
        public Quaternion OffRotation;
        public Quaternion OnRotation;
        public Transform HingeTransform;
        public HysteresisFilter Filter;
    }

    // An NVG mount's hinge: down while the night vision device under it is on.
    public class NightVisionMount : MonoBehaviour
    {
        [SerializeField] public CurveRotator _rotator;
    }
}

// Character skinning. A suit, head or worn item carries skinned meshes whose bones
// are named, not referenced: binding looks each name up in the character's
// Skeleton.
namespace Diz.Skinning
{
    // The character's bones by name.
    public class Skeleton : MonoBehaviour, ISerializationCallbackReceiver
    {
        public System.Collections.Generic.Dictionary<string, Transform> Bones =
            new System.Collections.Generic.Dictionary<string, Transform>();
        [SerializeField] public System.Collections.Generic.List<string> _keys = new System.Collections.Generic.List<string>();
        [SerializeField] public System.Collections.Generic.List<Transform> _values = new System.Collections.Generic.List<Transform>();

        public void OnBeforeSerialize() { }

        public void OnAfterDeserialize()
        {
            Bones.Clear();
            for (int i = 0; i < _keys.Count && i < _values.Count; i++) Bones[_keys[i]] = _values[i];
        }
    }

    public abstract class AbstractSkin : MonoBehaviour { }

    // One skinned mesh: rootBone and bones are the skeleton's bones of these names.
    public class Skin : AbstractSkin
    {
        public SkinnedMeshRenderer _skinnedMeshRenderer;
        public string[] _bonePaths;
        public string _rootBonePath;
    }
}

namespace EFT.Visual
{
    // A suit part (head, body, feet): its levels of detail, each a skin.
    public class LoddedSkin : MonoBehaviour
    {
        public Diz.Skinning.AbstractSkin[] _lods;
    }

    // A worn item skinned to the character (armor, rigs, backpacks).
    public class SkinDress : MonoBehaviour
    {
        public Diz.Skinning.AbstractSkin[] _lods;
    }

    // A body's skin whose mesh depends on what is worn: _armor under an armor vest
    // that is not vest-like, _vest under a vest-like armor, a rig or a backpack,
    // else _base.
    public class TorsoSkin : Diz.Skinning.AbstractSkin
    {
        public Diz.Skinning.Skin _skin;
        public Mesh _base;
        public Mesh _armor;
        public Mesh _vest;
    }

    // A skin plus data choosing its mesh by what is worn.
    // A head: FaceCover under a face cover that is not a half mask, else Base.
    public class HeadSkin : Diz.Skinning.AbstractSkin { public Diz.Skinning.Skin Skin; public HeadSkinData Data; }
    // A rig: Armor under an armor vest (or a suit with integrated armor), else Body.
    public class VestSkin : Diz.Skinning.AbstractSkin { public Diz.Skinning.Skin Skin; public VestSkinData Data; }
    // A backpack: ArmorVest under both, Armor or Vest under one, else Body.
    public class BackpackSkin : Diz.Skinning.AbstractSkin { public Diz.Skinning.Skin Skin; public BackpackSkinData Data; }

    [Serializable] public struct HeadSkinData { public Mesh Base; public Mesh FaceCover; }
    [Serializable] public struct VestSkinData { public Mesh Body; public Mesh Armor; }
    [Serializable] public struct BackpackSkinData { public Mesh Body; public Mesh Armor; public Mesh Vest; public Mesh ArmorVest; }

    // On pants with a pistol holster: the leg it is on, and the pose the holster bone
    // takes. Without it no pistol is drawn.
    public class LegsView : MonoBehaviour
    {
        public bool _isRightLeg = true;
        public Transform _holster;
    }

    // On an armor vest's dress: the body suit takes its vest mesh, not its armor mesh.
    public class VestlikeArmor : MonoBehaviour { }

    // A suit with a hood: _hooded objects shown and _notHooded hidden while hooded,
    // which is when no headwear is worn and no face cover takes its place.
    public class HoodedDress : MonoBehaviour
    {
        public GameObject[] _hooded;
        public GameObject[] _notHooded;
    }
}

// What gear clips away: a worn item of one of these types takes
// its Custom mesh while the equipment's rules include its type.
[Flags]
public enum EClippingCustoms : byte
{
    FaceCover = 1,
    Visor = 2,
    Hat = 4,
    Headphones = 8,
    Helmet = 0x10,
}

[Serializable] public struct CustomItemData { public Mesh Base; public Mesh Custom; }

// A skinned worn item with a clipped variant.
public class CustomItemSkin : Diz.Skinning.AbstractSkin
{
    public Diz.Skinning.Skin Skin;
    public CustomItemData Data;
    public EClippingCustoms _itemType;
}

// The same for a worn item on a bone.
public class CustomItem : MonoBehaviour
{
    public MeshFilter _meshFilter;
    public CustomItemData _data;
    public EClippingCustoms _itemType;
}

// On a suit: rules it adds to the equipment's.
public class ClippingRuleChanger : MonoBehaviour
{
    public EClippingCustoms _clippingCustoms;
}

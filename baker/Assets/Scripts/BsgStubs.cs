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

// The weapon container's reference to its in-game model; the rest of the
// original's fields are ignored on load.
public class WeaponPrefab : MonoBehaviour
{
    public GameObject _weaponObject;
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

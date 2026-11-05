// Records what the game's icon of a lone part, and of a folded item, depends on:
//
//   - PreviewPivot.Icon of every template that can hold a part: the icon camera
//     sets the loot prefab's root to Icon.rotation (ItemIconCreator.PoseModelByPivot),
//     so a part drawn on its own is at that rotation, not at its mount pose.
//   - FoldableStockView / RetractableStockView of every template: a non-animated
//     item (icons) sets each listed bone's local rotation / position to its folded
//     value while the item is folded (EFT.Visual.FoldableStockView.Init).
//
// Writes data/icon-poses.json. Menu: Kitbash! / Inspect Icon Poses

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT.Visual;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using static KitbashConfig;

public static class KitbashIconPoses
{
    [MenuItem("Kitbash!/Inspect Icon Poses")]
    public static void Run()
    {
        var slots = JObject.Parse(File.ReadAllText(Path.Combine(DataDir, "slots.json")));
        var targets = slots.Properties().ToList();
        var outMods = new JObject();
        int pivots = 0, folds = 0, hinged = 0, failed = 0;
        try
        {
            for (int start = 0; start < targets.Count; start += ChunkSize)
            {
                using (var loader = new BundleLoader(BundleRoot, BundlesJson))
                {
                    int end = Math.Min(start + ChunkSize, targets.Count);
                    for (int i = start; i < end; i++)
                    {
                        var p = targets[i];
                        if (EditorUtility.DisplayCancelableProgressBar(
                                "Kitbash! icon poses", $"{i + 1}/{targets.Count}", (float)i / targets.Count))
                            throw new OperationCanceledException();
                        var ids = ((JArray)p.Value["slots"]).Select(x => (string)x["id"]).ToList();
                        BundleLoader.Item item;
                        try { item = loader.LoadItem((string)p.Value["prefab"], ids); }
                        catch (Exception e) { Debug.LogWarning($"{p.Name}: {e.Message}"); failed++; continue; }
                        if (!item.Ok) { failed++; continue; }

                        var entry = new JObject { ["name"] = (string)p.Value["name"], ["model"] = item.ModelName };
                        var root = item.Container.transform;
                        var pivot = item.Container.GetComponent<PreviewPivot>();
                        if (pivot != null)
                        {
                            pivots++;
                            entry["iconRotation"] = Q(pivot.Icon.rotation);
                            entry["boundsScale"] = pivot.Icon.boundsScale;
                            entry["overrideIcon"] = pivot.Icon.overrideIcon != null;
                        }
                        // Where the baked model sits in the loot prefab the icon poses.
                        var m = item.Model.transform;
                        if (m == root) entry["modelInPrefab"] = "root";
                        else if (m.IsChildOf(root))
                        {
                            entry["modelInPrefab"] = "child";
                            entry["modelRotation"] = Q(Quaternion.Inverse(root.rotation) * m.rotation);
                            var pos = root.InverseTransformPoint(m.position);
                            entry["modelPosition"] = new JArray(pos.x, pos.y, pos.z);
                        }
                        else entry["modelInPrefab"] = "separate";

                        var fold = new JArray();
                        foreach (var go in new[] { item.Container, item.Model }.Distinct())
                        {
                            foreach (var v in go.GetComponentsInChildren<FoldableStockView>(true))
                                foreach (var b in v._bonePositions ?? new FoldableStockView.BonePosition[0])
                                    fold.Add(new JObject
                                    {
                                        ["kind"] = "rotate",
                                        ["on"] = go == item.Model ? "model" : "container",
                                        ["view"] = v.name,
                                        ["bone"] = b.Bone != null ? b.Bone.name : b.BoneName,
                                        ["folded"] = Q(b.FoldedRotation),
                                        ["unfolded"] = Q(b.UnfoldedRotation),
                                        ["foundInModel"] = BundleLoader.FindBone(m, b.Bone != null ? b.Bone.name : b.BoneName) != null,
                                    });
                            foreach (var v in go.GetComponentsInChildren<RetractableStockView>(true))
                                foreach (var b in v._bonePositions ?? new RetractableStockView.BonePosition[0])
                                    fold.Add(new JObject
                                    {
                                        ["kind"] = "move",
                                        ["on"] = go == item.Model ? "model" : "container",
                                        ["view"] = v.name,
                                        ["bone"] = b.Bone != null ? b.Bone.name : b.BoneName,
                                        ["folded"] = new JArray(b.RetractedPosition.x, b.RetractedPosition.y, b.RetractedPosition.z),
                                        ["unfolded"] = new JArray(b.NormalPosition.x, b.NormalPosition.y, b.NormalPosition.z),
                                        ["foundInModel"] = BundleLoader.FindBone(m, b.Bone != null ? b.Bone.name : b.BoneName) != null,
                                    });
                        }
                        var hinges = Hinges(item.Model, ids);
                        if (hinges.Count > 0) { entry["hinges"] = hinges; hinged++; }
                        if (fold.Count > 0)
                        {
                            entry["fold"] = fold;
                            entry["foldedBones"] = FoldedBones(item.Model, fold, ids);
                            folds++;
                        }
                        outMods[p.Name] = entry;
                    }
                }
            }
        }
        finally { EditorUtility.ClearProgressBar(); }

        // A fold view finds each bone by name, width first, through the whole
        // assembled item (TransformTools.FindTransformRecursiveWidthFirst), so a bone
        // missing from the folding item's own model lies in a part attached to it
        // (an AKMS or Mk 16 stock). Find the parts that hold such bones, and pose
        // their slot bones folded, per folding template.
        var partBones = new Dictionary<string, JArray>();   // folding tpl -> its fold entries in parts
        foreach (var o in outMods.Properties())
        {
            if ((bool?)slots[o.Name]["foldable"] != true || !(o.Value["fold"] is JArray fold)) continue;
            var missing = new JArray(fold.Where(f => !(bool)f["foundInModel"]).Select(f => f.DeepClone()));
            if (missing.Count > 0) partBones[o.Name] = missing;
        }
        var wanted = new HashSet<string>(partBones.Values.SelectMany(a => a.Select(f => (string)f["bone"])),
                                         StringComparer.OrdinalIgnoreCase);
        int parts = 0;
        var mods = targets.Where(p => !(bool)p.Value["isWeapon"] && outMods[p.Name] != null).ToList();
        try
        {
            for (int start = 0; start < mods.Count && wanted.Count > 0; start += ChunkSize)
            {
                using (var loader = new BundleLoader(BundleRoot, BundlesJson))
                {
                    int end = Math.Min(start + ChunkSize, mods.Count);
                    for (int i = start; i < end; i++)
                    {
                        var p = mods[i];
                        if (EditorUtility.DisplayCancelableProgressBar(
                                "Kitbash! fold parts", $"{i + 1}/{mods.Count}", (float)i / mods.Count))
                            throw new OperationCanceledException();
                        var ids = ((JArray)p.Value["slots"]).Select(x => (string)x["id"]).ToList();
                        BundleLoader.Item item;
                        try { item = loader.LoadItem((string)p.Value["prefab"], ids); }
                        catch { continue; }
                        if (!item.Ok) continue;
                        var names = new HashSet<string>(item.Model.GetComponentsInChildren<Transform>(true)
                            .Select(t => t.name).Where(wanted.Contains), StringComparer.OrdinalIgnoreCase);
                        if (names.Count == 0) continue;
                        var partFolds = new JObject();
                        foreach (var kv in partBones)
                        {
                            var here = new JArray(kv.Value.Where(f => names.Contains((string)f["bone"])).Select(f => f.DeepClone()));
                            if (here.Count == 0) continue;
                            partFolds[kv.Key] = new JObject
                            {
                                ["fold"] = here,
                                ["foldedBones"] = FoldedBones(item.Model, here, ids),
                            };
                        }
                        if (partFolds.Count > 0) { outMods[p.Name]["partFolds"] = partFolds; parts++; }
                    }
                }
            }
        }
        finally { EditorUtility.ClearProgressBar(); }

        var outPath = Path.Combine(DataDir, "icon-poses.json");
        File.WriteAllText(outPath, new JObject
        {
            ["gameVersion"] = GameVersion(),
            ["templates"] = outMods,
        }.ToString(Formatting.Indented));
        Debug.Log($"Kitbash! icon poses: {outMods.Count} templates, PreviewPivot on {pivots}, " +
                  $"fold views on {folds}, parts folded by them {parts}, hinges on {hinged}, {failed} not loaded -> {outPath}");
    }

    /// The model's hinges: HelmetDress turns HingeTransform to
    /// Lerp(OffRotation, OnRotation, x), x = Filter.Forward at its end (on) or
    /// Filter.Backward at 0 (off) (HysteresisFilter.Set, initial); NightVisionMount
    /// turns its CurveRotator's RotatedTransform to OnRotation or OffRotation
    /// (CurveRotator.Set, initial). Each with the slot bones as each pose leaves them.
    static JArray Hinges(GameObject model, List<string> ids)
    {
        var root = model.transform;
        var hinges = new JArray();
        void Add(string kind, Transform bone, Quaternion on, Quaternion off)
        {
            if (bone == null || !bone.IsChildOf(root)) { Debug.LogWarning($"{model.name}: {kind} hinge outside the model"); return; }
            var path = PathOf(root, bone);
            hinges.Add(new JObject
            {
                ["kind"] = kind,
                ["path"] = path,
                ["on"] = Q(on),
                ["off"] = Q(off),
                ["prefab"] = Q(bone.localRotation),
                ["onBones"] = PosedBones(model, path, on, ids),
                ["offBones"] = PosedBones(model, path, off, ids),
            });
        }
        foreach (var h in model.GetComponentsInChildren<HelmetDress>(true))
        {
            float x1 = h.Filter != null && h.Filter.Forward != null && h.Filter.Forward.length > 0
                ? h.Filter.Forward.Evaluate(h.Filter.Forward.keys[h.Filter.Forward.length - 1].time) : 1f;
            float x0 = h.Filter != null && h.Filter.Backward != null && h.Filter.Backward.length > 0
                ? h.Filter.Backward.Evaluate(0f) : 0f;
            Add("faceShield", h.HingeTransform, Quaternion.Lerp(h.OffRotation, h.OnRotation, x1),
                Quaternion.Lerp(h.OffRotation, h.OnRotation, x0));
        }
        foreach (var m in model.GetComponentsInChildren<NightVisionMount>(true))
        {
            var r = m._rotator;
            if (r == null) { Debug.LogWarning($"{model.name}: NightVisionMount without a rotator"); continue; }
            Add("nightVision", r.RotatedTransform, r.OnRotation, r.OffRotation);
        }
        return hinges;
    }

    /// A transform's path below root, as Transform.Find takes it.
    static string PathOf(Transform root, Transform t)
    {
        var parts = new List<string>();
        for (var x = t; x != root; x = x.parent) parts.Add(x.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// Each slot bone's pose in the model root's local space with the transform at
    /// path set to localRotation rot, in the form of data/bones-all.json.
    static JObject PosedBones(GameObject model, string path, Quaternion rot, List<string> ids)
    {
        var go = UnityEngine.Object.Instantiate(model);
        try
        {
            var root = go.transform;
            root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            if (path.Length == 0) root.localRotation = rot;
            else root.Find(path).localRotation = rot;
            var bones = new JObject();
            foreach (var id in ids)
            {
                var bone = BundleLoader.FindBone(root, id);
                if (bone == null) continue;
                var r = bone.rotation;
                var pos = bone.position;
                bones[id] = new JObject
                {
                    ["rot"] = new JArray(r.x, r.y, r.z, r.w),
                    ["pos"] = new JArray(pos.x, pos.y, pos.z),
                };
            }
            return bones;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    /// Each slot bone's pose in the model root's local space with the fold applied,
    /// in the form of data/bones-all.json.
    static JObject FoldedBones(GameObject model, JArray fold, List<string> ids)
    {
        var go = UnityEngine.Object.Instantiate(model);
        try
        {
            var root = go.transform;
            root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            foreach (var f in fold)
            {
                var bone = BundleLoader.FindBone(root, (string)f["bone"]);
                if (bone == null) continue;
                var v = (JArray)f["folded"];
                if ((string)f["kind"] == "rotate")
                    bone.localRotation = new Quaternion((float)v[0], (float)v[1], (float)v[2], (float)v[3]);
                else bone.localPosition = new Vector3((float)v[0], (float)v[1], (float)v[2]);
            }
            var bones = new JObject();
            foreach (var id in ids)
            {
                var bone = BundleLoader.FindBone(root, id);
                if (bone == null) continue;
                var rot = bone.rotation;
                var pos = bone.position;
                bones[id] = new JObject
                {
                    ["rot"] = new JArray(rot.x, rot.y, rot.z, rot.w),
                    ["pos"] = new JArray(pos.x, pos.y, pos.z),
                };
            }
            return bones;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    static JArray Q(Quaternion q)
    {
        if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        return new JArray(Math.Round(q.x, 7), Math.Round(q.y, 7), Math.Round(q.z, 7), Math.Round(q.w, 7));
    }
}

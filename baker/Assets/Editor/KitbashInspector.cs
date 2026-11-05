// One-shot probe. Answers the two questions that decide whether sprite
// compositing is viable:
//
//   1. How many distinct PreviewPivot.Icon.rotation values across the weapons.
//   2. Whether ModPlacer / PreviewPivot bind to bundle data at all. If they
//      deserialise to defaults, mount offsets are lost and the approach is dead.
//
// Menu: Kitbash! / Inspect Prefabs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using static KitbashConfig;

public static class KitbashInspector
{
    [MenuItem("Kitbash!/Inspect Prefabs")]
    public static void Run()
    {
        var slots = JObject.Parse(File.ReadAllText(Path.Combine(DataDir, "slots.json")));
        var weapons = slots.Properties().Where(p => (bool)p.Value["isWeapon"]).ToList();
        var mods = slots.Properties().Where(p => !(bool)p.Value["isWeapon"]).ToList();

        var report = new JObject();
        try
        {
            var rotations = new Dictionary<string, JArray>();
            int pivots = 0, bonesOk = 0, bonesMissing = 0;

            var weaponOut = Walk(weapons, "weapon", (p, item, entry) =>
            {
                entry["model"] = item.ModelName;
                entry["fromDep"] = item.FromDependency;
                var pivot = item.Container.GetComponentInChildren<PreviewPivot>(true)
                            ?? item.Model.GetComponentInChildren<PreviewPivot>(true);
                if (pivot == null) entry["error"] = "no PreviewPivot";
                else
                {
                    pivots++;
                    var r = pivot.Icon.rotation;
                    entry["rotation"] = new JArray(r.x, r.y, r.z, r.w);
                    entry["boundsScale"] = pivot.Icon.boundsScale;
                    entry["hasOffset"] = pivot.Icon.hasOffset;
                    entry["overrideIcon"] = pivot.Icon.overrideIcon != null;

                    var key = RotationKey(r);
                    if (!rotations.TryGetValue(key, out var g)) rotations[key] = g = new JArray();
                    g.Add(p.Name);
                }

                var boneMap = new JObject();
                foreach (var s in (JArray)p.Value["slots"])
                {
                    var id = (string)s["id"];
                    bool found = BundleLoader.FindBone(item.Model.transform, id) != null;
                    boneMap[id] = found;
                    if (found) bonesOk++; else bonesMissing++;
                }
                entry["bones"] = boneMap;
            });

            int placers = 0, nonDefault = 0;
            var modOut = Walk(mods, "mod", (p, item, entry) =>
            {
                entry["model"] = item.ModelName;
                entry["fromDep"] = item.FromDependency;
                entry["ownSlots"] = ((JArray)p.Value["slots"]).Count;
                // Unity's == null, not ??: a missing component is a fake-null object.
                var mp = item.Container.GetComponent<ModPlacer>();
                if (mp == null) mp = item.Model.GetComponent<ModPlacer>();
                if (mp == null) { entry["modPlacer"] = false; return; }
                placers++;
                bool nd = mp.ModPosition != Vector3.zero
                       || mp.ModRotation != Vector3.zero
                       || mp.ModScale != Vector3.one;
                if (nd) nonDefault++;
                entry["modPlacer"] = true;
                entry["nonDefault"] = nd;
                entry["pos"] = new JArray(mp.ModPosition.x, mp.ModPosition.y, mp.ModPosition.z);
                entry["rot"] = new JArray(mp.ModRotation.x, mp.ModRotation.y, mp.ModRotation.z);
                entry["scale"] = new JArray(mp.ModScale.x, mp.ModScale.y, mp.ModScale.z);
            });

            report["weapons"] = weaponOut;
            report["mods"] = modOut;
            report["weaponSummary"] = new JObject
            {
                ["inspected"] = weapons.Count,
                ["withPreviewPivot"] = pivots,
                ["bonesResolved"] = bonesOk,
                ["bonesMissing"] = bonesMissing,
            };
            report["modSummary"] = new JObject
            {
                ["inspected"] = mods.Count,
                ["withModPlacer"] = placers,
                ["nonDefaultValues"] = nonDefault,
            };
            var groups = new JObject();
            foreach (var kv in rotations.OrderByDescending(k => k.Value.Count)) groups[kv.Key] = kv.Value;
            report["rotationGroups"] = groups;
            report["distinctRotations"] = rotations.Count;

            var outPath = Path.Combine(DataDir, "inspect.json");
            File.WriteAllText(outPath, report.ToString(Formatting.Indented));

            Debug.Log(
                "Kitbash! inspect\n" +
                $"  weapons {weapons.Count}, PreviewPivot on {pivots}, distinct rotations {rotations.Count}\n" +
                $"  slot bones resolved {bonesOk}, missing {bonesMissing}\n" +
                $"  mods {mods.Count}, ModPlacer on {placers}, non-default {nonDefault}\n" +
                $"  -> {outPath}");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    /// Loads each prefab in chunks, disposing bundles between chunks, and hands
    /// the live prefab to <paramref name="probe"/>.
    static JArray Walk(List<JProperty> targets, string label,
                       Action<JProperty, BundleLoader.Item, JObject> probe)
    {
        var results = new JArray();
        for (int start = 0; start < targets.Count; start += ChunkSize)
        {
            using (var loader = new BundleLoader(BundleRoot, BundlesJson))
            {
                int end = Math.Min(start + ChunkSize, targets.Count);
                for (int i = start; i < end; i++)
                {
                    var p = targets[i];
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Kitbash!", $"{label} {i + 1}/{targets.Count}", (float)i / targets.Count))
                        throw new OperationCanceledException();

                    var entry = new JObject { ["tpl"] = p.Name, ["name"] = (string)p.Value["name"] };
                    var item = default(BundleLoader.Item);
                    var ids = ((JArray)p.Value["slots"]).Select(x => (string)x["id"]).ToList();
                    try { item = loader.LoadItem((string)p.Value["prefab"], ids); }
                    catch (Exception e) { entry["error"] = e.Message; }

                    if (!item.Ok) entry["error"] = entry["error"] ?? "prefab not loaded";
                    else probe(p, item, entry);
                    results.Add(entry);
                }
            }
        }
        return results;
    }

    /// q and -q are the same rotation; normalise sign, then quantise.
    static string RotationKey(Quaternion q)
    {
        if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        return $"{q.x:F3},{q.y:F3},{q.z:F3},{q.w:F3}";
    }
}

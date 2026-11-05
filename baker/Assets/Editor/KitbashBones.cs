// Measures each slot bone's pose in its model's local space.
//
// A part attached to a bone inherits that bone's world rotation. If a given slot id
// is oriented consistently across weapons, one sprite per part serves every weapon.
// If not, parts need a sprite per distinct bone orientation and the bake multiplies.
//
// Weapons only (177), so this is minutes, not tens of minutes.
//
// Menu: Kitbash! / Measure Bone Rotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using static KitbashConfig;

public static class KitbashBones
{
    [MenuItem("Kitbash!/Measure Bone Rotations (weapons)")]
    public static void RunWeapons() => Run(false);

    [MenuItem("Kitbash!/Measure Bone Rotations (weapons + mods)")]
    public static void RunAll() => Run(true);

    public static void Run(bool includeMods)
    {
        var slots = JObject.Parse(File.ReadAllText(Path.Combine(DataDir, "slots.json")));
        // Only templates that declare slots or hold rounds can host a bone.
        var weapons = slots.Properties()
            .Where(p => (bool)p.Value["isWeapon"] || (includeMods &&
                        (((JArray)p.Value["slots"]).Count > 0 || p.Value["cartridges"] != null)))
            .ToList();

        var perSlot = new Dictionary<string, List<string>>();   // slot id -> rotation keys
        var report = new JArray();

        try
        {
            for (int start = 0; start < weapons.Count; start += ChunkSize)
            {
                using (var loader = new BundleLoader(BundleRoot, BundlesJson))
                {
                    int end = Math.Min(start + ChunkSize, weapons.Count);
                    for (int i = start; i < end; i++)
                    {
                        var p = weapons[i];
                        if (EditorUtility.DisplayCancelableProgressBar(
                                "Kitbash! bones", $"{i + 1}/{weapons.Count}", (float)i / weapons.Count))
                            throw new OperationCanceledException();

                        var ids = ((JArray)p.Value["slots"]).Select(x => (string)x["id"]).ToList();
                        var item = loader.LoadItem((string)p.Value["prefab"], ids);
                        if (!item.Ok) continue;

                        var root = item.Model.transform;
                        var entry = new JObject { ["tpl"] = p.Name, ["name"] = (string)p.Value["name"] };
                        var bones = new JObject();

                        foreach (var id in ids)
                        {
                            var bone = BundleLoader.FindBone(root, id);
                            if (bone == null) continue;

                            // Pose in the model root's local space.
                            var rot = Quaternion.Inverse(root.rotation) * bone.rotation;
                            var pos = root.InverseTransformPoint(bone.position);
                            var key = RotKey(rot);

                            bones[id] = new JObject
                            {
                                ["rot"] = new JArray(rot.x, rot.y, rot.z, rot.w),
                                ["rotKey"] = key,
                                ["pos"] = new JArray(pos.x, pos.y, pos.z),
                            };
                            if (!perSlot.TryGetValue(id, out var list)) perSlot[id] = list = new List<string>();
                            list.Add(key);
                        }
                        entry["bones"] = bones;

                        if (p.Value["cartridges"] != null)
                        {
                            var rounds = new JArray();
                            foreach (var (n, t) in KitbashRig.RoundAnchors(root, (bool?)p.Value["belt"] == true))
                            {
                                var rot = Quaternion.Inverse(root.rotation) * t.rotation;
                                var pos = root.InverseTransformPoint(t.position);
                                rounds.Add(new JObject
                                {
                                    ["n"] = n,
                                    ["rot"] = new JArray(rot.x, rot.y, rot.z, rot.w),
                                    ["pos"] = new JArray(pos.x, pos.y, pos.z),
                                });
                            }
                            entry["rounds"] = rounds;
                        }
                        report.Add(entry);
                    }
                }
            }
        }
        finally { EditorUtility.ClearProgressBar(); }

        var summary = new JObject();
        int slotsTotal = 0, slotsUniform = 0;
        foreach (var kv in perSlot.OrderByDescending(k => k.Value.Count))
        {
            var groups = kv.Value.GroupBy(x => x).OrderByDescending(g => g.Count()).ToList();
            slotsTotal++;
            if (groups.Count == 1) slotsUniform++;
            summary[kv.Key] = new JObject
            {
                ["occurrences"] = kv.Value.Count,
                ["distinctRotations"] = groups.Count,
                ["top"] = new JArray(groups.Take(3).Select(g => (JToken)new JObject
                {
                    ["key"] = g.Key,
                    ["count"] = g.Count(),
                })),
            };
        }

        var outJson = new JObject { ["weapons"] = report, ["slotSummary"] = summary };
        var outPath = Path.Combine(DataDir, includeMods ? "bones-all.json" : "bones.json");
        File.WriteAllText(outPath, outJson.ToString(Formatting.Indented));

        Debug.Log($"Kitbash! bone rotations\n" +
                  $"  slot ids measured: {slotsTotal}\n" +
                  $"  uniform across all weapons: {slotsUniform}\n" +
                  $"  varying: {slotsTotal - slotsUniform}\n" +
                  $"  -> {outPath}");
    }

    /// Quantised so float noise does not split identical orientations.
    /// q and -q are the same rotation, so normalise sign first.
    static string RotKey(Quaternion q)
    {
        if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        return $"{q.x:F2},{q.y:F2},{q.z:F2},{q.w:F2}";
    }
}

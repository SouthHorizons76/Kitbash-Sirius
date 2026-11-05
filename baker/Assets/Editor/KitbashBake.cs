// Renders every (model, orientation) sprite in data/orientations.json and writes
// sprites/<model>/<n>.png plus data/sprites.manifest.json (see spec/manifest.md).
//
// Anchors, slot offsets and child orientations are measured from the rendered
// transforms, never recomputed. Re-running merges into the existing manifest.
//
// Menu: Kitbash! / Bake Sprites (subset)   data/orientations.subset.json only
//       Kitbash! / Bake Sprites (all)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using static KitbashConfig;
using Debug = UnityEngine.Debug;

public static class KitbashBake
{
    static string ManifestPath => Path.Combine(DataDir, "sprites.manifest.json");
    static string ReportPath => Path.Combine(DataDir, "bake-report.json");
    const int ManifestVersion = 5;

    [MenuItem("Kitbash!/Bake Sprites (subset)")]
    public static void RunSubset()
    {
        var subset = JObject.Parse(File.ReadAllText(Path.Combine(DataDir, "orientations.subset.json")));
        var only = ((JObject)subset["models"]).Properties()
            .ToDictionary(p => p.Name, p => p.Value.Select(i => (int)i).ToList());
        Bake(only);
    }

    [MenuItem("Kitbash!/Bake Sprites (all)")]
    public static void RunAll() => Bake(null);

    static void Bake(Dictionary<string, List<int>> only)
    {
        var sw = Stopwatch.StartNew();
        var slots = JObject.Parse(File.ReadAllText(Path.Combine(DataDir, "slots.json")));
        var orientsFile = JObject.Parse(File.ReadAllText(Path.Combine(DataDir, "orientations.json")));
        var orients = (JObject)orientsFile["models"];

        var manifest = LoadManifest();
        manifest["defaultAttach"] = orientsFile["defaultAttach"].DeepClone();
        manifest["attach"] = orientsFile["attach"].DeepClone();
        manifest["iconScale"] = orientsFile["iconScale"].DeepClone();
        if (orientsFile["pivots"] != null) manifest["pivots"] = orientsFile["pivots"].DeepClone();
        if (orientsFile["foldSets"] != null) manifest["foldSets"] = orientsFile["foldSets"].DeepClone();
        // Per model, the bones each fold set poses in it (scripts/orientations.py).
        var folds = orientsFile["folds"] as JObject ?? new JObject();
        // Per model, its face shield / NVG hinge (scripts/orientations.py).
        var hinges = orientsFile["modelHinges"] as JObject ?? new JObject();
        if (orientsFile["hinges"] != null) manifest["hinges"] = orientsFile["hinges"].DeepClone();
        var mModels = (JObject)manifest["models"];
        var mTemplates = (JObject)manifest["templates"];
        var gameVersion = GameVersion();
        if (gameVersion == null) Debug.LogWarning($"No EscapeFromTarkov.exe version under {GameRoot}; gameVersion left as is.");

        var work = orients.Properties()
            .Where(p => only == null || only.ContainsKey(p.Name))
            .Select(p => p.Name).ToList();
        int total = work.Sum(m => only != null ? only[m].Count : ((JArray)orients[m]["orientations"]).Count);

        var failures = new JArray();
        var folding = new SortedSet<string>(StringComparer.Ordinal);
        int done = 0, baked = 0, fallbackSwaps = 0;
        long bytes = 0;
        int maxW = 0, maxH = 0;
        string maxWhat = null;

        try
        {
            // One long-lived loader holds shaders and cubemaps; per-chunk loaders skip
            // them. Unity resolves cross-bundle references across all loaded bundles.
            using (var shaderLoader = new BundleLoader(BundleRoot, BundlesJson))
            using (var rig = new KitbashRig())
            {
                shaderLoader.Skip.Clear();
                shaderLoader.SkipPredicate = null;
                if (!rig.LoadShaders(shaderLoader)) return;
                shaderLoader.Load("cubemaps");

                for (int start = 0; start < work.Count; start += ChunkSize)
                {
                    using (var loader = new BundleLoader(BundleRoot, BundlesJson))
                    {
                        // Loading a bundle file twice fails, so skip what the shader
                        // loader already holds.
                        loader.SkipPredicate = null;
                        loader.Skip.UnionWith(shaderLoader.LoadedKeys);
                        int end = Math.Min(start + ChunkSize, work.Count);
                        for (int mi = start; mi < end; mi++)
                        {
                            var model = work[mi];
                            var entry = (JObject)orients[model];
                            var qs = ((JArray)entry["orientations"]).Select(ToQuat).ToList();
                            var indices = only != null ? only[model] : Enumerable.Range(0, qs.Count).ToList();
                            var tpls = entry["tpls"].Select(t => (string)t).ToList();
                            var modelFolds = folds[model] as JObject;
                            var modelHinge = hinges[model] as JObject;

                            // Union of slot ids declared by every template using this model.
                            var slotIds = tpls.SelectMany(t => slots[t]["slots"].Select(s => (string)s["id"]))
                                .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();

                            GameObject prefab = null;
                            foreach (var t in tpls)
                            {
                                var ids = slots[t]["slots"].Select(s => (string)s["id"]).ToList();
                                var item = loader.LoadItem((string)slots[t]["prefab"], ids);
                                if (item.Ok && item.ModelName == model) { prefab = item.Model; break; }
                            }
                            if (prefab == null)
                            {
                                Fail(failures, model, -1, "model not found in any template's bundle");
                                done += indices.Count;
                                continue;
                            }

                            // Assign only a new array: Newtonsoft clones a token that already
                            // has a parent, and writes would go to a detached copy.
                            var sprites = mModels[model] as JArray;
                            if (sprites == null) { sprites = new JArray(); mModels[model] = sprites; }
                            while (sprites.Count < qs.Count) sprites.Add(JValue.CreateNull());
                            foreach (var t in tpls) mTemplates[t] = model;

                            int fb = rig.FellBack;
                            var go = rig.Spawn(prefab);
                            bool belt = tpls.Any(t => (bool?)slots[t]["belt"] == true);
                            if (belt) KitbashRig.HideSpentBelt(go);
                            if (tpls.Any(t => (bool?)slots[t]["ammo"] == true)) KitbashRig.ShowFullRound(go);
                            bool? rounds = tpls.Any(t => slots[t]["cartridges"] != null) ? belt : (bool?)null;
                            fallbackSwaps += rig.FellBack - fb;
                            foreach (var idx in indices)
                            {
                                if (EditorUtility.DisplayCancelableProgressBar("Kitbash! bake",
                                        $"{done + 1}/{total}  {model} #{idx}", (float)done / total))
                                    throw new OperationCanceledException();
                                done++;

                                string err;
                                var sprite = BakeOne(rig, go, model, $"{idx}", qs[idx], slotIds, rounds, out err, ref bytes);
                                // Folded states: FoldableStockView / RetractableStockView set these
                                // bones on non-animated items, icons included, per fold set.
                                var stock = new JObject();
                                foreach (var set in modelFolds?.Properties() ?? Enumerable.Empty<JProperty>())
                                {
                                    if (sprite == null) break;
                                    var saved = ApplyFold(go.transform, (JArray)set.Value);
                                    try
                                    {
                                        var folded = BakeOne(rig, go, model, $"{idx}.fold{set.Name}", qs[idx], slotIds, rounds, out err, ref bytes);
                                        if (folded == null) { sprite = null; err = $"fold set {set.Name}: {err}"; }
                                        else { folded.Remove("q"); stock[set.Name] = folded; }
                                    }
                                    finally { foreach (var (t, r, pos) in saved) { t.localRotation = r; t.localPosition = pos; } }
                                }
                                if (sprite != null && stock.Count > 0) sprite["stockFolded"] = stock;
                                // Hinge states: HelmetDress / NightVisionMount snap the hinge to
                                // its on or off rotation on an icon item. Full entries, slots
                                // included; a pose the prefab already has names the base's files.
                                if (sprite != null && modelHinge != null)
                                {
                                    var path = (string)modelHinge["path"];
                                    var bone = path.Length == 0 ? go.transform : go.transform.Find(path);
                                    if (bone == null) { sprite = null; err = $"hinge {path} not found"; }
                                    var states = new JObject();
                                    foreach (var state in new[] { "on", "off" })
                                    {
                                        if (sprite == null) break;
                                        var want = ToQuat(modelHinge[state]);
                                        if (Quaternion.Angle(bone.localRotation, want) < 0.01f)
                                        {
                                            states[state] = new JObject(sprite.Properties().Where(x => x.Name != "q")
                                                .Select(x => new JProperty(x.Name, x.Value.DeepClone())));
                                            continue;
                                        }
                                        var saved = bone.localRotation;
                                        bone.localRotation = want;
                                        try
                                        {
                                            var posed = BakeOne(rig, go, model, $"{idx}.{state}", qs[idx], slotIds, rounds, out err, ref bytes);
                                            if (posed == null) { sprite = null; err = $"hinge {state}: {err}"; }
                                            else { posed.Remove("q"); states[state] = posed; }
                                        }
                                        finally { bone.localRotation = saved; }
                                    }
                                    if (sprite != null) sprite["hinge"] = states;
                                }
                                if (sprite == null) { Fail(failures, model, idx, err); continue; }
                                sprites[idx] = sprite;
                                if (sprite["fold"] != null) folding.Add(model);
                                baked++;
                                var size = (JArray)sprite["size"] ?? new JArray(0, 0);
                                if ((int)size[0] > maxW) { maxW = (int)size[0]; maxWhat = $"{model} #{idx}"; }
                                maxH = Math.Max(maxH, (int)size[1]);
                            }
                            rig.Destroy(go);
                        }
                        rig.ReleaseInstances();
                    }
                }
            }
        }
        catch (OperationCanceledException) { Debug.LogWarning("Kitbash! bake cancelled; writing what was baked."); }
        finally
        {
            EditorUtility.ClearProgressBar();
            // The newest sprites come from this client, so it names the manifest.
            // Kept next to "version" so it is readable without scrolling 6 MB.
            if (baked > 0 && gameVersion != null)
            {
                if (manifest["gameVersion"] != null) manifest["gameVersion"] = gameVersion;
                else manifest.Property("version").AddAfterSelf(new JProperty("gameVersion", gameVersion));
            }
            File.WriteAllText(ManifestPath, manifest.ToString(Formatting.Indented));

            var report = new JObject
            {
                ["scope"] = only == null ? "all" : "subset",
                ["gameVersion"] = gameVersion,
                ["requested"] = total,
                ["attempted"] = done,
                ["baked"] = baked,
                ["failed"] = failures.Count,
                ["fallbackShaderSwaps"] = fallbackSwaps,
                ["pngBytes"] = bytes,
                ["maxSize"] = new JArray(maxW, maxH),
                ["widest"] = maxWhat,
                ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
                ["failures"] = failures,
                ["autoFoldable"] = new JArray(folding),
            };
            File.WriteAllText(ReportPath, report.ToString(Formatting.Indented));
            Debug.Log($"Kitbash! bake: {baked}/{total} baked, {failures.Count} failed, " +
                      $"{bytes / 1048576.0:F1} MB, {sw.Elapsed.TotalSeconds:F0}s -> {ReportPath}");
        }
    }

    /// Poses each fold bone as folded; returns the bones' previous local poses.
    static List<(Transform, Quaternion, Vector3)> ApplyFold(Transform root, JArray fold)
    {
        var saved = new List<(Transform, Quaternion, Vector3)>();
        foreach (var f in fold)
        {
            var bone = BundleLoader.FindBone(root, (string)f["bone"]);
            if (bone == null) continue;
            saved.Add((bone, bone.localRotation, bone.localPosition));
            var v = (JArray)f["folded"];
            if ((string)f["kind"] == "rotate")
                bone.localRotation = new Quaternion((float)v[0], (float)v[1], (float)v[2], (float)v[3]);
            else bone.localPosition = new Vector3((float)v[0], (float)v[1], (float)v[2]);
        }
        return saved;
    }

    static JObject BakeOne(KitbashRig rig, GameObject go, string model, string idx, Quaternion q,
                           List<string> slotIds, bool? rounds, out string err, ref long bytes)
    {
        err = null;
        go.transform.SetPositionAndRotation(Vector3.zero, IconRotation * q);

        var slotsOut = new JObject();
        foreach (var id in slotIds)
        {
            var bone = BundleLoader.FindBone(go.transform, id);
            slotsOut[id] = bone == null ? JValue.CreateNull() : Anchor(bone);
        }

        // A magazine's round anchors, in fill order (see KitbashRig.RoundAnchors).
        JArray roundsOut = null;
        if (rounds != null)
        {
            roundsOut = new JArray();
            foreach (var (n, t) in KitbashRig.RoundAnchors(go.transform, rounds.Value))
            {
                var anchor = Anchor(t);
                anchor.AddFirst(new JProperty("n", n));
                roundsOut.Add(anchor);
            }
        }

        // A model with no renderers at all is deliberately invisible (e.g. the
        // game's empty magazine). It still hosts slots, so it gets an entry.
        if (go.GetComponentsInChildren<Renderer>(true).Length == 0)
        {
            var hollow = new JObject { ["q"] = Q(q), ["empty"] = true, ["slots"] = slotsOut };
            if (roundsOut != null) hollow["rounds"] = roundsOut;
            return hollow;
        }

        var entry = Visual(rig, go, model, $"{idx}", out err, ref bytes);
        if (entry == null) return null;
        entry.AddFirst(new JProperty("q", Q(q)));
        entry["slots"] = slotsOut;
        if (roundsOut != null) entry["rounds"] = roundsOut;

        var aim = AimCameras(go.transform);
        if (aim.Count > 0) entry["aim"] = new JArray(aim.Select(a => Q(Quaternion.Inverse(go.transform.rotation) * a.rotation)));

        // Weapons in hands set each AutoFoldableSight active iff
        // (Mode == On) ^ folded, built-in ones too. The prefab's own state is the
        // base sprite.
        var afs = go.GetComponentsInChildren<AutoFoldableSight>(true);
        if (afs.Length > 0)
        {
            var defaults = afs.Select(a => a.gameObject.activeSelf).ToArray();
            var fold = new JObject();
            foreach (var folded in new[] { false, true })
            {
                var want = afs.Select(a => (a.Mode == EAutoFoldableSightMode.On) ^ folded).ToArray();
                var name = folded ? "folded" : "raised";
                if (want.SequenceEqual(defaults))
                {
                    fold[name] = new JObject(entry.Properties()
                        .Where(p => p.Name != "q" && p.Name != "slots" && p.Name != "aim" && p.Name != "rounds"));
                    continue;
                }
                JObject v;
                for (int i = 0; i < afs.Length; i++) afs[i].gameObject.SetActive(want[i]);
                try
                {
                    v = KitbashRig.RendererBounds(go).extents == Vector3.zero
                        ? new JObject { ["empty"] = true }
                        : Visual(rig, go, model, $"{idx}.{name}", out err, ref bytes);
                }
                finally { for (int i = 0; i < afs.Length; i++) afs[i].gameObject.SetActive(defaults[i]); }
                if (v == null) { err = $"{name}: {err}"; return null; }
                fold[name] = v;
            }
            entry["fold"] = fold;
        }
        return entry;
    }

    /// Where a child lands, relative to the part origin (which is at 0), and the
    /// transform's orientation relative to the weapon root. The child's own attach
    /// rotation depends on the child, so the compositor applies it.
    static JObject Anchor(Transform t)
    {
        var p = t.position;
        return new JObject
        {
            ["offset"] = new JArray(R(p.x * PixelsPerUnit), R(-p.y * PixelsPerUnit)),
            ["z"] = Math.Round(p.z, 6),
            ["q"] = Q(Quaternion.Inverse(IconRotation) * t.rotation),
        };
    }

    /// The game's search for aim cameras under a sight (AssembleMods): names
    /// containing "mod_aim_camera", not descending into a child named "mod_scope".
    static List<Transform> AimCameras(Transform root)
    {
        var found = new List<Transform>();
        var queue = new Queue<Transform>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (t.name.Contains("mod_aim_camera")) found.Add(t);
            for (int i = 0; i < t.childCount; i++)
                if (t.GetChild(i).name != "mod_scope") queue.Enqueue(t.GetChild(i));
        }
        return found;
    }

    /// Renders the model as it stands and writes <model>/<stem>.png and .depth.png.
    static JObject Visual(KitbashRig rig, GameObject go, string model, string stem,
                          out string err, ref long bytes)
    {
        var cap = rig.RenderCropped(go, out err);
        if (cap == null) { err += "; " + KitbashRig.DescribeRenderers(go); return null; }
        // What the game frames icons on: the union of renderer world AABBs, minus
        // lenses and muzzle jets, as the game's icons are. Sprite pixels relative to
        // the anchor, image axes.
        var fb = KitbashRig.RendererBounds(go, ExcludedFromBounds);
        var frame = fb.extents == Vector3.zero ? null : new JArray(
            R(fb.min.x * PixelsPerUnit), R(-fb.max.y * PixelsPerUnit),
            R(fb.max.x * PixelsPerUnit), R(-fb.min.y * PixelsPerUnit));
        var cropped = cap.Color;
        try
        {
            int cw = cropped.width, ch = cropped.height;
            var png = cropped.EncodeToPNG();
            var depthPng = KitbashRig.EncodeDepth(cap.Depth, cw, ch);
            var dir = Path.Combine(SpritesDir, model);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"{stem}.png"), png);
            File.WriteAllBytes(Path.Combine(dir, $"{stem}.depth.png"), depthPng);
            bytes += png.Length + depthPng.Length;

            var entry = new JObject
            {
                ["file"] = $"{model}/{stem}.png",
                ["depth"] = $"{model}/{stem}.depth.png",
                ["size"] = new JArray(cw, ch),
                ["anchor"] = new JArray(R(cap.Anchor.x), R(cap.Anchor.y)),
                ["bounds"] = frame,
            };
            if (cap.Transmit != null)
            {
                var px = new Color32[cap.Transmit.Length];
                for (int i = 0; i < px.Length; i++) { var v = cap.Transmit[i]; px[i] = new Color32(v, v, v, 255); }
                var t = new Texture2D(cw, ch, TextureFormat.RGB24, false, true);
                t.SetPixels32(px);
                t.Apply();
                var tPng = t.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(t);
                File.WriteAllBytes(Path.Combine(dir, $"{stem}.transmit.png"), tPng);
                bytes += tPng.Length;
                entry["transmit"] = $"{model}/{stem}.transmit.png";
            }
            return entry;
        }
        finally { UnityEngine.Object.DestroyImmediate(cropped); }
    }

    static JObject LoadManifest()
    {
        if (File.Exists(ManifestPath))
        {
            var m = JObject.Parse(File.ReadAllText(ManifestPath));
            if ((float)m["pixelsPerUnit"] == PixelsPerUnit && (int)m["version"] == ManifestVersion) return m;
            Debug.LogWarning("Existing manifest has a different scale or version; starting fresh.");
        }
        return new JObject
        {
            ["version"] = ManifestVersion,
            ["pixelsPerUnit"] = PixelsPerUnit,
            ["supersample"] = Supersample,
            ["depthScale"] = DepthScale,
            ["iconRotation"] = Q(IconRotation),
            ["mergeDeg"] = (float)JObject.Parse(File.ReadAllText(Path.Combine(DataDir, "orientations.json")))["mergeDeg"],
            ["templates"] = new JObject(),
            ["models"] = new JObject(),
        };
    }

    static void Fail(JArray failures, string model, int idx, string why)
    {
        failures.Add(new JObject { ["model"] = model, ["index"] = idx, ["error"] = why });
        Debug.LogWarning($"bake failed: {model} #{idx}: {why}");
    }

    static Quaternion ToQuat(JToken t) =>
        new Quaternion((float)t[0], (float)t[1], (float)t[2], (float)t[3]);

    static JArray Q(Quaternion q)
    {
        if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        return new JArray(Math.Round(q.x, 7), Math.Round(q.y, 7), Math.Round(q.z, 7), Math.Round(q.w, 7));
    }

    static double R(float v) => Math.Round(v, 3);
}

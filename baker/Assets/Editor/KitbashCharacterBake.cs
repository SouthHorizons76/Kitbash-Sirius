// Bakes character sprites (spec/character.md) for one view: every piece rendered
// alone, cropped to what it covers, at twice the output size and halved.
//
//   headshot   the player icon's camera and lights, the armed pose
//   full       orthographic, a fixed 1.4 x 2.2 m frame at 562.5 px per metre, the
//              item icon lights, empty hands, plus feet
//
// A piece is a head, a body or feet suit, a worn item, or a part on a worn item's
// slot; each in every variant the rest of the outfit can choose:
//
//   head       base, under a full face cover
//   body       torso mesh base / armor / vest, hooded or not
//   feet       one
//   rig        alone, over armor;  backpack: alone, over armor, over a rig, over both
//   any item   clipped or not, per clipping type it has; face shield or NVG hinge on
//              or off; its parts placed by slot, per hinge state of the item
//
// Parts are walked from data/slots.json. Two placements of the same model at the same
// pose in the same variant render the same, so they share one sprite.
//
// Writes sprites/character/<view>/<piece>.png (premultiplied colour, alpha) and
// <piece>.depth.png (depth, code = round((z - depthOrigin) * depthScale), 0 where
// nothing drew; R high byte, G low), and the view's manifest.
// Menu: Kitbash! / Bake Character Headshots | Full Body (test kit | all)
//       Kitbash! / Bake Character Subset (both views): only data/character.subset.json's
//       suits and items, merged into the existing manifests; sprites no entry names
//       any more are deleted.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Diz.Skinning;
using EFT.Visual;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using static KitbashConfig;
using Object = UnityEngine.Object;

public static class KitbashCharacterBake
{
    const int MaxPartDepth = 6;
    const int UnloadAbove = 250;   // loaded bundles before item bundles are dropped
    const int GlassBlocks = 50;   // fully dithered 2 x 2 blocks that make a piece glass
    const float GlassBlend = 0.25f;   // or this share of its pixels partly covered

    static readonly string[] Slots =
        { "ArmorVest", "TacticalVest", "Backpack", "Headwear", "Earpiece", "Eyewear", "FaceCover", "ArmBand" };

    /// A camera, pose and output frame. Captures are W x H (twice the output),
    /// rows bottom-up; depth is world z along the view, larger farther.
    class View
    {
        public string Name, Pose, Manifest;
        public int WeaponType, W, H;
        public bool Feet, IconLights;
        public float DepthOrigin, DepthScale;
        public Func<GameObject, PlayerIcon.Layers> Capture;
        public int OutW => W / 2;
        public int OutH => H / 2;
        public string OutDir => Path.Combine(SpritesDir, "character", Name);
    }

    static readonly View Headshot = new View
    {
        Name = "headshot", Pose = "armed", WeaponType = 0, Manifest = "character.manifest.json",
        W = PlayerIcon.RenderSize, H = PlayerIcon.RenderSize,
        DepthOrigin = 1f, DepthScale = 20000f,   // 1 to 4.3 m from the camera
        Capture = c => PlayerIcon.Capture(c),
    };

    // The full-body frame, in the character's space (feet at y = 0): room for a
    // backpack and weapons slung on the back.
    static readonly Rect FullFrame = new Rect(-0.7f, -0.1f, 1.4f, 2.2f);

    static readonly View Full = new View
    {
        Name = "full", Pose = "empty", WeaponType = -1, Manifest = "character-full.manifest.json",
        W = 2 * Mathf.RoundToInt(FullFrame.width * KitbashCharacter.FullPpu),
        H = 2 * Mathf.RoundToInt(FullFrame.height * KitbashCharacter.FullPpu),
        Feet = true, IconLights = true,
        DepthOrigin = -1.5f, DepthScale = 20000f,   // -1.5 to 1.8 m about the character
        Capture = CaptureFull,
    };

    [MenuItem("Kitbash!/Bake Character Headshots (test kit)")]
    static void TestKit() => Run(Headshot, true);

    [MenuItem("Kitbash!/Bake Character Headshots (all)")]
    static void All() => Run(Headshot, false);

    [MenuItem("Kitbash!/Bake Character Full Body (test kit)")]
    static void FullTestKit() => Run(Full, true);

    [MenuItem("Kitbash!/Bake Character Full Body (all)")]
    static void FullAll() => Run(Full, false);

    [MenuItem("Kitbash!/Bake Character Subset (both views)")]
    static void SubsetBoth() => RunSubset(Headshot, Full);

    /// The full body: orthographic, from the front, the character turned as item
    /// icons are, under the item icon rig's lights (no shadows).
    static PlayerIcon.Layers CaptureFull(GameObject character)
    {
        character.transform.SetPositionAndRotation(Vector3.zero, KitbashCharacter.IconTurn);
        var go = new GameObject("FullBodyCamera");
        bool streaming = QualitySettings.streamingMipmapsActive;
        try
        {
            QualitySettings.streamingMipmapsActive = false;
            go.transform.SetPositionAndRotation(new Vector3(FullFrame.center.x, FullFrame.center.y, -10f), Quaternion.identity);
            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = Full.H / 2f / (2f * KitbashCharacter.FullPpu);
            cam.nearClipPlane = 7f;
            cam.farClipPlane = 13f;
            cam.renderingPath = RenderingPath.Forward;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.enabled = false;
            return new PlayerIcon.Layers
            {
                Black = PlayerIcon.Render(cam, Full.W, Full.H, new Color(0f, 0f, 0f, 0f)),
                White = PlayerIcon.Render(cam, Full.W, Full.H, new Color(1f, 1f, 1f, 0f)),
                Depth = PlayerIcon.RenderDepth(cam, Full.W, Full.H),
            };
        }
        finally
        {
            QualitySettings.streamingMipmapsActive = streaming;
            Object.DestroyImmediate(go);
        }
    }

    class Ctx
    {
        public View View;
        public BundleLoader Loader;
        public GameObject Character;
        public Skeleton Skeleton;
        public PlayerBones Bones;
        public JObject Items, Custom, SlotsJson;
        public JObject Pieces = new JObject();
        public readonly Dictionary<string, string> ByPose = new Dictionary<string, string>();
        public readonly List<string> Log = new List<string>();
        public int Captured, Shared, Empty;
    }

    static string SubsetPath => Path.Combine(DataDir, "character.subset.json");

    /// The ids of data/character.subset.json: head, body and feet suits and worn
    /// item templates, mixed (python -m kitbash.character subset writes it).
    static HashSet<string> LoadSubset()
    {
        if (!File.Exists(SubsetPath)) { Debug.LogError($"no {SubsetPath}: python -m kitbash.character subset <id> ..."); return null; }
        var ids = JObject.Parse(File.ReadAllText(SubsetPath))["ids"] as JArray;
        if (ids == null || ids.Count == 0) { Debug.LogError($"{SubsetPath} lists no ids"); return null; }
        return new HashSet<string>(ids.Select(i => (string)i));
    }

    static void RunSubset(params View[] views)
    {
        var only = LoadSubset();
        if (only != null) foreach (var v in views) Run(v, false, only);
    }

    /// After a subset merges in: drops pieces no entry names any more, and their files.
    /// A piece is a sprite name; entries name them as string values anywhere inside.
    static void DropUnused(JObject manifest, string outDir, List<string> log)
    {
        var pieces = (JObject)manifest["pieces"];
        var used = new HashSet<string>();
        foreach (var part in new[] { "heads", "bodies", "feet", "items" })
            if (manifest[part] is JToken t)
                foreach (var v in t.SelectTokens("..*").OfType<JValue>())
                    if (v.Type == JTokenType.String && pieces.ContainsKey((string)v)) used.Add((string)v);
        var gone = pieces.Properties().Select(p => p.Name).Where(n => !used.Contains(n)).ToList();
        foreach (var n in gone)
        {
            pieces.Remove(n);
            foreach (var ext in new[] { ".png", ".webp", ".depth.png", ".depth.webp" })
            {
                var f = Path.Combine(outDir, n + ext);
                if (File.Exists(f)) File.Delete(f);
            }
        }
        log.Insert(0, $"subset: {gone.Count} sprites no longer used, removed");
    }

    static void Run(View view, bool testKit, HashSet<string> only = null)
    {
        var (items0, custom0) = KitbashCharacter.ItemBase();
        var ctx = new Ctx
        {
            View = view,
            Items = items0,
            Custom = custom0,
            SlotsJson = JObject.Parse(File.ReadAllText(Path.Combine(DataDir, "slots.json"))),
        };
        var outDir = view.OutDir;
        var manifestPath = Path.Combine(DataDir, view.Manifest);
        Directory.CreateDirectory(outDir);
        // A subset merges into the existing manifest, so it needs one with its sprites.
        var old = only != null && File.Exists(manifestPath) ? JObject.Parse(File.ReadAllText(manifestPath)) : null;
        if (only != null && old == null) { Debug.LogError($"subset bake: no {manifestPath} to merge into; bake (all) first"); return; }
        if (only == null)
            foreach (var f in Directory.GetFiles(outDir)) File.Delete(f);

        // What to bake: every head, body and wearable template, the test kit's, or the subset's.
        var parts = new Dictionary<string, List<string>>
        {
            ["head"] = Customizations(ctx, "Head"),
            ["body"] = Customizations(ctx, "Body"),
            ["feet"] = view.Feet ? Customizations(ctx, "Feet") : new List<string>(),
        };
        var worn = Slots.ToDictionary(s => s, s => SlotTemplates(ctx.Items, s));
        if (only != null)
        {
            foreach (var k in parts.Keys.ToList()) parts[k] = parts[k].Where(only.Contains).ToList();
            foreach (var s in Slots) worn[s] = worn[s].Where(only.Contains).ToList();
            var found = new HashSet<string>(parts.Values.SelectMany(l => l).Concat(worn.Values.SelectMany(l => l)));
            foreach (var id in only.Where(i => !found.Contains(i)))
                ctx.Log.Add($"subset: {id} is no {(view.Feet ? "head, body, feet" : "head, body")} suit or wearable template; skipped");
        }
        if (testKit)
        {
            parts["head"] = new List<string> { KitbashCharacter.Defaults["head"] };
            parts["body"] = new List<string> { KitbashCharacter.Defaults["body"] };
            if (view.Feet) parts["feet"] = new List<string> { KitbashCharacter.Defaults["feet"] };
            var kit = JArray.Parse(KitbashCharacter.DefaultEquipment);
            foreach (var s in Slots)
                worn[s] = kit.Where(k => (string)k["slotId"] == s).Select(k => (string)k["_tpl"]).ToList();
        }
        int total = parts.Values.Sum(l => l.Count) + worn.Values.Sum(l => l.Count), done = 0;

        var manifest = new JObject
        {
            ["version"] = 1,
            ["gameVersion"] = GameVersion(),
            ["view"] = view.Name,
            ["pose"] = view.Pose,
            ["size"] = new JArray(view.OutW, view.OutH),   // rendered at twice this, halved per sprite
            ["depthOrigin"] = view.DepthOrigin,
            ["depthScale"] = view.DepthScale,
        };
        if (view == Full)
            manifest["frame"] = new JArray(FullFrame.xMin, FullFrame.yMin, FullFrame.width, FullFrame.height);
        // A subset starts from the old entries and pieces; what it bakes replaces them.
        var heads = old?["heads"] as JObject ?? new JObject();
        var bodies = old?["bodies"] as JObject ?? new JObject();
        var feet = old?["feet"] as JObject ?? new JObject();
        var items = old?["items"] as JObject ?? new JObject();
        if (old?["pieces"] is JObject oldPieces) ctx.Pieces = oldPieces;
        // One that now bakes to nothing (skipped) loses its old entry.
        void Put(JObject into, string id, JObject e) { if (e != null) into[id] = e; else into.Remove(id); }

        using (var loader = new BundleLoader(BundleRoot, BundlesJson))
        using (var rig = new KitbashRig())   // black ambient, as the icons render
        {
            // The full body is lit by the item icon rig, as the test render; the
            // headshot by the player icon's own lights only.
            foreach (var l in rig.Lights) l.enabled = view.IconLights && l.name != "Fill Light";
            loader.Skip.Clear();
            loader.SkipPredicate = k => k.Contains("/audio/") || k.Contains("/effects/");
            loader.AddManifest(Path.Combine(BundleRoot, "Windows.json"));
            ctx.Loader = loader;
            try
            {
                var prefab = loader.Load(KitbashCharacter.MenuPlayer)?.LoadAllAssets<GameObject>()
                    .FirstOrDefault(a => a.GetComponentInChildren<PlayerBones>(true) != null);
                if (prefab == null) { Debug.LogError("no menu character"); return; }
                ctx.Character = Object.Instantiate(prefab);
                ctx.Character.SetActive(true);
                foreach (var r in ctx.Character.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                KitbashCharacter.Pose(ctx.Character, ctx.Log, view.WeaponType);
                ctx.Skeleton = ctx.Character.GetComponentsInChildren<Skeleton>(true).OrderByDescending(s => s.Bones.Count).First();
                ctx.Bones = ctx.Character.GetComponentInChildren<PlayerBones>(true);

                bool Step(string what)
                {
                    done++;
                    if (loader.LoadedCount > UnloadAbove) loader.UnloadExcept(new[] { KitbashCharacter.MenuPlayer });
                    return EditorUtility.DisplayCancelableProgressBar($"Kitbash! character {view.Name}",
                        $"{done}/{total} {what} ({ctx.Captured} sprites)", (float)done / total);
                }
                foreach (var id in parts["head"]) { if (Step("head")) return; Put(heads, id, BakeSuit(ctx, "head", id)); }
                foreach (var id in parts["body"]) { if (Step("body")) return; Put(bodies, id, BakeSuit(ctx, "body", id)); }
                foreach (var id in parts["feet"]) { if (Step("feet")) return; Put(feet, id, BakeSuit(ctx, "feet", id)); }
                foreach (var s in Slots)
                    foreach (var tpl in worn[s])
                    {
                        if (Step(s)) return;
                        Put(items, tpl, BakeItem(ctx, s, tpl));
                    }
            }
            finally
            {
                if (ctx.Character != null) Object.DestroyImmediate(ctx.Character);
                EditorUtility.ClearProgressBar();
                manifest["heads"] = heads;
                manifest["bodies"] = bodies;
                if (view.Feet) manifest["feet"] = feet;
                manifest["items"] = items;
                manifest["pieces"] = ctx.Pieces;
                if (only != null) DropUnused(manifest, outDir, ctx.Log);
                File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented));
            }
        }
        Debug.Log($"Kitbash! character {view.Name}: {ctx.Captured} sprites, {ctx.Shared} placements sharing one, " +
                  $"{ctx.Empty} out of frame -> {outDir}, {manifestPath}\n" + string.Join("\n", ctx.Log.Take(400)));
    }

    // ---- what to bake ---------------------------------------------------------

    static List<string> Customizations(Ctx ctx, string part) => ctx.Custom.Properties()
        .Where(p => (string)p.Value["_props"]?["BodyPart"] == part && p.Value["_props"]?["Prefab"]?["path"] != null)
        .Select(p => p.Name).OrderBy(x => x).ToList();

    /// Templates an equipment slot accepts (its filters' leaf items with a prefab).
    static List<string> SlotTemplates(JObject items, string slot)
    {
        var inv = items.Properties().Select(p => p.Value).First(v => (string)v["_name"] == "Default Inventory");
        var filters = ((JArray)inv["_props"]["Slots"]).First(s => (string)s["_name"] == slot)["_props"]["filters"]
            .SelectMany(f => f["Filter"].Select(x => (string)x)).ToList();
        var children = items.Properties().GroupBy(p => (string)p.Value["_parent"] ?? "")
            .ToDictionary(g => g.Key, g => g.Select(p => p.Name).ToList());
        var outp = new SortedSet<string>();
        var stack = new Stack<string>(filters);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            var v = items[x];
            if ((string)v?["_type"] == "Item" && !string.IsNullOrEmpty((string)v["_props"]?["Prefab"]?["path"])) outp.Add(x);
            if (children.TryGetValue(x, out var cs)) foreach (var c in cs) stack.Push(c);
        }
        return outp.ToList();
    }

    // ---- suits ----------------------------------------------------------------

    static JObject BakeSuit(Ctx ctx, string part, string id)
    {
        var key = (string)ctx.Custom[id]["_props"]["Prefab"]["path"];
        var asset = ctx.Loader.Load(key)?.LoadAllAssets<GameObject>()
            .FirstOrDefault(a => a.GetComponentInChildren<LoddedSkin>(true) != null);
        if (asset == null) { ctx.Log.Add($"{part} {id}: no LoddedSkin in {key}"); return null; }
        var inst = Object.Instantiate(asset, ctx.Character.transform, false);
        try
        {
            inst.SetActive(true);
            var tally = new int[3];
            foreach (var lodded in inst.GetComponentsInChildren<LoddedSkin>(true))
                KitbashCharacter.BindLods(id, lodded._lods, ctx.Skeleton, tally, ctx.Log);
            // Suits rigged to another skeleton (the zombies' skips a spine bone) are
            // never worn by a player.
            if (tally[2] > 0) { ctx.Log.Add($"{part} {ctx.Custom[id]["_name"]}: skipped, {tally[2]} bones missing"); return null; }
            foreach (var g in inst.GetComponentsInChildren<LODGroup>(true)) g.ForceLOD(0);
            var own = Drawn(inst);
            var entry = new JObject();
            var variants = new JObject();

            if (part == "head")
            {
                var hs = inst.GetComponentsInChildren<HeadSkin>(true);
                var choices = new List<(string, Func<HeadSkin, Mesh>)> { ("base", h => h.Data.Base) };
                if (hs.Any(h => h.Data.FaceCover != null && h.Data.FaceCover != h.Data.Base))
                    choices.Add(("faceCover", h => h.Data.FaceCover));
                foreach (var (name, pick) in choices)
                {
                    foreach (var h in hs) SetMesh(h.Skin, pick(h));
                    variants[name] = Shoot(ctx, own, $"head_{id}_{name}", null);
                }
            }
            else if (part == "feet")
            {
                variants["base"] = Shoot(ctx, own, $"feet_{id}", null);
                // A pistol holster on the thigh, for carried pistols.
                entry["holster"] = inst.GetComponentsInChildren<LegsView>(true).Any(l => l._holster != null);
            }
            else
            {
                var ts = inst.GetComponentsInChildren<TorsoSkin>(true);
                var choices = new List<(string, Func<TorsoSkin, Mesh>)> { ("base", t => t._base) };
                if (ts.Any(t => t._armor != null && t._armor != t._base)) choices.Add(("armor", t => t._armor));
                if (ts.Any(t => t._vest != null && t._vest != t._base)) choices.Add(("vest", t => t._vest));
                var hood = inst.GetComponentsInChildren<HoodedDress>(true);
                var hoods = hood.Length > 0 ? new[] { true, false } : new[] { true };
                foreach (var up in hoods)
                {
                    foreach (var hd in hood) SetHooded(hd, up);
                    own = Drawn(inst);
                    foreach (var (name, pick) in choices)
                    {
                        foreach (var t in ts) SetMesh(t._skin, pick(t));
                        var h = hood.Length > 0 ? (up ? "hood" : "nohood") : "-";
                        variants[$"{name}/{h}"] =
                            Shoot(ctx, own, h == "-" ? $"body_{id}_{name}" : $"body_{id}_{name}_{h}", null);
                    }
                }
                entry["hooded"] = hood.Length > 0;
                entry["integratedArmor"] = (bool?)ctx.Custom[id]["_props"]["IntegratedArmorVest"] == true;
                entry["clipping"] = (int)inst.GetComponentsInChildren<ClippingRuleChanger>(true)
                    .Aggregate((EClippingCustoms)0, (a, c) => a | c._clippingCustoms);
            }
            entry["variants"] = variants;
            ctx.Log.Add($"{part} {ctx.Custom[id]["_name"]}: {variants.Count} variants, {tally[2]} bones missing");
            return entry;
        }
        finally { Object.DestroyImmediate(inst); }
    }

    static void SetHooded(HoodedDress hd, bool hooded)
    {
        foreach (var g in hd._hooded ?? new GameObject[0]) if (g != null) g.SetActive(hooded);
        foreach (var g in hd._notHooded ?? new GameObject[0]) if (g != null) g.SetActive(!hooded);
    }

    static void SetMesh(Skin s, Mesh m)
    {
        if (s?._skinnedMeshRenderer != null && m != null) s._skinnedMeshRenderer.sharedMesh = m;
    }

    // ---- worn items -------------------------------------------------------------

    static JObject BakeItem(Ctx ctx, string slot, string tpl)
    {
        var props = ctx.Items[tpl]?["_props"];
        var key = (string)props?["Prefab"]?["path"];
        if (key == null) return null;
        var go = SpawnItem(ctx, tpl, key, out var model);
        if (go == null) { ctx.Log.Add($"{slot} {tpl}: not loaded"); return null; }
        try
        {
            var dressItem = go.GetComponent<DressItem>();
            bool skinned = dressItem != null && dressItem.DressPrefab != null;
            if (skinned)
            {
                go.transform.SetParent(ctx.Character.transform, false);
                if (dressItem.LootPrefab != null) dressItem.LootPrefab.SetActive(false);
                dressItem.DressPrefab.SetActive(true);
                var tally = new int[3];
                foreach (var d in dressItem.DressPrefab.GetComponentsInChildren<SkinDress>(true))
                    KitbashCharacter.BindLods(tpl, d._lods, ctx.Skeleton, tally, ctx.Log);
                if (tally[2] > 0) { ctx.Log.Add($"{slot} {tpl}: skipped, {tally[2]} bones missing"); return null; }
            }
            else
            {
                var bone = slot == "ArmBand" ? ctx.Bones.RightShoulder?.Original : ctx.Bones.Head?.Original;
                if (bone == null) return null;
                go.transform.SetParent(bone, false);
                go.transform.localRotation = KitbashCharacter.BoneAttach;
                go.transform.localPosition = Vector3.zero;
            }
            foreach (var g in go.GetComponentsInChildren<LODGroup>(false)) g.ForceLOD(0);

            // What the outfit rules read from the template.
            var entry = new JObject { ["slot"] = slot, ["skinned"] = skinned };
            if (slot == "ArmorVest") entry["vestlike"] = go.GetComponentInChildren<VestlikeArmor>(true) != null;
            if (slot == "Headwear") entry["helmet"] = (string)props["MaterialType"] == "Helmet";
            if (slot == "FaceCover")
            {
                entry["halfMask"] = (bool?)props["IsHalfMask"] == true;
                entry["blocksHood"] = ((props["ConflictingItems"] as JArray)?.Count ?? 0) > 0
                                      || (bool?)props["BlocksHeadwear"] == true;
            }
            BakeNode(ctx, go, model, tpl, $"{slot}/{tpl}", entry, 0, !skinned);
            return entry;
        }
        finally { Object.DestroyImmediate(go); }
    }

    /// The prefab as it is worn: the container (it holds the model or the DressItem),
    /// unless the model lies outside it.
    static GameObject SpawnItem(Ctx ctx, string tpl, string key, out string model)
    {
        var def = ctx.SlotsJson[tpl];
        var slotIds = def != null ? def["slots"].Select(s => (string)s["id"]).ToList()
            : (ctx.Items[tpl]?["_props"]?["Slots"] as JArray)?.Select(s => (string)s["_name"]).ToList() ?? new List<string>();
        var item = ctx.Loader.LoadItem(key, slotIds);
        model = item.ModelName;
        if (item.Container == null && item.Model == null) return null;
        var src = item.Container != null && (item.Model == null || item.Container.GetComponent<DressItem>() != null
                                              || item.Model.transform.IsChildOf(item.Container.transform))
            ? item.Container : item.Model;
        model = src.name;
        var go = Object.Instantiate(src);
        foreach (var a in go.GetComponentsInChildren<Animator>(true)) a.enabled = false;
        go.SetActive(true);
        return go;
    }

    /// A node's own variants, then its parts per hinge state. Variant keys are
    /// "<hinge>|<mesh>|<clip mask>": hinge "on", "off" or "-"; mesh the rig or backpack
    /// mesh or "-"; the clipping types (EClippingCustoms bits) taking their custom mesh.
    static void BakeNode(Ctx ctx, GameObject go, string model, string tpl, string path, JObject node, int depth, bool parts)
    {
        var own = Drawn(go);
        bool shield = go.GetComponentsInChildren<HelmetDress>(true).Any(h => h.HingeTransform != null);
        bool nvg = go.GetComponentsInChildren<NightVisionMount>(true).Any(m => m._rotator?.RotatedTransform != null);
        var hinged = shield || nvg;
        var meshes = MeshVariants(go);
        var customSkins = go.GetComponentsInChildren<CustomItemSkin>(true)
            .Where(c => c.Data.Base != null && c.Data.Custom != null).ToList();
        var customs = go.GetComponentsInChildren<CustomItem>(true)
            .Where(c => c._meshFilter != null && c._data.Base != null && c._data.Custom != null).ToList();
        var clip = customSkins.Aggregate((EClippingCustoms)0, (a, c) => a | c._itemType)
                   | customs.Aggregate((EClippingCustoms)0, (a, c) => a | c._itemType);
        var masks = Subsets((int)clip);
        node["clip"] = (int)clip;
        if (hinged) node["hinge"] = shield ? "faceShield" : "nightVision";
        if (meshes.Count > 1) node["meshes"] = new JArray(meshes.Select(m => m.name));

        var variants = new JObject();
        foreach (var h in hinged ? new[] { "on", "off" } : new[] { "-" })
        {
            if (hinged) SetHinges(go, h == "on");
            foreach (var (mesh, apply) in meshes)
            {
                apply();
                foreach (var m in masks)
                {
                    foreach (var c in customSkins) SetMesh(c.Skin, (m & (int)c._itemType) != 0 ? c.Data.Custom : c.Data.Base);
                    foreach (var c in customs) c._meshFilter.sharedMesh = (m & (int)c._itemType) != 0 ? c._data.Custom : c._data.Base;
                    var vkey = $"{h}|{mesh}|{m}";
                    var pose = $"{model}|{PoseKey(ctx.Character.transform, go.transform)}|{vkey}";
                    variants[vkey] = Shoot(ctx, own, $"item_{tpl}_{Hash(path)}_{h}_{mesh}_{m}", pose);
                }
            }
        }
        node["variants"] = variants;

        var def = ctx.SlotsJson[tpl];
        if (!parts || depth >= MaxPartDepth || def == null || !(def["slots"] is JArray slots) || slots.Count == 0) return;
        var children = new JObject();
        foreach (var h in hinged ? new[] { "on", "off" } : new[] { "-" })
        {
            if (hinged) SetHinges(go, h == "on");
            var bones = slots.ToDictionary(s => (string)s["id"], s => BundleLoader.FindBone(go.transform, (string)s["id"]));
            var bySlot = new JObject();
            foreach (var s in slots)
            {
                var sid = (string)s["id"];
                if (bones[sid] == null) continue;
                var accepted = new JObject();
                foreach (var c in s["accepts"].Select(x => (string)x))
                {
                    var cdef = ctx.SlotsJson[c];
                    if (cdef == null) continue;
                    var child = SpawnItem(ctx, c, (string)cdef["prefab"], out var cmodel);
                    if (child == null) continue;
                    try
                    {
                        var placer = child.GetComponent<ModPlacer>();
                        child.transform.SetParent(bones[sid], false);
                        child.transform.localPosition = placer != null ? placer.ModPosition : Vector3.zero;
                        child.transform.localRotation = placer != null ? Quaternion.Euler(placer.ModRotation) : AttachRotation;
                        foreach (var g in child.GetComponentsInChildren<LODGroup>(false)) g.ForceLOD(0);
                        var cnode = new JObject();
                        // the hinge state is part of the placement: a face shield down and the
                        // same one up are two sprites, which one name would let overwrite each other
                        var cpath = hinged ? $"{path}/{h}:{sid}/{c}" : $"{path}/{sid}/{c}";
                        BakeNode(ctx, child, cmodel, c, cpath, cnode, depth + 1, true);
                        accepted[c] = cnode;
                    }
                    finally { Object.DestroyImmediate(child); }
                }
                if (accepted.Count > 0) bySlot[sid] = accepted;
            }
            if (bySlot.Count > 0) children[h] = bySlot;
        }
        if (children.Count > 0) node["parts"] = children;
    }

    /// A rig's or backpack's mesh choices (VestSkin, BackpackSkin), else none.
    static List<(string name, Action apply)> MeshVariants(GameObject go)
    {
        var vs = go.GetComponentsInChildren<VestSkin>(true);
        if (vs.Length > 0)
            return new List<(string, Action)>
            {
                ("body", () => { foreach (var v in vs) SetMesh(v.Skin, v.Data.Body); }),
                ("armor", () => { foreach (var v in vs) SetMesh(v.Skin, v.Data.Armor); }),
            };
        var bs = go.GetComponentsInChildren<BackpackSkin>(true);
        if (bs.Length > 0)
            return new List<(string, Action)>
            {
                ("body", () => { foreach (var b in bs) SetMesh(b.Skin, b.Data.Body); }),
                ("armor", () => { foreach (var b in bs) SetMesh(b.Skin, b.Data.Armor); }),
                ("vest", () => { foreach (var b in bs) SetMesh(b.Skin, b.Data.Vest); }),
                ("armorVest", () => { foreach (var b in bs) SetMesh(b.Skin, b.Data.ArmorVest); }),
            };
        return new List<(string, Action)> { ("-", () => { }) };
    }

    /// Face shield and NVG hinges snapped on or off, as the item icons do.
    static void SetHinges(GameObject go, bool on)
    {
        foreach (var h in go.GetComponentsInChildren<HelmetDress>(true))
        {
            if (h.HingeTransform == null) continue;
            float x = on
                ? (h.Filter?.Forward != null && h.Filter.Forward.length > 0
                    ? h.Filter.Forward.Evaluate(h.Filter.Forward.keys[h.Filter.Forward.length - 1].time) : 1f)
                : (h.Filter?.Backward != null && h.Filter.Backward.length > 0 ? h.Filter.Backward.Evaluate(0f) : 0f);
            h.HingeTransform.localRotation = Quaternion.Lerp(h.OffRotation, h.OnRotation, x);
        }
        foreach (var m in go.GetComponentsInChildren<NightVisionMount>(true))
            if (m._rotator?.RotatedTransform != null)
                m._rotator.RotatedTransform.localRotation = on ? m._rotator.OnRotation : m._rotator.OffRotation;
    }

    static List<int> Subsets(int bits)
    {
        var outp = new List<int> { 0 };
        for (int b = 1; b <= 0x80; b <<= 1)
            if ((bits & b) != 0) outp = outp.Concat(outp.Select(x => x | b)).ToList();
        return outp;
    }

    // ---- capture ----------------------------------------------------------------

    /// The renderers a piece draws: those under it that are on now.
    static List<Renderer> Drawn(GameObject go) =>
        go.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled).ToList();

    /// Renders the given renderers alone, crops and saves them. Returns the piece's
    /// name, or null when it leaves the frame. A pose key already seen returns the
    /// sprite saved for it.
    static string Shoot(Ctx ctx, List<Renderer> own, string name, string poseKey)
    {
        if (poseKey != null && ctx.ByPose.TryGetValue(poseKey, out var shared)) { ctx.Shared++; return shared; }
        var all = ctx.Character.GetComponentsInChildren<Renderer>(true);
        var was = all.Select(r => r.enabled).ToArray();
        var keep = new HashSet<Renderer>(own);
        foreach (var r in all) r.enabled = keep.Contains(r);
        PlayerIcon.Layers l;
        try { l = ctx.View.Capture(ctx.Character); }
        finally { for (int i = 0; i < all.Length; i++) all[i].enabled = was[i]; }

        var v = ctx.View;
        int n = v.W;   // row stride
        var alpha = new byte[v.W * v.H];
        int x0 = v.W, x1 = -1, r0 = v.H, r1 = -1;
        for (int r = 0; r < v.H; r++)
            for (int x = 0; x < v.W; x++)
            {
                int i = r * n + x;
                var a = (byte)Mathf.RoundToInt(PlayerIcon.Alpha(l.Black[i], l.White[i]) * 255f);
                alpha[i] = a;
                if (a == 0) continue;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (r < r0) r0 = r;
                if (r > r1) r1 = r;
            }
        string result = null;
        if (x1 < 0) ctx.Empty++;
        else
        {
            int w = x1 - x0 + 1, h = r1 - r0 + 1;
            var col = new Color32[w * h];
            var dep = new ushort[w * h];
            for (int r = 0; r < h; r++)
                for (int x = 0; x < w; x++)
                {
                    int i = (r0 + r) * n + x0 + x, o = r * w + x;
                    var k = l.Black[i];
                    col[o] = new Color32(k.r, k.g, k.b, alpha[i]);
                    float z = l.Depth[i];
                    dep[o] = z >= PlayerIcon.NoDepth * 0.5f || alpha[i] == 0 ? (ushort)0
                        : (ushort)Mathf.Clamp(Mathf.RoundToInt((z - v.DepthOrigin) * v.DepthScale), 1, 65535);
                }
            FillDepth(col, dep, w, h);
            var half = Halve(col, dep, x0, r0, w, h);
            var t = new Texture2D(half.W, half.H, TextureFormat.RGBA32, false);
            t.SetPixels32(half.Col);
            t.Apply();
            File.WriteAllBytes(Path.Combine(v.OutDir, name + ".png"), t.EncodeToPNG());
            Object.DestroyImmediate(t);
            File.WriteAllBytes(Path.Combine(v.OutDir, name + ".depth.png"), KitbashRig.EncodeDepth(half.Dep, half.W, half.H));
            // x, y: the sprite's top-left in the output frame, image rows top-down.
            var piece = new JObject { ["x"] = half.X, ["y"] = v.OutH - half.Row - half.H, ["w"] = half.W, ["h"] = half.H };
            if (half.Glass) piece["glass"] = true;
            ctx.Pieces[name] = piece;
            ctx.Captured++;
            result = name;
        }
        if (poseKey != null) ctx.ByPose[poseKey] = result;
        return result;
    }

    struct Half
    {
        public Color32[] Col;
        public ushort[] Dep;
        public int X, Row, W, H;   // in the output frame, rows bottom-up
        public bool Glass;
    }

    /// Halves a crop from the render frame to the output frame: colour (premultiplied)
    /// and alpha averaged over each 2 x 2 block, depth the nearest covered sample.
    /// Glass renders dithered, every other pixel, in the headshot's deferred path and
    /// blended in the full body's forward one. A piece with enough fully dithered
    /// blocks, or mostly partial coverage, is glass, which the compositor draws after
    /// everything else (a translucent layer would otherwise hide what is drawn later
    /// behind it). Thin opaque pieces can pass as glass; drawing them last is harmless.
    static Half Halve(Color32[] col, ushort[] dep, int x0, int r0, int w, int h)
    {
        int hx = x0 / 2, hr = r0 / 2;
        int hw = (x0 + w + 1) / 2 - hx, hh = (r0 + h + 1) / 2 - hr;
        var outCol = new Color32[hw * hh];
        var outDep = new ushort[hw * hh];
        int checker = 0, covered = 0, partial = 0;
        var a = new byte[4];
        for (int r = 0; r < hh; r++)
            for (int x = 0; x < hw; x++)
            {
                int sr = 0, sg = 0, sb = 0, sa = 0;
                ushort d = 0;
                for (int k = 0; k < 4; k++)
                {
                    int cx = (hx + x) * 2 + (k & 1) - x0, cr = (hr + r) * 2 + (k >> 1) - r0;
                    a[k] = 0;
                    if (cx < 0 || cx >= w || cr < 0 || cr >= h) continue;
                    var c = col[cr * w + cx];
                    sr += c.r; sg += c.g; sb += c.b; sa += c.a; a[k] = c.a;
                    var z = dep[cr * w + cx];
                    if (z != 0 && (d == 0 || z < d)) d = z;
                }
                int o = r * hw + x;
                outCol[o] = new Color32((byte)((sr + 2) / 4), (byte)((sg + 2) / 4), (byte)((sb + 2) / 4), (byte)((sa + 2) / 4));
                outDep[o] = outCol[o].a == 0 ? (ushort)0 : d;
                if (outCol[o].a > 0) covered++;
                if (outCol[o].a > 20 && outCol[o].a < 235) partial++;
                bool diag = a[0] > 250 && a[3] > 250 && a[1] < 5 && a[2] < 5;
                bool anti = a[1] > 250 && a[2] > 250 && a[0] < 5 && a[3] < 5;
                if (diag || anti) checker++;
            }
        return new Half { Col = outCol, Dep = outDep, X = hx, Row = hr, W = hw, H = hh, Glass = checker > GlassBlocks || partial > GlassBlend * covered };
    }

    /// Edge pixels with colour but no depth sample take their nearest covered
    /// neighbour's.
    static void FillDepth(Color32[] col, ushort[] dep, int w, int h)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            var next = (ushort[])dep.Clone();
            bool any = false;
            for (int r = 0; r < h; r++)
                for (int x = 0; x < w; x++)
                {
                    int i = r * w + x;
                    if (dep[i] != 0 || col[i].a == 0) continue;
                    ushort best = 0;
                    for (int dr = -1; dr <= 1; dr++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int rr = r + dr, xx = x + dx;
                            if (rr < 0 || rr >= h || xx < 0 || xx >= w) continue;
                            var v = dep[rr * w + xx];
                            if (v != 0 && (best == 0 || v < best)) best = v;
                        }
                    if (best != 0) { next[i] = best; any = true; }
                }
            Array.Copy(next, dep, dep.Length);
            if (!any) break;
        }
    }

    /// A transform's pose in the character's frame, to 0.05 mm and 1e-4.
    static string PoseKey(Transform root, Transform t)
    {
        var p = root.InverseTransformPoint(t.position);
        var q = Quaternion.Inverse(root.rotation) * t.rotation;
        if (q.w < 0) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        return $"{Mathf.RoundToInt(p.x * 20000)},{Mathf.RoundToInt(p.y * 20000)},{Mathf.RoundToInt(p.z * 20000)}," +
               $"{Mathf.RoundToInt(q.x * 10000)},{Mathf.RoundToInt(q.y * 10000)},{Mathf.RoundToInt(q.z * 10000)},{Mathf.RoundToInt(q.w * 10000)}";
    }

    /// A short stable name for a placement path (FNV-1a, 32 bits).
    static string Hash(string s)
    {
        uint h = 2166136261;
        foreach (var c in s) { h ^= c; h *= 16777619; }
        return h.ToString("x8");
    }
}

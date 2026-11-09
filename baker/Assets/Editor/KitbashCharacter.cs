// A test render of a dressed character, full body and headshot, to check the pose,
// the skinning and the lighting before baking character sprites.
//
// The character is the menu's (commonprefabs/menuplayer), posed by its animator for
// empty hands (WeaponType -1) at the first frame. A head, body and feet suit are each
// a LoddedSkin whose skinned meshes name their bones; every name is looked up in the
// character's Skeleton. Only the first level of detail is drawn.
//
// The full body is orthographic, under the item icon lights, the model turned 185
// degrees. The headshot is the trader screen's player icon (PlayerIcon).
// Character shaders are kept (the player icon swaps none), so alpha comes from
// rendering over black and over white: alpha = 1 - (white - black).
//
// Worn gear: an item with a DressItem is skinned: its
// DressPrefab's dresses bind to the skeleton like a suit, its LootPrefab hidden. Any
// other item, with its parts on their slot bones, hangs on its slot's bone (the head;
// the right shoulder for an armband) at localRotation (0.5, -0.5, -0.5, -0.5). Then
// what depends on the whole outfit (Variants): the body's, a rig's, a backpack's and
// the head's mesh, the clipped variants of worn items, and a suit's hood. Face shield
// and NVG hinges follow the compositor's rule. Carried weapons hang from their slot's
// bone with Weapon_root on it (CarryBone, Carry); a pistol only on pants with a holster.
//
// Suits are the customization templates in data/character-test.json
// ({"head": id, "body": id, "feet": id}), else USEC defaults; gear is its "equipment",
// items as a profile lists them ({_id, _tpl, slotId, parentId}: an equipment slot name
// and no parent for worn items, a slot of the parent for parts), else a test kit, plus
// "builds": weapon builds by equipment slot, files in data/builds holding a build's
// "items" with the weapon's slotId "hideout" (none by default). Templates come from
// the item base (ItemBase). Writes
// data/render/character/full.png, full_back.png, headshot.png (armed pose) and headshot_unarmed.png.
// Menu: Kitbash! / Character Test Render

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Diz.Skinning;
using EFT.Visual;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using static KitbashConfig;
using Object = UnityEngine.Object;

public static class KitbashCharacter
{
    internal const string MenuPlayer = "assets/content/commonprefabs/menuplayer.bundle";
    internal static readonly Quaternion IconTurn = Quaternion.Euler(0f, 185f, 0f);
    const int SS = 2;
    // The full body: 562.5 px per metre, about 1,070 px tall.
    internal const float FullPpu = 562.5f;

    internal static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["head"] = "5cde96047d6c8b20b577f016",   // DefaultUsecHead
        ["body"] = "5e4bb35286f77406a511c9bc",   // usec_upper_tier_2
        ["feet"] = "5e9dcadd86f7743e20652f23",   // usec_lower_DeepRecon
    };

    // 6B47 helmet, GSSh-01 headset, 6B23-1 armor, BlackRock rig, Pilgrim backpack,
    // ESS goggles, skull balaclava.
    internal const string DefaultEquipment = @"[
        {""_id"": ""helmet"", ""_tpl"": ""5aa7cfc0e5b5b00015693143"", ""slotId"": ""Headwear""},
        {""_id"": ""headset"", ""_tpl"": ""5b432b965acfc47a8774094e"", ""slotId"": ""Earpiece""},
        {""_id"": ""armor"", ""_tpl"": ""5c0e5bab86f77461f55ed1f3"", ""slotId"": ""ArmorVest""},
        {""_id"": ""rig"", ""_tpl"": ""5648a69d4bdc2ded0b8b457b"", ""slotId"": ""TacticalVest""},
        {""_id"": ""backpack"", ""_tpl"": ""59e763f286f7742ee57895da"", ""slotId"": ""Backpack""},
        {""_id"": ""eyewear"", ""_tpl"": ""5d5fca1ea4b93635fd598c07"", ""slotId"": ""Eyewear""},
        {""_id"": ""facecover"", ""_tpl"": ""5ab8f4ff86f77431c60d91ba"", ""slotId"": ""FaceCover""}
    ]";

    // Carried weapons: none unless data/character-test.json lists builds.
    const string DefaultBuilds = "{}";

    static readonly HashSet<string> WeaponSlots = new HashSet<string>
        { "FirstPrimaryWeapon", "SecondPrimaryWeapon", "Holster", "Scabbard" };

    internal static readonly Quaternion BoneAttach = new Quaternion(0.5f, -0.5f, -0.5f, -0.5f);

    /// The item base (scripts/update_items.py): data/items.json when it exists, else
    /// the shipped data/items-base.json. Its "items" and "customization" hold the item
    /// and customization templates, trimmed to the fields the bake reads.
    internal static (JObject items, JObject custom) ItemBase()
    {
        var path = Path.Combine(DataDir, "items.json");
        if (!File.Exists(path)) path = Path.Combine(DataDir, "items-base.json");
        var b = JObject.Parse(File.ReadAllText(path));
        if (!(b["customization"] is JObject custom))
            throw new InvalidOperationException(
                $"{path} has no customization templates: delete data/items.json, git pull, and rerun scripts/update_items.py");
        return ((JObject)b["items"], custom);
    }

    [MenuItem("Kitbash!/Character Test Render")]
    public static void Run()
    {
        var pick = new Dictionary<string, string>(Defaults);
        var equipment = JArray.Parse(DefaultEquipment);
        var builds = JObject.Parse(DefaultBuilds);
        var cfg = Path.Combine(DataDir, "character-test.json");
        if (File.Exists(cfg))
            foreach (var p in JObject.Parse(File.ReadAllText(cfg)).Properties())
                if (p.Name == "equipment") equipment = (JArray)p.Value;
                else if (p.Name == "builds") builds = (JObject)p.Value;
                else pick[p.Name] = (string)p.Value;
        // Weapon builds (data/builds, items under a root in "hideout") into their slots.
        foreach (var b in builds.Properties())
        {
            var buildPath = Path.Combine(DataDir, "builds", (string)b.Value);
            if (!File.Exists(buildPath)) { Debug.LogWarning($"{b.Name}: no build {buildPath}"); continue; }
            var buildItems = (JArray)JObject.Parse(File.ReadAllText(buildPath))["items"];
            var prefix = b.Name + ":";
            foreach (var it in buildItems)
            {
                var c = (JObject)it.DeepClone();
                bool root = (string)c["slotId"] == "hideout";
                c["_id"] = prefix + (string)c["_id"];
                c["parentId"] = root ? JValue.CreateNull() : (JToken)(prefix + (string)c["parentId"]);
                if (root) c["slotId"] = b.Name;
                equipment.Add(c);
            }
        }
        var slots = JObject.Parse(File.ReadAllText(Path.Combine(DataDir, "slots.json")));
        var (items, custom) = ItemBase();

        var outDir = Path.Combine(DataDir, "render", "character");
        Directory.CreateDirectory(outDir);
        var log = new List<string>();
        using (var loader = new BundleLoader(BundleRoot, BundlesJson))
        using (var rig = new KitbashRig())
        {
            loader.Skip.Clear();
            loader.SkipPredicate = k => k.Contains("/audio/") || k.Contains("/effects/");
            loader.AddManifest(Path.Combine(BundleRoot, "Windows.json"));
            GameObject character = null;
            var spawned = new List<GameObject>();
            try
            {
                EditorUtility.DisplayProgressBar("Kitbash! character", "menu character", 0f);
                var prefab = loader.Load(MenuPlayer)?.LoadAllAssets<GameObject>()
                    .FirstOrDefault(a => a.GetComponentInChildren<PlayerBones>(true) != null);
                if (prefab == null) { Debug.LogError($"no character in {MenuPlayer}"); return; }
                character = Object.Instantiate(prefab);
                character.SetActive(true);
                character.transform.SetPositionAndRotation(Vector3.zero, IconTurn);
                // The menu character's own renderers (a ground shadow) are not part of it.
                foreach (var r in character.GetComponentsInChildren<Renderer>(true))
                {
                    log.Add($"character renderer off: {r.name}");
                    r.enabled = false;
                }
                Pose(character, log);

                var skeleton = character.GetComponentsInChildren<Skeleton>(true)
                    .OrderByDescending(s => s.Bones.Count).FirstOrDefault();
                if (skeleton == null) { Debug.LogError("no Skeleton on the character"); return; }
                log.Add($"skeleton {skeleton.name}: {skeleton.Bones.Count} bones; others: " +
                        string.Join(", ", character.GetComponentsInChildren<Skeleton>(true)
                            .Where(s => s != skeleton).Select(s => $"{s.name} {s.Bones.Count}")));

                foreach (var part in new[] { "body", "feet", "head" })
                {
                    EditorUtility.DisplayProgressBar("Kitbash! character", part, 0.3f);
                    var id = pick[part];
                    var key = (string)custom[id]?["_props"]?["Prefab"]?["path"];
                    if (key == null) { log.Add($"{part} {id}: no prefab"); continue; }
                    var asset = loader.Load(key)?.LoadAllAssets<GameObject>()
                        .FirstOrDefault(a => a.GetComponentInChildren<LoddedSkin>(true) != null);
                    if (asset == null) { log.Add($"{part} {key}: no LoddedSkin"); continue; }
                    var suit = Object.Instantiate(asset, character.transform, false);
                    suit.SetActive(true);
                    spawned.Add(suit);
                    log.Add($"{part} {custom[id]["_name"]} ({key}): {Dress(suit, skeleton, log)}");
                }
                AdjustHolster(character, log);

                EditorUtility.DisplayProgressBar("Kitbash! character", "equipment", 0.5f);
                var worn = Equip(loader, character, skeleton, items, slots, equipment, spawned, log);
                bool integrated = (bool?)custom[pick["body"]]?["_props"]?["IntegratedArmorVest"] == true;
                Variants(character, items, worn, integrated, log);

                EditorUtility.DisplayProgressBar("Kitbash! character", "render", 0.7f);
                // Drawn renderers only: the character's own (gizmos, a shadow) are off.
                var b = KitbashRig.RendererBounds(character, r => !r.enabled);
                log.Add($"bounds centre {b.center:F3} size {b.size:F3}");
                foreach (var l in rig.Lights) l.enabled = l.name != "Fill Light";

                // Full body: framed on the bounds at the headshot's density; from the
                // front, then from behind to check what hangs on the back.
                float ppu = FullPpu * SS;
                foreach (var (turn, file) in new[] { (IconTurn, "full.png"), (IconTurn * Quaternion.Euler(0f, 180f, 0f), "full_back.png") })
                {
                    character.transform.rotation = turn;
                    var fb = KitbashRig.RendererBounds(character, r => !r.enabled);
                    int w = Mathf.CeilToInt(fb.size.x * ppu) + 8, h = Mathf.CeilToInt(fb.size.y * ppu) + 8;
                    Save(CropToAlpha(Shoot(rig, new Vector2(fb.center.x, fb.center.y), h / ppu / 2f, w, h), 4),
                         Path.Combine(outDir, file));
                }
                character.transform.rotation = IconTurn;
                // Headshots: the player icon's own camera and lights, in the armed pose
                // (a weapon in the profile) and the empty-hands pose.
                foreach (var l in rig.Lights) l.enabled = false;
                Pose(character, log, -1);
                Save(PlayerIcon.Shoot(character), Path.Combine(outDir, "headshot_unarmed.png"));
                Pose(character, log, 0);
                Save(PlayerIcon.Shoot(character), Path.Combine(outDir, "headshot.png"));

                // Every piece alone in the headshot (the rest neither drawn nor casting
                // shadows), and all of them together, to check compositing by depth.
                EditorUtility.DisplayProgressBar("Kitbash! character", "pieces", 0.9f);
                var pieceDir = Path.Combine(outDir, "pieces");
                Directory.CreateDirectory(pieceDir);
                foreach (var f in Directory.GetFiles(pieceDir)) File.Delete(f);
                var drawn = spawned.Where(s => s != null)
                    .Select(s => (go: s, rs: s.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled).ToList()))
                    .Where(p => p.rs.Count > 0).ToList();
                SaveLayers(PlayerIcon.Capture(character), Path.Combine(pieceDir, "all"));
                for (int i = 0; i < drawn.Count; i++)
                {
                    foreach (var p in drawn) foreach (var r in p.rs) r.enabled = false;
                    foreach (var r in drawn[i].rs) r.enabled = true;
                    var name = $"{i:D2}_{string.Concat(drawn[i].go.name.Replace("(Clone)", "").Select(c => char.IsLetterOrDigit(c) ? c : '_'))}";
                    SaveLayers(PlayerIcon.Capture(character), Path.Combine(pieceDir, name));
                }
                foreach (var p in drawn) foreach (var r in p.rs) r.enabled = true;
                log.Add($"{drawn.Count} pieces -> {pieceDir}");
                log.Add($"-> {outDir}");
            }
            finally
            {
                foreach (var s in spawned) if (s != null) Object.DestroyImmediate(s);
                if (character != null) Object.DestroyImmediate(character);
                EditorUtility.ClearProgressBar();
            }
        }
        Debug.Log("Kitbash! character test render\n" + string.Join("\n", log));
    }

    /// The menu's pose for a weapon type (-1 empty hands, 0 a two-handed firearm),
    /// first frame.
    internal static void Pose(GameObject character, List<string> log, int weaponType = -1)
    {
        var anim = character.GetComponentInChildren<Animator>(true);
        if (anim == null || anim.runtimeAnimatorController == null) { log.Add("no animator: bind pose"); return; }
        anim.enabled = true;
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        anim.Rebind();
        foreach (var p in anim.parameters)
            if (p.name == "WeaponType" || p.name == "WeaponTypeFloat" || p.name == "UI_WeaponType")
            {
                if (p.type == AnimatorControllerParameterType.Int) anim.SetInteger(p.name, weaponType);
                else if (p.type == AnimatorControllerParameterType.Float) anim.SetFloat(p.name, weaponType);
            }
        anim.Update(0f);
        // Left enabled: in edit mode it only moves when updated, and disabling it can
        // reset the pose.
        log.Add($"posed by {anim.runtimeAnimatorController.name}, weapon type {weaponType}");
        AdjustHolster(character, null);
        // Re-skin now, for the new pose.
        foreach (var r in character.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            if (r.enabled) { r.enabled = false; r.enabled = true; }
    }

    /// Pants with a holster (LegsView) pose the holster bone of their leg.
    static void AdjustHolster(GameObject character, List<string> log)
    {
        var legs = character.GetComponentInChildren<LegsView>(true);
        var bones = character.GetComponentInChildren<PlayerBones>(true);
        if (legs == null || legs._holster == null || bones == null) { log?.Add("pants without a holster"); return; }
        var bone = legs._isRightLeg ? bones.HolsterPistol : bones.LeftLegHolsterPistol;
        if (bone == null) return;
        bone.localPosition = legs._holster.localPosition;
        bone.localRotation = legs._holster.localRotation;
        log?.Add($"holster on the {(legs._isRightLeg ? "right" : "left")} leg");
    }

    /// Binds every LoddedSkin in a suit to the skeleton. Returns a one-line report.
    static string Dress(GameObject suit, Skeleton skeleton, List<string> log)
    {
        var tally = new int[3];
        foreach (var lodded in suit.GetComponentsInChildren<LoddedSkin>(true))
            BindLods(lodded.name, lodded._lods, skeleton, tally, log);
        foreach (var g in suit.GetComponentsInChildren<LODGroup>(true)) g.ForceLOD(0);
        return $"{tally[0]} skins, {tally[1]} bones bound, {tally[2]} missing";
    }

    /// Binds each level of detail: every bone name looked up in the
    /// skeleton. The top level drawn, the rest off. tally: skins, bound, missing.
    /// The list can hold more than one set of levels (a suit's hood up and down
    /// are both in it), so a skin is a lower level only when its LOD group puts
    /// it below LOD 0; with no group, or one that would leave nothing drawn (the
    /// zombies' list their only mesh at LOD 1), everything after the first entry is.
    internal static void BindLods(string owner, AbstractSkin[] lods, Skeleton skeleton, int[] tally, List<string> log)
    {
        lods = lods ?? new AbstractSkin[0];
        var group = lods.FirstOrDefault(l => l != null)?.GetComponentInParent<LODGroup>(true);
        var lower = new HashSet<Renderer>(group == null ? new Renderer[0]
            : group.GetLODs().Skip(1).SelectMany(l => l.renderers).Where(r => r != null));
        if (lods.Select(l => Inner(l)?._skinnedMeshRenderer).All(r => r == null || lower.Contains(r))) group = null;
        for (int i = 0; i < lods.Length; i++)
        {
            var skin = Inner(lods[i]);
            if (skin == null || skin._skinnedMeshRenderer == null)
            {
                log.Add($"  {owner} lod {i}: {(lods[i] == null ? "missing" : lods[i].GetType().Name)} not bound");
                continue;
            }
            var smr = skin._skinnedMeshRenderer;
            if (group != null ? lower.Contains(smr) : i > 0) { smr.enabled = false; continue; }
            var bones = new Transform[skin._bonePaths?.Length ?? 0];
            for (int j = 0; j < bones.Length; j++)
            {
                if (skeleton.Bones.TryGetValue(skin._bonePaths[j], out var t)) { bones[j] = t; tally[1]++; }
                else { tally[2]++; if (tally[2] <= 5) log.Add($"  {owner}: no bone {skin._bonePaths[j]}"); }
            }
            smr.bones = bones;
            if (skin._rootBonePath != null && skeleton.Bones.TryGetValue(skin._rootBonePath, out var root))
                smr.rootBone = root;
            smr.updateWhenOffscreen = true;
            // Skin on every render: in edit mode skinning otherwise follows the editor's
            // frames, and a render right after a new pose would show the last one.
            smr.forceMatrixRecalculationPerRender = true;
            smr.enabled = true;
            tally[0]++;
        }
    }

    /// Puts on every worn item (an equipment slot, no parent) with its parts. Returns
    /// the worn items by equipment slot.
    static Dictionary<string, (string tpl, GameObject go)> Equip(BundleLoader loader, GameObject character,
        Skeleton skeleton, JObject items, JObject slots, JArray equipment, List<GameObject> spawned, List<string> log)
    {
        var worn = new Dictionary<string, (string, GameObject)>();
        var bones = character.GetComponentInChildren<PlayerBones>(true);
        var roots = equipment.Where(x => string.IsNullOrEmpty((string)x["parentId"])).ToList();
        var backpack = roots.FirstOrDefault(x => (string)x["slotId"] == "Backpack");
        bool lean = backpack != null && (bool?)items[(string)backpack["_tpl"]]?["_props"]?["LeanWeaponAgainstBody"] == true;
        foreach (var it in roots)
        {
            var slot = (string)it["slotId"];
            var tpl = (string)it["_tpl"];
            var name = (string)items[tpl]?["_name"] ?? tpl;
            if (WeaponSlots.Contains(slot))
            {
                var bone = CarryBone(slot, character, bones, backpack != null, lean);
                if (slot == "Scabbard" && (bool?)items[tpl]?["_props"]?["DisplayOnModel"] != true)
                { log.Add($"{slot} {name}: not drawn on the body"); continue; }
                if (bone == null) { log.Add($"{slot} {name}: no bone to carry it"); continue; }
                var w = SpawnTree(loader, items, slots, equipment, (string)it["_id"], log);
                if (w == null) continue;
                spawned.Add(w);
                log.Add($"{slot} {name}: {Carry(w, bone, (bool?)items[tpl]?["_props"]?["UseAltMountBone"] == true)}");
                // The camera stands 1 km off: left to themselves, LOD groups cull.
                foreach (var g in w.GetComponentsInChildren<LODGroup>(false)) g.ForceLOD(0);
                worn[slot] = (tpl, w);
                continue;
            }
            var go = SpawnTree(loader, items, slots, equipment, (string)it["_id"], log);
            if (go == null) continue;
            spawned.Add(go);
            var dressItem = go.GetComponent<DressItem>();
            if (dressItem != null && dressItem.DressPrefab != null)
            {
                // The dress under the character, the loot model hidden.
                go.transform.SetParent(character.transform, false);
                if (dressItem.LootPrefab != null) dressItem.LootPrefab.SetActive(false);
                dressItem.DressPrefab.SetActive(true);
                var tally = new int[3];
                foreach (var d in dressItem.DressPrefab.GetComponentsInChildren<SkinDress>(true))
                    BindLods(name, d._lods, skeleton, tally, log);
                log.Add($"{slot} {name}: skinned, {tally[0]} skins, {tally[1]} bones bound, {tally[2]} missing");
            }
            else
            {
                var bone = slot == "ArmBand" ? bones?.RightShoulder?.Original : bones?.Head?.Original;
                if (bone == null) { log.Add($"{slot} {name}: no {(slot == "ArmBand" ? "right shoulder" : "head")} bone"); continue; }
                go.transform.SetParent(bone, false);
                go.transform.localRotation = BoneAttach;
                go.transform.localPosition = Vector3.zero;
                log.Add($"{slot} {name}: on {bone.name}, {go.GetComponentsInChildren<Renderer>().Length} renderers");
            }
            foreach (var g in go.GetComponentsInChildren<LODGroup>(false)) g.ForceLOD(0);
            worn[slot] = (tpl, go);
        }
        return worn;
    }

    /// The bone a carried weapon hangs from. A long weapon takes the
    /// alternative bone unless a backpack is worn that does not lean weapons against
    /// the body. A pistol is drawn only on pants with a holster, on their leg.
    static Transform CarryBone(string slot, GameObject character, PlayerBones b, bool backpack, bool lean)
    {
        if (b == null) return null;
        switch (slot)
        {
            case "FirstPrimaryWeapon": return backpack && !lean ? b.HolsterPrimary : b.HolsterPrimaryAlternative;
            case "SecondPrimaryWeapon": return backpack && !lean ? b.HolsterSecondary : b.HolsterSecondaryAlternative;
            case "Scabbard": return b.ScabbardTagillaMelee;
            case "Holster":
                var legs = character.GetComponentInChildren<LegsView>(true);
                return legs == null ? null : legs._isRightLeg ? b.HolsterPistol : b.LeftLegHolsterPistol;
        }
        return null;
    }

    /// A carried weapon on its bone, turned and moved so its
    /// Weapon_root lands on the bone; Weapon_root_alt's offset on top when the template
    /// says so.
    static string Carry(GameObject w, Transform bone, bool altMount)
    {
        var root = BundleLoader.FindBone(w.transform, "Weapon_root");
        if (root == null) return "no Weapon_root";
        var t = w.transform;
        t.SetParent(bone, false);
        t.localRotation = Quaternion.identity;
        t.localPosition = Vector3.zero;
        t.localRotation *= Quaternion.Inverse(root.rotation) * bone.rotation;
        t.position += bone.position - root.position;
        if (altMount)
        {
            var alt = BundleLoader.FindBone(w.transform, "Weapon_root_alt");
            if (alt != null) { t.localPosition += alt.localPosition; t.localRotation *= alt.localRotation; }
        }
        return $"on {bone.name}{(altMount ? " (alt mount)" : "")}, {w.GetComponentsInChildren<Renderer>().Length} renderers";
    }

    /// An item as it is worn: its prefab, and each part's on the bone named
    /// by its slot, at its ModPlacer pose or the default attach rotation. Face shield
    /// and NVG hinges are set as the compositor sets them (HingeOn).
    static GameObject SpawnTree(BundleLoader loader, JObject items, JObject slots, JArray equipment, string id,
                                List<string> log)
    {
        var it = equipment.First(x => (string)x["_id"] == id);
        var tpl = (string)it["_tpl"];
        var props = items[tpl]?["_props"];
        var key = (string)props?["Prefab"]?["path"];
        if (key == null) { log.Add($"{tpl}: no prefab"); return null; }
        var slotIds = (props["Slots"] as JArray)?.Select(s => (string)s["_name"]).ToList() ?? new List<string>();
        var item = loader.LoadItem(key, slotIds);
        if (item.Container == null && item.Model == null) { log.Add($"{tpl}: {key} not loaded"); return null; }
        // The prefab to instantiate: the container (it holds the model, or
        // the DressItem), unless the model lies outside it.
        var src = item.Container != null && (item.Model == null || item.Container.GetComponent<DressItem>() != null
                                              || item.Model.transform.IsChildOf(item.Container.transform))
            ? item.Container : item.Model;
        var go = Object.Instantiate(src);
        foreach (var a in go.GetComponentsInChildren<Animator>(true)) a.enabled = false;
        go.SetActive(true);

        // This item's own hinges, before its parts are attached.
        foreach (var h in go.GetComponentsInChildren<HelmetDress>(true))
        {
            if (h.HingeTransform == null) continue;
            bool on = HingeOn(slots, equipment, id, "faceShield");
            float x = on
                ? (h.Filter?.Forward != null && h.Filter.Forward.length > 0
                    ? h.Filter.Forward.Evaluate(h.Filter.Forward.keys[h.Filter.Forward.length - 1].time) : 1f)
                : (h.Filter?.Backward != null && h.Filter.Backward.length > 0 ? h.Filter.Backward.Evaluate(0f) : 0f);
            h.HingeTransform.localRotation = Quaternion.Lerp(h.OffRotation, h.OnRotation, x);
            log.Add($"  {tpl}: face shield {(on ? "down" : "up")}");
        }
        foreach (var m in go.GetComponentsInChildren<NightVisionMount>(true))
        {
            var r = m._rotator;
            if (r?.RotatedTransform == null) continue;
            bool on = HingeOn(slots, equipment, id, "nightVision");
            r.RotatedTransform.localRotation = on ? r.OnRotation : r.OffRotation;
            log.Add($"  {tpl}: night vision {(on ? "down" : "up")}");
        }

        var children = equipment.Where(x => (string)x["parentId"] == id).ToList();
        var slotBones = children.ToDictionary(c => (string)c["_id"], c => BundleLoader.FindBone(go.transform, (string)c["slotId"]));
        foreach (var c in children)
        {
            var bone = slotBones[(string)c["_id"]];
            if (bone == null) { log.Add($"{tpl}: no bone {(string)c["slotId"]}"); continue; }
            var child = SpawnTree(loader, items, slots, equipment, (string)c["_id"], log);
            if (child == null) continue;
            var placer = child.GetComponent<ModPlacer>();
            child.transform.SetParent(bone, false);
            child.transform.localPosition = placer != null ? placer.ModPosition : Vector3.zero;
            child.transform.localRotation = placer != null ? Quaternion.Euler(placer.ModRotation) : AttachRotation;
        }
        return go;
    }

    /// The compositor's hinge rule (kitbash/compositor.py, hinge_on), over the item and
    /// its parts: a face shield hinge is down unless the first face shield is togglable
    /// and off; an NVG hinge is down while the first togglable is on.
    static bool HingeOn(JObject slots, JArray equipment, string id, string kind)
    {
        IEnumerable<JToken> Merged(string n)
        {
            var node = equipment.First(x => (string)x["_id"] == n);
            yield return node;
            var order = (slots[(string)node["_tpl"]]?["slots"] as JArray)?.Select(s => (string)s["id"]).ToList()
                        ?? new List<string>();
            foreach (var c in equipment.Where(x => (string)x["parentId"] == n && order.Contains((string)x["slotId"]))
                                       .OrderBy(x => order.IndexOf((string)x["slotId"])))
                foreach (var d in Merged((string)c["_id"])) yield return d;
        }
        bool Flag(JToken it, string f) => (bool?)slots[(string)it["_tpl"]]?[f] == true;
        bool IsOn(JToken it) => (bool?)it["upd"]?["Togglable"]?["On"] == true;
        var first = Merged(id).FirstOrDefault(it => Flag(it, kind == "faceShield" ? "faceShield" : "togglable"));
        return kind == "faceShield"
            ? first == null || !Flag(first, "togglable") || IsOn(first)
            : first != null && IsOn(first);
    }

    /// What the whole outfit decides (TorsoSkin, VestSkin, BackpackSkin, HeadSkin,
    /// CustomItem(Skin) with the clipping rules, HoodedDress).
    static void Variants(GameObject character, JObject items, Dictionary<string, (string tpl, GameObject go)> worn,
                         bool integrated, List<string> log)
    {
        JToken Props(string slot) => worn.TryGetValue(slot, out var w) ? items[w.tpl]?["_props"] : null;
        bool armor = worn.ContainsKey("ArmorVest"), rig = worn.ContainsKey("TacticalVest"),
             pack = worn.ContainsKey("Backpack"), headwear = worn.ContainsKey("Headwear"),
             faceCover = worn.ContainsKey("FaceCover");
        bool vestlike = armor && worn["ArmorVest"].go.GetComponentInChildren<VestlikeArmor>(true) != null;
        bool armored = armor || integrated;
        bool fullMask = faceCover && (bool?)Props("FaceCover")?["IsHalfMask"] != true;

        void SetMesh(Skin s, Mesh m, string what)
        {
            if (s?._skinnedMeshRenderer == null || m == null) return;
            s._skinnedMeshRenderer.sharedMesh = m;
            log.Add($"  {what}: {m.name}");
        }
        foreach (var t in character.GetComponentsInChildren<TorsoSkin>(true))
            SetMesh(t._skin, armor && !vestlike ? t._armor : (armor && vestlike) || rig || pack ? t._vest : t._base, "body");
        foreach (var v in character.GetComponentsInChildren<VestSkin>(true))
            SetMesh(v.Skin, armored ? v.Data.Armor : v.Data.Body, "rig");
        foreach (var b in character.GetComponentsInChildren<BackpackSkin>(true))
            SetMesh(b.Skin, armored && rig ? b.Data.ArmorVest : armored ? b.Data.Armor : rig ? b.Data.Vest : b.Data.Body, "backpack");
        foreach (var h in character.GetComponentsInChildren<HeadSkin>(true))
            SetMesh(h.Skin, fullMask ? h.Data.FaceCover : h.Data.Base, "head");

        // What the worn items clip away, then the suits' own rules (ClippingRuleChanger).
        EClippingCustoms rules = 0;
        if (faceCover) rules |= EClippingCustoms.Hat | EClippingCustoms.Helmet | (fullMask ? EClippingCustoms.Visor : 0);
        if (worn.ContainsKey("Eyewear")) rules |= EClippingCustoms.FaceCover | EClippingCustoms.Hat;
        if (headwear && (string)Props("Headwear")?["MaterialType"] != "Helmet") rules |= EClippingCustoms.Headphones;
        if (worn.ContainsKey("Earpiece")) rules |= EClippingCustoms.Hat;
        foreach (var c in character.GetComponentsInChildren<ClippingRuleChanger>(true)) rules |= c._clippingCustoms;
        log.Add($"clipping rules: {rules}");
        foreach (var c in character.GetComponentsInChildren<CustomItemSkin>(true))
            if (c.Data.Base != null && c.Data.Custom != null)
                SetMesh(c.Skin, (rules & c._itemType) != 0 ? c.Data.Custom : c.Data.Base, $"{c.name} ({c._itemType})");
        foreach (var c in character.GetComponentsInChildren<CustomItem>(true))
            if (c._meshFilter != null && c._data.Base != null && c._data.Custom != null)
            {
                c._meshFilter.sharedMesh = (rules & c._itemType) != 0 ? c._data.Custom : c._data.Base;
                log.Add($"  {c.name} ({c._itemType}): {c._meshFilter.sharedMesh.name}");
            }

        // The hood: up unless headwear, or a face cover that takes its place, is worn.
        var fc = Props("FaceCover");
        bool blocks = fc != null && (((fc["ConflictingItems"] as JArray)?.Count ?? 0) > 0 || (bool?)fc["BlocksHeadwear"] == true);
        bool hooded = !headwear && !blocks;
        foreach (var hd in character.GetComponentsInChildren<HoodedDress>(true))
        {
            foreach (var g in hd._hooded ?? new GameObject[0]) if (g != null) g.SetActive(hooded);
            foreach (var g in hd._notHooded ?? new GameObject[0]) if (g != null) g.SetActive(!hooded);
            log.Add($"  hood: {(hooded ? "up" : "down")}");
        }
    }

    internal static Skin Inner(AbstractSkin s)
    {
        switch (s)
        {
            case Skin k: return k;
            case TorsoSkin t: return t._skin;
            case HeadSkin x: return x.Skin;
            case VestSkin x: return x.Skin;
            case BackpackSkin x: return x.Skin;
            case CustomItemSkin x: return x.Skin;
            default: return null;
        }
    }

    /// Renders the frame centred on (x, y) with half-height half, over black and over
    /// white, and returns straight-alpha pixels at half the size (bottom-up rows).
    static Texture2D Shoot(KitbashRig rig, Vector2 centre, float half, int w, int h)
    {
        var cam = rig.Cam;
        cam.transform.position = new Vector3(centre.x, centre.y, -CamDistance);
        cam.orthographicSize = half;
        cam.nearClipPlane = CamDistance - 3f;
        cam.farClipPlane = CamDistance + 3f;
        var bg = cam.backgroundColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        var black = rig.Render(w, h);
        cam.backgroundColor = new Color(1f, 1f, 1f, 0f);
        var white = rig.Render(w, h);
        cam.backgroundColor = bg;
        var pb = black.GetPixels32();
        var pw = white.GetPixels32();
        Object.DestroyImmediate(black);
        Object.DestroyImmediate(white);

        // Premultiplied colour is the render over black; coverage from the difference.
        int ow = w / SS, oh = h / SS;
        var outPx = new Color32[ow * oh];
        for (int y = 0; y < oh; y++)
            for (int x = 0; x < ow; x++)
            {
                float r = 0, g = 0, bl = 0, a = 0;
                for (int dy = 0; dy < SS; dy++)
                    for (int dx = 0; dx < SS; dx++)
                    {
                        int i = (y * SS + dy) * w + x * SS + dx;
                        var k = pb[i];
                        var wh = pw[i];
                        float t = ((wh.r - k.r) + (wh.g - k.g) + (wh.b - k.b)) / (3f * 255f);
                        r += k.r; g += k.g; bl += k.b; a += Mathf.Clamp01(1f - t);
                    }
                float n = SS * SS;
                a /= n;
                var c = a > 0f ? new Color(r / n / 255f / a, g / n / 255f / a, bl / n / 255f / a, a) : Color.clear;
                outPx[y * ow + x] = c;
            }
        var tex = new Texture2D(ow, oh, TextureFormat.RGBA32, false);
        tex.SetPixels32(outPx);
        tex.Apply();
        return tex;
    }

    /// The image cropped to what has alpha, with pad pixels kept around it.
    static Texture2D CropToAlpha(Texture2D t, int pad)
    {
        int w = t.width, h = t.height;
        var px = t.GetPixels32();
        int x0 = w, x1 = -1, y0 = h, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[y * w + x].a > 0)
                {
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                }
        if (x1 < 0) return t;
        x0 = Math.Max(0, x0 - pad); y0 = Math.Max(0, y0 - pad);
        x1 = Math.Min(w - 1, x1 + pad); y1 = Math.Min(h - 1, y1 + pad);
        var c = new Texture2D(x1 - x0 + 1, y1 - y0 + 1, TextureFormat.RGBA32, false);
        c.SetPixels32(CopyRect(px, w, x0, y0, c.width, c.height));
        c.Apply();
        Object.DestroyImmediate(t);
        return c;
    }

    static Color32[] CopyRect(Color32[] px, int w, int x0, int y0, int cw, int ch)
    {
        var o = new Color32[cw * ch];
        for (int r = 0; r < ch; r++) Array.Copy(px, (y0 + r) * w + x0, o, r * cw, cw);
        return o;
    }

    /// path.png: premultiplied colour (the render over black) with alpha from black
    /// and white; path.depth: view depth, float32 little-endian, NoDepth where nothing
    /// drew. Both RenderSize square, rows bottom-up.
    static void SaveLayers(PlayerIcon.Layers l, string path)
    {
        int n = PlayerIcon.RenderSize;
        var px = new Color32[n * n];
        for (int i = 0; i < px.Length; i++)
        {
            var k = l.Black[i];
            px[i] = new Color32(k.r, k.g, k.b, (byte)Mathf.RoundToInt(PlayerIcon.Alpha(k, l.White[i]) * 255f));
        }
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
        t.SetPixels32(px);
        t.Apply();
        Save(t, path + ".png");
        var bytes = new byte[l.Depth.Length * 4];
        Buffer.BlockCopy(l.Depth, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(path + ".depth", bytes);
    }

    static void Save(Texture2D t, string path)
    {
        File.WriteAllBytes(path, t.EncodeToPNG());
        Object.DestroyImmediate(t);
    }
}

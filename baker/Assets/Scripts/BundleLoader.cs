// Loads game AssetBundles by manifest key, resolving the dependency closure from
// data/bundles.json. Every manifest key is a plain file under the bundle root, so
// Path.Combine(root, key) always resolves.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class BundleLoader : IDisposable
{
    readonly string _root;
    readonly Dictionary<string, List<string>> _deps = new Dictionary<string, List<string>>();
    readonly Dictionary<string, AssetBundle> _loaded = new Dictionary<string, AssetBundle>();

    /// Never loaded. None of this contributes to prefab hierarchy or MonoBehaviour
    /// data, and skipping it saves ~400 MB per load.
    public HashSet<string> Skip = new HashSet<string> { "shaders", "cubemaps" };
    public Func<string, bool> SkipPredicate = k =>
        k.Contains("/audio/") || k.Contains("/animations/") || k.Contains("/effects/");

    public BundleLoader(string bundleRoot, string bundlesJsonPath)
    {
        _root = bundleRoot;
        var closure = (JObject)JObject.Parse(File.ReadAllText(bundlesJsonPath))["closure"];
        foreach (var kv in closure)
            _deps[kv.Key] = ((JArray)kv.Value).Select(d => (string)d).ToList();
    }

    /// Adds the dependencies of bundles outside data/bundles.json (which holds only
    /// the items' closure) from the client's own manifest, Windows.json.
    public void AddManifest(string windowsJsonPath)
    {
        foreach (var kv in JObject.Parse(File.ReadAllText(windowsJsonPath)))
            if (!_deps.ContainsKey(kv.Key) && kv.Value["Dependencies"] is JArray d)
                _deps[kv.Key] = d.Select(x => (string)x).ToList();
    }

    public IEnumerable<string> LoadedKeys => _loaded.Keys;

    public IEnumerable<string> DepsOf(string key) =>
        _deps.TryGetValue(key, out var d) ? d : Enumerable.Empty<string>();

    bool Skipped(string key) => Skip.Contains(key) || (SkipPredicate != null && SkipPredicate(key));

    void LoadClosure(string key, HashSet<string> seen)
    {
        if (!seen.Add(key) || Skipped(key) || _loaded.ContainsKey(key)) return;
        if (_deps.TryGetValue(key, out var deps))
            foreach (var d in deps) LoadClosure(d, seen);

        var path = Path.Combine(_root, key);
        if (!File.Exists(path)) { Debug.LogWarning($"missing bundle: {key}"); return; }
        var bundle = AssetBundle.LoadFromFile(path);
        if (bundle == null) { Debug.LogWarning($"failed to load: {key}"); return; }
        _loaded[key] = bundle;
    }

    /// Loads a bundle and its dependency closure. Null if it failed.
    public AssetBundle Load(string key)
    {
        LoadClosure(key, new HashSet<string>());
        return _loaded.TryGetValue(key, out var b) ? b : null;
    }

    /// A weapon's icon-framing container and its model are separate assets, and are
    /// not always in the same bundle: variant weapons (RPDN, AUG blk) carry only a
    /// container and reuse a base weapon's model from a dependency. So rather than
    /// guess by name, score every candidate by how many of the template's declared
    /// slot bones it actually contains. The winner is the model by definition.
    public struct Item
    {
        public GameObject Container;   // PreviewPivot lives here
        public GameObject Model;       // mod_* bones live here
        public string ModelName;
        public int BonesFound;
        public int BonesWanted;
        public bool FromDependency;
        public bool Ok => Model != null;
    }

    /// Every GameObject LoadItem considers for a key: the bundle's own, plus those in
    /// sibling-container and same-directory dependencies.
    public List<(GameObject go, bool fromDep)> Candidates(string key, GameObject[] own = null)
    {
        own = own ?? Load(key)?.LoadAllAssets<GameObject>() ?? new GameObject[0];
        var candidates = own.Select(g => (go: g, fromDep: false)).ToList();

        // Variant weapons reuse a base model held in a sibling container bundle.
        var dir = key.Substring(0, key.LastIndexOf('/') + 1);
        foreach (var dep in DepsOf(key))
        {
            if (Skipped(dep)) continue;
            if (!dep.EndsWith("_container.bundle", StringComparison.OrdinalIgnoreCase)
                && !dep.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
            var db = Load(dep);
            if (db == null) continue;
            candidates.AddRange(db.LoadAllAssets<GameObject>().Select(g => (go: g, fromDep: true)));
        }
        return candidates;
    }

    public Item LoadItem(string key, IList<string> slotIds)
    {
        var result = new Item { BonesWanted = slotIds?.Count ?? 0 };
        var bundle = Load(key);
        if (bundle == null) return result;

        var own = bundle.LoadAllAssets<GameObject>();
        if (own.Length == 0) return result;

        var wanted = Path.GetFileNameWithoutExtension(key);
        result.Container = own.FirstOrDefault(g => string.Equals(g.name, wanted, StringComparison.OrdinalIgnoreCase))
                        ?? own.FirstOrDefault(g => g.GetComponentInChildren<PreviewPivot>(true) != null)
                        ?? own[0];

        // A weapon container names its model (WeaponPrefab._weaponObject); that is
        // the object the game instantiates, so it wins over any ranking.
        var wp = result.Container.GetComponent<WeaponPrefab>();
        if (wp != null && wp._weaponObject != null)
        {
            var go = wp._weaponObject;
            var names = new HashSet<string>(go.GetComponentsInChildren<Transform>(true).Select(t => t.name),
                                            StringComparer.OrdinalIgnoreCase);
            result.Model = go;
            result.ModelName = go.name;
            result.BonesFound = slotIds?.Count(names.Contains) ?? 0;
            result.FromDependency = !own.Contains(go);
            return result;
        }

        var candidates = Candidates(key, own);

        // Ranked by bones found, then whether any renderer has a mesh, then the
        // item's own bundle over a dependency, then how many renderers have a mesh,
        // then size.
        // - Some weapons (MP-443, P226R) ship a ".generated" model whose meshes are
        //   built at runtime: every bone, no geometry. It would win on size.
        // - Dependencies hold source and animation rigs with every bone plus extra
        //   parts and placeholder materials (AKM with a GP-34, MCX rendered white).
        //   The item's own "_model.generated" is the in-game model. Variants with no
        //   model of their own (RPDN, AUG blk) still fall through to a dependency.
        // - A weapon's ".generated" can be the first-person rig: arm bones inflate its
        //   size while most of its renderers have no mesh (SR-1MP: 1 of 7, no slide).
        // - On a full tie, "_model" wins: the MXLR ships a ".generated" that matches its
        //   "_model.generated" in every count but has its stock and hammer bones posed.
        int bestScore = -1, bestSize = -1, bestMeshed = -1;
        bool bestMesh = false, bestOwn = false, bestNamed = false;
        foreach (var (go, fromDep) in candidates)
        {
            var transforms = go.GetComponentsInChildren<Transform>(true);
            int score = 0;
            if (slotIds != null && slotIds.Count > 0)
            {
                var names = new HashSet<string>(transforms.Select(t => t.name), StringComparer.OrdinalIgnoreCase);
                foreach (var id in slotIds) if (names.Contains(id)) score++;
            }
            int meshed = go.GetComponentsInChildren<Renderer>(true).Count(HasMesh);
            bool mesh = meshed > 0, inOwn = !fromDep;
            int size = transforms.Length;
            bool named = go.name.IndexOf("_model", StringComparison.OrdinalIgnoreCase) >= 0;
            bool better = score != bestScore ? score > bestScore
                        : mesh != bestMesh ? mesh
                        : inOwn != bestOwn ? inOwn
                        : meshed != bestMeshed ? meshed > bestMeshed
                        : size != bestSize ? size > bestSize
                        : named && !bestNamed;
            if (better)
            {
                bestScore = score; bestSize = size; bestMesh = mesh; bestOwn = inOwn;
                bestMeshed = meshed; bestNamed = named;
                result.Model = go; result.ModelName = go.name;
                result.BonesFound = score; result.FromDependency = fromDep;
            }
        }
        return result;
    }

    public static bool HasMesh(Renderer r) =>
        r is SkinnedMeshRenderer sk ? sk.sharedMesh != null
        : r is MeshRenderer && r.TryGetComponent(out MeshFilter f) && f.sharedMesh != null;

    /// Unloads every loaded bundle except those listed and their dependency closures.
    /// Instances from the unloaded bundles must be destroyed first.
    public void UnloadExcept(IEnumerable<string> keep)
    {
        var closure = new HashSet<string>();
        void Add(string k)
        {
            if (!closure.Add(k)) return;
            foreach (var d in DepsOf(k)) Add(d);
        }
        foreach (var k in keep) Add(k);
        foreach (var k in _loaded.Keys.Where(k => !closure.Contains(k)).ToList())
        {
            if (_loaded[k] != null) _loaded[k].Unload(true);
            _loaded.Remove(k);
        }
    }

    public int LoadedCount => _loaded.Count;

    public void Dispose()
    {
        foreach (var b in _loaded.Values) if (b != null) b.Unload(true);
        _loaded.Clear();
        Resources.UnloadUnusedAssets();
    }

    /// Recursive, case-insensitive transform lookup - mirrors how the game resolves
    /// a slot id to its attachment bone.
    public static Transform FindBone(Transform root, string name)
    {
        if (string.Equals(root.name, name, StringComparison.OrdinalIgnoreCase)) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var hit = FindBone(root.GetChild(i), name);
            if (hit != null) return hit;
        }
        return null;
    }
}

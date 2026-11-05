// The icon camera, lights and shader swap used by the bake.
//
// Dispose restores RenderSettings and destroys everything created, including the
// swapped materials.

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using static KitbashConfig;
using Object = UnityEngine.Object;

public class KitbashRig : IDisposable
{
    public readonly Camera Cam;
    public readonly List<Light> Lights = new List<Light>();
    readonly Transform _root;

    // The game's "_Icon" shader variants exist to write ALPHA for the icon cutout;
    // the stock p0/* opaque shaders leave it at zero. Without this swap every render
    // is fully transparent and any compositing silently becomes a no-op.
    readonly Dictionary<string, Shader> _shaders = new Dictionary<string, Shader>();
    public int Swapped, FellBack;

    // Swapped copies, keyed by source material. Built from sharedMaterials with
    // new Material() rather than renderer.materials, which logs a leak warning per
    // call in edit mode.
    readonly Dictionary<Material, Material> _swaps = new Dictionary<Material, Material>();

    readonly List<GameObject> _spawned = new List<GameObject>();

    readonly AmbientMode _ambientMode;
    readonly Color _ambientLight, _ambientSky, _ambientEquator, _ambientGround;
    readonly float _ambientIntensity;
    readonly SphericalHarmonicsL2 _ambientProbe;
    readonly bool _fog;

    public KitbashRig()
    {
        _ambientMode = RenderSettings.ambientMode;
        _ambientLight = RenderSettings.ambientLight;
        _ambientSky = RenderSettings.ambientSkyColor;
        _ambientEquator = RenderSettings.ambientEquatorColor;
        _ambientGround = RenderSettings.ambientGroundColor;
        _ambientIntensity = RenderSettings.ambientIntensity;
        _ambientProbe = RenderSettings.ambientProbe;
        _fog = RenderSettings.fog;
        // The game's IconRenderSettings.Reset(). The probe is zeroed explicitly: in
        // edit mode setting the colours does not regenerate it.
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = Color.black;
        RenderSettings.ambientSkyColor = Color.black;
        RenderSettings.ambientEquatorColor = Color.black;
        RenderSettings.ambientGroundColor = Color.black;
        RenderSettings.ambientIntensity = 0f;
        RenderSettings.ambientProbe = new SphericalHarmonicsL2();
        RenderSettings.fog = false;

        _root = new GameObject("KitbashRig").transform;

        var camGO = new GameObject("IconCamera");
        camGO.transform.SetParent(_root, false);
        camGO.transform.position = new Vector3(0f, 0f, -CamDistance);
        camGO.transform.rotation = Quaternion.identity;
        Cam = camGO.AddComponent<Camera>();
        Cam.orthographic = true;
        Cam.clearFlags = CameraClearFlags.Color;
        Cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        Cam.useOcclusionCulling = false;
        // As the game's IconCamera prefab: forward, LDR.
        Cam.renderingPath = RenderingPath.Forward;
        Cam.allowHDR = false;
        SetClip(CamDistance);
        Cam.enabled = false;

        // The game's own rig: the three child lights of Resources/IconCamera
        // (resources.assets). That camera sits at identity rotation, so these local
        // rotations are world rotations here too. Intensities are fitted to 12 game
        // icons: the key per pixel at
        // 0.7x, the hair folded into ambient SH at 0.75x, the fill off. That gives
        // brightness x1.03, contrast x0.90 and the game's blue/red balance (x0.97);
        // the hair per pixel instead draws a hard cyan rim the game doesn't have.
        AddLight("Key Light", new Quaternion(0.43866229f, 0.03020835f, 0.14241691f, 0.88678086f),
                 Color.white, 1.24f * 0.7f, true, LightRenderMode.Auto);
        AddLight("Fill Light", new Quaternion(0.23751970f, -0.44608897f, 0.26779628f, 0.82028908f),
                 Color.white, 0.39f, false, LightRenderMode.Auto);
        AddLight("Hair Light", new Quaternion(-0.17590034f, -0.55845559f, -0.80096382f, 0.12507433f),
                 new Color(0.51470590f, 0.89959419f, 1f), 0.91f * 0.75f, true, LightRenderMode.ForceVertex);
    }

    /// Clip tightly around the model. Orthographic depth is linear, so a clip range
    /// of 2000 m in a 16-bit buffer is 3 cm per step: internal parts z-fought
    /// through surfaces. The game's own camera spans 0.3-16.35 m.
    const float ClipHalfDepth = 3f;

    void SetClip(float camDistance)
    {
        Cam.nearClipPlane = Mathf.Max(0.01f, camDistance - ClipHalfDepth);
        Cam.farClipPlane = camDistance + ClipHalfDepth;
    }

    void AddLight(string name, Quaternion rotation, Color color, float intensity, bool on,
                  LightRenderMode mode)
    {
        var l = new GameObject(name).AddComponent<Light>();
        l.transform.SetParent(_root, false);
        l.type = LightType.Directional;
        l.transform.localRotation = rotation;
        l.color = color;
        l.intensity = intensity;
        l.shadows = LightShadows.None;
        l.enabled = on;
        l.renderMode = mode;
        Lights.Add(l);
    }

    /// Indexes every shader in the "shaders" bundle by name. Shader.Find cannot see
    /// them - nothing ever calls LoadAllAssets on that bundle - so we index it here,
    /// mirroring the game's own ShadersFinder dictionary. The loader must not skip
    /// "shaders" or "cubemaps".
    public bool LoadShaders(BundleLoader loader)
    {
        var bundle = loader.Load("shaders");
        if (bundle == null) { Debug.LogError("shaders bundle failed to load"); return false; }
        foreach (var s in bundle.LoadAllAssets<Shader>()) _shaders[s.name] = s;
        Debug.Log($"shaders indexed: {_shaders.Count}, of which _Icon variants: " +
                  $"{_shaders.Keys.Count(k => k.EndsWith("_Icon"))}");
        return true;
    }

    /// Instantiates a prefab at the origin, ready to render.
    public GameObject Spawn(GameObject prefab)
    {
        var go = Object.Instantiate(prefab);
        go.transform.SetParent(null, false);
        go.transform.localScale = Vector3.one;
        foreach (var a in go.GetComponentsInChildren<Animator>(true)) a.enabled = false;
        ReplaceShaders(go);
        go.SetActive(true);
        // After activation: forcing a LOD on an inactive group (the M995 round's
        // prefab is saved inactive) is ignored, and every LOD level would draw.
        foreach (var g in go.GetComponentsInChildren<LODGroup>(true)) g.ForceLOD(0);
        _spawned.Add(go);
        return go;
    }

    /// BeltMagazineInHandsVisual.RefreshVisualParts: a belt magazine's spent links
    /// (empty_link_*) start hidden, loaded or not.
    public static void HideSpentBelt(GameObject go)
    {
        foreach (var t in go.GetComponentsInChildren<Transform>(true))
            if (t.name.Contains("empty_link_")) t.gameObject.SetActive(false);
    }

    /// A magazine's round anchors in the order the game fills them: every
    /// transform named *patron_<n>*, breadth first (FindAllTransformsWithNameContains),
    /// reversed for belts (BeltMagazineInHandsVisual). Round i goes to anchor i.
    public static List<(int n, Transform t)> RoundAnchors(Transform root, bool belt)
    {
        var found = new List<(int, Transform)>();
        var queue = new Queue<Transform>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (t.name.Contains("patron_") && int.TryParse(t.name.Replace("patron_", ""), out var n))
                found.Add((n, t));
            for (int i = 0; i < t.childCount; i++) queue.Enqueue(t.GetChild(i));
        }
        if (belt) found.Reverse();
        return found;
    }

    /// AmmoPoolObject.SetUsed(false), as every loaded round is drawn: *shell*
    /// objects off, then *full* objects on.
    public static void ShowFullRound(GameObject go)
    {
        var all = go.GetComponentsInChildren<Transform>(true);
        foreach (var t in all) if (t.name.Contains("shell")) t.gameObject.SetActive(false);
        foreach (var t in all) if (t.name.Contains("full")) t.gameObject.SetActive(true);
    }

    public void Destroy(GameObject go)
    {
        _spawned.Remove(go);
        if (go != null) Object.DestroyImmediate(go);
    }

    /// Port of the game's ShaderReplacer. Never mutates the bundle's materials.
    void ReplaceShaders(GameObject model)
    {
        foreach (var r in model.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            var changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null || m.shader == null) continue;
                var n = m.shader.name;
                if (n == "Diffuse" || n == "Standard" || n.Contains("_Icon")) continue;

                if (!_swaps.TryGetValue(m, out var copy))
                {
                    Shader repl;
                    if (_shaders.TryGetValue(n + "_Icon", out repl)) Swapped++;
                    else if (_shaders.TryGetValue(FallbackIconShader, out repl)) FellBack++;
                    else continue;
                    copy = new Material(m) { shader = repl };
                    _swaps[m] = copy;
                }
                mats[i] = copy;
                changed = true;
            }
            if (changed) r.sharedMaterials = mats;
        }
    }

    /// Destroys spawned objects and swapped materials. Call before unloading the
    /// bundles they came from.
    public void ReleaseInstances()
    {
        foreach (var g in _spawned) if (g != null) Object.DestroyImmediate(g);
        _spawned.Clear();
        foreach (var m in _swaps.Values) if (m != null) Object.DestroyImmediate(m);
        _swaps.Clear();
    }

    public void Dispose()
    {
        ReleaseInstances();
        if (_root != null) Object.DestroyImmediate(_root.gameObject);
        RenderSettings.ambientMode = _ambientMode;
        RenderSettings.ambientLight = _ambientLight;
        RenderSettings.ambientSkyColor = _ambientSky;
        RenderSettings.ambientEquatorColor = _ambientEquator;
        RenderSettings.ambientGroundColor = _ambientGround;
        RenderSettings.ambientIntensity = _ambientIntensity;
        RenderSettings.ambientProbe = _ambientProbe;
        RenderSettings.fog = _fog;
    }

    /// Renders w x h at 8x MSAA and reads back. Rows are bottom-up, as Unity stores
    /// them. Colour is effectively premultiplied at edges: MSAA resolves against a
    /// transparent black clear.
    public Texture2D Render(int w, int h)
    {
        var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32,
                                            RenderTextureReadWrite.Default, 8);
        var resolved = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        Cam.targetTexture = rt;
        Cam.aspect = (float)w / h;
        Cam.Render();
        Graphics.Blit(rt, resolved);

        var prev = RenderTexture.active;
        RenderTexture.active = resolved;
        var tex = new Texture2D(w, h, TextureFormat.ARGB32, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;

        Cam.targetTexture = null;
        RenderTexture.ReleaseTemporary(rt);
        RenderTexture.ReleaseTemporary(resolved);
        return tex;
    }

    public class Capture
    {
        public Texture2D Color;     // cropped, premultiplied
        public ushort[] Depth;      // same crop, Unity row order; 0 = no coverage
        public Vector2 Anchor;      // world (0,0) in image pixels, x right, y down
        // Same crop: how much of what lies behind shows through, 0..255, where that
        // differs from 1 - alpha (blended glass: Transparent/DepthZwriteDithered_Icon
        // writes alpha 1 in one pass and blends RGB over the background in another).
        // Null when every pixel is plain coverage.
        public byte[] Transmit;
    }

    /// Frames on everything visible at PixelsPerUnit, renders colour and depth, and
    /// crops both to the alpha bbox. The camera is snapped to the world pixel grid,
    /// so the origin always lands on a pixel corner and Anchor is whole. Null, with
    /// err, on an empty render.
    public Capture RenderCropped(GameObject go, out string err, float camDistance = CamDistance)
    {
        err = null;
        var b = RendererBounds(go);
        if (b.extents == Vector3.zero) { err = "no renderers"; return null; }
        if (b.min.z < -ClipHalfDepth || b.max.z > ClipHalfDepth)
        {
            err = $"depth {b.min.z:F2}..{b.max.z:F2} outside the clip range";
            return null;
        }

        float ppu = PixelsPerUnit;
        int cx = Mathf.RoundToInt(b.center.x * ppu), cy = Mathf.RoundToInt(b.center.y * ppu);
        int hw = Mathf.CeilToInt(Mathf.Max(b.max.x * ppu - cx, cx - b.min.x * ppu)) + SpritePad;
        int hh = Mathf.CeilToInt(Mathf.Max(b.max.y * ppu - cy, cy - b.min.y * ppu)) + SpritePad;
        int w = 2 * hw, h = 2 * hh;
        if (w > MaxTextureDim || h > MaxTextureDim) { err = $"too large: {w}x{h}"; return null; }

        Cam.transform.position = new Vector3(cx / ppu, cy / ppu, -camDistance);
        SetClip(camDistance);
        Cam.orthographicSize = hh / ppu;
        var tex = Render(w, h);
        // The same frame over white: per pixel, white - black is what the background
        // contributes, the transmittance.
        var bg = Cam.backgroundColor;
        Cam.backgroundColor = new Color(1f, 1f, 1f, 0f);
        var white = Render(w, h);
        Cam.backgroundColor = bg;
        try
        {
            var z = RenderDepth(w, h);
            var wpx = white.GetPixels32();

            // Unity rows are bottom-up; image y is top-down.
            var px = tex.GetPixels32();
            int x0 = w, x1 = -1, r0 = h, r1 = -1;
            for (int r = 0; r < h; r++)
                for (int x = 0; x < w; x++)
                    if (px[r * w + x].a > 0)
                    {
                        if (x < x0) x0 = x;
                        if (x > x1) x1 = x;
                        if (r < r0) r0 = r;
                        if (r > r1) r1 = r;
                    }
            // An empty render composites as a silent no-op. Refuse it.
            if (x1 < 0) { err = "empty alpha (shader swap failed?)"; return null; }

            int cw = x1 - x0 + 1, ch = r1 - r0 + 1;
            int top = h - 1 - r1;   // image-space y of the crop's top row
            var crop = new Color32[cw * ch];
            var zc = new float[cw * ch];
            var wc = new Color32[cw * ch];
            for (int r = 0; r < ch; r++)
            {
                Array.Copy(px, (r0 + r) * w + x0, crop, r * cw, cw);
                Array.Copy(z, (r0 + r) * w + x0, zc, r * cw, cw);
                Array.Copy(wpx, (r0 + r) * w + x0, wc, r * cw, cw);
            }
            var cropped = new Texture2D(cw, ch, TextureFormat.ARGB32, false);
            cropped.SetPixels32(crop);
            cropped.Apply();

            return new Capture
            {
                Color = cropped,
                Depth = QuantiseDepth(crop, zc, cw, ch),
                Transmit = Transmittance(crop, wc),
                // World (0,0) lands at (hw - cx, hh + cy) in the uncropped image.
                Anchor = new Vector2(hw - cx - x0, hh + cy - top),
            };
        }
        finally { Object.DestroyImmediate(tex); Object.DestroyImmediate(white); }
    }

    /// Per pixel, the mean of white - black over RGB; null when it is 1 - alpha
    /// everywhere within MSAA and encoding noise.
    static byte[] Transmittance(Color32[] black, Color32[] white)
    {
        var t = new byte[black.Length];
        int differ = 0;
        for (int i = 0; i < t.Length; i++)
        {
            int d = (white[i].r - black[i].r + white[i].g - black[i].g + white[i].b - black[i].b + 1) / 3;
            t[i] = (byte)Mathf.Clamp(d, 0, 255);
            if (Math.Abs(t[i] - (255 - black[i].a)) > 8) differ++;
        }
        return differ > 16 ? t : null;
    }

    Shader _depthShader;
    const float NoDepth = 1e6f;

    /// World z per pixel, NoDepth where nothing drew. No MSAA: one surface per pixel.
    float[] RenderDepth(int w, int h)
    {
        if (_depthShader == null) _depthShader = Shader.Find("Kitbash/Depth");
        if (_depthShader == null) throw new InvalidOperationException("Kitbash/Depth shader not found");

        var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.RFloat,
                                            RenderTextureReadWrite.Linear, 1);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(true, true, new Color(NoDepth, 0f, 0f, 0f));
        var flags = Cam.clearFlags;
        Cam.clearFlags = CameraClearFlags.Nothing;
        Cam.targetTexture = rt;
        Cam.RenderWithShader(_depthShader, "");
        Cam.targetTexture = null;
        Cam.clearFlags = flags;

        RenderTexture.active = rt;
        var t = new Texture2D(w, h, TextureFormat.RFloat, false, true);
        t.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        t.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        var data = t.GetRawTextureData<float>().ToArray();
        Object.DestroyImmediate(t);
        return data;
    }

    /// Encodes z as round(z * DepthScale) + 32768, 0 meaning no coverage. MSAA edge
    /// pixels have colour but can miss the single depth sample, so fill those from
    /// covered neighbours, nearest surface first.
    static ushort[] QuantiseDepth(Color32[] color, float[] z, int w, int h)
    {
        var q = new ushort[w * h];
        for (int i = 0; i < q.Length; i++)
            if (z[i] < NoDepth * 0.5f)
                q[i] = (ushort)Mathf.Clamp(Mathf.RoundToInt(z[i] * DepthScale) + 32768, 1, 65535);

        for (int pass = 0; pass < 3; pass++)
        {
            var next = (ushort[])q.Clone();
            bool any = false;
            for (int r = 0; r < h; r++)
                for (int x = 0; x < w; x++)
                {
                    int i = r * w + x;
                    if (q[i] != 0 || color[i].a == 0) continue;
                    ushort best = 0;
                    for (int dr = -1; dr <= 1; dr++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int rr = r + dr, xx = x + dx;
                            if (rr < 0 || rr >= h || xx < 0 || xx >= w) continue;
                            var v = q[rr * w + xx];
                            if (v != 0 && (best == 0 || v < best)) best = v;
                        }
                    if (best != 0) { next[i] = best; any = true; }
                }
            q = next;
            if (!any) break;
        }
        return q;
    }

    /// 16-bit depth packed into a PNG as R = high byte, G = low byte.
    public static byte[] EncodeDepth(ushort[] q, int w, int h)
    {
        var px = new Color32[q.Length];
        for (int i = 0; i < q.Length; i++)
            px[i] = new Color32((byte)(q[i] >> 8), (byte)(q[i] & 0xFF), 0, 255);
        var t = new Texture2D(w, h, TextureFormat.RGB24, false, true);
        t.SetPixels32(px);
        t.Apply();
        var png = t.EncodeToPNG();
        Object.DestroyImmediate(t);
        return png;
    }

    public static float MaxAlpha(Texture2D t)
    {
        byte max = 0;
        foreach (var p in t.GetPixels32()) if (p.a > max) max = p.a;
        return max / 255f;
    }

    /// A skinned renderer that has never been drawn can report empty bounds in edit
    /// mode; fall back to its mesh's bounds in its own transform.
    static Bounds BoundsOf(Renderer r)
    {
        var b = r.bounds;
        if (b.extents != Vector3.zero || !(r is SkinnedMeshRenderer s) || s.sharedMesh == null) return b;
        var m = s.sharedMesh.bounds;
        var t = s.transform.localToWorldMatrix;
        var acc = new Bounds(t.MultiplyPoint3x4(m.min), Vector3.zero);
        for (int i = 1; i < 8; i++)
            acc.Encapsulate(t.MultiplyPoint3x4(new Vector3(
                (i & 1) != 0 ? m.max.x : m.min.x,
                (i & 2) != 0 ? m.max.y : m.min.y,
                (i & 4) != 0 ? m.max.z : m.min.z)));
        return acc;
    }

    /// For failure reports: what renderers a model has and why none counted.
    public static string DescribeRenderers(GameObject go)
    {
        var all = go.GetComponentsInChildren<Renderer>(true);
        var types = string.Join(",", all.GroupBy(r => r.GetType().Name).Select(g => $"{g.Key}x{g.Count()}"));
        int inactive = all.Count(r => !r.gameObject.activeInHierarchy);
        int disabled = all.Count(r => !r.enabled);
        int zero = all.Count(r => BoundsOf(r).extents == Vector3.zero);
        int noMesh = all.OfType<MeshRenderer>().Count(r => { var f = r.GetComponent<MeshFilter>(); return f == null || f.sharedMesh == null; })
                   + all.OfType<SkinnedMeshRenderer>().Count(r => r.sharedMesh == null);
        return $"{all.Length} renderers [{types}], inactive {inactive}, disabled {disabled}, " +
               $"zero bounds {zero}, no mesh {noMesh}";
    }

    /// Union of active renderer bounds.
    public static Bounds RendererBounds(GameObject go, Func<Renderer, bool> exclude = null)
    {
        var rs = go.GetComponentsInChildren<Renderer>(false)
            .Where(r => exclude == null || !exclude(r))
            .Select(BoundsOf)
            .Where(b => b.extents != Vector3.zero)
            .ToList();
        if (rs.Count == 0) return new Bounds();
        var acc = rs[0];
        for (int i = 1; i < rs.Count; i++) acc.Encapsulate(rs[i]);
        return acc;
    }
}

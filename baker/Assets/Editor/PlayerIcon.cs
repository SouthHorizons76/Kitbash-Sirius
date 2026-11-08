// The player icon: the trader screen's headshot.
//
// Its settings asset (PlayerIconCreatorSettings in resources.assets) chooses the
// perspective camera: field of view 12, clip 0.3-10, deferred shading, 500 x 500. The
// model sits at (0.01, -1.68, 2.0) in the camera's frame, turned 180 degrees about y
// (facing the camera). The camera's prefab (PlayerIconCamera) carries the lights; only
// its "Final 2" group is active, the seven lights below, in the camera's frame. Their
// culling mask is a preview layer the model is moved to in game; here they light
// every layer. Ambient is black, both projects are in gamma
// colour space, and no material is retuned (every meshMaterialSettings entry is off).
//
// The game renders at twice the size into an 8x MSAA target, but deferred shading
// ignores MSAA, so its only anti-aliasing is the halving; so here.
//
// The game lights through its own deferred shader (Hidden/Internal-DeferredShadingEFT,
// in globalgamemanagers.assets, which the editor cannot load), and its faces come out
// darker than these lights give under Unity's. LightScale scales every light to match.

using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public static class PlayerIcon
{
    public const int Size = 500;
    const int SS = 2;
    const float Fov = 12f, Near = 0.3f, Far = 10f;
    static readonly Vector3 ModelPosition = new Vector3(0.01f, -1.68f, 2.0f);
    static readonly Quaternion ModelRotation = Quaternion.Euler(0f, 180f, 0f);

    struct L
    {
        public string Name; public LightType Type; public Vector3 Pos; public Quaternion Rot;
        public Color Color; public float Intensity, Range, Spot, InnerSpot;
        public LightShadows Shadows; public float Strength, NormalBias, NearPlane;
        public LightShadowResolution Resolution;
    }

    static L Def(string name, LightType type, Vector3 pos, Quaternion rot, Color color, float intensity,
                   float range, float spot, float inner, LightShadows shadows, float strength, float nearPlane,
                   LightShadowResolution res) => new L
    {
        Name = name, Type = type, Pos = pos, Rot = rot, Color = color, Intensity = intensity, Range = range,
        Spot = spot, InnerSpot = inner, Shadows = shadows, Strength = strength, NormalBias = 0.1f,
        NearPlane = nearPlane, Resolution = res,
    };

    // PlayerIconCamera / Final 2.
    static readonly L[] Lights =
    {
        Def("Top Accent", LightType.Spot, new Vector3(0.3580f, 1.5640f, 2.4990f),
              new Quaternion(0.739367f, 0.016590f, -0.125926f, 0.661214f), Color.white, 6f, 2.5f, 23f, 1f,
              LightShadows.Soft, 1f, 0.025f, LightShadowResolution.Low),
        Def("Top Main", LightType.Directional, new Vector3(0.3580f, 1.5640f, 2.8050f),
              new Quaternion(0.790474f, -0.032047f, -0.213825f, 0.573064f), Color.white, 0.4f, 2.207277f, 23f, 1f,
              LightShadows.Soft, 1f, 0.022073f, LightShadowResolution.Medium),
        Def("Contur left", LightType.Spot, new Vector3(-0.8090f, 1.3270f, 3.4740f),
              new Quaternion(-0.052259f, 0.906636f, -0.417355f, 0.033080f), Color.white, 13f, 3f, 39.6471f, 21.80208f,
              LightShadows.Soft, 1f, 0.06f, LightShadowResolution.Low),
        Def("Contur right", LightType.Point, new Vector3(0.9490f, -0.2330f, 3.1930f),
              new Quaternion(-0.009387f, 0.986990f, -0.129018f, 0.095479f), Color.white, 1.5f, 2.300987f, 30f, 21.80208f,
              LightShadows.Soft, 1f, 0.06f, LightShadowResolution.Low),
        Def("Face right", LightType.Point, new Vector3(0.3260f, 0.1390f, 1.9490f),
              new Quaternion(0f, 0.125465f, 0f, 0.992098f), Color.white, 1.8f, 1.2f, 30f, 21.80208f,
              LightShadows.Soft, 1f, 0.012f, LightShadowResolution.High),
        Def("Shadows", LightType.Spot, new Vector3(0.4580f, 0.2400f, 0.3080f),
              new Quaternion(0.071839f, -0.042835f, -0.003433f, 0.996490f), new Color(1f, 0.9877f, 0.9387f), 8f, 3f,
              35.64355f, 4f, LightShadows.Soft, 0.96f, 0.07f, LightShadowResolution.High),
        Def("Direct", LightType.Spot, Vector3.zero, Quaternion.identity, Color.white, 1.3f, 7f, 40f, 4f,
              LightShadows.None, 0.5f, 0.07f, LightShadowResolution.Low),
    };

    /// Every light's intensity times this; fitted to the game's cached icons: on the
    /// same head (DefaultUsecHead), 0.6 gives the face's mean colour within 5/255 per
    /// channel and its clipped share 0.44 against the game's 0.40.
    public const float LightScale = 0.6f;

    /// The icon's render size before halving.
    public const int RenderSize = Size * SS;

    /// What is drawn, at RenderSize: colour over black (premultiplied colour),
    /// colour over white, and view depth in metres (NoDepth where nothing drew).
    /// Rows bottom-up.
    public struct Layers { public Color32[] Black, White; public float[] Depth; }

    public const float NoDepth = 1e6f;

    /// Places the model in the icon's frame, renders it over black and over white, and
    /// returns straight-alpha 500 x 500 pixels. Everything created is destroyed.
    public static Texture2D Shoot(GameObject model, float lightScale = LightScale)
    {
        var l = Capture(model, lightScale, false);
        int n = RenderSize;
        var outPx = new Color32[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int dy = 0; dy < SS; dy++)
                    for (int dx = 0; dx < SS; dx++)
                    {
                        int i = (y * SS + dy) * n + x * SS + dx;
                        var k = l.Black[i];
                        r += k.r; g += k.g; b += k.b; a += Alpha(k, l.White[i]);
                    }
                float m = SS * SS;
                a /= m;
                outPx[y * Size + x] = a > 0f
                    ? (Color32)new Color(r / m / 255f / a, g / m / 255f / a, b / m / 255f / a, a)
                    : new Color32(0, 0, 0, 0);
            }
        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
        tex.SetPixels32(outPx);
        tex.Apply();
        return tex;
    }

    /// Coverage from the render over black and over white: alpha = 1 - (white - black).
    public static float Alpha(Color32 black, Color32 white) =>
        Mathf.Clamp01(1f - ((white.r - black.r) + (white.g - black.g) + (white.b - black.b)) / (3f * 255f));

    /// The full-size layers, with depth, for compositing pieces.
    public static Layers Capture(GameObject model, float lightScale = LightScale, bool depth = true)
    {
        var root = new GameObject("PlayerIconCamera");
        float shadowDistance = QualitySettings.shadowDistance;
        var shadowQuality = QualitySettings.shadows;
        var shadowProjection = QualitySettings.shadowProjection;
        bool streaming = QualitySettings.streamingMipmapsActive;
        try
        {
            // Full textures always: streaming loads mip levels by what is in view, so a
            // piece alone would sample different detail than among the others.
            QualitySettings.streamingMipmapsActive = false;
            QualitySettings.shadowDistance = 20f;
            QualitySettings.shadows = ShadowQuality.All;
            // Directional shadow maps fitted to the camera, not to what is in view, so a
            // piece rendered alone shadows itself exactly as it does among the others.
            QualitySettings.shadowProjection = ShadowProjection.StableFit;

            var cam = root.AddComponent<Camera>();
            cam.orthographic = false;
            cam.fieldOfView = Fov;
            cam.nearClipPlane = Near;
            cam.farClipPlane = Far;
            cam.renderingPath = RenderingPath.DeferredShading;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.cullingMask = ~0;
            cam.enabled = false;
            foreach (var l in Lights)
            {
                var go = new GameObject(l.Name);
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = l.Pos;
                go.transform.localRotation = l.Rot;
                var light = go.AddComponent<Light>();
                light.type = l.Type;
                light.color = l.Color;
                light.intensity = l.Intensity * lightScale;
                light.range = l.Range;
                light.spotAngle = l.Spot;
                light.innerSpotAngle = l.InnerSpot;
                light.shadows = l.Shadows;
                light.shadowStrength = l.Strength;
                light.shadowBias = 0f;
                light.shadowNormalBias = l.NormalBias;
                light.shadowNearPlane = l.NearPlane;
                light.shadowResolution = l.Resolution;
                light.bounceIntensity = 0f;
                light.cullingMask = ~0;
                light.renderMode = LightRenderMode.ForcePixel;
            }

            model.transform.SetPositionAndRotation(root.transform.TransformPoint(ModelPosition), ModelRotation);

            int n = RenderSize;
            return new Layers
            {
                Black = Render(cam, n, n, new Color(0f, 0f, 0f, 0f)),
                White = Render(cam, n, n, new Color(1f, 1f, 1f, 0f)),
                Depth = depth ? RenderDepth(cam, n, n) : null,
            };
        }
        finally
        {
            QualitySettings.shadowDistance = shadowDistance;
            QualitySettings.shadows = shadowQuality;
            QualitySettings.shadowProjection = shadowProjection;
            QualitySettings.streamingMipmapsActive = streaming;
            Object.DestroyImmediate(root);
        }
    }

    /// The camera sits at the origin looking down +z, so world z is view depth.
    internal static float[] RenderDepth(Camera cam, int w, int h)
    {
        var shader = Shader.Find("Kitbash/Depth");
        if (shader == null) throw new InvalidOperationException("Kitbash/Depth shader not found");
        var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear, 1);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(true, true, new Color(NoDepth, 0f, 0f, 0f));
        var path = cam.renderingPath;
        var flags = cam.clearFlags;
        cam.renderingPath = RenderingPath.Forward;
        cam.clearFlags = CameraClearFlags.Nothing;
        cam.targetTexture = rt;
        cam.aspect = (float)w / h;
        cam.RenderWithShader(shader, "");
        cam.targetTexture = null;
        cam.clearFlags = flags;
        cam.renderingPath = path;
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

    internal static Color32[] Render(Camera cam, int w, int h, Color background)
    {
        var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 1);
        cam.backgroundColor = background;
        cam.targetTexture = rt;
        cam.aspect = (float)w / h;
        cam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.ARGB32, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        cam.targetTexture = null;
        RenderTexture.ReleaseTemporary(rt);
        var px = tex.GetPixels32();
        Object.DestroyImmediate(tex);
        return px;
    }
}

// Paths and constants shared by every Kitbash! editor script.
//
// GameRoot can be overridden with the KITBASH_GAME_ROOT environment variable.

using System;
using System.IO;
using UnityEngine;

public static class KitbashConfig
{
    public static readonly string GameRoot =
        Environment.GetEnvironmentVariable("KITBASH_GAME_ROOT") ?? @"C:\Battlestate Games\Escape from Tarkov";

    public static string BundleRoot =>
        Path.Combine(GameRoot, @"EscapeFromTarkov_Data\StreamingAssets\Windows");

    /// The client version of GameRoot as the game prints it (1.1.5.0.47242), read from
    /// the exe's ProductVersion (1.1.5.0-47242-bca02170). Null if the exe is missing.
    public static string GameVersion()
    {
        var exe = Path.Combine(GameRoot, "EscapeFromTarkov.exe");
        if (!File.Exists(exe)) return null;
        var product = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Trim();
        if (string.IsNullOrEmpty(product)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(product, @"^(\d+(?:\.\d+)+)-(\d+)");
        return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}" : product;
    }
    public static string RepoRoot =>
        Path.GetFullPath(Path.Combine(Application.dataPath, @"..\.."));
    public static string DataDir => Path.Combine(RepoRoot, "data");
    public static string SpritesDir => Path.Combine(RepoRoot, "sprites");
    public static string BundlesJson => Path.Combine(DataDir, "bundles.json");

    /// Bundles are unloaded every ChunkSize prefabs. The full closure is ~6 GB, so
    /// holding it all would exhaust the editor.
    public const int ChunkSize = 40;

    /// The game's icon rotation, identical for all 177 weapons (see data/inspect.json).
    public static readonly Quaternion IconRotation =
        new Quaternion(0f, 0.8433914f, 0f, -0.537299633f);

    /// How a mod sits on its slot bone: localPosition zero, this localRotation. The
    /// game's fallback when there is no ModPlacer, and every ModPlacer is default.
    public static readonly Quaternion AttachRotation = Quaternion.Euler(90f, 0f, 0f);

    /// Unity derives specular and reflection from _WorldSpaceCameraPos even under an
    /// orthographic projection, so moving a part changes its highlights. Pushing the
    /// camera far back makes view vectors near-parallel and the shading effectively
    /// position-independent, which is what sprite reuse requires.
    public const float CamDistance = 1000f;

    /// Bake scale, identical for every sprite. 800 px per world unit at 1x covers
    /// the densest icons (pistols, ~600 px/m). x2 over that: visually identical to
    /// x4 at icon size, ~4x fewer pixels to composite and ship. Raise for larger
    /// output; a full re-bake takes about a minute.
    public const int Supersample = 2;
    public const float PixelsPerUnit = 800f * Supersample;
    public const int SpritePad = 2;   // transparent border, in sprite pixels, before crop
    public const int MaxTextureDim = 16384;   // Unity's hard limit

    /// Depth maps store round(z * DepthScale) + 32768 in 16 bits: 0.05 mm steps,
    /// +-1.6 m around the part origin.
    public const float DepthScale = 20000f;

    /// The game sizes icons as (cells * 63 + 1). Largest is 6x2 cells.
    public const int CellPx = 63;
    public const int IconW = 6 * CellPx + 1, IconH = 2 * CellPx + 1;   // 379 x 127

    public const string FallbackIconShader = "p0/Reflective/Bumped Specular SMap_Icon";

    /// Renderers the game leaves out of icon framing bounds.
    public static bool ExcludedFromBounds(Renderer r) =>
        r.name.Contains("linza") || r.name == "MuzzleJetCombinedMesh";
}

// Inspect Prefabs, Measure Bone Rotations (weapons + mods) and Inspect Icon Poses in
// one run: everything scripts/orientations.py reads after data/slots.json changes.
// Menu: Kitbash! / Inspect All

using UnityEditor;

public static class KitbashInspectAll
{
    [MenuItem("Kitbash!/Inspect All")]
    public static void Run()
    {
        KitbashInspector.Run();
        KitbashBones.RunAll();
        KitbashIconPoses.Run();
    }
}

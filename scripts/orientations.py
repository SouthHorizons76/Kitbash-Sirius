"""Enumerate every (model, orientation) a part can be rendered at.

A part's orientation relative to the weapon root is the product down its attach
chain: hostOrient * boneRot(host, slot) * attach(part), where attach is
Euler(ModRotation) for a mod with a ModPlacer and Euler(90,0,0) otherwise. Walks the whole
attachment graph from every weapon and writes data/orientations.json, which the
baker renders from and the compositor keys sprites by.

    python scripts/orientations.py
    python scripts/orientations.py --only <weapon tpl> ...   # also a bake subset

With data/icon-poses.json (Kitbash! / Inspect Icon Poses), it also walks:
  - a folded item's parts, on its slot bones as FoldableStockView /
    RetractableStockView pose them (the game's icons of a folded item);
  - every headwear template from its icon pose, PreviewPivot.Icon.rotation, as a
    root (the game's icon of a helmet shows everything on it), with each face
    shield / NVG hinge (HelmetDress, NightVisionMount) in both its poses;
  - with --lone-parts, every part that can hold others from its own icon pose,
    PreviewPivot.Icon.rotation, as the game draws it on its own. Without it, the
    manifest still gets each part's pose, and the compositor draws a lone part
    whenever its sprites already exist.

--only writes data/orientations.subset.json: the (model, orientation index) pairs
reachable from those weapons, indices into the full list, for test bakes.
"""

import argparse
import json
import math
from collections import defaultdict
from pathlib import Path

DATA = Path(__file__).resolve().parent.parent / "data"

# Quaternions are (x, y, z, w), Unity convention.
IDENTITY = (0.0, 0.0, 0.0, 1.0)
S = math.sqrt(0.5)
E90 = (S, 0.0, 0.0, S)  # Quaternion.Euler(90, 0, 0)
# The weapons' PreviewPivot.Icon.rotation, which every sprite is rendered at times q.
ICON_ROTATION = (0.0, 0.8433914, 0.0, -0.537299633)


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
        aw * bw - ax * bx - ay * by - az * bz,
    )


def qnorm(q):
    n = math.sqrt(sum(c * c for c in q))
    return tuple(c / n for c in q)


# Orientations closer than this are one sprite. Chained products leave float noise
# (<0.001 deg) and some bones carry sub-0.05 deg modelling tilt; at the largest
# icon (379 px) this moves a part's far end by at most ~0.33 px.
MERGE_DEG = 0.05


def euler(x, y, z):
    """Unity's Quaternion.Euler: rotates z, then x, then y (degrees)."""
    def axis(ax, deg):
        h = math.radians(deg) / 2
        v = [0.0, 0.0, 0.0]
        v[ax] = math.sin(h)
        return (v[0], v[1], v[2], math.cos(h))
    return qmul(qmul(axis(1, y), axis(0, x)), axis(2, z))


def orient_key(q):
    """Walk dedupe only, ~0.01 deg. Sprites are matched by angle, never by key."""
    if q[3] < 0:
        q = tuple(-c for c in q)
    return ",".join(f"{c + 0.0:.4f}" for c in q)


def qinv(q):
    return (-q[0], -q[1], -q[2], q[3])


def angle_deg(a, b):
    dot = abs(sum(x * y for x, y in zip(a, b)))
    return math.degrees(2 * math.acos(min(1.0, dot)))


def cluster(qs, reps=None):
    """Greedy, in a fixed order, so ids are stable across runs. Existing reps keep
    their indices (the bake writes sprites by index); new ones are appended."""
    baked = list(reps or [])
    reps = list(baked)
    for q in qs:
        # The compositor takes the nearest sprite within 2 * MERGE_DEG, so an
        # already baked one that close serves q.
        if any(angle_deg(q, r) < 2 * MERGE_DEG for r in baked):
            continue
        if not any(angle_deg(q, r) < MERGE_DEG for r in reps):
            reps.append(q)
    return reps


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", nargs="+", metavar="TPL")
    ap.add_argument("--lone-parts", action="store_true",
                    help="also bake every pose a part's children take when it is drawn on its own")
    ap.add_argument("--new", action="store_true",
                    help="write data/orientations.subset.json with only the orientations the manifest "
                         "has no sprite for, plus every orientation of a model with a folded state")
    args = ap.parse_args()

    slots = json.loads((DATA / "slots.json").read_text())
    inspect = json.loads((DATA / "inspect.json").read_text())
    bones_all = json.loads((DATA / "bones-all.json").read_text())

    model_of = {e["tpl"]: e["model"] for e in inspect["weapons"] + inspect["mods"] if e.get("model")}
    # A ModPlacer replaces the default attach rotation. Verified against game
    # renders (SCAR stock, P90 shell, Desert Eagle slide): its values are used as-is.
    placed = [e for e in inspect["mods"] if e.get("modPlacer")]
    attach = {e["tpl"]: qnorm(euler(*e["rot"])) for e in placed}
    if any(any(abs(c) > 1e-6 for c in e["pos"]) for e in placed):
        raise SystemExit("a ModPlacer has a non-zero ModPosition; the bake does not handle that yet")
    bones = {e["tpl"]: e["bones"] for e in bones_all["weapons"]}
    # A magazine's round anchors; the game sets each round to Euler(90,0,0) on its
    # anchor regardless of ModPlacer (SpringMagazineVisual, BeltMagazineInHandsVisual).
    rounds = {e["tpl"]: e["rounds"] for e in bones_all["weapons"] if e.get("rounds")}

    poses_path = DATA / "icon-poses.json"
    poses = json.loads(poses_path.read_text())["templates"] if poses_path.exists() else {}
    # Fold sets. A foldable item's fold views pose bones by name, found width first
    # through the whole assembled item: in its own model, or in a part attached to
    # it (partFolds). Views with equal bones and values are one set; a set gives
    # every model it reaches a folded state, and posed slot bones for its parts.
    signature = {}
    for t, e in poses.items():
        if slots.get(t, {}).get("foldable") and e.get("fold"):
            signature[t] = tuple(sorted((f["bone"].lower(), f["kind"], tuple(round(c, 6) for c in f["folded"]))
                                        for f in e["fold"]))
    set_ids = {sig: str(i) for i, sig in enumerate(sorted(set(signature.values())))}
    fold_sets = {t: set_ids[sig] for t, sig in sorted(signature.items())}
    model_folds = defaultdict(dict)    # model -> set id -> the bones it poses there
    folded_bones = defaultdict(list)   # tpl -> slot bones as posed by each set reaching it
    strip = lambda fs: [{"bone": f["bone"], "kind": f["kind"], "folded": f["folded"]} for f in fs]
    for t, sid in fold_sets.items():
        own = [f for f in poses[t]["fold"] if f["foundInModel"]]
        if own and t in model_of:
            model_folds[model_of[t]][sid] = strip(own)
            folded_bones[t].append(poses[t]["foldedBones"])
    # Hinges (HelmetDress, NightVisionMount): an icon item snaps each to its on or off
    # pose, which moves the slot bones under it. One hinge per model so far.
    model_hinges = {}                  # model -> {kind, path, on, off}
    hinge_kinds = {}                   # tpl -> kind
    for t, e in poses.items():
        hs = e.get("hinges") or []
        if not hs or t not in model_of:
            continue
        if len(hs) > 1:
            raise SystemExit(f"{t}: {len(hs)} hinges in one model; the bake handles one")
        h = hs[0]
        model_hinges[model_of[t]] = {k: h[k] for k in ("kind", "path", "on", "off")}
        hinge_kinds[t] = h["kind"]
        folded_bones[t].extend([h["onBones"], h["offBones"]])
    def reachable(tpl):
        seen, stack = set(), [tpl]
        while stack:
            for slot in slots.get(stack.pop(), {}).get("slots", ()):
                for c in slot["accepts"]:
                    if c not in seen:
                        seen.add(c)
                        stack.append(c)
        return seen
    under = {t: reachable(t) for t in fold_sets}
    for t, e in poses.items():
        for owner, pf in (e.get("partFolds") or {}).items():
            # A bone of that name in a part that cannot be attached to it never folds.
            if owner in fold_sets and t in model_of and t in under[owner]:
                model_folds[model_of[t]][fold_sets[owner]] = strip(pf["fold"])
                folded_bones[t].append(pf["foldedBones"])
    # A part drawn on its own: the icon camera sets its loot prefab's root to
    # Icon.rotation (ItemIconCreator.PoseModelByPivot). Relative to the weapon frame
    # the sprites are rendered in, its model is at inv(ICON_ROTATION) * Icon.rotation
    # * the model's rotation in its prefab. Only parts that can hold others need it.
    pivots = {}
    for t, e in poses.items():
        v = slots.get(t)
        if not v or v["isWeapon"] or "iconRotation" not in e or not (v["slots"] or v.get("cartridges")):
            continue
        place = e.get("modelInPrefab")
        if place == "root":
            inner = IDENTITY
        elif place == "child":
            inner = tuple(e["modelRotation"])
        else:
            continue
        pivots[t] = qnorm(qmul(qmul(qinv(ICON_ROTATION), tuple(e["iconRotation"])), inner))

    # model -> key -> exact quaternion (first seen); model -> tpls using it
    seen = defaultdict(dict)
    tpls_of = defaultdict(set)
    visited = set()
    roots_of = defaultdict(set)  # (model, key) -> weapon roots reaching it

    def walk(tpl, q, root):
        model = model_of.get(tpl)
        if model is None:
            return
        key = orient_key(q)
        roots_of[(model, key)].add(root)
        if (tpl, key, root) in visited:
            return
        visited.add((tpl, key, root))
        seen[model].setdefault(key, q)
        tpls_of[model].add(tpl)
        for posed in (bones.get(tpl, {}), *folded_bones.get(tpl, ())):
            for slot in slots[tpl]["slots"]:
                bone = posed.get(slot["id"])
                if bone is None:
                    continue
                bone_q = qmul(q, tuple(bone["rot"]))
                for child in slot["accepts"]:
                    if child in slots:
                        walk(child, qnorm(qmul(bone_q, attach.get(child, E90))), root)
        mag = slots[tpl].get("cartridges")
        for anchor in rounds.get(tpl, []) if mag else []:
            round_q = qnorm(qmul(qmul(q, tuple(anchor["rot"])), E90))
            for ammo in mag["accepts"]:
                if ammo in slots:
                    walk(ammo, round_q, root)

    for tpl, v in slots.items():
        if v["isWeapon"] and tpl in model_of:
            walk(tpl, IDENTITY, tpl)
    for tpl, q in sorted(pivots.items()):
        if slots[tpl].get("isHeadwear"):
            walk(tpl, q, tpl)
    if args.lone_parts:
        for tpl, q in sorted(pivots.items()):
            walk(tpl, q, tpl)

    prev_path = DATA / "orientations.json"
    prev = json.loads(prev_path.read_text())["models"] if prev_path.exists() else {}
    # Keep the indices the manifest has sprites under; later ones were never baked.
    manifest_path = DATA / "sprites.manifest.json"
    baked = json.loads(manifest_path.read_text())["models"] if manifest_path.exists() else {}
    prev_reps = {m: [tuple(q) for q in v["orientations"][:len(baked.get(m, []))]] for m, v in prev.items()}
    reps = {m: cluster([q for _, q in sorted(seen[m].items())], prev_reps.get(m)) for m in seen}
    folding_models = set(model_folds) | set(model_hinges)
    hist = defaultdict(int)
    for r in reps.values():
        hist[len(r)] += 1
    total = sum(len(r) for r in reps.values())

    out = {
        "version": 1,
        "mergeDeg": MERGE_DEG,
        "defaultAttach": list(E90),
        "attach": {t: [round(c, 7) for c in q] for t, q in sorted(attach.items())},
        # PreviewPivot.Icon.boundsScale: how much of the icon a weapon's bounds fill.
        "iconScale": {
            **{t: poses[t]["boundsScale"] for t in sorted(pivots) if poses[t].get("boundsScale", 1) != 1},
            **{e["tpl"]: e["boundsScale"] for e in sorted(inspect["weapons"], key=lambda e: e["tpl"])},
        },
        # A part's root pose when drawn on its own; weapons are at identity.
        "pivots": {t: [round(c, 7) for c in q] for t, q in sorted(pivots.items())},
        # A foldable template's fold set (FoldableStockView / RetractableStockView).
        "foldSets": fold_sets,
        # Per model, the bones each fold set poses in it, for the bake.
        "folds": {m: dict(sorted(v.items(), key=lambda kv: int(kv[0]))) for m, v in sorted(model_folds.items())},
        # A template's hinge kind: "faceShield" (HelmetDress) or "nightVision" (NightVisionMount).
        "hinges": dict(sorted(hinge_kinds.items())),
        # Per model, its hinge's transform path and local rotations, for the bake.
        "modelHinges": dict(sorted(model_hinges.items())),
        "models": {
            m: {
                "tpls": sorted(tpls_of[m]),
                "orientations": [[round(c, 7) for c in q] for q in reps[m]],
            }
            for m in sorted(seen)
        },
    }
    (DATA / "orientations.json").write_text(json.dumps(out, indent=1))

    if args.new:
        subset = {m: sorted({i for i in range(len(r)) if i >= len(baked.get(m, [])) or baked[m][i] is None}
                            | (set(range(len(r))) if m in folding_models else set()))
                  for m, r in reps.items()}
        subset = {m: v for m, v in sorted(subset.items()) if v}
        (DATA / "orientations.subset.json").write_text(json.dumps(
            {"weapons": [], "models": subset}, indent=1))
        print(f"new: {len(subset)} models, {sum(len(v) for v in subset.values())} sprites")

    if args.only:
        want = set(args.only)
        subset = defaultdict(set)
        for m, keys in seen.items():
            for k, q in keys.items():
                if roots_of[(m, k)] & want:
                    i = min(range(len(reps[m])), key=lambda j: angle_deg(q, reps[m][j]))
                    subset[m].add(i)
        (DATA / "orientations.subset.json").write_text(json.dumps(
            {"weapons": sorted(want), "models": {m: sorted(v) for m, v in sorted(subset.items())}},
            indent=1))
        print(f"subset: {len(subset)} models, {sum(len(v) for v in subset.values())} sprites")

    print(f"distinct part models  {len(seen)}")
    for n in sorted(hist):
        print(f"  {n:3d} orientations   {hist[n]}")
    print(f"total sprites         {total}   (raw keys {sum(len(k) for k in seen.values())})")


if __name__ == "__main__":
    main()

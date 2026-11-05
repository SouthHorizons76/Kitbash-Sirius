"""Derive Kitbash!'s data tables from the item base and a game install.

Emits:

  data/slots.json    slot topology and prefab paths, keyed by BSG template id
                     (armor slots left out: the game draws nothing in them)
  data/bundles.json  the AssetBundle dependency closure the baker must load
  data/sizes.json    inventory cell sizes, for the icon size of a build
  data/sights.json   SightMod templates, "iron" or "sight", for auto-folding sights
  data/ammo.json     magazine capacities and chambers, and the ammo each accepts

  data/tables-version.txt  the client the item base is for

None contains game assets - only derived tables (ids, slot names, paths,
dependency edges).

Usage:
    python scripts/update_items.py              # optional: bring the item base up to date
    python scripts/extract_data.py [--game "C:/Battlestate Games/Escape from Tarkov"] [--items <file>]

--items defaults to data/items.json (update_items.py's output) when it exists, else the
shipped data/items-base.json. A plain {template id: template} file works too.
"""

import argparse
import json
import os
from pathlib import Path

WEAPON_PARENT = "5422acb9af1c889c16000029"  # BSG's "Weapon" base node
# (not 5447b5f1... - that is AssaultRifle, only one of its twelve children)
# The game's SightMod and IronSight classes (EFT.InventoryLogic JsonTypes).
SIGHT_PARENT = "5448fe7a4bdc2d6f028b456b"
IRON_SIGHT_PARENT = "55818ac54bdc2d5b648b456e"
AMMO_PARENT = "5485a8684bdc2da71d8b4567"
HEADWEAR_PARENT = "5a341c4086f77401f2541505"  # helmets, caps, masks worn on the head
# TogglableComponent holders (EFT.InventoryLogic): NightVision and ThermalVision always,
# ArmoredEquipment when its template HasHinge.
NIGHT_VISION_PARENT = "5a2c3a9486f774688b05e574"
THERMAL_VISION_PARENT = "5d21f59b6dbe99052b54ef83"
ARMORED_EQUIPMENT_PARENT = "57bef4c42459772e8d35a53b"


DATA = Path(__file__).resolve().parent.parent / "data"
DEFAULT_GAME = Path(os.environ.get("KITBASH_GAME_ROOT", "C:/Battlestate Games/Escape from Tarkov"))


def load(game: Path, items_path: Path):
    """(templates, Windows.json, client version or None). The item base wraps its
    templates with the client they are for; a plain templates file has no version."""
    raw = json.loads(items_path.read_text(encoding="utf-8"))
    version = None
    if isinstance(raw.get("items"), dict) and "version" in raw:
        raw, version = raw["items"], raw["version"]
    manifest = json.loads(
        (game / "EscapeFromTarkov_Data/StreamingAssets/Windows/Windows.json").read_text(encoding="utf-8")
    )
    return raw, manifest, version


def descends_from(items, tpl, ancestor):
    seen = set()
    while tpl in items and tpl not in seen:
        seen.add(tpl)
        if tpl == ancestor:
            return True
        tpl = items[tpl].get("_parent")
    return False


def armor_slot(slot):
    """An ArmorSlot (built-in helmet / vest inserts): the game draws nothing in it
    (ObjectsFactory.AttachMods skips ArmorPlate contents, and ArmorSlots have no bone)."""
    return any("armorColliders" in f for f in slot["_props"]["filters"])


def drawn_slots(props):
    return [s for s in props.get("Slots", []) if not armor_slot(s)]


def accepts(entry):
    return sorted({t for f in entry["_props"]["filters"] for t in f["Filter"]})


def chambers(props):
    return [c for c in props.get("Chambers") or [] if accepts(c)]


def cartridges(props):
    """A magazine's single cartridge stack, if it holds any rounds."""
    c = props.get("Cartridges") or []
    return c[0] if c and c[0].get("_max_count", 0) > 0 and accepts(c[0]) else None


def collect(items):
    """Weapons, headwear that can hold something drawn (a helmet's icon shows its NVG,
    face shield, mounts: ItemIconCreator renders the whole item), plus every template
    transitively reachable through drawn slot, chamber and cartridge filters."""
    weapons = {t for t in items if descends_from(items, t, WEAPON_PARENT)}
    headwear = {t for t in items if items[t].get("_type") == "Item"
                and descends_from(items, t, HEADWEAR_PARENT) and drawn_slots(items[t].get("_props", {}))}
    mods, frontier = set(), weapons | headwear
    while frontier:
        nxt = set()
        for tpl in frontier:
            props = items.get(tpl, {}).get("_props", {})
            holders = drawn_slots(props) + chambers(props)
            if cartridges(props):
                holders.append(cartridges(props))
            for holder in holders:
                for candidate in accepts(holder):
                    if candidate in items and candidate not in mods:
                        mods.add(candidate)
                        nxt.add(candidate)
        frontier = nxt
    return weapons, headwear, mods - headwear


def togglable(items, tpl):
    return (descends_from(items, tpl, NIGHT_VISION_PARENT) or descends_from(items, tpl, THERMAL_VISION_PARENT)
            or (descends_from(items, tpl, ARMORED_EQUIPMENT_PARENT) and items[tpl]["_props"].get("HasHinge")))


def slot_table(items, templates, weapons, headwear):
    out = {}
    for tpl in sorted(templates):
        props = items[tpl].get("_props", {})
        prefab = props.get("Prefab", {}).get("path", "")
        if not prefab:
            continue
        mag = cartridges(props)
        out[tpl] = {
            "name": items[tpl].get("_name", ""),
            "prefab": prefab,
            "isWeapon": tpl in weapons,
            # a root like a weapon, drawn at its PreviewPivot icon pose
            **({"isHeadwear": True} if tpl in headwear else {}),
            # the game gives these a BeltMagazineInHandsVisual
            **({"belt": True} if props.get("MagazineWithBelt") else {}),
            # has a FoldableComponent: its icon is drawn folded while folded
            **({"foldable": True} if props.get("Foldable") else {}),
            **({"ammo": True} if descends_from(items, tpl, AMMO_PARENT) else {}),
            # has a TogglableComponent (TurnOn / TurnOff): face shield and NVG hinges
            # follow it (HelmetDress, NightVisionMount), and IconsHash keys on it
            **({"togglable": True} if togglable(items, tpl) else {}),
            # has a FaceShieldComponent (ArmoredEquipment): HelmetDress looks for it
            **({"faceShield": True} if descends_from(items, tpl, ARMORED_EQUIPMENT_PARENT)
               and props.get("FaceShieldComponent") else {}),
            **({"cartridges": {"capacity": mag["_max_count"], "accepts": accepts(mag)}} if mag else {}),
            # slot name == the transform name the baker looks up in the prefab.
            # Chambers are slots too: the game attaches a chambered round to its bone.
            "slots": [
                {"id": s["_name"], "required": bool(s.get("_required")), "accepts": sorted(s["_props"]["filters"][0]["Filter"])}
                for s in drawn_slots(props)
            ] + [
                {"id": c["_name"], "required": False, "accepts": accepts(c), "chamber": True}
                for c in chambers(props)
            ],
        }
    return out


def ammo_table(slots):
    """What the compositor needs to load a build: every magazine's capacity and
    accepted rounds, and every chamber slot's accepted rounds."""
    mags = {t: v["cartridges"] for t, v in slots.items() if "cartridges" in v}
    chambered = {t: {s["id"]: s["accepts"] for s in v["slots"] if s.get("chamber")}
                 for t, v in slots.items() if any(s.get("chamber") for s in v["slots"])}
    return {"magazines": mags, "chambers": chambered}


def size_table(items, templates):
    """Per template: base cells, ExtraSize (left, right, up, down), whether it is
    force-added, and whether its attachments count towards the size ("own": the
    template does not merge with its children; the game's slots all inherit this). Only
    non-default fields are written."""
    out = {}
    for tpl in sorted(templates):
        props = items[tpl].get("_props", {})
        e = {"cells": [props.get("Width", 1), props.get("Height", 1)]}
        extra = [props.get(f"ExtraSize{k}", 0) for k in ("Left", "Right", "Up", "Down")]
        if any(extra):
            e["extra"] = extra
        if props.get("ExtraSizeForceAdd"):
            e["force"] = True
        if not props.get("MergesWithChildren", True):
            e["own"] = True
        out[tpl] = e
    return out


def sight_table(items, templates):
    """Per SightMod template: "iron" for IronSight, else "sight". A non-iron one looking
    straight back folds every AutoFoldableSight on the weapon."""
    return {tpl: "iron" if descends_from(items, tpl, IRON_SIGHT_PARENT) else "sight"
            for tpl in sorted(templates) if descends_from(items, tpl, SIGHT_PARENT)}


def closure(manifest, seeds):
    seen, stack = set(), [s for s in seeds if s in manifest]
    while stack:
        key = stack.pop()
        if key in seen:
            continue
        seen.add(key)
        stack.extend(d for d in manifest[key].get("Dependencies", []) if d in manifest and d not in seen)
    return seen


def measure(root: Path, key: str) -> int:
    path = root / key
    if path.is_file():
        return path.stat().st_size
    if path.is_dir():
        return sum(f.stat().st_size for f in path.rglob("*") if f.is_file())
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", type=Path, default=DEFAULT_GAME,
                    help="game install root (default: KITBASH_GAME_ROOT, else the standard install path)")
    ap.add_argument("--items", type=Path, help="item templates (default: data/items.json, else data/items-base.json)")
    ap.add_argument("--out", type=Path, default=DATA)
    args = ap.parse_args()

    items_path = args.items or (DATA / "items.json" if (DATA / "items.json").exists() else DATA / "items-base.json")
    print(f"items from {items_path}")
    items, manifest, version = load(args.game, items_path)
    weapons, headwear, mods = collect(items)
    slots = slot_table(items, weapons | headwear | mods, weapons, headwear)

    bundle_root = args.game / "EscapeFromTarkov_Data/StreamingAssets/Windows"
    seeds = {v["prefab"] for v in slots.values()}
    keys = closure(manifest, seeds)
    total = sum(measure(bundle_root, k) for k in keys)

    bundles = {
        "seeds": sorted(seeds),
        "closure": {k: sorted(d for d in manifest[k].get("Dependencies", []) if d in manifest) for k in sorted(keys)},
    }

    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "slots.json").write_text(json.dumps(slots, indent=1, sort_keys=True), encoding="utf-8", newline="\n")
    (args.out / "bundles.json").write_text(json.dumps(bundles, indent=1, sort_keys=True), encoding="utf-8", newline="\n")
    sizes = size_table(items, weapons | headwear | mods)
    (args.out / "sizes.json").write_text(
        "{\n" + ",\n".join(f"{json.dumps(t)}: {json.dumps(v)}" for t, v in sizes.items()) + "\n}\n",
        encoding="utf-8", newline="\n")
    sights = sight_table(items, weapons | headwear | mods)
    (args.out / "sights.json").write_text(json.dumps(sights, indent=1, sort_keys=True), encoding="utf-8", newline="\n")
    ammo = ammo_table(slots)
    (args.out / "ammo.json").write_text(json.dumps(ammo, indent=1, sort_keys=True), encoding="utf-8", newline="\n")

    missing = sorted(s for s in seeds if s not in manifest)
    print(f"weapons          {len(weapons)}")
    print(f"headwear         {len(headwear)}")
    print(f"mods             {len(mods)}")
    print(f"templates w/prefab {len(slots)}")
    print(f"bundle closure   {len(keys)} bundles, {total / 2**30:.2f} GB on disk")
    if missing:
        print(f"unresolved prefabs ({len(missing)}):")
        for m in missing:
            print("   ", m)
    print(f"sight mods       {len(sights)} ({sum(v == 'iron' for v in sights.values())} iron)")
    print(f"magazines        {len(ammo['magazines'])}, chambered {len(ammo['chambers'])}, "
          f"rounds {sum(1 for v in slots.values() if v.get('ammo'))}")
    if version:
        (args.out / "tables-version.txt").write_text(version + "\n", encoding="utf-8", newline="\n")
        print(f"tables for client {version}")
    for name in ("slots.json", "bundles.json", "sizes.json", "sights.json", "ammo.json"):
        print(f"wrote {args.out / name}  ({(args.out / name).stat().st_size / 1024:.0f} KB)")


if __name__ == "__main__":
    main()

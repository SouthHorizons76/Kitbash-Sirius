"""Bring the item base forward to the live client with Atlas's change sets.

    python scripts/update_items.py
    python scripts/extract_data.py

Atlas (https://www.eftforge.com/atlas) publishes every change to the game's item data
as a change set. This replays the sets newer than our item base onto it and writes
data/items.json, which extract_data.py then turns into the tables the bake reads.

The base is data/items.json when it exists (your last update), else the shipped
data/items-base.json. Both keep only the fields Kitbash! reads (see trim()).

Atlas opens a set to the public some hours after it appears. Sets still locked are
listed with the time they open; rerun after that, or pass an Atlas key (--key, or the
ATLAS_KEY environment variable) to read them now.
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "data"
SHIPPED = DATA / "items-base.json"
LOCAL = DATA / "items.json"
ATLAS = "https://www.eftforge.com/atlas/api"
ITEMS_FILE = "client/items"

# The item fields extract_data.py reads. Everything else is dropped from the base.
KEEP_PROPS = {"Prefab", "Slots", "Chambers", "Cartridges", "Width", "Height", "Foldable", "MergesWithChildren",
              "MagazineWithBelt", "FaceShieldComponent", "HasHinge", "ExtraSizeLeft", "ExtraSizeRight",
              "ExtraSizeUp", "ExtraSizeDown", "ExtraSizeForceAdd"}
HOLDERS = ("Slots", "Chambers", "Cartridges")
MISSING = object()


def trim_holder(s: dict) -> dict:
    out = {"_id": s.get("_id"), "_name": s.get("_name"), "_required": s.get("_required")}
    if "_max_count" in s:
        out["_max_count"] = s["_max_count"]
    filters = []
    for f in (s.get("_props") or {}).get("filters") or []:
        kept = {"Filter": f.get("Filter", [])}
        if "armorColliders" in f:
            kept["armorColliders"] = f["armorColliders"]
        filters.append(kept)
    out["_props"] = {"filters": filters}
    return out


def trim(template: dict) -> dict:
    props = {k: v for k, v in (template.get("_props") or {}).items() if k in KEEP_PROPS}
    for k in HOLDERS:
        if isinstance(props.get(k), list):
            props[k] = [trim_holder(s) for s in props[k] if isinstance(s, dict)]
    return {"_id": template.get("_id"), "_name": template.get("_name"), "_parent": template.get("_parent"),
            "_type": template.get("_type"), "_props": props}


def get(url: str, key: str | None):
    headers = {"User-Agent": "Kitbash-Sirius"}
    if key:
        headers["X-Atlas-Key"] = key
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=120) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        if e.code == 403 and key:
            sys.exit("Atlas refused the key (invalid, expired or locked out).")
        raise


def _child(node, seg):
    if isinstance(node, dict):
        return node.get(seg, MISSING)
    if isinstance(node, list):
        if isinstance(seg, int):
            return node[seg] if 0 <= seg < len(node) else MISSING
        return next((x for x in node if isinstance(x, dict) and x.get("_id") == seg), MISSING)
    return MISSING


def apply(items: dict, changes: list, resolve) -> int:
    """Applies one set's item changes. Each path starts at "data", then the template id.
    Objects in lists are addressed by their _id, plain lists by index. A change under a
    field the base dropped has nowhere to go and is skipped. Returns how many were."""
    skipped = 0
    removals = []  # positional removals, applied last from the highest index down
    for c in changes:
        path = c["path"][1:]
        node = items
        for seg in path[:-1]:
            node = _child(node, seg)
            if node is MISSING:
                break
        if node is MISSING:
            skipped += 1
            continue
        last = path[-1]
        if c["kind"] == "removed":
            if isinstance(node, dict):
                node.pop(last, None)
            elif isinstance(last, int):
                removals.append((node, last))
            else:
                node[:] = [x for x in node if not (isinstance(x, dict) and x.get("_id") == last)]
            continue
        value = resolve(c["new"])
        if isinstance(node, dict):
            node[last] = value
        elif isinstance(last, int):
            if last < len(node):
                node[last] = value
            elif last == len(node):
                node.append(value)
            else:
                sys.exit(f"change set out of order: no index {last - 1} before {c['path']}")
        else:
            i = next((i for i, x in enumerate(node) if isinstance(x, dict) and x.get("_id") == last), None)
            if i is None:
                node.append(value)
            else:
                node[i] = value
    for node, i in sorted(removals, key=lambda r: -r[1]):
        if i < len(node):
            del node[i]
    return skipped


def when(iso: str) -> datetime:
    return datetime.fromisoformat(iso).astimezone(timezone.utc)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--key", default=os.environ.get("ATLAS_KEY"), help="Atlas key, to read sets not yet public")
    ap.add_argument("--base", type=Path, help="item base to start from (default: data/items.json, else the shipped one)")
    ap.add_argument("--out", type=Path, default=LOCAL)
    a = ap.parse_args()

    base_path = a.base or (LOCAL if LOCAL.exists() else SHIPPED)
    base = json.loads(base_path.read_text(encoding="utf-8"))
    items = base["items"]
    print(f"base {base_path.name}: client {base['version']}, Atlas set {base['set']} ({base['detected']})")

    index = get(f"{ATLAS}/index", a.key)
    if base["set"] not in {s["id"] for s in index["sets"]}:
        # Without the base's own set we can't tell whether any sets in between are gone.
        sys.exit(f"Atlas no longer lists set {base['set']}, so we can't be sure every later change is "
                 f"there. Start again from the shipped base: delete data/items.json and git pull.")
    newer = sorted((s for s in index["sets"] if when(s["detected"]) > when(base["detected"])),
                   key=lambda s: when(s["detected"]))
    if not newer:
        print("already up to date")
        return

    applied = None
    before = len(items)
    for s in newer:
        if s.get("locked"):
            # Sets apply in order, so we stop at the first one we cannot read yet.
            print(f"set {s['id']} ({s['version']}) opens to the public at {s.get('public_at')}; "
                  f"{len(newer) - newer.index(s)} set(s) left. Rerun then, or use --key.")
            break
        n = s["files"].get(ITEMS_FILE)
        if n:
            body = get(f"{ATLAS}/sets/{s['id']}", a.key)
            values = {}

            def resolve(v, sid=s["id"]):
                # {"$atlas_value": n, "bytes": size} stands in for a value too large for the set file.
                if isinstance(v, dict) and "$atlas_value" in v:
                    ref = v["$atlas_value"]
                    if ref not in values:
                        values[ref] = get(f"{ATLAS}/sets/{sid}/values/{ref}", a.key)
                    return values[ref]
                return v

            skipped = sum(apply(items, f["changes"], resolve) for f in body["files"] if f["file"] == ITEMS_FILE)
            print(f"set {s['id']} ({s['version']}): {n} item changes, {n - skipped} kept")
        applied = s

    if not applied:
        return
    out = {"version": applied["version"], "set": applied["id"], "detected": applied["detected"],
           "items": {t: trim(v) for t, v in items.items()}}
    a.out.parent.mkdir(parents=True, exist_ok=True)
    a.out.write_text(json.dumps(out, separators=(",", ":"), ensure_ascii=False), encoding="utf-8")
    print(f"wrote {a.out}: client {out['version']}, {len(items)} templates ({len(items) - before:+d})")
    print("next: python scripts/extract_data.py")


if __name__ == "__main__":
    main()

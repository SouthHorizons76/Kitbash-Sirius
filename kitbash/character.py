"""Character images composited from pre-baked sprites (spec/character.md).

Two views: the headshot (the trader screen's player icon) and the full body. A
profile's head, body and feet suits and worn items, each drawn from a sprite baked
alone in the view's fixed frame, stacked by depth. Every piece always lands on the
same pixels, so sprites are placed by their crop rect alone. Variants follow the
outfit:

- the head under a full face cover, the body suit under armor, a vest-like armor,
  a rig or a backpack, a rig over armor, a backpack over armor and a rig;
- the hood up unless headwear, or a face cover that takes its place, is worn;
- worn items' clipped meshes by the outfit's clipping rules;
- face shield and NVG hinges as on item icons.
"""

from __future__ import annotations

import json
from collections import defaultdict
from pathlib import Path

from PIL import Image, ImageChops, ImageMath

from .compositor import FAR, KitbashError, _depth_test, is_on, unpremultiply

REPO = Path(__file__).resolve().parent.parent

# Clipping types (bit flags): what a worn item's clipped mesh is for.
FACE_COVER, VISOR, HAT, HEADPHONES, HELMET = 1, 2, 4, 8, 16
WORN = ("ArmorVest", "TacticalVest", "Backpack", "Headwear", "Earpiece", "Eyewear", "FaceCover", "ArmBand")


MANIFESTS = {"headshot": "character.manifest.json", "full": "character-full.manifest.json"}


class CharacterCompositor:
    def __init__(self, view: str = "headshot", manifest: Path | str | None = None,
                 sprites: Path | str | None = None, slots: Path | str | None = None):
        if view not in MANIFESTS:
            raise KitbashError(f"unknown view {view!r}")
        self.m = json.loads(Path(manifest or REPO / "data" / MANIFESTS[view]).read_text())
        self.dir = Path(sprites or REPO / "sprites" / "character" / view)
        size = self.m["size"]
        self.size = (size, size) if isinstance(size, int) else tuple(size)
        self.slots = json.loads(Path(slots or REPO / "data" / "slots.json").read_text())

    # ------------------------------------------------------------- outfit ----

    def pieces(self, customization: dict, equipment: list[dict]) -> list[str]:
        """The sprites a profile's headshot is made of."""
        m = self.m
        items = m["items"]
        worn = {it["slotId"]: it for it in equipment
                if it.get("slotId") in WORN and it.get("_tpl") in items}
        children = defaultdict(list)
        for it in equipment:
            children[it.get("parentId")].append(it)
        entry = {s: items[it["_tpl"]] for s, it in worn.items()}
        body = m["bodies"].get(customization.get("body"))
        head = m["heads"].get(customization.get("head"))

        armor, rig, pack = "ArmorVest" in worn, "TacticalVest" in worn, "Backpack" in worn
        vestlike = armor and entry["ArmorVest"].get("vestlike", False)
        armored = armor or bool(body and body.get("integratedArmor"))
        face = entry.get("FaceCover")
        full_mask = face is not None and not face.get("halfMask", False)
        headwear = entry.get("Headwear")

        rules = 0
        if face is not None:
            rules |= HAT | HELMET | (VISOR if full_mask else 0)
        if "Eyewear" in worn:
            rules |= FACE_COVER | HAT
        if headwear is not None and not headwear.get("helmet", False):
            rules |= HEADPHONES
        if "Earpiece" in worn:
            rules |= HAT
        if body:
            rules |= body.get("clipping", 0)

        out = []
        if head:
            v = head["variants"]
            out.append(v.get("faceCover") if full_mask and v.get("faceCover") else v.get("base"))
        feet = self.m.get("feet", {}).get(customization.get("feet"))
        if feet:
            out.append(feet["variants"].get("base"))
        if body:
            torso = "armor" if armor and not vestlike else "vest" if armor or rig or pack else "base"
            hooded = headwear is None and not (face is not None and face.get("blocksHood", False))
            hood = ("hood" if hooded else "nohood") if body.get("hooded") else "-"
            v = body["variants"]
            out.append(v.get(f"{torso}/{hood}") or v.get(f"base/{hood}"))
        for slot, it in worn.items():
            mesh = "-"
            if slot == "TacticalVest":
                mesh = "armor" if armored else "body"
            elif slot == "Backpack":
                mesh = "armorVest" if armored and rig else "armor" if armored else "vest" if rig else "body"
            self._node(entry[slot], it, mesh, rules, children, out)
        return [p for p in out if p]

    def _node(self, node: dict, it: dict, mesh: str, rules: int, children, out: list) -> None:
        h = "-"
        if "hinge" in node:
            h = "on" if self._hinge_on(it, node["hinge"], children) else "off"
        mesh = mesh if "meshes" in node else "-"
        mask = rules & node.get("clip", 0)
        v = node.get("variants", {})
        out.append(v.get(f"{h}|{mesh}|{mask}") or v.get(f"{h}|{mesh}|0"))
        parts = node.get("parts", {}).get(h, {})
        for c in children.get(it["_id"], []):
            sub = parts.get(c.get("slotId"), {}).get(c.get("_tpl"))
            if sub is not None:
                self._node(sub, c, "-", rules, children, out)

    def _hinge_on(self, it: dict, kind: str, children) -> bool:
        """As item icons: a face shield is down unless the first face shield in the
        item is togglable and off; an NVG is down while the first togglable is on."""
        def merged(n):
            yield n
            order = [s["id"] for s in self.slots.get(n["_tpl"], {}).get("slots", ())]
            kids = [c for c in children.get(n["_id"], []) if c.get("slotId") in order]
            for c in sorted(kids, key=lambda c: order.index(c["slotId"])):
                yield from merged(c)

        flag = "faceShield" if kind == "faceShield" else "togglable"
        first = next((n for n in merged(it) if self.slots.get(n["_tpl"], {}).get(flag)), None)
        if kind == "faceShield":
            return first is None or not self.slots[first["_tpl"]].get("togglable") or is_on(first)
        return first is not None and is_on(first)

    # --------------------------------------------------------- compositing ----

    def _load(self, name: str):
        p = self.m["pieces"][name]
        im = Image.open(self.dir / f"{name}.webp").convert("RGBA")
        im.load()
        r, g, _ = Image.open(self.dir / f"{name}.depth.webp").convert("RGB").split()
        k, z0 = self.m["depthScale"], self.m["depthOrigin"]
        z = ImageMath.lambda_eval(
            lambda a: a["float"](a["r"] * 256 + a["g"]) / k + z0 + a["float"]((a["r"] + a["g"]) == 0) * FAR,
            r=r, g=g)
        return p, im, z

    def compose(self, customization: dict, equipment: list[dict]) -> Image.Image:
        """The image, premultiplied."""
        n = self.size
        layers = [self._load(p) for p in self.pieces(customization, equipment)]
        if not layers:
            raise KitbashError("nothing to draw")
        canvas = Image.new("RGBA", n, (0, 0, 0, 0))
        zbuf = Image.new("F", n, FAR)
        tbuf = Image.new("L", n, 255)
        # Back to front by nearest depth: occlusion is the per-pixel test, this only
        # orders blending at edges. Glass goes last: the depth buffer holds one depth
        # per pixel, so a lens drawn early would hide a face drawn behind it later.
        for p, im, z in sorted(layers, key=lambda L: (L[0].get("glass", False), -L[2].getextrema()[0])):
            box = (p["x"], p["y"], p["x"] + p["w"], p["y"] + p["h"])
            t = ImageChops.invert(im.getchannel("A"))
            colour, depth, trans = _depth_test(canvas.crop(box), im, zbuf.crop(box), z, tbuf.crop(box), t)
            canvas.paste(colour, box)
            zbuf.paste(depth, box)
            tbuf.paste(trans, box)
        return canvas

    def render(self, customization: dict, equipment: list[dict]) -> Image.Image:
        """The image, straight alpha."""
        return unpremultiply(self.compose(customization, equipment))


def pack(view: str = "headshot") -> int:
    """Converts a view's baked PNG sprites to lossless WebP, about two thirds the
    size with identical pixels, and removes the PNGs. Returns the number converted."""
    from concurrent.futures import ProcessPoolExecutor
    files = sorted((REPO / "sprites" / "character" / view).glob("*.png"))
    with ProcessPoolExecutor() as pool:
        return sum(pool.map(_pack_one, files, chunksize=32))


def _pack_one(f: Path) -> int:
    im = Image.open(f)
    im.load()
    # exact: keep colour under zero alpha; method 4 is within 2% of the smallest
    # output at a thirtieth of the time.
    im.save(f.with_suffix(".webp"), "WEBP", lossless=True, quality=50, method=4, exact=True)
    f.unlink()
    return 1


def write_subset(ids: list[str]) -> Path:
    """Writes data/character.subset.json for Unity's Kitbash! / Bake Character Subset:
    head, body and feet suit ids and worn item templates, mixed."""
    path = REPO / "data" / "character.subset.json"
    path.write_text(json.dumps({"ids": sorted(set(ids))}, indent=2) + "\n", encoding="utf-8")
    return path


USAGE = """usage: python -m kitbash.character pack [headshot|full]
       python -m kitbash.character subset <suit id | item tpl> ..."""

if __name__ == "__main__":
    import sys
    args = sys.argv[1:]
    if args[:1] == ["subset"] and len(args) > 1:
        print(write_subset(args[1:]), "written; then Unity: Kitbash! / Bake Character Subset (both views),"
              " then pack headshot and full")
    elif args[:1] == ["pack"] and args[1:] in ([], ["headshot"], ["full"]):
        print(pack(*args[1:]), "sprites packed")
    else:
        sys.exit(USAGE)

"""Composite a weapon build from baked sprites. See spec/manifest.md.

Sprites are premultiplied RGBA. Everything here stays premultiplied until the
final icon, where it is unpremultiplied once.
"""

from __future__ import annotations

import hashlib
import json
import logging
import math
from collections import OrderedDict
from dataclasses import dataclass
from pathlib import Path

from PIL import Image, ImageChops, ImageMath

log = logging.getLogger(__name__)

REPO = Path(__file__).resolve().parent.parent
IDENTITY = (0.0, 0.0, 0.0, 1.0)

# Per-channel gain on the finished icon. The fitted rig still comes out greener
# than the game's icons by a near-constant factor from shadows to highlights
# (ours/game R 1.04, G 1.06, B 1.00 at p25-p99 over 160 weapon presets), so it
# is corrected as a gain rather than in any one light.
ICON_GAIN = (0.962, 0.944, 1.0)


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)

FAR = 1e9  # depth where a layer has no coverage

# Sub-pixel placement is rounded to 1/SUBPX of a sprite pixel so that shifted
# sprites can be cached: a part on a given host always lands at the same fraction.
SUBPX = 8

# A magazine's stack of rounds, drawn on the magazine's patron_<n> anchors.
CARTRIDGES = "cartridges"
# Rounds in a revolver cylinder: the game never assembles a CylinderMagazine's
# slots and its prefab has no anchors, so they are not drawn.
CAMORA_PREFIX = "camora_"
# A chambered round sits on its bone like any part (AttachMods).
CHAMBER_PREFIX = "patron_in_weapon"
# How SpringMagazineVisual and BeltMagazineInHandsVisual set every round on its
# anchor, ModPlacer or not.
ROUND_ATTACH = (math.sqrt(0.5), 0.0, 0.0, math.sqrt(0.5))  # Euler(90, 0, 0)


def holds_round(slot_id: str) -> bool:
    return slot_id == CARTRIDGES or slot_id.startswith((CAMORA_PREFIX, CHAMBER_PREFIX))


def is_folded(item: dict) -> bool:
    """FoldableComponent.Folded, which the game's icon key includes (IconsHash)."""
    return bool(((item.get("upd") or {}).get("Foldable") or {}).get("Folded"))


def is_on(item: dict) -> bool:
    """TogglableComponent.On (TurnOn / TurnOff), which the icon key includes (IconsHash).
    A new item's is off."""
    return bool(((item.get("upd") or {}).get("Togglable") or {}).get("On"))


def stack_count(item: dict) -> int:
    return int((item.get("upd") or {}).get("StackObjectsCount", 1))


def child_id(parent: str, slot: str) -> str:
    return hashlib.md5(f"{parent}:{slot}".encode()).hexdigest()[:24]


# The game sizes icons as cells * 63 + 1 pixels.
CELL_PX = 63


class KitbashError(Exception):
    pass


class UnsupportedWeapon(KitbashError):
    """The weapon itself cannot be drawn, so no part of the build can."""


@dataclass
class Layer:
    sprite: dict
    x: float  # this part's anchor, relative to the root anchor, sprite pixels
    y: float
    z: float
    order: int  # tree order, breaks z ties


def looks_back(q) -> bool:
    """AssembleMods' test for an aim camera that folds sights: its eulerAngles.y
    relative to the weapon is within 1 deg of 180."""
    x, y, z, w = q
    yaw = math.degrees(math.atan2(2 * (x * z + w * y), 1 - 2 * (x * x + y * y))) % 360
    return abs(180 - yaw) < 1


def angle_deg(a, b) -> float:
    dot = abs(sum(x * y for x, y in zip(a, b)))
    return math.degrees(2 * math.acos(min(1.0, dot)))


def _map_bands(im: Image.Image, fn) -> Image.Image:
    """Applies a resampling op per band. Pillow premultiplies RGBA itself before
    resampling; our data already is, so resample each channel as plain L."""
    return Image.merge("RGBA", [fn(b) for b in im.split()])


def _over(dst: Image.Image, src: Image.Image, t: Image.Image) -> Image.Image:
    """Premultiplied source-over through src's transmittance: dst = src + dst * t,
    t = 1 - src.a except where blended glass lets more through (spec/manifest.md)."""
    return ImageChops.add(src, ImageChops.multiply(dst, Image.merge("RGBA", [t] * 4)))


def _depth_test(dst, src, dst_z, src_z, dst_t, src_t):
    """Per pixel: src over dst where src is nearer, under it where farther.
    Returns (colour, depth, transmittance)."""
    nearer = ImageMath.lambda_eval(
        lambda a: a["convert"]((a["s"] < a["d"]) * 255, "L"), s=src_z, d=dst_z)
    colour = Image.composite(_over(dst, src, src_t), _over(src, dst, dst_t), nearer)
    depth = ImageMath.lambda_eval(lambda a: a["min"](a["s"], a["d"]), s=src_z, d=dst_z)
    return colour, depth, ImageChops.multiply(dst_t, src_t)


def unpremultiply(im: Image.Image) -> Image.Image:
    return Image.frombytes("RGBa", im.size, im.tobytes()).convert("RGBA")


class Compositor:
    def __init__(self, manifest: Path | str | None = None, sprites: Path | str | None = None,
                 cache_mb: int = 256):
        manifest = Path(manifest or REPO / "data" / "sprites.manifest.json")
        self.sprites_dir = Path(sprites or REPO / "sprites")
        m = json.loads(manifest.read_text())
        self.ppu = m["pixelsPerUnit"]
        self.supersample = m["supersample"]
        self.depth_scale = m.get("depthScale")
        self.tolerance = 2 * m["mergeDeg"]
        self.templates = m["templates"]
        # A part's attach rotation on its slot bone: ModPlacer values, else the default.
        self.default_attach = tuple(m["defaultAttach"])
        self.attach = {t: tuple(q) for t, q in m["attach"].items()}
        self.models = m["models"]
        # PreviewPivot.Icon.boundsScale per weapon: its bounds fill this much of the icon.
        self.icon_scale = m.get("iconScale", {})
        # A part drawn on its own: its root pose, from its PreviewPivot.Icon.rotation.
        self.pivots = {t: tuple(q) for t, q in m.get("pivots", {}).items()}
        # A foldable template's fold set: which folded state its parts are drawn in.
        self.fold_sets = m.get("foldSets", {})
        # A template's hinge: "faceShield" (HelmetDress) or "nightVision" (NightVisionMount).
        self.hinges = m.get("hinges", {})
        # Slot order and TogglableComponent / FaceShieldComponent holders, for the hinge rule.
        self.slots = json.loads((REPO / "data" / "slots.json").read_text())
        self.sizes = json.loads((REPO / "data" / "sizes.json").read_text())
        self.sights = json.loads((REPO / "data" / "sights.json").read_text())
        self.ammo = json.loads((REPO / "data" / "ammo.json").read_text())
        # LRU of decoded and shifted sprites, bounded by decoded bytes.
        self._cache: OrderedDict = OrderedDict()
        self._cache_bytes = 0
        self._cache_limit = cache_mb * 2**20

    def _cached(self, key, make):
        v = self._cache.get(key)
        if v is not None:
            self._cache.move_to_end(key)
            return v[0], v[1]
        v = make()
        im, z = v
        cost = im.width * im.height * (8 if z is not None else 4)
        self._cache[key] = (im, z, cost)
        self._cache_bytes += cost
        while self._cache_bytes > self._cache_limit and len(self._cache) > 1:
            _, (_, _, c) = self._cache.popitem(last=False)
            self._cache_bytes -= c
        return v

    # ------------------------------------------------------------ layout ----

    def sprite_for(self, tpl: str, q) -> dict:
        model = self.templates.get(tpl)
        if model is None:
            raise KitbashError(f"template {tpl} has no baked model")
        best, best_deg = None, 1e9
        for s in self.models[model]:
            if s is None:
                continue
            d = angle_deg(q, s["q"])
            if d < best_deg:
                best, best_deg = s, d
        if best is None or best_deg > self.tolerance:
            raise KitbashError(
                f"{model}: no sprite within {self.tolerance} deg of {q} "
                f"(nearest {best_deg:.3f} deg); not baked?")
        return best

    def load_ammo(self, items: list[dict], ammo: str, chamber: bool = False) -> list[dict]:
        """The build with every magazine that takes `ammo` filled to capacity with
        it, replacing any rounds it held; with chamber, also a round in every empty
        chamber that takes it (a UBGL's grenade, say). Magazines and chambers that
        take other rounds are left as they are."""
        mags, chambers = self.ammo["magazines"], self.ammo["chambers"]
        full = {it["_id"]: mags[it["_tpl"]]["capacity"] for it in items
                if ammo in mags.get(it["_tpl"], {}).get("accepts", ())}
        out = [it for it in items if not (it["slotId"] == CARTRIDGES and it["parentId"] in full)]
        taken = {(it["parentId"], it["slotId"]) for it in out}
        for it in items:
            if it["_id"] in full:
                out.append({"_id": child_id(it["_id"], CARTRIDGES), "_tpl": ammo, "slotId": CARTRIDGES,
                            "parentId": it["_id"], "location": 0,
                            "upd": {"StackObjectsCount": full[it["_id"]]}})
            if chamber:
                for sid, takes in chambers.get(it["_tpl"], {}).items():
                    if ammo in takes and (it["_id"], sid) not in taken:
                        out.append({"_id": child_id(it["_id"], sid), "_tpl": ammo, "slotId": sid,
                                    "parentId": it["_id"]})
        return out

    def drawable(self, items: list[dict]) -> tuple[list[dict], list[dict]]:
        """The build without the parts that cannot be drawn, and those parts. A
        skipped part takes everything mounted on it along, since its slot bones are
        unknown. Raises UnsupportedWeapon when the weapon itself cannot be drawn."""
        skipped: list[dict] = []
        self.layers(items, skipped)
        if not skipped:
            return items, []
        children: dict[str, list[dict]] = {}
        for it in items[1:]:
            children.setdefault(it["parentId"], []).append(it)
        drop, stack = set(), list(skipped)
        while stack:
            it = stack.pop()
            drop.add(it["_id"])
            stack.extend(children.get(it["_id"], ()))
        return [it for it in items if it["_id"] not in drop], skipped

    def layers(self, items: list[dict], skipped: list[dict] | None = None) -> list[Layer]:
        """Walks an EFTForge build tree (items[0] is the weapon, or a part drawn on
        its own) into placed layers.
        A part that cannot be drawn raises KitbashError, or with a skipped list is
        left out along with its children and appended to it."""
        children: dict[str, list[dict]] = {}
        loose = 0  # items drawn as part of their host, or not at all
        for it in items[1:]:
            children.setdefault(it["parentId"], []).append(it)

        out: list[Layer] = []
        placed: dict[str, tuple] = {}  # _id -> (q, sprite, layer index)

        def skip(node, exc):
            nonlocal loose
            if skipped is None:
                raise exc
            log.info("skipping %s in %s: %s", node["_tpl"], node["slotId"], exc)
            skipped.append(node)
            loose += count(node)

        def place(node, s, q, x, y, z, fold=None):
            nonlocal loose
            # A folded item is drawn as FoldableStockView / RetractableStockView pose
            # it on non-animated items: its fold set's bones, found through the whole
            # item, move in its own model and in the parts attached to it.
            if fold is None and is_folded(node):
                fold = self.fold_sets.get(node["_tpl"])
            if fold is not None and fold in s.get("stockFolded", {}):
                s = {**s["stockFolded"][fold], "q": s["q"]}
            # A face shield / NVG hinge as HelmetDress / NightVisionMount snap it on an icon item.
            kind = self.hinges.get(node["_tpl"])
            if kind is not None and "hinge" in s:
                s = {**s["hinge"]["on" if hinge_on(node, kind) else "off"], "q": s["q"]}
            placed[node["_id"]] = (q, s, len(out))
            out.append(Layer(s, x, y, z, len(out)))
            stacks = []
            for child in children.get(node["_id"], []):
                sid = child["slotId"]
                if sid == CARTRIDGES or sid.startswith(CAMORA_PREFIX):
                    loose += count(child)
                    if sid == CARTRIDGES:
                        stacks.append(child)
                    continue
                slot = s["slots"].get(sid)
                if slot is None and sid.startswith(CHAMBER_PREFIX):
                    # AttachMods logs a missing chamber bone and draws no round.
                    loose += count(child)
                    continue
                if slot is None:
                    why = "has no bone" if child["slotId"] in s["slots"] else "is not a slot"
                    skip(child, KitbashError(f"{s.get('file', node['_tpl'])}: {child['slotId']} {why}"))
                    continue
                ox, oy = slot["offset"]
                q_child = qmul(slot["q"], self.attach.get(child["_tpl"], self.default_attach))
                try:
                    cs = self.sprite_for(child["_tpl"], q_child)
                except KitbashError as exc:
                    skip(child, exc)
                    continue
                place(child, cs, q_child, x + ox, y + oy, z + slot["z"], fold)
            if stacks:
                load(s, stacks, x, y, z)

        def load(s, stacks, x, y, z):
            """FillAmmoObjectsOnStart: anchor i, in fill order, takes the round at
            stack position count - 1 - i, unless its number is above the count."""
            stacks = sorted(stacks, key=lambda c: c.get("location", 0) if isinstance(c.get("location"), int) else 0)
            sizes = [stack_count(c) for c in stacks]
            total = sum(sizes)

            def at(index):
                # StackSlot.GetItemAtPosition; a negative index lands on the first stack.
                n = 0
                for c, k in zip(stacks, sizes):
                    n += k
                    if index < n:
                        return c
                raise KitbashError(f"no round at position {index} of {total}")

            bad = set()
            for i, r in enumerate(s.get("rounds", ()) if total > 0 else ()):
                if r["n"] > total:
                    continue
                c = at(total - 1 - i)
                if c["_id"] in bad:
                    continue
                try:
                    rs = self.sprite_for(c["_tpl"], qmul(r["q"], ROUND_ATTACH))
                except KitbashError as exc:
                    if skipped is None:
                        raise
                    # Its rounds are already counted as loose.
                    log.info("skipping round %s: %s", c["_tpl"], exc)
                    bad.add(c["_id"])
                    skipped.append(c)
                    continue
                ox, oy = r["offset"]
                out.append(Layer(rs, x + ox, y + oy, z + r["z"], len(out)))

        def count(node):
            return 1 + sum(count(c) for c in children.get(node["_id"], []))

        def merged(node):
            """GetItemComponentsInChildren: the item, then each slot's contents in the
            template's slot order, not descending below an item that does not merge
            with its children (ContainerExtensions.MergesWithChildren)."""
            yield node
            if self.sizes.get(node["_tpl"], {}).get("own"):
                return
            order = [sl["id"] for sl in self.slots.get(node["_tpl"], {}).get("slots", ())]
            kids = [c for c in children.get(node["_id"], []) if c["slotId"] in order]
            for c in sorted(kids, key=lambda c: order.index(c["slotId"])):
                yield from merged(c)

        def hinge_on(node, kind):
            """HelmetDress: down (on) unless the first face shield in the item has a
            hinge and is off. NightVisionMount: down while the first togglable in the
            item is on."""
            flag = "faceShield" if kind == "faceShield" else "togglable"
            first = next((n for n in merged(node) if self.slots.get(n["_tpl"], {}).get(flag)), None)
            if kind == "faceShield":
                return first is None or not self.slots[first["_tpl"]].get("togglable") or is_on(first)
            return first is not None and is_on(first)

        def aims(node):
            """Aim cameras under a sight's slot bone, relative to the weapon; the
            game's search skips any child named mod_scope."""
            q, s, _ = placed[node["_id"]]
            yield from (qmul(q, a) for a in s.get("aim", ()))
            for c in children.get(node["_id"], []):
                if c["_id"] in placed and c["slotId"] != "mod_scope":
                    yield from aims(c)

        # The icon camera poses the root's loot prefab at its PreviewPivot.Icon.rotation
        # (ItemIconCreator.PoseModelByPivot): identity for every weapon, a part's own
        # pose when it is drawn on its own.
        root_q = self.pivots.get(items[0]["_tpl"], IDENTITY)
        try:
            root = self.sprite_for(items[0]["_tpl"], root_q)
        except KitbashError as exc:
            raise UnsupportedWeapon(str(exc)) from exc
        place(items[0], root, root_q, 0.0, 0.0, 0.0)
        if len(placed) + loose != len(items):
            raise KitbashError(f"{len(items) - len(placed) - loose} items not reachable from the root")

        # A non-iron sight looking straight back folds
        # every AutoFoldableSight on the weapon, built-in ones too; otherwise they stand raised.
        # Only items in hands do this (AssembleMods, isHandsItem): a part or helmet drawn
        # on its own keeps its prefab's state, the base sprite.
        if items[0]["_tpl"] in self.pivots:
            return out
        folded = any(self.sights.get(it["_tpl"]) == "sight" and any(map(looks_back, aims(it)))
                     for it in items if it["_id"] in placed)
        for _, s, i in placed.values():
            if "fold" in s:
                v = s["fold"]["folded" if folded else "raised"]
                out[i].sprite = {**v, "q": s["q"], "slots": s["slots"]}
        return out

    def cells(self, items: list[dict]) -> tuple[int, int]:
        """Inventory size of the build, as the game computes it (CompoundItem):
        base cells plus every attachment's ExtraSize, merged per side by max, or
        summed for force-added ones. Attachments under a template that does not
        merge with its children still count themselves, not their children."""
        children: dict[str, list[dict]] = {}
        for it in items[1:]:
            children.setdefault(it["parentId"], []).append(it)
        root = items[0]
        size = self.sizes.get(root["_tpl"])
        if size is None:
            raise UnsupportedWeapon(f"template {root['_tpl']} has no size")
        side, forced = [0, 0, 0, 0], [0, 0, 0, 0]
        stack = [(c, True) for c in children.get(root["_id"], [])] if not size.get("own") else []
        while stack:
            node, _ = stack.pop()
            if holds_round(node["slotId"]):
                continue
            s = self.sizes.get(node["_tpl"], {})
            extra = s.get("extra", (0, 0, 0, 0))
            if s.get("force"):
                forced = [a + b for a, b in zip(forced, extra)]
            else:
                side = [max(a, b) for a, b in zip(side, extra)]
            if not s.get("own"):
                stack.extend((c, True) for c in children.get(node["_id"], []))
        w, h = size["cells"]
        return (w + side[0] + side[1] + forced[0] + forced[1],
                h + side[2] + side[3] + forced[2] + forced[3])

    def icon_size(self, items: list[dict], scale: int = 1) -> tuple[int, int]:
        cw, ch = self.cells(items)
        return (cw * CELL_PX + 1) * scale, (ch * CELL_PX + 1) * scale

    # --------------------------------------------------------- compositing ----

    def _load(self, sprite: dict):
        """(colour, depth relative to the part origin as F or None), unshifted."""
        im = Image.open(self.sprites_dir / sprite["file"]).convert("RGBA")
        im.load()
        z = None
        if self.depth_scale and "depth" in sprite:
            r, g, _ = Image.open(self.sprites_dir / sprite["depth"]).convert("RGB").split()
            k = self.depth_scale
            z = ImageMath.lambda_eval(
                lambda a: a["float"](a["r"] * 256 + a["g"] - 32768) / k
                + a["float"]((a["r"] + a["g"]) == 0) * FAR, r=r, g=g)
        return im, z

    def _placed(self, sprite: dict, qx: int, qy: int):
        """Colour and relative depth shifted right/down by (qx, qy) / SUBPX pixels."""
        def make():
            im, z = self._cached((sprite["file"], 0, 0), lambda: self._load(sprite))
            tx, ty = qx / SUBPX, qy / SUBPX
            w, h = im.size
            shift = (1, 0, -tx, 0, 1, -ty)
            im = _map_bands(im, lambda b: b.transform(
                (w + 1, h + 1), Image.AFFINE, shift, resample=Image.BILINEAR))
            if z is not None:
                z = z.transform((w + 1, h + 1), Image.AFFINE, shift,
                                resample=Image.NEAREST, fillcolor=FAR)
            return im, z
        if qx == 0 and qy == 0:
            return self._cached((sprite["file"], 0, 0), lambda: self._load(sprite))
        return self._cached((sprite["file"], qx, qy), make)

    def _transmit(self, sprite: dict, im: Image.Image, qx: int, qy: int) -> Image.Image:
        """The sprite's transmittance on its placed (shifted) grid: its map where
        baked, else 1 - alpha."""
        if "transmit" not in sprite:
            return ImageChops.invert(im.getchannel("A"))
        def make():
            t = Image.open(self.sprites_dir / sprite["transmit"]).convert("L")
            t.load()
            if qx or qy:
                w, h = t.size
                t = t.transform((w + 1, h + 1), Image.AFFINE, (1, 0, -qx / SUBPX, 0, 1, -qy / SUBPX),
                                resample=Image.BILINEAR, fillcolor=255)
            return t, None
        return self._cached((sprite["transmit"], qx, qy), make)[0]

    def compose(self, items: list[dict], size=None, origin=None):
        """Paints the build at sprite resolution, premultiplied.

        Returns (image, root_anchor). With size and origin, paints onto a canvas of
        that size with the root anchor at origin; otherwise the canvas is fitted
        and cropped to the build.
        """
        # Empty sprites (deliberately invisible parts) only host slots.
        layers = [L for L in self.layers(items) if not L.sprite.get("empty")]
        boxes = []
        for L in layers:
            ax, ay = L.sprite["anchor"]
            w, h = L.sprite["size"]
            boxes.append((L.x - ax, L.y - ay, w, h))  # top-left, fractional

        if origin is None:
            ox = -min(b[0] for b in boxes) + 1
            oy = -min(b[1] for b in boxes) + 1
            W = math.ceil(max(b[0] + b[2] for b in boxes) + ox) + 2
            H = math.ceil(max(b[1] + b[3] for b in boxes) + oy) + 2
        else:
            ox, oy = origin
            W, H = size

        canvas = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        zbuf = Image.new("F", (W, H), FAR)
        tbuf = Image.new("L", (W, H), 255)
        # Back to front by anchor depth (larger z is farther). With depth maps this
        # only decides blending order at edges; the per-pixel test decides occlusion.
        for L, (bx, by, w, h) in sorted(zip(layers, boxes), key=lambda p: (-p[0].z, p[0].order)):
            qx, qy = round((bx + ox) * SUBPX), round((by + oy) * SUBPX)
            ix, iy = qx // SUBPX, qy // SUBPX
            im, z = self._placed(L.sprite, qx % SUBPX, qy % SUBPX)
            t = self._transmit(L.sprite, im, qx % SUBPX, qy % SUBPX)
            if z is not None:
                z0 = L.z
                z = ImageMath.lambda_eval(lambda a: a["z"] + z0, z=z)
            box = (ix, iy, ix + im.width, iy + im.height)
            if z is None:
                canvas.paste(_over(canvas.crop(box), im, t), box)
                tbuf.paste(ImageChops.multiply(tbuf.crop(box), t), box)
            else:
                colour, depth, trans = _depth_test(canvas.crop(box), im, zbuf.crop(box), z, tbuf.crop(box), t)
                canvas.paste(colour, box)
                zbuf.paste(depth, box)
                tbuf.paste(trans, box)

        if origin is not None:
            return canvas, (ox, oy)
        bbox = canvas.getchannel("A").getbbox()
        if bbox is None:
            raise KitbashError("composite is empty")
        return canvas.crop(bbox), (ox - bbox[0], oy - bbox[1])

    def frame(self, layers: list[Layer]) -> tuple[float, float, float, float]:
        """What the game frames the icon on: the union of every part's renderer
        bounds, relative to the root anchor, sprite pixels. Falls back to the sprite
        rect for sprites baked before bounds were recorded."""
        x0 = y0 = math.inf
        x1 = y1 = -math.inf
        for L in layers:
            b = L.sprite.get("bounds")
            if b is None:
                if L.sprite.get("empty"):
                    continue
                ax, ay = L.sprite["anchor"]
                w, h = L.sprite["size"]
                b = (-ax, -ay, w - ax, h - ay)
            x0, y0 = min(x0, L.x + b[0]), min(y0, L.y + b[1])
            x1, y1 = max(x1, L.x + b[2]), max(y1, L.y + b[3])
        return x0, y0, x1, y1

    def render(self, items: list[dict], scale: int = 2, size: tuple[int, int] | None = None) -> Image.Image:
        """The finished icon, straight alpha, framed as the game does: centred on the
        parts' bounds, which fill boundsScale of the limiting side. Size defaults to
        the build's cells * 63 + 1, times scale. No drop shadow: the game's is
        invisible on its icons."""
        W, H = size or self.icon_size(items, scale)
        fx0, fy0, fx1, fy1 = self.frame(self.layers(items))
        im, (ox, oy) = self.compose(items)
        bs = self.icon_scale.get(items[0]["_tpl"], 1.0)
        k = min(W * bs / (fx1 - fx0), H * bs / (fy1 - fy0))  # icon px per sprite px
        if k > 1:
            log.warning("icon needs %.2fx upscale; bake pixelsPerUnit is too low", k)
        elif k > 1 / self.supersample:
            log.info("icon downsample %.2fx is under the %dx supersample", 1 / k, self.supersample)

        # The icon covers this box of the canvas; pad the canvas so it lies inside.
        cx, cy = (fx0 + fx1) / 2 + ox, (fy0 + fy1) / 2 + oy
        bx0, by0 = cx - W / (2 * k), cy - H / (2 * k)
        bx1, by1 = cx + W / (2 * k), cy + H / (2 * k)
        px, py = max(0, math.ceil(-bx0)), max(0, math.ceil(-by0))
        cw = max(im.width + px, math.ceil(bx1) + px)
        ch = max(im.height + py, math.ceil(by1) + py)
        if (px, py, cw, ch) != (0, 0, im.width, im.height):
            padded = Image.new("RGBA", (cw, ch), (0, 0, 0, 0))
            padded.paste(im, (px, py))
            im = padded
        box = (bx0 + px, by0 + py, bx1 + px, by1 + py)
        out = _map_bands(im, lambda b: b.resize((W, H), Image.BOX, box=box))
        # Premultiplied, so a gain on colour is the same gain after unpremultiplying.
        r, g, b, a = out.split()
        out = Image.merge("RGBA", [c.point(lambda v, k=k: round(v * k)) for c, k in zip((r, g, b), ICON_GAIN)] + [a])
        return unpremultiply(out)

# Sprite manifest format

The contract between `baker/` (Unity, build time) and `kitbash/` (Python, runtime).
Both halves depend on this file and nothing else about each other.

## Files

```
sprites/<model>/<n>.webp        colour, one per (model, orientation), cropped to alpha
sprites/<model>/<n>.depth.webp  depth, same crop: code = R*256 + G, 0 = no coverage
sprites/<model>/<n>.raised.webp, <n>.folded.webp (+ .depth)   auto-folding sight states
sprites/<model>/<n>.transmit.webp  transmittance, same crop (blended glass only)
data/sprites.manifest.json     the index below
data/sights.json               SightMod templates: "iron" (IronSight) or "sight"
data/ammo.json                 magazines {tpl: {capacity, accepts}}, chambers {tpl: {slot: accepts}}
data/orientations.json         bake input, from scripts/orientations.py
data/sizes.json                inventory cells per template, from scripts/extract_data.py
```

The bake writes PNG; `scripts/pack_sprites.py` converts to WebP and rewrites the
manifest paths. Colour is lossy q90, depth lossless. The compositor reads whatever
the manifest names.

Sprite colour is **premultiplied**: MSAA resolves edges against a transparent black
clear. Composite with `dst = src + dst * (1 - src.a)` and unpremultiply only when
writing the final icon.

Blended glass (`Transparent/DepthZwriteDithered_Icon`: one pass writes alpha 1, the
next blends RGB over whatever is behind) cannot be one premultiplied layer: in the
game's single render the helmet shows through a face shield whose icon alpha is 1.
The bake renders such a sprite over black and over white; white - black, the mean
over RGB, is the transmittance `t`, written as `transmit` (grey, 255 = all shows
through) only where it differs from `1 - alpha`. Composite every layer as
`dst = src + dst * t` (`t = 1 - src.a` without a map), and keep the canvas's own
transmittance (the product of the layers'), so a part placed behind glass already
drawn still shows through it.

Depth is world z relative to the part's origin, larger is farther:
`z = (code - 32768) / depthScale`. Edge pixels that have colour but missed the
single depth sample are filled from their nearest covered neighbour.

Sprites are rendered on the world pixel grid, so every `anchor` is a whole number
and a root part is never resampled. Child offsets are fractional.

## Orientation

A part's orientation is relative to the weapon root, and it is the product down its
attach chain:

```
childQ = hostQ * boneRot(host model, slot) * attach(child)
attach(child) = Euler(ModRotation) if the child has a ModPlacer, else Euler(90,0,0)
```

The weapon itself is at identity. All 50 ModPlacers hold zero rotation, so those
mods attach at identity; applying the default to them instead stands their
children upright (checked against game renders of SCAR-L, P90, Desert Eagle L6). Every sprite is rendered at
`iconRotation * q`. Orientations within `mergeDeg` of each other share one
sprite. Sprites are looked up by **nearest angle**, never by a quantised string key,
because float noise in the chain lands on both sides of any rounding boundary.

## Index schema

```jsonc
{
  "version": 5,
  "gameVersion": "1.1.5.1.47510", // client of the newest bake, from EscapeFromTarkov.exe
  "pixelsPerUnit": 1600,          // sprite pixels per world unit, same for every sprite
  "supersample": 2,               // pixelsPerUnit / supersample = densest 1x output
  "depthScale": 20000,            // depth code steps per world unit
  "iconRotation": [0, 0.8433914, 0, -0.537299633],
  "defaultAttach": [0.7071068, 0, 0, 0.7071068],   // Euler(90,0,0)
  "attach": { "<tpl>": [0, 0, 0, 1] },            // ModPlacer mods only
  "iconScale": { "<weapon tpl>": 0.9 },           // PreviewPivot.Icon.boundsScale (parts: when not 1)
  // A part that can hold others, drawn on its own: its root orientation, relative
  // to the weapon frame. The icon camera sets the loot prefab's root to
  // PreviewPivot.Icon.rotation, so this is inv(iconRotation) * Icon.rotation *
  // the model's rotation in its prefab (data/icon-poses.json).
  "pivots": { "<part tpl>": [0, 0, 0, 1] },
  // A foldable template's fold set. Its FoldableStockView / RetractableStockView
  // find their bones by name through the whole assembled item, so a set poses
  // bones in the folding item's model and in parts attached to it (an AKMS stock).
  "foldSets": { "<foldable tpl>": "3" },
  // A template whose model has a face shield / NVG hinge: "faceShield" (HelmetDress)
  // or "nightVision" (NightVisionMount). Its sprites carry "hinge" below.
  "hinges": { "<tpl>": "faceShield" },
  "mergeDeg": 0.05,
  "templates": {                  // every bakeable template id -> its model
    "5447a9cd4bdc2dbd208b4567": "weapon_colt_m4a1_556x45_model.generated"
  },
  "models": {
    "weapon_colt_m4a1_556x45_model.generated": [
      {
        "q": [0, 0, 0, 1],        // orientation, relative to the weapon root
        "file": "weapon_colt_m4a1_556x45_model.generated/0.webp",
        "depth": "weapon_colt_m4a1_556x45_model.generated/0.depth.webp",
        "size": [1156, 351],
        "anchor": [611, 402],     // part origin in sprite pixels
        // union of renderer world AABBs minus lenses and muzzle jets, as the
        // game frames icons: x0, y0, x1, y1 in sprite
        // pixels relative to the anchor
        "bounds": [-640.1, -380.5, 560.2, 120.9],
        // blended glass only: see above
        "transmit": "<model>/0.transmit.webp",
        "slots": {
          // where a child's anchor lands, relative to THIS anchor, and the
          // bone's orientation (relative to the weapon root); the child's own
          // orientation is q * attach(child)
          "mod_magazine": {"offset": [-12.41, 88.93], "z": 0.0012, "q": [...]},
          "mod_mount":    null     // declared slot, no bone found
        },
        // magazines only: every transform named *patron_<n>*, breadth first,
        // reversed for belt magazines - the order the game fills them in. Same
        // fields as a slot, plus the anchor's number n.
        "rounds": [{"n": 1, "offset": [3.2, -40.1], "z": 0.001, "q": [...]}],
        // transforms named *mod_aim_camera*, not below a child named mod_scope,
        // relative to this part's origin (optional)
        "aim": [[0, 1, 0, 0]],
        // only for models with AutoFoldableSight children: the part as drawn with
        // them raised (no forward optic) and folded; each has file, depth, size,
        // anchor, bounds, or is {"empty": true}. The base entry is the prefab's
        // own state. A state equal to it names the base's files.
        "fold": {"raised": {...}, "folded": {...}},
        // per fold set that poses bones in this model: the part with those bones
        // posed folded, as on a non-animated (icon) item whose FoldableComponent is
        // folded. A full entry of its own, without q: file, depth, size, anchor,
        // bounds, slots (moved with the bones), and rounds, aim and fold as above.
        "stockFolded": {"<fold set>": {...}},
        // only for models with a hinge: the part with its hinge transform at its on
        // and off rotation, as an icon item snaps it. Full entries without q (file,
        // depth, size, anchor, bounds, slots, ...); a pose the prefab already has
        // repeats the base entry.
        "hinge": {"on": {...}, "off": {...}}
      }
    ]
  }
}
```

Pixel axes are image convention: x right, y down, origin at the top-left corner of
pixel (0,0). `z` is the camera-space depth of the slot bone relative to the host
origin, in world units, larger is farther.

`slots` covers the union of slot ids declared by every template using the model.
A slot is `null` only when the bone is missing, so the compositor can fail loudly
instead of dropping an attachment.

## Composite algorithm

```
place(node, q, xy, z):
    sprites = models[templates[node._tpl]]
    s = nearest(sprites, q)                 # fail if angle > 2 * mergeDeg
    emit(s, at = xy - s.anchor, z = z)
    for child in node.children:
        slot = s.slots[child.slotId]        # fail if missing or null
        q_child = slot.q * attach.get(child._tpl, defaultAttach)
        place(child, q_child, xy + slot.offset, z + slot.z)

place(root, pivots.get(root._tpl, identity), (0, 0), 0)
```

The root is a weapon, a headwear item, or a part drawn on its own (the game's icon
of an item shows whatever is attached to it, and a magazine its rounds:
`ItemIconCreator.RenderModel`, `IconsHash`). Headwear and parts sit at `pivots`. A node whose
`upd.Foldable.Folded` is true starts its fold set, `foldSets[node._tpl]`: it and
every part under it is placed with `s.stockFolded[set]` in place of `s` where
that exists, its slots included.

Rounds (`SpringMagazineVisual` / `BeltMagazineInHandsVisual.FillAmmoObjectsOnStart`):
a magazine's `cartridges` children are stacks, in `location` order, of
`upd.StackObjectsCount` rounds each (default 1). With `count` rounds in all,
anchor `i` of `rounds` whose `n <= count` takes the round at stack position
`count - 1 - i` (a negative position is the first stack), placed at the anchor
with orientation `anchor.q * Euler(90,0,0)`; no rounds, nothing. Rounds are baked
as the game shows them loaded, `AmmoPoolObject.SetUsed(false)`: `*shell*` objects
off, `*full*` on. A chambered round (`patron_in_weapon*`) is an ordinary slot
child; with no bone the game draws none. Revolver rounds (`camora_*`) are never
drawn: the game does not assemble a cylinder, and cylinders have no anchors.

Hinges (`HelmetDress`, `NightVisionMount`): a node whose template is in `hinges`
draws `s.hinge.on` or `s.hinge.off` in place of `s`, slots included. Walk the node
and everything under it as `GetItemComponentsInChildren` does: the node, then each
slot's contents in the template's slot order (`data/slots.json`), not descending
below a template that does not merge with its children (`own` in `data/sizes.json`).
A `faceShield` hinge is on unless the first `faceShield` template found is
`togglable` and not `upd.Togglable.On`; a `nightVision` hinge is on only if the first
`togglable` template found is `upd.Togglable.On`. A new item is off.

Then, for a weapon root only (`AssembleMods` does it for items in hands; a part or
helmet drawn on its own keeps the base, its prefab's state), fold sights as the game does. The build is *folded* if a
non-iron SightMod has an aim camera (its own, or a descendant's not reached through
a `mod_scope` slot) whose `eulerAngles.y` relative to the weapon, `q * aim`, is
within 1 deg of 180. Every part with `fold` draws `fold.folded` if the build is
folded, else `fold.raised`, including built-in sights on a receiver, handguard,
barrel or gas block: weapons in hands toggle every AutoFoldableSight.

1. At sprite resolution, place each layer at its fractional offset (colour
   bilinear, depth nearest; no per-part rounding) and depth-test per pixel against
   a z-buffer: over where nearer, under where farther. Layers go in back to front
   by anchor `z`, which only matters for blending at edges.
2. Crop to the union of non-transparent pixels.
3. Frame as the game does and downsample once. The icon is `(cells.x * 63 + 1,
   cells.y * 63 + 1)` times a scale (EFTForge uses 2). Cells are the weapon's
   base size plus every attachment's ExtraSize, merged per side by max, force-added
   ones summed, not descending below templates that don't merge with children
   (`CompoundItem`). The union of all parts' `bounds` fills `iconScale` of the
   limiting side, centred.
4. No drop shadow. The game's `IconShadow` code defaults to black at `(-5, +5)`,
   but the IconCamera prefab overrides that to colour 10/255 at offset `(0, 0)`,
   blurred; on tarkov.dev icons it shows as alpha 1-3/255 at most.

Painter's order alone was measured wrong on the first build tried: a PM grip wraps
the frame and magazine, partly in front and partly behind. Depth maps exist for
that.

## Invariants the baker must guarantee

- One `pixelsPerUnit` for every sprite, or composites are meaningless.
- `anchor` is the projection of the part's prefab origin, **not** its bounds
  centre.
- Anchors and offsets are measured from the rendered transforms, not recomputed.
- No sprite has an empty alpha channel.
- Every sprite has a depth map on the same crop.

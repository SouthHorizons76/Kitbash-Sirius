# Character renders

Two images of a player's character, composited from pre-baked sprites like the
weapon builds: the **headshot** the trader screen shows, and a **full-body**
image. Nothing renders at runtime; the server stacks sprites by depth and sends
one picture. `Kitbash! / Character Test Render` renders the same character
directly in Unity and is the reference every bake is checked against.

## The two views

| | Headshot | Full body |
|---|---|---|
| Camera | perspective, field of view 12, clip 0.3-10 | orthographic |
| Model | at (0.01, -1.68, 2.0) from the camera, turned 180 | turned 185, in a fixed 1.4 x 2.2 m frame |
| Output | 500 x 500; each sprite rendered at 1000 x 1000 and halved | 788 x 1238 at 562.5 px per metre, the same way |
| Lights | the player icon's seven, scaled by 0.6 (`PlayerIcon.cs`) | the item icon rig, no shadows |
| Pose | armed if a weapon is carried, else empty hands | empty hands |

The headshot matches the game's own (checked against its icon cache). The full
body is ours to frame. Its lights cast no shadows, so its pieces composite with
no gap between them. The frame runs from x -0.7 to 0.7 m and y -0.1 to 2.1 m in
the character's space (feet at 0), with room for a backpack and slung weapons;
the compositor returns the whole frame.

**Both views are fixed.** The pose, the camera and every attach rule are fixed,
so a piece always lands on the same pixels with the same depth. Sprites are
stacked with no offsets: the weapon compositor's 2D placement is not needed.
Under the headshot's perspective camera this is the only way that works, since a
piece cannot be shifted without changing how it projects.

**Poses.** The menu character has two: armed and empty hands (pistol equals
two-handed; throwable and melee equal empty hands). The game's headshot is armed
when anything is in the primary, secondary or holster slot, even though the
weapon is out of frame. Headshot pieces are baked armed only, since nearly every
PMC carries a weapon; full-body pieces in empty hands only.

## Pieces and variants

Every piece is baked once per view (and pose, for the headshot) and per variant.
A variant is a mesh or object set chosen by the rest of the outfit:

| Piece | Count | Variants |
|---|---|---|
| Head | 50 | base, under a full face cover |
| Body (upper) | 193 | base, under armor, under a vest-like armor / rig / backpack; hooded or not |
| Feet | 147 | one; some carry a pistol holster |
| Armor vest | 49 | one; some are vest-like |
| Rig | 105 | alone, over armor |
| Backpack | 48 | alone, over armor, over a rig, over both |
| Headwear | 151 | base, clipped; face shield and NVG hinges on / off; parts on its slots |
| Earpiece | 23 | base, clipped |
| Eyewear | 41 | base, clipped |
| Face cover | 132 | base, clipped |
| Armband | 39 | one |
| Carried weapons | 138 long, 31 pistols, 31 knives | weapon part sprites at each carry pose |

"Clipped" is the item's custom mesh, used while the outfit's clipping rules name
its type:

- a face cover clips hats and helmets, and visors unless it is a half mask;
- eyewear clips face covers and hats;
- headwear that is not a helmet clips headphones;
- an earpiece clips hats;
- a body suit can add rules of its own.

**Hood:** up unless headwear is worn, or a face cover that takes its place.

**Face shield:** down unless the item's first face shield is togglable and off.
**NVG:** down while its first togglable is on. These are the weapon compositor's
rules, unchanged.

**Headwear parts.** A helmet's parts (headsets, mounts, NVGs, visors) sit on its
slot bones as on a helmet icon. Each placement (a part on a given slot path) is
fixed, so each one is a sprite of its own. The bake enumerates them from the slot
tree, as `orientations.py` does for weapons; built-in armor inserts are never
drawn.

**Carried weapons** are left out of both views for now. From the front they
barely show (a muzzle past the backpack, a pistol in a thigh holster), and
drawing them would take weapon part sprites at about five more orientations
(see below), a full weapon rebake. The rules, for when they are added: the
primary and secondary hang on the back, on bones chosen
by the backpack: with no backpack, or one that leans weapons against the body,
the bones nearer the body. The pistol sits in the pants' holster and is only
drawn on pants that have one. A knife is drawn only for the three templates that
show it. Long weapons clip into large backpacks exactly as they do in game.

- **Full body:** a carried weapon is a weapon build at one of five fixed
  rotations (primary and secondary on either bone, pistol). Each rotation is one
  more orientation for the existing weapon part sprites, and the compositor
  places parts as it does for icons.
- **Headshot:** a weapon on the back can reach into the frame (a stock over the
  shoulder). See open questions.

Heads and suits rigged to another skeleton (the zombies', which skip a spine
bone) are skipped: only AI wears them.

## Runtime

Input, as a profile lists it:

```json
{"customization": {"head": id, "body": id, "feet": id},
 "equipment": [{"_id", "_tpl", "slotId", "parentId", "upd"}]}
```

1. Walk the equipment: worn items, their parts, carried weapons.
2. Apply the outfit rules above to pick every piece's variant, hinge states and
   carry bones.
3. Look each piece up in the character manifest and composite it by depth
   (premultiplied colour, transmittance for glass), the same algorithm as
   `manifest.md`.
4. Weapons on the full body go through the weapon compositor at their carry
   rotation and land at their carry bone's fixed pixel position.

## Size

Headshot sprites only need pieces that reach the frame: heads, upper bodies,
armor, rigs, backpacks, head gear, and weapon parts near the shoulders. Feet and
most weapons are full-body only.

The full-body bake: 2,959 sprites (3,410 placements), 215 MB, with 136 feet
suits, 73 of them with a thigh holster. Full-body sprites are about the size of
headshot ones, piece for piece: the
frame is twice as tall but at half the density. Each view has its own manifest
(`data/character.manifest.json`, `data/character-full.manifest.json`) and sprite
folder (`sprites/character/headshot`, `sprites/character/full`).

The headshot bake: 2,822 sprites (colour and depth), 194 MB. The bake writes
PNG; `python -m kitbash.character pack [headshot|full]` then converts them to lossless WebP
(identical pixels, two thirds the size, about 25 s), which is what the
compositor reads. Two placements of
one model at the same pose share a sprite, which folds about 6,000 attachment
placements (a helmet's colourways repeat every part).

**Sprites are halved before storing**, a quarter of the full-resolution size
(1.1 GB). Compositing at the output size against the test render differs by
3.5/255 on average, against 3.6/255 when compositing at full resolution and
halving after. The halved depth is the nearest of each 2 x 2 block.

**Glass** renders dithered, every other pixel opaque. At full resolution the
gaps let what is behind show through; halved, a lens becomes one translucent
layer, and since the depth buffer holds one depth per pixel, a lens drawn
before the face would hide it. A piece with more than 50 fully dithered blocks
is flagged `glass` (103 pieces: eyewear, visors, NVG lenses) and drawn after
everything else. The full body renders forward, where glass blends instead of
dithering; there a piece with over a quarter of its pixels partly covered is
glass (117 pieces). Thin opaque pieces can pass that test too, and drawing them
last does no harm. Composite time is about 0.2 s per headshot.

## Shadows between pieces

The headshot's lights cast shadows, and a piece baked alone casts none on the
others. Measured on the test kit (6B47, GSSh-01, 6B23-1, BlackRock, Pilgrim, ESS,
skull balaclava, three carried weapons): its 13 pieces composited by depth against
the same character rendered whole match in coverage exactly, are never darker, and
differ by 3.0/255 on average. 7.7% of pixels are brighter by more than 8/255,
nearly all on the armor collar under the head gear, the rest faint rims along
straps. Side by side the difference is hard to see. The first version leaves
cross-piece shadows out.

Two things the bake must do for pieces to agree with each other: re-skin every
skinned mesh on each render (in the editor skinning otherwise follows the
editor's frames, so a render right after a pose change shows the previous pose),
and keep texture streaming off so every piece samples full-detail textures.

## Open questions
- **Carried weapons.** Whether they are worth the weapon rebake; the holstered
  pistol alone would need one orientation and only pistols.
- **Weapons in the headshot.** How often a carried weapon reaches the headshot
  frame. If it is common, bake the weapon parts that reach the frame per carry
  pose in the headshot camera. If it is rare, leave weapons out of headshots.
- **Headshot pose.** Whether unarmed profiles need the empty-hands pose, or the
  armed pose is close enough.

"""Convert baked PNG sprites to WebP and point the manifest at them.

    python scripts/pack_sprites.py

Colour: lossy WebP q90 (measured: no change at icon size). Depth: lossless, since
the channels encode a 16-bit number. Idempotent: only entries still pointing at
.png are converted; the PNGs are deleted once written.
"""

import json
from pathlib import Path

from PIL import Image

REPO = Path(__file__).resolve().parent.parent
MANIFEST = REPO / "data" / "sprites.manifest.json"
SPRITES = REPO / "sprites"


def convert(rel: str, **webp) -> str:
    src = SPRITES / rel
    out = rel[: -len(".png")] + ".webp"
    im = Image.open(src)
    im.convert("RGB" if webp.get("lossless") else "RGBA").save(SPRITES / out, "WEBP", **webp)
    src.unlink()
    return out


def main():
    m = json.loads(MANIFEST.read_text())
    n = before = after = 0
    # A fold variant equal to the base names the base's files; convert each once.
    done = {}
    for sprites in m["models"].values():
        for base in sprites:
            if base is None:
                continue
            states = [base, *base.get("fold", {}).values()]
            for stock in base.get("stockFolded", {}).values():
                states += [stock, *stock.get("fold", {}).values()]
            for hinge in base.get("hinge", {}).values():
                states += [hinge, *hinge.get("fold", {}).values()]
            for s in states:
                if not s.get("file", "").endswith(".png"):
                    continue
                for key, opts in (("file", dict(quality=90, method=4, exact=True)),
                                  ("depth", dict(lossless=True, method=4)),
                                  ("transmit", dict(lossless=True, method=4))):
                    if key not in s:
                        continue
                    if s[key] not in done:
                        before += (SPRITES / s[key]).stat().st_size
                        done[s[key]] = convert(s[key], **opts)
                        after += (SPRITES / done[s[key]]).stat().st_size
                    s[key] = done[s[key]]
                n += 1
    MANIFEST.write_text(json.dumps(m, indent=2))
    print(f"packed {n} sprites: {before / 2**20:.1f} MB -> {after / 2**20:.1f} MB")


if __name__ == "__main__":
    main()

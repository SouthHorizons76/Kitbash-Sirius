"""One entry point for the sprite bake.

    python scripts/bake.py                      # step 1: orientations, then names the Unity menu item
    python scripts/bake.py --pack               # step 2: after the Unity bake
    python scripts/bake.py --only <weapon tpl> ...   # limit either step to these weapons

Unity menu items are run by hand. The order after a game update (the item tables in
data/ ship with the repo):

    git pull                                    # updated item tables, or:
    python scripts/update_items.py              # bring the item base forward from Atlas
    python scripts/extract_data.py              # and rebuild the tables from it
    Unity: Kitbash! / Inspect All               # inspect.json, bones-all.json, icon-poses.json
    python scripts/bake.py [--only ...]         # writes data/orientations.json
    Unity: Kitbash! / Bake Sprites (all)        # or (subset) with --only
    python scripts/bake.py --pack [--only ...]  # PNG -> WebP, summarises the bake report

The bake needs a current inspect.json; bake.py stops if data/slots.json lists parts
that inspect.json does not know yet.
"""

import argparse
import json
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCRIPTS = os.path.join(ROOT, "scripts")
DATA = os.path.join(ROOT, "data")


def run(*args):
    print("\n$ python " + " ".join(args), flush=True)
    r = subprocess.run([sys.executable, *args], cwd=ROOT)
    if r.returncode != 0:
        sys.exit(f"failed: {os.path.basename(args[0])} (exit {r.returncode})")


def check_inspect():
    # extract_data.py writes every weapon and mod it found to slots.json; inspect.json
    # only knows what Inspect All has seen.
    if not os.path.exists(os.path.join(DATA, "slots.json")):
        sys.exit("data/slots.json is missing. It ships with the repo: restore it, or regenerate it with "
                 "scripts/extract_data.py.")
    if not os.path.exists(os.path.join(DATA, "inspect.json")):
        sys.exit("data/inspect.json is missing. Run Unity: Kitbash! / Inspect All first.")
    slots = json.load(open(os.path.join(DATA, "slots.json")))
    insp = json.load(open(os.path.join(DATA, "inspect.json")))
    known = {w["tpl"] for w in insp["weapons"] + insp["mods"]}
    missing = [t for t in slots if t not in known]
    if missing:
        names = ", ".join(slots[t].get("name", t) for t in missing[:5])
        sys.exit(f"inspect.json is behind data/slots.json: {len(missing)} parts unknown ({names}"
                 f"{', ...' if len(missing) > 5 else ''}).\nRun Unity: Kitbash! / Inspect All, then rerun.")


def prepare(only):
    run(os.path.join(SCRIPTS, "orientations.py"), *(["--only", *only] if only else []))
    menu = "Kitbash! / Bake Sprites (subset)" if only else "Kitbash! / Bake Sprites (all)"
    print(f"\nNext: in Unity run  {menu}")
    print("Then: python scripts/bake.py --pack" + (f" --only {' '.join(only)}" if only else ""))


def pack():
    run(os.path.join(SCRIPTS, "pack_sprites.py"))
    path = os.path.join(DATA, "bake-report.json")
    if not os.path.exists(path):
        print("no data/bake-report.json yet: has the Unity bake run?")
        return
    r = json.load(open(path))
    print(f"\nbake: {r.get('baked')}/{r.get('requested')} sprites, {len(r.get('failures', []))} failed, "
          f"{r.get('seconds')} s ({r.get('scope')})")
    for f in r.get("failures", [])[:20]:
        print("  FAILED", f)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--pack", action="store_true", help="step 2, after the Unity bake")
    ap.add_argument("--only", nargs="+", metavar="TPL", help="limit to these weapon templates")
    a = ap.parse_args()
    check_inspect()
    if a.pack:
        pack()
    else:
        prepare(a.only)


if __name__ == "__main__":
    main()

"""Convert a 4x3 horizontal-cross cubemap into Godot's 3x2 cubemap layout.

Input cross (standard horizontal cross, faces viewed from inside the cube):

        [  .  ][ top ][  .  ][  .  ]
        [left ][front][right][back ]
        [  .  ][bottom][ .  ][  .  ]

Output, for Import As: Cubemap with Slices > Arrangement: 3x2:

        [ X+ right ][ X- left  ][ Y+ top  ]
        [ Y- bottom][ Z+ front ][ Z- back ]

That face mapping was checked by sampling across all 12 cube edges: it stitches seamlessly,
while swapping front and back produces seams about 15x stronger than the in-face noise. It
does not say whether the sky is mirrored, since mirroring leaves no seam - if it reads
backwards in-engine, flip one axis of the lookup direction in the sky shader instead.

Usage:
    python cross_to_3x2.py cubemap.png space_skybox_3x2.png
    python cross_to_3x2.py cubemap.png out.png --face right=right_nosun.png
    python cross_to_3x2.py cubemap.png out.png --check

--face replaces one face from the cross with a separate image (repeatable), e.g. an edited
copy. --check reports seam continuity for the output.
"""

import argparse

import numpy as np
from PIL import Image

# (column, row) of each face in the 4x3 cross.
CROSS_TILES = {
    "top": (1, 0),
    "left": (0, 1),
    "front": (1, 1),
    "right": (2, 1),
    "back": (3, 1),
    "bottom": (1, 2),
}

# Godot's face order, read row by row across the 3x2 grid: X+ X- Y+ / Y- Z+ Z-.
GODOT_3X2 = [["right", "left", "top"], ["bottom", "front", "back"]]


def extract_faces(cross: Image.Image) -> dict[str, Image.Image]:
    w, h = cross.size
    if w * 3 != h * 4:
        raise ValueError(f"expected a 4:3 horizontal cross, got {w}x{h}")
    size = w // 4
    return {
        name: cross.crop((c * size, r * size, (c + 1) * size, (r + 1) * size))
        for name, (c, r) in CROSS_TILES.items()
    }


def build_3x2(faces: dict[str, Image.Image]) -> Image.Image:
    size = faces["front"].size[0]
    for name, face in faces.items():
        if face.size != (size, size):
            raise ValueError(f"face '{name}' is {face.size[0]}x{face.size[1]}, expected {size}x{size}")
    out = Image.new("RGB", (3 * size, 2 * size))
    for r, row in enumerate(GODOT_3X2):
        for c, name in enumerate(row):
            out.paste(faces[name].convert("RGB"), (c * size, r * size))
    return out


def seam_check(layout: Image.Image, samples: int = 4000) -> tuple[float, float]:
    """Mean colour difference across cube edges vs. across the same distance within a face,
    sampling the way a GPU samples a cubemap. A ratio near 1 means the edges are seamless."""
    img = np.asarray(layout.convert("RGB"), dtype=np.float32)
    size = img.shape[1] // 3
    order = [f for row in GODOT_3X2 for f in row]
    faces = [
        img[(i // 3) * size:(i // 3 + 1) * size, (i % 3) * size:(i % 3 + 1) * size]
        for i in range(len(order))
    ]

    def sample(d):
        x, y, z = d
        ax, ay, az = abs(x), abs(y), abs(z)
        if ax >= ay and ax >= az:
            f, sc, tc, ma = (0, -z, -y, ax) if x > 0 else (1, z, -y, ax)
        elif ay >= az:
            f, sc, tc, ma = (2, x, z, ay) if y > 0 else (3, x, -z, ay)
        else:
            f, sc, tc, ma = (4, x, -y, az) if z > 0 else (5, -x, -y, az)
        s, t = (sc / ma + 1) / 2, (tc / ma + 1) / 2
        return faces[f][min(int(t * size), size - 1), min(int(s * size), size - 1)]

    rng = np.random.default_rng(0)
    eps = 2.0 / size
    across, within = [], []
    for _ in range(samples):
        # A random point on one of the 12 cube edges, then one step onto each adjacent face.
        axis = rng.integers(3)
        others = [i for i in range(3) if i != axis]
        p = np.zeros(3)
        p[axis] = rng.uniform(-0.98, 0.98)
        p[others[0]], p[others[1]] = rng.choice([-1, 1], 2)
        pa, pb, pc = p.copy(), p.copy(), p.copy()
        pa[others[1]] *= 1 - eps
        pb[others[0]] *= 1 - eps
        pc[others[1]] *= 1 - 3 * eps
        a, b, c = sample(pa), sample(pb), sample(pc)
        across.append(np.abs(a - b).mean())
        within.append(np.abs(a - c).mean())
    return float(np.mean(across)), float(np.mean(within))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("cross", help="4x3 horizontal-cross cubemap image")
    parser.add_argument("output", help="output image in Godot's 3x2 arrangement")
    parser.add_argument("--face", action="append", default=[], metavar="NAME=PATH",
                        help="replace one face with a separate image, e.g. right=right_nosun.png")
    parser.add_argument("--check", action="store_true", help="report seam continuity of the output")
    args = parser.parse_args()

    faces = extract_faces(Image.open(args.cross))
    for override in args.face:
        name, _, path = override.partition("=")
        if name not in faces or not path:
            parser.error(f"--face expects NAME=PATH with NAME one of {sorted(faces)}, got '{override}'")
        faces[name] = Image.open(path)

    out = build_3x2(faces)
    out.save(args.output, optimize=True)
    print(f"wrote {args.output} ({out.size[0]}x{out.size[1]})")

    if args.check:
        across, within = seam_check(out)
        print(f"seam check: across-edge {across:.2f}, within-face {within:.2f}, ratio {across / within:.2f}"
              " (near 1 = seamless)")


if __name__ == "__main__":
    main()

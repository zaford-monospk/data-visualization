#!/usr/bin/env python3
"""Generate a 3-second time sequence of temperature volumes from room.vtk,
modelling the server racks as fluctuating heat sources.

Pipeline:
  1. Voxelize the steady-state CFD temperature + velocity onto a 0.1 m grid.
  2. Detect the server racks (solid blocks in the room interior) via
     connected-component labelling.
  3. Time-step the temperature field with semi-Lagrangian advection along the
     CFD velocity field, where
       - each rack pulses its heat output with its own frequency/phase/amplitude
         (server load fluctuation), applied as a relaxation target around the rack
       - the velocity field gets a swirl perturbation proportional to local speed
         so the thermal plumes flutter instead of drifting rigidly.

Output:
  data/frames.raw        - uint8[F][nz][ny][nx], 0 = solid/empty, 1..255 = temp
  data/frames_meta.json

Usage: python3 tools/make_frames.py [path/to/room.vtk]
"""
import sys, os, json, time
import numpy as np
from scipy import ndimage

VTK_PATH = sys.argv[1] if len(sys.argv) > 1 else "/Users/normanlee/Downloads/온도/room.vtk"
OUT_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "data")
VOXEL = 0.1        # meters
FPS = 10           # simulation steps per second
DURATION = 3.0     # seconds
DT = 1.0 / FPS
RELAX = 0.14       # per-step pull toward the (modulated) steady state
PULSE_AMP = (0.20, 0.45)   # per-rack heat pulse amplitude range
SWIRL = 0.30       # velocity flutter strength (fraction of local speed)

rng = np.random.default_rng(42)
t0 = time.time()
print("reading", VTK_PATH)
text = open(VTK_PATH, "r", errors="replace").read()

def floats_between(start_key, end_key):
    i = text.index(start_key)
    i = text.index("\n", i) + 1
    j = text.index(end_key, i) if end_key else len(text)
    return np.fromstring(text[i:j], dtype=np.float64, sep=" ")

pts = floats_between("POINTS", "CELLS").reshape(-1, 3)
cells = floats_between("CELLS", "CELL_TYPES").astype(np.int64).reshape(-1, 9)[:, 1:]
temps = floats_between("LOOKUP_TABLE default", "SCALARS")  # first scalar = Temperature
vel = floats_between("VECTORS", None).reshape(-1, 3)
ncells = len(cells)
print("parsed: %d points, %d cells  (%.1fs)" % (len(pts), ncells, time.time() - t0))

o = pts.min(axis=0); m = pts.max(axis=0)
nx, ny, nz = (np.ceil((m - o) / VOXEL)).astype(int)
print("grid: %dx%dx%d @ %gm" % (nx, ny, nz, VOXEL))

tmin, tmax = temps.min(), temps.max()
T = np.zeros((nz, ny, nx), dtype=np.float32)
V = np.zeros((nz, ny, nx, 3), dtype=np.float32)

cmin = pts[cells].min(axis=1)
cmax = pts[cells].max(axis=1)
ia = np.clip(np.round((cmin - o) / VOXEL).astype(int), 0, [nx - 1, ny - 1, nz - 1])
ib = np.round((cmax - o) / VOXEL).astype(int)
ib = np.clip(np.maximum(ib, ia + 1), 1, [nx, ny, nz])
tn = ((temps - tmin) / (tmax - tmin)).astype(np.float32)

for c in range(ncells):
    x0, y0, z0 = ia[c]; x1, y1, z1 = ib[c]
    T[z0:z1, y0:y1, x0:x1] = tn[c]
    V[z0:z1, y0:y1, x0:x1] = vel[c]
mask = T > 0
T[mask & (T == 0)] = 1.0 / 254.0
print("voxelized, filled %.1f%%  (%.1fs)" % (100.0 * mask.mean(), time.time() - t0))

# --- rack detection: solid components inside the room, standing on the floor ---
solid = ~mask
labels, ncomp = ndimage.label(solid)
racks = []  # voxel-indices of each rack's thermal influence shell
objects = ndimage.find_objects(labels)
sizes = np.bincount(labels.ravel())
for idx in range(1, ncomp + 1):
    size = sizes[idx]
    if not (200 <= size <= 5000):
        continue  # walls / ceiling / tiny fragments
    sl = objects[idx - 1]
    # expand bbox by 3 voxels (0.3 m) for the thermal influence shell
    zs = slice(max(sl[0].start - 3, 0), min(sl[0].stop + 3, nz))
    ys = slice(max(sl[1].start - 3, 0), min(sl[1].stop + 5, ny))  # a bit more headroom above
    xs = slice(max(sl[2].start - 3, 0), min(sl[2].stop + 3, nx))
    shell = np.zeros((nz, ny, nx), dtype=bool)
    sub = ndimage.binary_dilation(labels[zs, ys, xs] == idx, iterations=3)
    shell[zs, ys, xs] = sub
    shell &= mask  # only air voxels
    racks.append(np.where(shell))
nracks = len(racks)
print("racks detected: %d  (%.1fs)" % (nracks, time.time() - t0))
assert nracks >= 10, "rack detection failed"

# per-rack pulse parameters: integer cycles over DURATION so the loop closes
freq = rng.choice([1, 2, 3], nracks, p=[0.5, 0.35, 0.15])   # cycles per 3 s
phase = rng.uniform(0, 2 * np.pi, nracks)
amp = rng.uniform(PULSE_AMP[0], PULSE_AMP[1], nracks)

# --- advection setup ---
zz, yy, xx = np.meshgrid(
    np.arange(nz, dtype=np.float32),
    np.arange(ny, dtype=np.float32),
    np.arange(nx, dtype=np.float32), indexing="ij")
maskf = mask.astype(np.float32)
speed = np.sqrt((V ** 2).sum(axis=-1))

def trilinear(field, px, py, pz):
    x0 = np.clip(np.floor(px).astype(int), 0, nx - 2)
    y0 = np.clip(np.floor(py).astype(int), 0, ny - 2)
    z0 = np.clip(np.floor(pz).astype(int), 0, nz - 2)
    fx = np.clip(px - x0, 0, 1).astype(np.float32)
    fy = np.clip(py - y0, 0, 1).astype(np.float32)
    fz = np.clip(pz - z0, 0, 1).astype(np.float32)
    c00 = field[z0, y0, x0] * (1 - fx) + field[z0, y0, x0 + 1] * fx
    c10 = field[z0, y0 + 1, x0] * (1 - fx) + field[z0, y0 + 1, x0 + 1] * fx
    c01 = field[z0 + 1, y0, x0] * (1 - fx) + field[z0 + 1, y0, x0 + 1] * fx
    c11 = field[z0 + 1, y0 + 1, x0] * (1 - fx) + field[z0 + 1, y0 + 1, x0 + 1] * fx
    return (c00 * (1 - fy) + c10 * fy) * (1 - fz) + (c01 * (1 - fy) + c11 * fy) * fz

nframes = int(DURATION * FPS) + 1
frames = np.empty((nframes, nz, ny, nx), dtype=np.uint8)
T0 = T.copy()
cur = T.copy()

def encode(f):
    e = np.zeros_like(f, dtype=np.uint8)
    e[mask] = 1 + np.clip(f[mask] * 254.0, 0, 254).astype(np.uint8)
    return e

frames[0] = encode(cur)
for f in range(1, nframes):
    t = f * DT
    w = 2 * np.pi * t / DURATION

    # velocity flutter: swirl perturbation proportional to local speed
    dvx = SWIRL * speed * np.sin(2 * w + zz * 0.35 + yy * 0.2)
    dvy = 0.5 * SWIRL * speed * np.sin(3 * w + xx * 0.3 + zz * 0.25)
    dvz = SWIRL * speed * np.cos(2 * w + xx * 0.35 + yy * 0.2)
    px = xx - (V[..., 0] + dvx) * (DT / VOXEL)
    py = yy - (V[..., 1] + dvy) * (DT / VOXEL)
    pz = zz - (V[..., 2] + dvz) * (DT / VOXEL)

    adv = trilinear(cur, px, py, pz)
    wv = trilinear(maskf, px, py, pz)
    ok = wv > 0.5
    nT = cur.copy()
    nT[ok] = adv[ok] / wv[ok]

    # rack heat pulses: relaxation target = steady state boosted around each rack
    boost = np.zeros_like(T0)
    pulse = amp * np.sin(freq * w + phase)
    for r in range(nracks):
        boost[racks[r]] += pulse[r]
    target = np.clip(T0 * (1.0 + boost), 0.0, 1.0)

    nT = nT * (1 - RELAX) + target * RELAX
    nT[~mask] = 0
    cur = nT
    frames[f] = encode(cur)
    print("frame %d/%d  (%.1fs)" % (f, nframes - 1, time.time() - t0))

os.makedirs(OUT_DIR, exist_ok=True)
out = os.path.join(OUT_DIR, "frames.raw")
frames.tofile(out)
meta = {
    "dims": [int(nx), int(ny), int(nz)],
    "voxelSize": VOXEL,
    "bboxMin": list(o),
    "bboxMax": list(m),
    "tempMin": float(tmin),
    "tempMax": float(tmax),
    "frames": nframes,
    "fps": FPS,
    "duration": DURATION,
    "racks": nracks,
    "source": os.path.basename(VTK_PATH),
}
with open(os.path.join(OUT_DIR, "frames_meta.json"), "w") as fp:
    json.dump(meta, fp, indent=2)
print("wrote %s (%.1f MB), %d racks  total %.1fs"
      % (out, os.path.getsize(out) / 1e6, nracks, time.time() - t0))

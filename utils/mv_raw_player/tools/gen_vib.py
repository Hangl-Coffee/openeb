#!/usr/bin/env python3
"""Synthetic EVT2 RAW with two vibrating dark bars on a bright background (no numpy needed).

  gen_vib.py out.raw [seconds]

Bar A (x~160, y 100..130) oscillates at 40 Hz, bar B (x~480, y 300..330) at 50 Hz, amplitude 10 px.
Leading edges give OFF events, trailing edges ON events (dark object); every pixel crossing is a short
two-event burst, plus a little random background noise.
"""
import math
import random
import struct
import sys

W, H = 640, 480
out = sys.argv[1]
dur_s = float(sys.argv[2]) if len(sys.argv) > 2 else 4.0
rng = random.Random(7)

BARS = [  # (centre x, y0, y1, frequency Hz)
    (160.0, 100, 130, 40.0),
    (480.0, 300, 330, 50.0),
]
AMP = 10.0
HALF_W = 4.0
STEP_US = 20

ev = []  # (t, x, y, p)


def emit(t, x, y0, y1, p):
    for y in range(y0, y1):
        j = rng.randint(0, 15)
        ev.append((t + j, x, y, p))
        ev.append((t + j + 60, x, y, p))  # burst


end_us = int(dur_s * 1e6)
for (cx, y0, y1, f) in BARS:
    prev_r = prev_l = None
    t = 0
    while t < end_us:
        x = cx + AMP * math.sin(2 * math.pi * f * t / 1e6)
        r = int(math.floor(x + HALF_W))
        l = int(math.floor(x - HALF_W))
        if prev_r is not None:
            if r > prev_r:      # right edge moves right: leading -> OFF
                for c in range(prev_r + 1, r + 1):
                    emit(t, c, y0, y1, 0)
            elif r < prev_r:    # right edge moves left: trailing -> ON
                for c in range(r + 1, prev_r + 1):
                    emit(t, c, y0, y1, 1)
            if l > prev_l:      # left edge moves right: trailing -> ON
                for c in range(prev_l, l):
                    emit(t, c, y0, y1, 1)
            elif l < prev_l:    # left edge moves left: leading -> OFF
                for c in range(l, prev_l):
                    emit(t, c, y0, y1, 0)
        prev_r, prev_l = r, l
        t += STEP_US

for _ in range(int(2000 * dur_s)):  # background noise
    ev.append((rng.randint(0, end_us - 1), rng.randint(0, W - 1), rng.randint(0, H - 1), rng.randint(0, 1)))

ev.sort()
hdr = "% evt 2.0\n% format EVT2;height={};width={}\n% geometry {}x{}\n% end\n".format(H, W, W, H).encode()
buf = bytearray()
last_hi = -1
for (t, x, y, p) in ev:
    hi = t >> 6
    if hi != last_hi:
        buf += struct.pack('<I', (0x8 << 28) | (hi & 0x0FFFFFFF))
        last_hi = hi
    buf += struct.pack('<I', (p << 28) | ((t & 0x3F) << 22) | (x << 11) | y)
with open(out, 'wb') as f:
    f.write(hdr)
    f.write(buf)
print(len(ev), 'events', dur_s, 's')

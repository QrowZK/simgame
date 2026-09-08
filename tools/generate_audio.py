#!/usr/bin/env python3
"""Generates the game's sound effects.

Synthesised rather than sourced, for the same reason the machine models and the
item icons are: a build should be reproducible from the repository alone, and a
handful of short mechanical noises are cheap to describe as maths and expensive
to license. Run it and commit the result; CI regenerates and diffs, so a
hand-edited wav fails the build the way hand-edited data does.

    python3 tools/generate_audio.py
"""

import math
import os
import random
import struct
import wave

RATE = 44100
OUT = os.path.join(os.path.dirname(__file__), "..", "game", "audio")


def write(name, samples):
    """One channel, 16-bit, at the engine's rate."""
    path = os.path.join(OUT, name + ".wav")
    peak = max(1e-9, max(abs(s) for s in samples))
    with wave.open(path, "w") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(RATE)
        f.writeframes(b"".join(
            struct.pack("<h", int(max(-1.0, min(1.0, s / peak * 0.86)) * 32767))
            for s in samples))
    return path


def envelope(i, n, attack=0.005, release=0.4):
    """Percussive by default: almost no attack, an exponential tail."""
    t = i / RATE
    total = n / RATE
    if t < attack:
        return t / attack
    return math.exp(-(t - attack) / (release * total + 1e-9))


def noise(n, seed):
    rng = random.Random(seed)
    return [rng.uniform(-1.0, 1.0) for _ in range(n)]


def lowpass(xs, alpha):
    out, y = [], 0.0
    for x in xs:
        y += alpha * (x - y)
        out.append(y)
    return out


def dig(seed=7):
    """A short, dull impact. Filtered noise plus a low body, no ring: a hand
    tool hitting rock, not a bell."""
    n = int(RATE * 0.20)
    body = lowpass(noise(n, seed), 0.06)
    return [
        (body[i] * 0.8 + math.sin(2 * math.pi * 96 * i / RATE) * 0.5)
        * envelope(i, n, 0.002, 0.30)
        for i in range(n)
    ]


def place():
    """Something heavy set down and settling: a thud with a metal tick on top."""
    n = int(RATE * 0.26)
    body = lowpass(noise(n, 11), 0.05)
    out = []
    for i in range(n):
        t = i / RATE
        thud = math.sin(2 * math.pi * 72 * t) * math.exp(-t / 0.05)
        tick = math.sin(2 * math.pi * 1180 * t) * math.exp(-t / 0.012) * 0.35
        out.append((body[i] * 0.5 + thud + tick) * envelope(i, n, 0.001, 0.32))
    return out


def deliver():
    """Research accepted. The only pitched sound in the set, and the only one
    that rises: everything else in this game is something landing."""
    n = int(RATE * 0.42)
    out = []
    for i in range(n):
        t = i / RATE
        a = math.sin(2 * math.pi * 587.33 * t)          # D5
        b = math.sin(2 * math.pi * 880.00 * t) * (0.0 if t < 0.09 else 0.7)   # A5
        out.append((a + b) * envelope(i, n, 0.004, 0.5) * 0.6)
    return out


def refuse():
    """A refusal, and it has to be unmistakably not a confirmation: low, short,
    and falling."""
    n = int(RATE * 0.18)
    out = []
    for i in range(n):
        t = i / RATE
        f = 220 - 90 * (t / (n / RATE))
        out.append(math.sin(2 * math.pi * f * t) * envelope(i, n, 0.003, 0.25) * 0.7)
    return out


def main():
    os.makedirs(OUT, exist_ok=True)
    for name, fn in (("dig", dig), ("place", place),
                     ("deliver", deliver), ("refuse", refuse)):
        path = write(name, fn())
        print(f"{name:9} {os.path.getsize(path):6} bytes")


if __name__ == "__main__":
    main()

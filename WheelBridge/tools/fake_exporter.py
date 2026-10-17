"""Stand-in for the in-game Lua exporter: sends synthetic frames to the WheelBridge pipe.

Mirrors the wire format in Mods/WheelBridgeExporter/Scripts/main.lua (frame v1).
Usage: python fake_exporter.py [seconds]
"""
import math
import struct
import sys
import time

PIPE = r"\\.\pipe\swgr_wheelbridge"
HEADER = "<4sHHIdfIBB"
N_FLOATS, N_BYTES, MAX_COLL = 37, 8, 4
COLL = "<fffffffiB"
SIZE = struct.calcsize(HEADER) + 4 * N_FLOATS + N_BYTES + MAX_COLL * struct.calcsize(COLL)


def frame(seq: int, t: float, dt: float) -> bytes:
    steer = math.sin(t * 1.5)
    floats = [0.0] * N_FLOATS
    floats[0] = 300 + 50 * math.sin(t * 0.3)   # SpeedKmph
    floats[1] = 346.0                           # TopSpeedKmph
    floats[2] = floats[0] * 27.78               # RelVelX
    floats[8] = 20 * steer                      # SlipDeg
    floats[9] = steer                           # SteerInput
    floats[24] = 1.0                            # QuatW (identity rotation)
    floats[15] = 1.0                            # OnGroundScale
    bytes_ = [4, 3, 2, 0, 0, 3, 0, 0]           # Ramjet / Active / Recovering / ... / Racing
    flags = 1 | 16 | (2 if abs(steer) > 0.8 else 0)
    colls = b""
    n = 0
    if seq % 100 == 0:                          # an impact on the right side every 100 frames
        colls = struct.pack(COLL, 0.0, -1.0, 0.0, 50.0, 150.0, 0.0, 2500.0, seq, 0)
        n = 1
    colls += struct.pack(COLL, *([0.0] * 7), 0, 0) * (MAX_COLL - n)
    return (struct.pack(HEADER, b"SWGR", 1, SIZE, seq, t, dt, flags, 1, n)
            + struct.pack("<%df" % N_FLOATS, *floats)
            + struct.pack("<%dB" % N_BYTES, *bytes_)
            + colls)


def main() -> None:
    seconds = float(sys.argv[1]) if len(sys.argv) > 1 else 5.0
    with open(PIPE, "wb", buffering=0) as pipe:
        t, seq, dt = 0.0, 0, 1 / 120
        end = time.time() + seconds
        while time.time() < end:
            seq += 1
            t += dt
            pipe.write(frame(seq, t, dt))
            time.sleep(dt)
    print(f"sent {seq} frames of {SIZE} bytes")


if __name__ == "__main__":
    main()

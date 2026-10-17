"""Offline test of Mods/WheelBridgeExporter/Scripts/main.lua in a real Lua 5.4 VM (lupa), with UE4SS stubbed.

Checks: frame size/version, vehicle class detection from gameplay tag and blueprint name, on-foot flag,
that a broken property read can't throw out of the tick, and that a closed pipe is retried.
Usage: python exporter_test.py   (needs lupa: pip install lupa, or set LUPA_PATH to a folder that has it)
"""
import os
import struct
import sys

# package.ps1 runs this with python -I (no PYTHONPATH), so a lupa outside site-packages comes from LUPA_PATH.
if os.environ.get("LUPA_PATH"):
    sys.path.insert(0, os.environ["LUPA_PATH"])
try:
    from lupa.lua54 import LuaRuntime  # UE4SS embeds Lua 5.4
except ImportError:
    from lupa import LuaRuntime

HERE = os.path.dirname(os.path.abspath(__file__))
MAIN = os.path.join(HERE, "..", "Mods", "WheelBridgeExporter", "Scripts", "main.lua")
HEADER = "<4sHHIdfIBB"

STUBS = r"""
written = {}
pipe_open_ok = true
local Pipe = {}
Pipe.__index = Pipe
function Pipe:setvbuf() end
function Pipe:write(s) table.insert(written, s) return true end
function Pipe:flush() return true end
function Pipe:close() end
io.open = function(path, mode) if pipe_open_ok then return setmetatable({}, Pipe) end return nil end

local function obj(t)
  t.IsValid = function() return true end
  t.GetAddress = function() return t._addr end
  t.GetFullName = function() return t._name end
  return t
end
function make_name(s) return { ToString = function() return s end } end
function make_vehicle(addr, bpName, tag)
  local vs = obj({ _addr = addr + 1, SpeedKmph = 200, TopSpeedKmph = 300, RelativeVelocity = { X = 1, Y = 2, Z = 3 },
                   VehicleTransform = { Rotation = { X = 0, Y = 0, Z = 0, W = 1 } }, InputMode = 3, Drifting = true })
  local p = obj({ _addr = addr, _name = bpName .. " /Game/Level." .. bpName })
  p.VehicleStateComponent = obj({ _addr = addr + 2, VehicleState = vs })
  p.VehicleTypeTag = { TagName = make_name(tag) }
  p.GetClass = function() return { GetFName = function() return make_name(bpName) end, GetFullName = function() return "BlueprintGeneratedClass " .. bpName end } end
  p.CollisionState = { CollisionList = { ForEach = function(self, fn) end } }
  p.IsA = function() return false end
  return p
end
function make_character(addr)
  local p = obj({ _addr = addr, _name = "BP_GfnCharacter_Customisable_C" })
  p.IsA = function() return true end
  p.GetClass = function() return { GetFName = function() return make_name("BP_GfnCharacter_Customisable_C") end } end
  return p
end
current_pawn = nil
local pc = obj({ _addr = 1 })
setmetatable(pc, { __index = function(t, k)
  if k == "Pawn" then return current_pawn end
  if k == "PlayerState" then return { RaceEventComponent = obj({ _addr = 9, bHasFinishedEvent = false }) } end
end })
package.preload["UEHelpers"] = function() return { GetPlayerController = function() return pc end } end
StaticFindObject = function() return obj({ _addr = 77 }) end
tick = nil
RegisterEngineTickPostCallback = function(fn) tick = fn end
print = function() end
"""


def frames(lua):
    w = lua.globals().written
    return [w[i] for i in range(1, len(w) + 1)]


def run_ticks(lua, n):
    for _ in range(n):
        lua.globals().tick(None, 1 / 60)


def main():
    lua = LuaRuntime(unpack_returned_tuples=True, encoding=None)
    lua.execute(STUBS)
    with open(MAIN, "rb") as f:
        lua.execute(f.read())
    g = lua.globals()
    failures = []

    def check(cond, what):
        print(("PASS  " if cond else "FAIL  ") + what)
        if not cond:
            failures.append(what)

    cases = [
        ("VBP_Car_C", "Vehicle.Type.Car", 1),
        ("VBP_Bike_C", "Vehicle.Type.Bike", 2),
        ("VBP_Skimmer_C", "Vehicle.Type.Unrecognised", 3),
        ("VBP_Plane_Memory_C", "Vehicle.Type.Unrecognised", 4),
        ("VBP_Mystery_C", "Vehicle.Type.Pod", 4),
        ("VBP_Mystery_C", "Vehicle.Type.Mystery", 0),
    ]
    addr = 1000
    for bp, tag, expect in cases:
        addr += 10
        g.current_pawn = g.make_vehicle(addr, bp.encode(), tag.encode())
        before = len(frames(lua))
        run_ticks(lua, 1)
        fr = frames(lua)[before:]
        if not fr:
            check(False, f"{bp}: no frame written")
            continue
        raw = fr[-1]
        magic, ver, size, seq, sim, dt, flags, cls, ncoll = struct.unpack_from(HEADER, raw)
        check(magic == b"SWGR" and ver == 1 and size == 318 and len(raw) == 318, f"{bp}: frame v{ver} {len(raw)} B")
        check(cls == expect, f"{bp} / {tag}: class {cls} (want {expect})")
        check(flags & 1 and flags & 2, f"{bp}: HasVehicle + Drifting flags ({flags})")

    g.current_pawn = g.make_character(5000)
    run_ticks(lua, 1)
    _, _, _, _, _, _, flags, cls, _ = struct.unpack_from(HEADER, frames(lua)[-1])
    check(flags & 64 and not flags & 1 and cls == 0, f"on foot: flags {flags}, class {cls}")

    # A property read that throws must not escape the tick (the game would log errors every frame).
    lua.execute("local p = make_vehicle(6000, 'VBP_Car_C', 'Vehicle.Type.Car'); "
                "p.VehicleStateComponent.VehicleState.SpeedKmph = nil; "
                "setmetatable(p.VehicleStateComponent.VehicleState, { __index = function() error('boom') end }); current_pawn = p")
    before = len(frames(lua))
    run_ticks(lua, 2)
    check(len(frames(lua)) > before, "tick survives a throwing property read")

    # Bridge gone: write fails → pipe closed → retried after RECONNECT_FRAMES.
    lua.execute("local P = getmetatable(io.open('x','wb')); P.write = function() return nil end")
    run_ticks(lua, 1)
    lua.execute("local P = getmetatable(io.open('x','wb')); P.write = function(self, s) table.insert(written, s) return true end")
    before = len(frames(lua))
    run_ticks(lua, 60)
    check(len(frames(lua)) == before, "waits before reconnecting")
    run_ticks(lua, 100)
    check(len(frames(lua)) > before, "reconnects after the bridge comes back")

    print(f"\n{len(failures)} failed")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()

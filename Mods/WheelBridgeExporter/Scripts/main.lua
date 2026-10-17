-- WheelBridgeExporter: streams per-frame vehicle telemetry to the WheelBridge app over a named pipe.
-- Frame layout is defined once in FRAME below; WheelBridge/Telemetry.cs must match it (FRAME_VERSION guards this).
-- The bridge app owns the pipe; until it is running, this mod quietly retries every ~2 s.

local UEHelpers = require("UEHelpers")

local PIPE_PATH = "\\\\.\\pipe\\swgr_wheelbridge"
local FRAME_VERSION = 1
local READ_COLLISIONS = true     -- set false if reading CollisionList ever proves unstable
local MAX_COLLISIONS = 4
local RECONNECT_FRAMES = 120

-- Float fields read from pawn.VehicleStateComponent.VehicleState, in wire order.
-- Each entry: { wire name, property path... }
local FLOATS = {
    { "SpeedKmph", "SpeedKmph" },
    { "TopSpeedKmph", "TopSpeedKmph" },
    { "RelVelX", "RelativeVelocity", "X" },
    { "RelVelY", "RelativeVelocity", "Y" },
    { "RelVelZ", "RelativeVelocity", "Z" },
    { "AngVelX", "RelativeAngularVelocity", "X" },
    { "AngVelY", "RelativeAngularVelocity", "Y" },
    { "AngVelZ", "RelativeAngularVelocity", "Z" },
    { "SlipDeg", "VehicleSlipAngleDegrees" },
    { "SteerInput", "SteeringInput" },
    { "SteerIntent", "SteeringIntent" },
    { "Throttle", "Throttle" },
    { "Brake", "Brake" },
    { "Rpm", "RPM" },
    { "RpmScale", "RPMScale" },
    { "OnGroundScale", "OnGroundScale" },
    { "LevitationScale", "LevitationScale" },
    { "TimeInAir", "TimeInAir" },
    { "Roll", "Roll" },
    { "Pitch", "Pitch" },
    { "SpeedScale", "SpeedScale" },
    { "QuatX", "VehicleTransform", "Rotation", "X" },
    { "QuatY", "VehicleTransform", "Rotation", "Y" },
    { "QuatZ", "VehicleTransform", "Rotation", "Z" },
    { "QuatW", "VehicleTransform", "Rotation", "W" },
    { "GroundNormalX", "GroundNormal", "X" },
    { "GroundNormalY", "GroundNormal", "Y" },
    { "GroundNormalZ", "GroundNormal", "Z" },
    { "RamjetHeat", "RamjetHeat" },
    { "RamjetBypassHeat", "RamjetBypassHeat" },
    { "BypassOverheatPct", "BypassOverheatingPercentage" },
    { "AfterburnerEnergy", "AfterburnerEnergy" },
    { "BoostTransitionScale", "BoostTransitionScale" },
    { "CrashTimeRemaining", "CrashTimeRemaining" },
    { "DraftingScale", "DraftingScale" },
    { "AirBrakeScale", "AirBrakeScale" },
    { "PredictedHitTime", "PredictedVelocityHitTime" },
}

-- Enum fields (one byte each), in wire order.
local BYTES = {
    { "BoostState", "BoostState" },
    { "RamjetState", "RamjetState" },
    { "AfterburnerState", "AfterburnerState" },
    { "LandingState", "LandingState" },
    { "PerfectLandingState", "PerfectLandingState" },
    { "InputMode", "InputMode" },
    { "DraftingState", "DraftingState" },
    { "EnergyBrakeState", "EnergyBrakeState" },
}

-- Flag bits.
local FLAG_HAS_VEHICLE   = 1
local FLAG_DRIFTING      = 2
local FLAG_UPSIDE_DOWN   = 4
local FLAG_BOOST_TOGGLED = 8
local FLAG_COLLISIONS_OK = 16
local FLAG_RACE_DONE     = 32   -- player finished / timed out / was eliminated (PlayerState.RaceEventComponent)
local FLAG_ON_FOOT       = 64   -- the pawn is a character (Shade walking the hub), not a vehicle

-- Vehicle class codes: the game's four physics families. Pods are "Plane" in game code (VBP_Plane_*, FPlaneConfig).
local CLASS_UNKNOWN, CLASS_CAR, CLASS_BIKE, CLASS_SKIMMER, CLASS_POD = 0, 1, 2, 3, 4

-- magic, version, frameSize, seq, simTime, dt, flags, vehicleClass, nCollisions
local HEADER_FMT = "<c4I2I2I4dfI4BB"
local FLOATS_FMT = "<" .. string.rep("f", #FLOATS)
local BYTES_FMT = "<" .. string.rep("B", #BYTES)
-- dirX,dirY,dirZ, localX,localY,localZ, pointVelocityDelta, collisionId, otherIsVehicle
local COLL_FMT = "<fffffffiB"
local FRAME_SIZE = string.packsize(HEADER_FMT) + string.packsize(FLOATS_FMT) + string.packsize(BYTES_FMT)
    + MAX_COLLISIONS * string.packsize(COLL_FMT)

local pipe = nil
local framesUntilRetry = 0
local seq = 0
local simTime = 0.0
local pawn, vstate = nil, nil
local raceEvent = nil
local footPawnAddr, footResult = nil, false
local characterClass = nil
local vehicleClass = CLASS_UNKNOWN
local floatVals, byteVals = {}, {}
local EMPTY_COLL = string.pack(COLL_FMT, 0, 0, 0, 0, 0, 0, 0, 0, 0)

local function log(msg) print("[WheelBridgeExporter] " .. msg .. "\n") end

local function isValid(o)
    if o == nil then return false end
    local ok, r = pcall(function() return o:IsValid() end)
    return ok and r == true
end

local function read(root, path)
    local v = root
    for i = 2, #path do
        local key = path[i]
        local ok, r = pcall(function() return v[key] end)
        if not ok or r == nil then return nil end
        v = r
    end
    return v
end

local function classFromName(s)
    if s == nil then return CLASS_UNKNOWN end
    if string.find(s, "Car", 1, true) then return CLASS_CAR end
    if string.find(s, "Bike", 1, true) then return CLASS_BIKE end
    if string.find(s, "Skim", 1, true) then return CLASS_SKIMMER end
    if string.find(s, "Plane", 1, true) or string.find(s, "Pod", 1, true) then return CLASS_POD end
    return CLASS_UNKNOWN
end

-- Gameplay tag first; the blueprint class name (VBP_Car_C, VBP_Bike_C, VBP_Plane_Memory_C, ...) as a fallback,
-- because the tag of VBP_Plane_Memory_C matched none of the names.
local function vehicleClassOf(p)
    local okTag, tag = pcall(function() return p.VehicleTypeTag.TagName:ToString() end)
    if not okTag then tag = nil end
    local okName, cls = pcall(function() return p:GetClass():GetFName():ToString() end)
    if not okName then cls = nil end
    local c = classFromName(tag)
    if c == CLASS_UNKNOWN then c = classFromName(cls) end
    return c, tostring(tag), tostring(cls)
end

local function resolveVehicle()
    local pc = UEHelpers.GetPlayerController()
    if not isValid(pc) then pawn, vstate = nil, nil return false end
    local p = pc.Pawn
    -- Keep the tracked vehicle only while the controller still possesses it (it may outlive the swap to Shade).
    if isValid(pawn) and isValid(vstate) and isValid(p) and p:GetAddress() == pawn:GetAddress() then return true end
    pawn, vstate = nil, nil
    if not isValid(p) then return false end
    local ok, comp = pcall(function() return p.VehicleStateComponent end)
    if not ok or not isValid(comp) then return false end
    local vs = comp.VehicleState
    if not isValid(vs) then return false end
    pawn, vstate = p, vs
    local tag, cls
    vehicleClass, tag, cls = vehicleClassOf(p)
    log(string.format("tracking %s (class %d, tag %s, bp %s)", p:GetFullName(), vehicleClass, tag, cls))
    return true
end

-- Race-over check, read from the player's race event component (scalar bools only).
local function raceDone()
    if not isValid(raceEvent) then
        raceEvent = nil
        local ok, comp = pcall(function() return UEHelpers.GetPlayerController().PlayerState.RaceEventComponent end)
        if not ok or not isValid(comp) then return false end
        raceEvent = comp
    end
    local ok, done = pcall(function()
        return raceEvent.bHasFinishedEvent == true or raceEvent.bHasTimedOut == true or raceEvent.bHasBeenEliminated == true
    end)
    return ok and done
end

-- True when the controlled pawn is a character (AGfnCharacter) rather than a vehicle. Cached per pawn.
local function onFoot()
    local ok, p = pcall(function() return UEHelpers.GetPlayerController().Pawn end)
    if not ok or not isValid(p) then footPawnAddr = nil return false end
    local addr = p:GetAddress()
    if addr == footPawnAddr then return footResult end
    footPawnAddr = addr
    if not isValid(characterClass) then characterClass = StaticFindObject("/Script/Griffin.GfnCharacter") end
    local isChar = false
    if isValid(characterClass) then
        local okIsA, r = pcall(function() return p:IsA(characterClass) end)
        isChar = okIsA and r == true
    else
        local okName, name = pcall(function() return p:GetClass():GetFullName() end)
        isChar = okName and name ~= nil and string.find(name, "Character", 1, true) ~= nil
    end
    footResult = isChar
    log(string.format("pawn %s is %s", p:GetFullName(), isChar and "on foot" or "not a character"))
    return isChar
end

local function packCollisions()
    if not READ_COLLISIONS then return 0, false, "", 0 end
    local parts, n = {}, 0
    local ok = pcall(function()
        local list = pawn.CollisionState.CollisionList
        list:ForEach(function(_, elem)
            n = n + 1
            if #parts < MAX_COLLISIONS then
                local c = elem:get()
                local other = isValid(c.OtherPawn) and 1 or 0
                table.insert(parts, string.pack(COLL_FMT,
                    c.WorldDirection.X, c.WorldDirection.Y, c.WorldDirection.Z,
                    c.VehicleLocalLocation.X, c.VehicleLocalLocation.Y, c.VehicleLocalLocation.Z,
                    c.PointVelocityDelta, c.CollisionID, other))
            end
        end)
    end)
    if not ok then return 0, false, "", 0 end
    return n, true, table.concat(parts), #parts
end

local function buildFrame(dt)
    local flags, nColl, collBytes, shown = 0, 0, "", 0
    local has = resolveVehicle()
    if has then
        flags = flags | FLAG_HAS_VEHICLE
        for i, f in ipairs(FLOATS) do
            local v = read(vstate, f)
            floatVals[i] = type(v) == "number" and v or 0
        end
        for i, b in ipairs(BYTES) do
            local v = read(vstate, b)
            byteVals[i] = type(v) == "number" and (math.floor(v) & 0xFF) or 0
        end
        if read(vstate, { "", "Drifting" }) == true then flags = flags | FLAG_DRIFTING end
        if read(vstate, { "", "IsUpsideDown" }) == true then flags = flags | FLAG_UPSIDE_DOWN end
        if read(vstate, { "", "BoostToggledOn" }) == true then flags = flags | FLAG_BOOST_TOGGLED end
        local ok
        nColl, ok, collBytes, shown = packCollisions()
        if ok then flags = flags | FLAG_COLLISIONS_OK end
        if raceDone() then flags = flags | FLAG_RACE_DONE end
    else
        if onFoot() then flags = flags | FLAG_ON_FOOT end
        for i = 1, #FLOATS do floatVals[i] = 0 end
        for i = 1, #BYTES do byteVals[i] = 0 end
    end
    seq = (seq + 1) & 0xFFFFFFFF
    return string.pack(HEADER_FMT, "SWGR", FRAME_VERSION, FRAME_SIZE, seq, simTime, dt, flags,
            has and vehicleClass or CLASS_UNKNOWN, math.min(nColl, 255))
        .. string.pack(FLOATS_FMT, table.unpack(floatVals, 1, #FLOATS))
        .. string.pack(BYTES_FMT, table.unpack(byteVals, 1, #BYTES))
        .. collBytes .. string.rep(EMPTY_COLL, MAX_COLLISIONS - shown)
end

local function closePipe(reason)
    if pipe then pcall(function() pipe:close() end) end
    pipe = nil
    framesUntilRetry = RECONNECT_FRAMES
    if reason then log("pipe closed: " .. reason) end
end

local function onTick(dt)
    simTime = simTime + dt
    if pipe == nil then
        if framesUntilRetry > 0 then framesUntilRetry = framesUntilRetry - 1 return end
        pipe = io.open(PIPE_PATH, "wb")
        if pipe == nil then framesUntilRetry = RECONNECT_FRAMES return end
        pipe:setvbuf("full", 4096)
        log("connected to WheelBridge (frame " .. FRAME_SIZE .. " bytes)")
    end
    local frame = buildFrame(dt)
    local ok = pipe:write(frame)
    if ok then ok = pipe:flush() end
    if not ok then closePipe("bridge went away") end
end

local function dtOf(DeltaSeconds)
    if type(DeltaSeconds) == "number" then return DeltaSeconds end
    local ok, v = pcall(function() return DeltaSeconds:get() end)
    if ok and type(v) == "number" then return v end
    return 0
end

if RegisterEngineTickPostCallback then
    RegisterEngineTickPostCallback(function(_, DeltaSeconds)
        local ok, err = pcall(onTick, dtOf(DeltaSeconds))
        if not ok then closePipe("error: " .. tostring(err)) end
    end)
else
    local last = os.clock()
    LoopInGameThreadAfterFrames(1, function()
        local now = os.clock()
        local ok, err = pcall(onTick, now - last)
        last = now
        if not ok then closePipe("error: " .. tostring(err)) end
        return false
    end)
end

log(string.format("loaded (frame v%d, %d bytes, pipe %s)", FRAME_VERSION, FRAME_SIZE, PIPE_PATH))

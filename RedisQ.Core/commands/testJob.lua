-- Derived from BullMQ - Copyright (c) 2018 BullForce Labs AB. - MIT License
-- Original source: https://github.com/taskforcesh/bullmq

local eventsKey = KEYS[8]

local jobId
local jobIdKey
local rcall = redis.call

local args = cmsgpack.unpack(ARGV[1])

local data = ARGV[2]
local opts = cmsgpack.unpack(ARGV[3])

local repeatJobKey = args[5]
local deduplicationKey = args[6]

-- Includes
--- @include "includes/addJobInTargetList"
--- @include "includes/deduplicateJob"

local jobCounter = rcall("INCR", KEYS[4])

local metaKey = KEYS[3]
local maxEvents = getOrSetMaxEvents(metaKey)

local timestamp = args[4]
if args[2] == "" then
    jobId = jobCounter
    jobIdKey = args[1] .. jobId
else
    jobId = args[2]
    jobIdKey = args[1] .. jobId
    if rcall("EXISTS", jobIdKey) == 1 then
        return -1 -- Job already exists
    end
end

-- Emit waiting event
rcall("XADD", eventsKey, "MAXLEN", "~", maxEvents, "*", "event", "waiting",
      "jobId", jobId)

return jobId .. "" -- convert to string

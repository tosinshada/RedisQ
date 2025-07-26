-- Derived from BullMQ - Copyright (c) 2018 BullForce Labs AB. - MIT License
-- Original source: https://github.com/taskforcesh/bullmq

--[[
  Adds a job to the queue by doing the following:
    - Increases the job counter if needed.
    - Creates a new job key with the job data.

    - if delayed:
      - computes timestamp.
      - adds to delayed zset.
      - Emits a global event 'delayed' if the job is delayed.
    - if not delayed
      - Adds the jobId to the wait/paused list in one of three ways:
         - LIFO
         - FIFO
         - prioritized.
      - Adds the job to the "added" list so that workers gets notified.

    Input:
      KEYS[1] 'wait',
      KEYS[2] 'paused'
      KEYS[3] 'meta'
      KEYS[4] 'id'
      KEYS[5] 'delayed'
      KEYS[6] 'active'
      KEYS[7] events stream key
      KEYS[8] marker key

      ARGV[1] msgpacked arguments array
            [1]  key prefix,
            [2]  custom id (optional, will generate if empty)
            [3]  name
            [4]  timestamp
            [5]  repeat job key
            [6] deduplication key

      ARGV[2] Json stringified job data
      ARGV[3] msgpacked options

      Output:
        jobId  - OK
        -1     - Job already exists
]]

local metaKey = KEYS[3]
local eventsKey = KEYS[7]

local jobId
local jobIdKey
local rcall = redis.call

local args = cmsgpack.unpack(ARGV[1])
local data = ARGV[2]
local opts = cmsgpack.unpack(ARGV[3])

local timestamp = args[4]
local repeatJobKey = args[5]
local deduplicationKey = args[6]

-- Includes
--- @include "includes/addJobInTargetList"
--- @include "includes/deduplicateJob"
--- @include "includes/getOrSetMaxEvents"
--- @include "includes/getTargetQueueList"
--- @include "includes/storeJob"

local jobCounter = rcall("INCR", KEYS[4])

local maxEvents = getOrSetMaxEvents(metaKey)

if args[2] == "" then
    jobId = jobCounter
    jobIdKey = args[1] .. jobId
else
    jobId = args[2]
    jobIdKey = args[1] .. jobId
    if rcall("EXISTS", jobIdKey) == 1 then
        rcall("XADD", eventsKey, "MAXLEN", "~", maxEvents, "*", "event",
          "duplicated", "jobId", jobId)
        return -1 -- Job already exists
    end
end

local deduplicationJobId = deduplicateJob(opts['de'], jobId, KEYS[5],
  deduplicationKey, eventsKey, maxEvents, ARGS[1])
if deduplicationJobId then
  return deduplicationJobId
end

-- Store the job.
storeJob(eventsKey, jobIdKey, jobId, args[3], ARGV[2], opts, timestamp)

local target, isPausedOrMaxed = getTargetQueueList(metaKey, KEYS[6], KEYS[1], KEYS[2])

-- LIFO or FIFO
local pushCmd = opts['lifo'] and 'RPUSH' or 'LPUSH'
addJobInTargetList(target, KEYS[8], pushCmd, isPausedOrMaxed, jobId)

-- Emit waiting event
rcall("XADD", eventsKey, "MAXLEN", "~", maxEvents, "*", "event", "waiting",
      "jobId", jobId)

return jobId .. "" -- convert to string

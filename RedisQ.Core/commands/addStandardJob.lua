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

      ARGV[1] key prefix
      ARGV[2] custom id (optional, will generate if empty)
      ARGV[3] name
      ARGV[4] timestamp
      ARGV[5] Json stringified job data
      ARGV[6] msgpacked options

      Output:
        jobId  - OK
        -1     - Job already exists
]]

local waitKey = KEYS[1]
local pausedKey = KEYS[2]
local metaKey = KEYS[3]
local jobIdCounterKey = KEYS[4]
local delayedKey = KEYS[5]
local activeKey = KEYS[6]
local eventsKey = KEYS[7]
local markerKey = KEYS[8]

local jobId
local jobIdKey
local rcall = redis.call

local keyPrefix = ARGV[1]
local customId = ARGV[2]
local jobName = ARGV[3]
local timestamp = ARGV[4]
local data = ARGV[5]
local opts = cmsgpack.unpack(ARGV[6])

-- Includes
--- @include "includes/addJobInTargetQueue"
--- @include "includes/getOrSetMaxEvents"
--- @include "includes/getTargetQueue"
--- @include "includes/storeJob"

local jobCounter = rcall("INCR", jobIdCounterKey)

local maxEvents = getOrSetMaxEvents(metaKey)

if customId == "" then
    jobId = jobCounter
    jobIdKey = keyPrefix .. jobId
else
    jobId = customId
    jobIdKey = keyPrefix .. jobId
    if rcall("EXISTS", jobIdKey) == 1 then
        rcall("XADD", eventsKey, "MAXLEN", "~", maxEvents, "*", "event",
          "duplicated", "jobId", jobId)
        return -1 -- Job already exists
    end
end

-- Store the job.
storeJob(eventsKey, jobIdKey, jobId, jobName, data, opts, timestamp)

local target, isPausedOrMaxed = getTargetQueue(metaKey, activeKey, waitKey, pausedKey)

-- LIFO or FIFO
local pushCmd = (opts['order'] == 'lifo') and 'RPUSH' or 'LPUSH'
addJobInTargetQueue(target, markerKey, pushCmd, isPausedOrMaxed, jobId)

-- Emit waiting event
rcall("XADD", eventsKey, "MAXLEN", "~", maxEvents, "*", "event", "waiting",
      "jobId", jobId)

return jobId .. "" -- convert to string

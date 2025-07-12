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

    Input Parameters:
      @waitKey - wait queue key
      @pausedKey - paused queue key  
      @metaKey - meta key
      @idKey - id counter key
      @completedKey - completed queue key
      @delayedKey - delayed queue key
      @activeKey - active queue key
      @eventsKey - events stream key
      @markerKey - marker key
      @keyPrefix - key prefix for job keys
      @customId - custom job id (optional, will generate if empty)
      @jobName - job name
      @timestamp - job timestamp
      @deduplicationKey - deduplication key (optional)
      @jobData - JSON stringified job data
      @jobOptions - msgpacked job options

      Output:
        jobId  - OK
        -1     - Job already exists
]]

local jobId
local jobIdKey
local rcall = redis.call
local opts = cmsgpack.unpack(@jobOptions)

-- Includes
--- @include "includes/addJobInTargetList"
--- @include "includes/deduplicateJob"
--- @include "includes/getOrSetMaxEvents"
--- @include "includes/getTargetQueueList"
--- @include "includes/storeJob"

local jobCounter = rcall("INCR", @idKey)

local maxEvents = getOrSetMaxEvents(@metaKey)

if @customId == "" then
    jobId = jobCounter
    jobIdKey = @keyPrefix .. jobId
else
    jobId = @customId
    jobIdKey = @keyPrefix .. jobId
    if rcall("EXISTS", jobIdKey) == 1 then
        return -1 -- Job already exists
    end
end

local deduplicationJobId = deduplicateJob(opts['de'], jobId, @delayedKey,
  @deduplicationKey, @eventsKey, maxEvents, @keyPrefix)
if deduplicationJobId then
  return deduplicationJobId
end

-- Store the job.
storeJob(@eventsKey, jobIdKey, jobId, @jobName, @jobData, opts, @timestamp)

local target, isPausedOrMaxed = getTargetQueueList(@metaKey, @activeKey, @waitKey, @pausedKey)

-- LIFO or FIFO
local pushCmd = opts['lifo'] and 'RPUSH' or 'LPUSH'
addJobInTargetList(target, @markerKey, pushCmd, isPausedOrMaxed, jobId)

-- Emit waiting event
rcall("XADD", @eventsKey, "MAXLEN", "~", maxEvents, "*", "event", "waiting",
      "jobId", jobId)

return jobId .. "" -- convert to string

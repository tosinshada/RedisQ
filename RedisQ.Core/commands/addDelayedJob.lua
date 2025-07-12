--[[
  Adds a delayed job to the queue by doing the following:
    - Increases the job counter if needed.
    - Creates a new job key with the job data.

    - computes timestamp.
    - adds to delayed zset.
    - Emits a global event 'delayed' if the job is delayed.
    
    Input Parameters:
      @markerKey - marker key
      @metaKey - meta key
      @idKey - id counter key
      @delayedKey - delayed queue key
      @completedKey - completed queue key
      @eventsKey - events stream key
      @keyPrefix - key prefix for job keys
      @customId - custom job id (optional, will generate if empty)
      @jobName - job name
      @timestamp - job timestamp
      @deduplicationKey - deduplication key (optional)
      @jobData - JSON stringified job data
      @jobOptions - msgpacked job options

      Output:
        jobId  - OK
]]

local jobId
local jobIdKey
local rcall = redis.call
local opts = cmsgpack.unpack(@jobOptions)

-- Includes
--- @include "includes/addDelayedJob"
--- @include "includes/deduplicateJob"
--- @include "includes/getOrSetMaxEvents"
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

local deduplicationJobId = deduplicateJob(opts['de'], jobId, @delayedKey, @deduplicationKey,
  @eventsKey, maxEvents, @keyPrefix)
if deduplicationJobId then
  return deduplicationJobId
end

local delay, priority = storeJob(@eventsKey, jobIdKey, jobId, @jobName, @jobData,
    opts, @timestamp)

addDelayedJob(jobId, @delayedKey, @eventsKey, @timestamp, maxEvents, @markerKey, delay)

return jobId .. "" -- convert to string

--[[
  Adds a delayed job to the queue by doing the following:
    - Increases the job counter if needed.
    - Creates a new job key with the job data.

    - computes timestamp.
    - adds to delayed zset.
    - Emits a global event 'delayed' if the job is delayed.
    
    Input Parameters:
      KEYS[1] - markerKey - marker key
      KEYS[2] - metaKey - meta key
      KEYS[3] - idKey - id counter key
      KEYS[4] - delayedKey - delayed queue key
      KEYS[5] - completedKey - completed queue key
      KEYS[6] - eventsKey - events stream key
      KEYS[7] - deduplicationKey - deduplication key (optional)
      
      ARGV[1] - keyPrefix - key prefix for job keys
      ARGV[2] - customId - custom job id (optional, will generate if empty)
      ARGV[3] - jobName - job name
      ARGV[4] - timestamp - job timestamp
      ARGV[5] - jobData - JSON stringified job data
      ARGV[6] - jobOptions - msgpacked job options

      Output:
        jobId  - OK
]]

local jobId
local jobIdKey
local rcall = redis.call
local opts = cmsgpack.unpack(ARGV[6])

-- Includes
--- @include "includes/addDelayedJob"
--- @include "includes/deduplicateJob"
--- @include "includes/getOrSetMaxEvents"
--- @include "includes/storeJob"

local jobCounter = rcall("INCR", KEYS[3])

local maxEvents = getOrSetMaxEvents(KEYS[2])

if ARGV[2] == "" then
    jobId = jobCounter
    jobIdKey = ARGV[1] .. jobId
else
    jobId = ARGV[2]
    jobIdKey = ARGV[1] .. jobId
    if rcall("EXISTS", jobIdKey) == 1 then
        return -1 -- Job already exists
    end
end

local deduplicationJobId = deduplicateJob(opts['de'], jobId, KEYS[4], KEYS[7],
  KEYS[6], maxEvents, ARGV[1])
if deduplicationJobId then
  return deduplicationJobId
end

local delay, priority = storeJob(KEYS[6], jobIdKey, jobId, ARGV[3], ARGV[5],
    opts, ARGV[4])

addDelayedJob(jobId, KEYS[4], KEYS[6], ARGV[4], maxEvents, KEYS[1], delay)

return jobId .. "" -- convert to string

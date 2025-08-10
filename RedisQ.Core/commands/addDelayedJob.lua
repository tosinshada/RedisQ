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
      KEYS[3] - idCounterKey - id counter key
      KEYS[4] - delayedKey - delayed queue key
      KEYS[5] - completedKey - completed queue key
      KEYS[6] - eventsKey - events stream key
      
      keyPrefix - keyPrefix - key prefix for job keys
      customId - customId - custom job id (optional, will generate if empty)
      ARGV[3] - jobName - job name
      ARGV[4] - timestamp - job timestamp
      ARGV[5] - jobData - JSON stringified job data
      ARGV[6] - jobOptions - msgpacked job options

      Output:
        jobId  - OK
        -1     - Job already exists
]]

local markerKey = KEYS[1]
local metaKey = KEYS[2]
local idCounterKey = KEYS[3]
local delayedKey = KEYS[4]
local completedKey = KEYS[5]
local eventsKey = KEYS[6]

local jobId
local jobIdKey
local rcall = redis.call

local keyPrefix = ARGV[1]
local customId = ARGV[2]
local jobName = ARGV[3]
local timestamp = ARGV[4]
local jobData = ARGV[5]
local opts = cmsgpack.unpack(ARGV[6])

-- Includes
--- @include "addDelayMarkerIfNeeded"
--- @include "getDelayedScore"
--- @include "includes/getOrSetMaxEvents"
--- @include "includes/storeJob"

local jobCounter = rcall("INCR", idCounterKey)

local maxEvents = getOrSetMaxEvents(metaKey)

if customId == "" then
    jobId = jobCounter
    jobIdKey = keyPrefix .. jobId
else
    jobId = customId
    jobIdKey = keyPrefix .. jobId
    if rcall("EXISTS", jobIdKey) == 1 then
        return -1 -- Job already exists
    end
end

local delay = opts['delay'] or 0
local priority = opts['priority'] or 0

storeJob(eventsKey, jobIdKey, jobId, jobName, jobData, delay, priority, timestamp)

local score, delayedTimestamp = getDelayedScore(delayedKey, timestamp, tonumber(delay))

rcall("ZADD", delayedKey, score, jobId)
rcall("XADD", eventsKey, "MAXLEN", "~", maxEvents, "*", "event", "delayed",
   "jobId", jobId, "delay", delay, "timestamp", delayedTimestamp)
   
-- mark that a delayed job is available
addDelayMarkerIfNeeded(markerKey, delayedKey)

return jobId .. "" -- convert to string

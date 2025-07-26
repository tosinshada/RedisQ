--[[
  Move next job to be processed to active, lock it and fetch its data. The job
  may be delayed, in that case we need to move it to the delayed set instead.

  This operation guarantees that the worker owns the job during the lock
  expiration time. The worker is responsible of keeping the lock fresh
  so that no other worker picks this job again.

  Input Parameters:
    KEYS[1] - wait queue key
    KEYS[2] - active queue key
    KEYS[3] - prioritized queue key
    KEYS[4] - stream events key
    KEYS[5] - rate limiter key
    KEYS[6] - delayed queue key
    KEYS[7] - paused queue key
    KEYS[8] - meta key
    KEYS[9] - pc priority counter key
    KEYS[10] - marker key
    ARGV[1] - key prefix
    ARGV[2] - current timestamp
    ARGV[3] - msgpacked options (token, lockDuration, limiter, name)

    opts - token - lock token
    opts - lockDuration
    opts - limiter
    opts - name - worker name
]]
local rcall = redis.call
local opts = cmsgpack.unpack(ARGV[3])

-- Includes
--- @include "includes/getNextDelayedTimestamp"
--- @include "includes/getRateLimitTTL"
--- @include "includes/getTargetQueueList"
--- @include "includes/moveJobFromPrioritizedToActive"
--- @include "includes/prepareJobForProcessing"
--- @include "includes/promoteDelayedJobs"

local target, isPausedOrMaxed = getTargetQueueList(KEYS[8], KEYS[2], KEYS[1], KEYS[7])

-- Check if there are delayed jobs that we can move to wait.
promoteDelayedJobs(KEYS[6], KEYS[10], target, KEYS[3], KEYS[4], ARGV[1],
                   ARGV[2], KEYS[9], isPausedOrMaxed)

local maxJobs = tonumber(opts['limiter'] and opts['limiter']['max'])
local expireTime = getRateLimitTTL(maxJobs, KEYS[5])

-- Check if we are rate limited first.
if expireTime > 0 then return {0, 0, expireTime, 0} end

-- paused or maxed queue
if isPausedOrMaxed then return {0, 0, 0, 0} end

-- no job ID, try non-blocking move from wait to active
local jobId = rcall("RPOPLPUSH", KEYS[1], KEYS[2])

-- Markers in waitlist DEPRECATED in v5: Will be completely removed in v6.
if jobId and string.sub(jobId, 1, 2) == "0:" then
    rcall("LREM", KEYS[2], 1, jobId)
    jobId = rcall("RPOPLPUSH", KEYS[1], KEYS[2])
end

if jobId then
    return prepareJobForProcessing(ARGV[1], KEYS[5], KEYS[4], jobId, ARGV[2],
                                   maxJobs, KEYS[10], opts)
else
    jobId = moveJobFromPrioritizedToActive(KEYS[3], KEYS[2], KEYS[9])
    if jobId then
        return prepareJobForProcessing(ARGV[1], KEYS[5], KEYS[4], jobId, ARGV[2],
                                       maxJobs, KEYS[10], opts)
    end
end

-- Return the timestamp for the next delayed job if any.
local nextTimestamp = getNextDelayedTimestamp(KEYS[6])
if nextTimestamp ~= nil then return {0, 0, 0, nextTimestamp} end

return {0, 0, 0, 0}

--[[
  Move next job to be processed to active, lock it and fetch its data. The job
  may be delayed, in that case we need to move it to the delayed set instead.

  This operation guarantees that the worker owns the job during the lock
  expiration time. The worker is responsible of keeping the lock fresh
  so that no other worker picks this job again.

  Input Parameters:
    @waitKey - wait queue key
    @activeKey - active queue key
    @prioritizedKey - prioritized queue key
    @eventStreamKey - stream events key
    @stalledKey - stalled queue key
    @rateLimiterKey - rate limiter key
    @delayedKey - delayed queue key
    @pausedKey - paused queue key
    @metaKey - meta key
    @pcKey - pc priority counter key
    @markerKey - marker key
    @keyPrefix - key prefix
    @timestamp - current timestamp
    @opts - msgpacked options (token, lockDuration, limiter, name)

    opts - token - lock token
    opts - lockDuration
    opts - limiter
    opts - name - worker name
]]
local rcall = redis.call
local opts = cmsgpack.unpack(@opts)

-- Includes
--- @include "includes/getNextDelayedTimestamp"
--- @include "includes/getRateLimitTTL"
--- @include "includes/getTargetQueueList"
--- @include "includes/moveJobFromPrioritizedToActive"
--- @include "includes/prepareJobForProcessing"
--- @include "includes/promoteDelayedJobs"

local target, isPausedOrMaxed = getTargetQueueList(@metaKey, @activeKey, @waitKey, @pausedKey)

-- Check if there are delayed jobs that we can move to wait.
promoteDelayedJobs(@delayedKey, @markerKey, target, @prioritizedKey, @eventStreamKey, @keyPrefix,
                   @timestamp, @pcKey, isPausedOrMaxed)

local maxJobs = tonumber(opts['limiter'] and opts['limiter']['max'])
local expireTime = getRateLimitTTL(maxJobs, @rateLimiterKey)

-- Check if we are rate limited first.
if expireTime > 0 then return {0, 0, expireTime, 0} end

-- paused or maxed queue
if isPausedOrMaxed then return {0, 0, 0, 0} end

-- no job ID, try non-blocking move from wait to active
local jobId = rcall("RPOPLPUSH", @waitKey, @activeKey)

-- Markers in waitlist DEPRECATED in v5: Will be completely removed in v6.
if jobId and string.sub(jobId, 1, 2) == "0:" then
    rcall("LREM", @activeKey, 1, jobId)
    jobId = rcall("RPOPLPUSH", @waitKey, @activeKey)
end

if jobId then
    return prepareJobForProcessing(@keyPrefix, @rateLimiterKey, @eventStreamKey, jobId, @timestamp,
                                   maxJobs, @markerKey, opts)
else
    jobId = moveJobFromPrioritizedToActive(@prioritizedKey, @activeKey, @pcKey)
    if jobId then
        return prepareJobForProcessing(@keyPrefix, @rateLimiterKey, @eventStreamKey, jobId, @timestamp,
                                       maxJobs, @markerKey, opts)
    end
end

-- Return the timestamp for the next delayed job if any.
local nextTimestamp = getNextDelayedTimestamp(@delayedKey)
if nextTimestamp ~= nil then return {0, 0, 0, nextTimestamp} end

return {0, 0, 0, 0}

--[[
  Move job from active to a finished status (completed o failed)
  A job can only be moved to completed if it was active.
  The job must be locked before it can be moved to a finished status,
  and the lock must be released in this script.

    Input Parameters:
      KEYS[1] - wait key
      KEYS[2] - active key
      KEYS[3] - prioritized key
      KEYS[4] - event stream key
      KEYS[5] - stalled key
      KEYS[6] - rate limiter key
      KEYS[7] - delayed key
      KEYS[8] - paused key
      KEYS[9] - meta key
      KEYS[10] - pc priority counter
      KEYS[11] - finished key
      KEYS[12] - jobId key
      KEYS[13] - metrics key
      KEYS[14] - marker key
      ARGV[1] - job ID
      ARGV[2] - timestamp
      ARGV[3] - msg property returnvalue / failedReason
      ARGV[4] - return value / failed reason
      ARGV[5] - target (completed/failed)
      ARGV[6] - fetch next?
      ARGV[7] - keys prefix
      ARGV[8] - msgpacked options
      ARGV[9] - job fields to update

      opts - token - lock token
      opts - keepJobs
      opts - lockDuration - lock duration in milliseconds
      opts - attempts max attempts
      opts - maxMetricsSize
      opts - name - worker name

    Output:
      0 OK
      -1 Missing key.
      -2 Missing lock.
      -3 Job not in active set
      -6 Lock is not owned by this client

    Events:
      'completed/failed'
]]
local rcall = redis.call

--- Includes
--- @include "includes/collectMetrics"
--- @include "includes/getNextDelayedTimestamp"
--- @include "includes/getRateLimitTTL"
--- @include "includes/getTargetQueueList"
--- @include "includes/moveJobFromPrioritizedToActive"
--- @include "includes/prepareJobForProcessing"
--- @include "includes/promoteDelayedJobs"
--- @include "includes/removeDeduplicationKeyIfNeededOnFinalization"
--- @include "includes/removeJobKeys"
--- @include "includes/removeJobsByMaxAge"
--- @include "includes/removeJobsByMaxCount"
--- @include "includes/removeLock"
--- @include "includes/trimEvents"
--- @include "includes/updateJobFields"

if rcall("EXISTS", KEYS[12]) == 1 then -- Make sure job exists
    local opts = cmsgpack.unpack(ARGV[8])

    local token = opts['token']

    local errorCode = removeLock(KEYS[12], KEYS[5], token, ARGV[1])
    if errorCode < 0 then
        return errorCode
    end

    updateJobFields(KEYS[12], ARGV[9]);

    local attempts = opts['attempts']
    local maxMetricsSize = opts['maxMetricsSize']
    local maxCount = opts['keepJobs']['count']
    local maxAge = opts['keepJobs']['age']

    -- Remove from active list (if not active we shall return error)
    local numRemovedElements = rcall("LREM", KEYS[2], -1, ARGV[1])

    if (numRemovedElements < 1) then
        return -3
    end

    -- Trim events before emiting them to avoid trimming events emitted in this script
    trimEvents(KEYS[9], KEYS[4])

    local jobAttributes = rcall("HMGET", KEYS[12], "deid")
    removeDeduplicationKeyIfNeededOnFinalization(ARGV[7], jobAttributes[1], ARGV[1])

    local attemptsMade = rcall("HINCRBY", KEYS[12], "atm", 1)

    -- Remove job?
    if maxCount ~= 0 then
        local targetSet = KEYS[11]
        -- Add to complete/failed set
        rcall("ZADD", targetSet, ARGV[2], ARGV[1])
        rcall("HSET", KEYS[12], ARGV[3], ARGV[4], "finishedOn", ARGV[2])
        -- "returnvalue" / "failedReason" and "finishedOn"

        if ARGV[5] == "failed" then
            rcall("HDEL", KEYS[12], "defa")
        end

        -- Remove old jobs?
        if maxAge ~= nil then
            removeJobsByMaxAge(ARGV[2], maxAge, targetSet, ARGV[7])
        end

        if maxCount ~= nil and maxCount > 0 then
            removeJobsByMaxCount(maxCount, targetSet, ARGV[7])
        end
    else
        removeJobKeys(KEYS[12])
    end

    rcall("XADD", KEYS[4], "*", "event", ARGV[5], "jobId", ARGV[1], ARGV[3], ARGV[4], "prev", "active")

    if ARGV[5] == "failed" then
        if tonumber(attemptsMade) >= tonumber(attempts) then
            rcall("XADD", KEYS[4], "*", "event", "retries-exhausted", "jobId", ARGV[1], "attemptsMade",
                attemptsMade)
        end
    end

    -- Collect metrics
    if maxMetricsSize ~= "" then
        collectMetrics(KEYS[13], KEYS[13] .. ':data', maxMetricsSize, ARGV[2])
    end

    -- Try to get next job to avoid an extra roundtrip if the queue is not closing,
    -- and not rate limited.
    if (ARGV[6] == "1") then

        local target, isPausedOrMaxed = getTargetQueueList(KEYS[9], KEYS[2], KEYS[1], KEYS[8])

        -- Check if there are delayed jobs that can be promoted
        promoteDelayedJobs(KEYS[7], KEYS[14], target, KEYS[3], KEYS[4], ARGV[7], ARGV[2], KEYS[10],
            isPausedOrMaxed)

        local maxJobs = tonumber(opts['limiter'] and opts['limiter']['max'])
        -- Check if we are rate limited first.
        local expireTime = getRateLimitTTL(maxJobs, KEYS[6])

        if expireTime > 0 then
            return {0, 0, expireTime, 0}
        end

        -- paused or maxed queue
        if isPausedOrMaxed then
            return {0, 0, 0, 0}
        end

        local jobId = rcall("RPOPLPUSH", KEYS[1], KEYS[2])

        if jobId then
            -- Markers in waitlist DEPRECATED in v5: Remove in v6.
            if string.sub(jobId, 1, 2) == "0:" then
                rcall("LREM", KEYS[2], 1, jobId)

                -- If jobId is special ID 0:delay (delay greater than 0), then there is no job to process
                -- but if ID is 0:0, then there is at least 1 prioritized job to process
                if jobId == "0:0" then
                    jobId = moveJobFromPrioritizedToActive(KEYS[3], KEYS[2], KEYS[10])
                    return prepareJobForProcessing(ARGV[7], KEYS[6], KEYS[4], jobId, ARGV[2], maxJobs,
                        KEYS[14], opts)
                end
            else
                return prepareJobForProcessing(ARGV[7], KEYS[6], KEYS[4], jobId, ARGV[2], maxJobs, KEYS[14],
                    opts)
            end
        else
            jobId = moveJobFromPrioritizedToActive(KEYS[3], KEYS[2], KEYS[10])
            if jobId then
                return prepareJobForProcessing(ARGV[7], KEYS[6], KEYS[4], jobId, ARGV[2], maxJobs, KEYS[14],
                    opts)
            end
        end

        -- Return the timestamp for the next delayed job if any.
        local nextTimestamp = getNextDelayedTimestamp(KEYS[7])
        if nextTimestamp ~= nil then
            -- The result is guaranteed to be positive, since the
            -- ZRANGEBYSCORE command would have return a job otherwise.
            return {0, 0, 0, nextTimestamp}
        end
    end

    local waitLen = rcall("LLEN", KEYS[1])
    if waitLen == 0 then
        local activeLen = rcall("LLEN", KEYS[2])

        if activeLen == 0 then
            local prioritizedLen = rcall("ZCARD", KEYS[3])

            if prioritizedLen == 0 then
                rcall("XADD", KEYS[4], "*", "event", "drained")
            end
        end
    end

    return 0
else
    return -1
end

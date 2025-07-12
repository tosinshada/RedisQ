--[[
  Move job from active to a finished status (completed o failed)
  A job can only be moved to completed if it was active.
  The job must be locked before it can be moved to a finished status,
  and the lock must be released in this script.

    Input Parameters:
      @waitKey - wait key
      @activeKey - active key
      @prioritizedKey - prioritized key
      @eventStreamKey - event stream key
      @stalledKey - stalled key
      @rateLimiterKey - rate limiter key
      @delayedKey - delayed key
      @pausedKey - paused key
      @metaKey - meta key
      @pcKey - pc priority counter
      @finishedKey - completed/failed key
      @jobIdKey - jobId key
      @metricsKey - metrics key
      @markerKey - marker key
      @jobId - job ID
      @timestamp - timestamp
      @msgProperty - msg property returnvalue / failedReason
      @returnValue - return value / failed reason
      @target - target (completed/failed)
      @fetchNext - fetch next?
      @keysPrefix - keys prefix
      @opts - msgpacked options
      @jobFields - job fields to update

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

if rcall("EXISTS", @jobIdKey) == 1 then -- Make sure job exists
    local opts = cmsgpack.unpack(@opts)

    local token = opts['token']

    local errorCode = removeLock(@jobIdKey, @stalledKey, token, @jobId)
    if errorCode < 0 then
        return errorCode
    end

    updateJobFields(@jobIdKey, @jobFields);

    local attempts = opts['attempts']
    local maxMetricsSize = opts['maxMetricsSize']
    local maxCount = opts['keepJobs']['count']
    local maxAge = opts['keepJobs']['age']

    -- Remove from active list (if not active we shall return error)
    local numRemovedElements = rcall("LREM", @activeKey, -1, @jobId)

    if (numRemovedElements < 1) then
        return -3
    end

    -- Trim events before emiting them to avoid trimming events emitted in this script
    trimEvents(@metaKey, @eventStreamKey)

    local jobAttributes = rcall("HMGET", @jobIdKey, "deid")
    removeDeduplicationKeyIfNeededOnFinalization(@keysPrefix, jobAttributes[1], @jobId)

    local attemptsMade = rcall("HINCRBY", @jobIdKey, "atm", 1)

    -- Remove job?
    if maxCount ~= 0 then
        local targetSet = @finishedKey
        -- Add to complete/failed set
        rcall("ZADD", targetSet, @timestamp, @jobId)
        rcall("HSET", @jobIdKey, @msgProperty, @returnValue, "finishedOn", @timestamp)
        -- "returnvalue" / "failedReason" and "finishedOn"

        if @target == "failed" then
            rcall("HDEL", @jobIdKey, "defa")
        end

        -- Remove old jobs?
        if maxAge ~= nil then
            removeJobsByMaxAge(@timestamp, maxAge, targetSet, @keysPrefix)
        end

        if maxCount ~= nil and maxCount > 0 then
            removeJobsByMaxCount(maxCount, targetSet, @keysPrefix)
        end
    else
        removeJobKeys(@jobIdKey)
    end

    rcall("XADD", @eventStreamKey, "*", "event", @target, "jobId", @jobId, @msgProperty, @returnValue, "prev", "active")

    if @target == "failed" then
        if tonumber(attemptsMade) >= tonumber(attempts) then
            rcall("XADD", @eventStreamKey, "*", "event", "retries-exhausted", "jobId", @jobId, "attemptsMade",
                attemptsMade)
        end
    end

    -- Collect metrics
    if maxMetricsSize ~= "" then
        collectMetrics(@metricsKey, @metricsKey .. ':data', maxMetricsSize, @timestamp)
    end

    -- Try to get next job to avoid an extra roundtrip if the queue is not closing,
    -- and not rate limited.
    if (@fetchNext == "1") then

        local target, isPausedOrMaxed = getTargetQueueList(@metaKey, @activeKey, @waitKey, @pausedKey)

        -- Check if there are delayed jobs that can be promoted
        promoteDelayedJobs(@delayedKey, @markerKey, target, @prioritizedKey, @eventStreamKey, @keysPrefix, @timestamp, @pcKey,
            isPausedOrMaxed)

        local maxJobs = tonumber(opts['limiter'] and opts['limiter']['max'])
        -- Check if we are rate limited first.
        local expireTime = getRateLimitTTL(maxJobs, @rateLimiterKey)

        if expireTime > 0 then
            return {0, 0, expireTime, 0}
        end

        -- paused or maxed queue
        if isPausedOrMaxed then
            return {0, 0, 0, 0}
        end

        local jobId = rcall("RPOPLPUSH", @waitKey, @activeKey)

        if jobId then
            -- Markers in waitlist DEPRECATED in v5: Remove in v6.
            if string.sub(jobId, 1, 2) == "0:" then
                rcall("LREM", @activeKey, 1, jobId)

                -- If jobId is special ID 0:delay (delay greater than 0), then there is no job to process
                -- but if ID is 0:0, then there is at least 1 prioritized job to process
                if jobId == "0:0" then
                    jobId = moveJobFromPrioritizedToActive(@prioritizedKey, @activeKey, @pcKey)
                    return prepareJobForProcessing(@keysPrefix, @rateLimiterKey, @eventStreamKey, jobId, @timestamp, maxJobs,
                        @markerKey, opts)
                end
            else
                return prepareJobForProcessing(@keysPrefix, @rateLimiterKey, @eventStreamKey, jobId, @timestamp, maxJobs, @markerKey,
                    opts)
            end
        else
            jobId = moveJobFromPrioritizedToActive(@prioritizedKey, @activeKey, @pcKey)
            if jobId then
                return prepareJobForProcessing(@keysPrefix, @rateLimiterKey, @eventStreamKey, jobId, @timestamp, maxJobs, @markerKey,
                    opts)
            end
        end

        -- Return the timestamp for the next delayed job if any.
        local nextTimestamp = getNextDelayedTimestamp(@delayedKey)
        if nextTimestamp ~= nil then
            -- The result is guaranteed to be positive, since the
            -- ZRANGEBYSCORE command would have return a job otherwise.
            return {0, 0, 0, nextTimestamp}
        end
    end

    local waitLen = rcall("LLEN", @waitKey)
    if waitLen == 0 then
        local activeLen = rcall("LLEN", @activeKey)

        if activeLen == 0 then
            local prioritizedLen = rcall("ZCARD", @prioritizedKey)

            if prioritizedLen == 0 then
                rcall("XADD", @eventStreamKey, "*", "event", "drained")
            end
        end
    end

    return 0
else
    return -1
end

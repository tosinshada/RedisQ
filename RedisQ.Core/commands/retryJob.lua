--[[
  Retries a failed job by moving it back to the wait queue.

    Input Parameters:
      @activeKey - active queue key
      @waitKey - wait queue key
      @pausedKey - paused queue key
      @jobKey - job key
      @metaKey - meta key
      @eventsKey - events stream key
      @delayedKey - delayed queue key
      @prioritizedKey - prioritized queue key
      @pcKey - priority counter key
      @markerKey - marker key
      @stalledKey - stalled queue key
      @keyPrefix - key prefix
      @timestamp - current timestamp
      @pushCmd - push command (LPUSH/RPUSH)
      @jobId - job ID
      @token - job token
      @fieldsToUpdate - optional job fields to update

    Events:
      'waiting'

    Output:
     0  - OK
     -1 - Missing key
     -2 - Missing lock
     -3 - Job not in active set
]]
local rcall = redis.call

-- Includes
--- @include "includes/addJobInTargetList"
--- @include "includes/addJobWithPriority"
--- @include "includes/getOrSetMaxEvents"
--- @include "includes/getTargetQueueList"
--- @include "includes/isQueuePausedOrMaxed"
--- @include "includes/promoteDelayedJobs"
--- @include "includes/removeLock"
--- @include "includes/updateJobFields"

local target, isPausedOrMaxed = getTargetQueueList(@metaKey, @activeKey, @waitKey, @pausedKey)

-- Check if there are delayed jobs that we can move to wait.
-- test example: when there are delayed jobs between retries
promoteDelayedJobs(@delayedKey, @markerKey, target, @prioritizedKey, @eventsKey, @keyPrefix, @timestamp, @pcKey, isPausedOrMaxed)

if rcall("EXISTS", @jobKey) == 1 then
  local errorCode = removeLock(@jobKey, @stalledKey, @token, @jobId) 
  if errorCode < 0 then
    return errorCode
  end

  updateJobFields(@jobKey, @fieldsToUpdate)

  local numRemovedElements = rcall("LREM", @activeKey, -1, @jobId)
  if (numRemovedElements < 1) then return -3 end

  local priority = tonumber(rcall("HGET", @jobKey, "priority")) or 0

  --need to re-evaluate after removing job from active
  isPausedOrMaxed = isQueuePausedOrMaxed(@metaKey, @activeKey)

  -- Standard or priority add
  if priority == 0 then
    addJobInTargetList(target, @markerKey, @pushCmd, isPausedOrMaxed, @jobId)
  else
    addJobWithPriority(@markerKey, @prioritizedKey, priority, @jobId, @pcKey, isPausedOrMaxed)
  end

  rcall("HINCRBY", @jobKey, "atm", 1)

  local maxEvents = getOrSetMaxEvents(@metaKey)

  -- Emit waiting event
  rcall("XADD", @eventsKey, "MAXLEN", "~", maxEvents, "*", "event", "waiting",
    "jobId", @jobId, "prev", "failed")

  return 0
else
  return -1
end

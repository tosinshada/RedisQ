--[[
  Retries a failed job by moving it back to the wait queue.

    Input Parameters:
      KEYS[1] - active queue key
      KEYS[2] - wait queue key
      KEYS[3] - paused queue key
      KEYS[4] - job key
      KEYS[5] - meta key
      KEYS[6] - events stream key
      KEYS[7] - delayed queue key
      KEYS[8] - prioritized queue key
      KEYS[9] - priority counter key
      KEYS[10] - marker key
      KEYS[11] - stalled queue key
      ARGV[1] - key prefix
      ARGV[2] - current timestamp
      ARGV[3] - push command (LPUSH/RPUSH)
      ARGV[4] - job ID
      ARGV[5] - job token
      ARGV[6] - optional job fields to update

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
--- @include "includes/addJobInTargetQueue"
--- @include "includes/addJobWithPriority"
--- @include "includes/getOrSetMaxEvents"
--- @include "includes/getTargetQueue"
--- @include "includes/isQueuePausedOrMaxed"
--- @include "includes/promoteDelayedJobs"
--- @include "includes/removeLock"
--- @include "includes/updateJobFields"

local target, isPausedOrMaxed = getTargetQueue(KEYS[5], KEYS[1], KEYS[2], KEYS[3])

-- Check if there are delayed jobs that we can move to wait.
-- test example: when there are delayed jobs between retries
promoteDelayedJobs(KEYS[7], KEYS[10], target, KEYS[8], KEYS[6], ARGV[1], ARGV[2], KEYS[9], isPausedOrMaxed)

if rcall("EXISTS", KEYS[4]) == 1 then
  local errorCode = removeLock(KEYS[4], KEYS[11], ARGV[5], ARGV[4]) 
  if errorCode < 0 then
    return errorCode
  end

  updateJobFields(KEYS[4], ARGV[6])

  local numRemovedElements = rcall("LREM", KEYS[1], -1, ARGV[4])
  if (numRemovedElements < 1) then return -3 end

  local priority = tonumber(rcall("HGET", KEYS[4], "priority")) or 0

  --need to re-evaluate after removing job from active
  isPausedOrMaxed = isQueuePausedOrMaxed(KEYS[5], KEYS[1])

  -- Standard or priority add
  if priority == 0 then
    addJobInTargetQueue(target, KEYS[10], ARGV[3], isPausedOrMaxed, ARGV[4])
  else
    addJobWithPriority(KEYS[10], KEYS[8], priority, ARGV[4], KEYS[9], isPausedOrMaxed)
  end

  rcall("HINCRBY", KEYS[4], "atm", 1)

  local maxEvents = getOrSetMaxEvents(KEYS[5])

  -- Emit waiting event
  rcall("XADD", KEYS[6], "MAXLEN", "~", maxEvents, "*", "event", "waiting",
    "jobId", ARGV[4], "prev", "failed")

  return 0
else
  return -1
end

-- Derived from BullMQ - Copyright (c) 2018 BullForce Labs AB. - MIT License
-- Original source: https://github.com/taskforcesh/bullmq

--[[
  Function to get the target queue for a job based on the queue's state.
  It checks if the queue is paused or if it has a concurrency limit.
  Returns the target queue key and a boolean indicating if the queue is paused.
  If the queue is paused, it returns the paused key.
  If the queue has a concurrency limit, it checks the active count and returns
  the wait key and whether the queue is paused or not.
  If the queue is neither paused nor has a concurrency limit, it defaults to the wait key
  and returns false for the paused state.
]]

local function getTargetQueue(queueMetaKey, activeKey, waitKey, pausedKey)
  local queueAttributes = rcall("HMGET", queueMetaKey, "paused", "concurrency")

  if queueAttributes[1] then
    return pausedKey, true
  else
    if queueAttributes[2] then
      local activeCount = rcall("LLEN", activeKey)
      if activeCount >= tonumber(queueAttributes[2]) then
        return waitKey, true
      else
        return waitKey, false
      end
    end
  end
  return waitKey, false
end

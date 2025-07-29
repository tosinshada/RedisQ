--[[
  Function to remove job.
]]

-- Includes
--- @include "removeJobKeys"

local function removeJob(jobId, baseKey)
  local jobKey = baseKey .. jobId
  removeJobKeys(jobKey)
end

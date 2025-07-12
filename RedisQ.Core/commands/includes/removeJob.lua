--[[
  Function to remove job.
]]

-- Includes
--- @include "removeDeduplicationKeyIfNeededOnRemoval"
--- @include "removeJobKeys"

local function removeJob(jobId, baseKey, shouldRemoveDeduplicationKey)
  local jobKey = baseKey .. jobId
  if shouldRemoveDeduplicationKey then
    removeDeduplicationKeyIfNeededOnRemoval(baseKey, jobKey, jobId)
  end
  removeJobKeys(jobKey)
end

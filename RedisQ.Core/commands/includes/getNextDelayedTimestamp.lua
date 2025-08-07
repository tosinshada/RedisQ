--[[
  Function to return the next delayed job timestamp.
]]
local function getNextDelayedTimestamp(delayedKey)
  local result = rcall("ZRANGE", delayedKey, 0, 0, "WITHSCORES")
  if #result then
    -- Extract timestamp from string score "timestamp:jobid"
    local timestampStr = string.match(result[2], "^(%d+):")
    if timestampStr then
      return tonumber(timestampStr)
    end
  end
end
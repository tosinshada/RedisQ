--[[
    getDelayedScore.lua
    This function retrieves the delayed score for a given key and timestamp.
    It calculates the score based on the provided delay and returns it along with the adjusted timestamp.
    
    @param delayedKey: The key in Redis where the delayed scores are stored.
    @param timestamp: The timestamp to base the score on.
    @param delay: The delay in seconds to apply to the timestamp.
    
    @return: A tuple containing the calculated score and the adjusted timestamp.
]]

local function getDelayedScore(delayedKey, timestamp, delay)
  local delayedTimestamp = (delay > 0 and (tonumber(timestamp) + delay)) or tonumber(timestamp)
  local minScore = delayedTimestamp * 0x1000
  local maxScore = (delayedTimestamp + 1 ) * 0x1000 - 1

  local result = rcall("ZREVRANGEBYSCORE", delayedKey, maxScore,
    minScore, "WITHSCORES","LIMIT", 0, 1)
  if #result then
    local currentMaxScore = tonumber(result[2])
    if currentMaxScore ~= nil then
      if currentMaxScore >= maxScore then
        return maxScore, delayedTimestamp
      else
        return currentMaxScore + 1, delayedTimestamp
      end
    end
  end
  return minScore, delayedTimestamp
end

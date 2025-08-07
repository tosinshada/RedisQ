--[[
  This function generates unique lexicographic scores for delayed jobs to ensure FIFO 
      execution order while avoiding floating-point precision issues. It formats scores 
      as "timestamp:jobid" strings with zero-padded components, uses ZREVRANGEBYLEX to 
      find the highest existing job ID for a given timestamp, and increments it to create 
      the next unique score. The zero-padding ensures lexicographic ordering matches 
      chronological ordering.
]]
local function getDelayedScore(delayedKey, timestamp, delay)
  local delayedTimestamp = (delay > 0 and (tonumber(timestamp) + delay)) or tonumber(timestamp)
  
  -- Format as zero-padded string: "timestamp:jobid"
  local baseScore = string.format("%020d", delayedTimestamp)
  
  -- This searches for the lexicographically highest string that:
  -- Starts with the base timestamp string
  -- Has a colon separator
  -- Contains any job ID (represented by the range from : to :\xff)
  -- The parentheses indicate exclusive bounds, and \xff represents the highest possible byte value.
  local result = rcall("ZREVRANGEBYLEX", delayedKey, 
    "(" .. baseScore .. ":\xff", "(" .. baseScore .. ":", "LIMIT", 0, 1)
    
  if #result > 0 then
    local lastJobId = string.match(result[1], baseScore .. ":(%d+)")
    local nextJobId = (tonumber(lastJobId) or 0) + 1
    return baseScore .. ":" .. string.format("%012d", nextJobId), delayedTimestamp
  end
  
  return baseScore .. ":000000000001", delayedTimestamp
end

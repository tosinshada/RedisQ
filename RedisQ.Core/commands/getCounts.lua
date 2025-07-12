--[[
  Get counts per provided states

    Input Parameters:
      @prefix - key prefix for the queue
      @types - array of queue types to count (wait, paused, active, delayed, etc.)
]]
local rcall = redis.call;
local results = {}

for i = 1, #@types do
  local stateKey = @prefix .. @types[i]
  if @types[i] == "wait" or @types[i] == "paused" or @types[i] == "active" then
    results[#results+1] = rcall("LLEN", stateKey)
  else
    results[#results+1] = rcall("ZCARD", stateKey)
  end
end

return results

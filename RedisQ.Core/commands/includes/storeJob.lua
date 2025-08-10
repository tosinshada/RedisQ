-- Derived from BullMQ - Copyright (c) 2018 BullForce Labs AB. - MIT License
-- Original source: https://github.com/taskforcesh/bullmq

--[[
  Function to store a job
]]
local function storeJob(eventsKey, jobIdKey, jobId, name, data, 
                        delay, priority, timestamp)
    local jsonOpts = cjson.encode(opts)

    rcall("HMSET", jobIdKey, "name", name, "data", data, "opts", jsonOpts,
          "timestamp", timestamp, "delay", delay, "priority", priority)

    rcall("XADD", eventsKey, "*", "event", "added", "jobId", jobId, "name", name)
end

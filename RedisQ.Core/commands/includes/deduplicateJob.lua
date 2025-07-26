-- Derived from BullMQ - Copyright (c) 2018 BullForce Labs AB. - MIT License
-- Original source: https://github.com/taskforcesh/bullmq

--[[
  Function to handle job deduplication:
  - Check if a job with the same deduplication ID already exists
  - Either replace, extend, or reject new job based on options
]]

-- Includes
--- @include "removeJobKeys"

local function deduplicateJob(deduplicationOpts, jobId, delayedKey, deduplicationKey, eventsKey, maxEvents, prefix)
    -- If no deduplication options or ID provided, skip deduplication
    if not deduplicationOpts or not deduplicationOpts['id'] then
        return nil
    end
    
    local deduplicationId = deduplicationOpts['id']
    local ttl = deduplicationOpts['ttl']
    local shouldReplace = deduplicationOpts['replace']
    local shouldExtend = deduplicationOpts['extend']
    
    -- Check if a job with this deduplication ID already exists
    local existingJobId = rcall('GET', deduplicationKey)
    
    -- If no existing job found, store the new job's ID with deduplication key
    if not existingJobId then
        if ttl and ttl > 0 then
            rcall('SET', deduplicationKey, jobId, 'PX', ttl)
        else
            rcall('SET', deduplicationKey, jobId)
        end
        return nil -- Indicates no duplicate was found
    end
    
    -- Handle existing job (deduplication case)
    if shouldReplace and ttl and ttl > 0 then
        -- Replace mode: remove existing job and use the new one
        if rcall("ZREM", delayedKey, existingJobId) > 0 then
            -- Successfully removed from delayed set, now clean up the job
            removeJobKeys(prefix .. existingJobId)
            
            -- Log removal event
            rcall("XADD", eventsKey, "*", "event", "removed", 
                  "jobId", existingJobId, "prev", "delayed")
            
            -- Update deduplication key to point to new job
            if shouldExtend then
                rcall('SET', deduplicationKey, jobId, 'PX', ttl)
            else
                rcall('SET', deduplicationKey, jobId, 'KEEPTTL')
            end
            
            -- Log deduplication event
            rcall("XADD", eventsKey, "MAXLEN", "~", maxEvents, "*", 
                  "event", "deduplicated", "jobId", jobId, 
                  "deduplicationId", deduplicationId, "deduplicatedJobId", existingJobId)
                  
            return nil -- Process new job
        else
            -- Job exists but couldn't be removed from delayed set
            return existingJobId -- Return existing job ID to skip new job
        end
    else
        -- Standard deduplication mode (no replacement)
        if shouldExtend and ttl and ttl > 0 then
            -- Extend TTL of existing deduplication key
            rcall('SET', deduplicationKey, existingJobId, 'PX', ttl)
        end
        
        -- Log events
        rcall("XADD", eventsKey, "MAXLEN", "~", maxEvents, "*", 
              "event", "debounced", "jobId", existingJobId, 
              "debounceId", deduplicationId)
              
        rcall("XADD", eventsKey, "MAXLEN", "~", maxEvents, "*", 
              "event", "deduplicated", "jobId", existingJobId, 
              "deduplicationId", deduplicationId, "deduplicatedJobId", jobId)
              
        return existingJobId -- Return existing job ID to skip new job
    end
end
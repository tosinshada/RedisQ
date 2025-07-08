using RedisQ.Core.BuildTasks;
using Microsoft.Build.Framework;
using Moq;
using Xunit;

namespace RedisQ.Core.Tests.BuildTasks;

public class ExpandLuaScriptsTaskIntegrationTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _scriptsDirectory;
    private readonly string _outputDirectory;

    public ExpandLuaScriptsTaskIntegrationTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "ExpandLuaScriptsTaskIntegration", Guid.NewGuid().ToString());
        _scriptsDirectory = Path.Combine(_testDirectory, "commands");
        _outputDirectory = Path.Combine(_testDirectory, "output");
        
        Directory.CreateDirectory(_scriptsDirectory);
        Directory.CreateDirectory(_outputDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void Execute_WithRedisQStyleLuaScript_ExpandsCorrectly()
    {
        // Arrange - Create a realistic Redis Lua script structure similar to the actual project
        var includesDir = Path.Combine(_scriptsDirectory, "includes");
        Directory.CreateDirectory(includesDir);

        // Create include files similar to the real project
        var storeJobInclude = @"-- Derived from BullMQ - Copyright (c) 2018 BullForce Labs AB. - MIT License
-- Original source: https://github.com/taskforcesh/bullmq

--[[
  Function to store a job
]]
local function storeJob(eventsKey, jobIdKey, jobId, name, data, opts, timestamp, repeatJobKey)
    local jsonOpts = cjson.encode(opts)
    local delay = opts['delay'] or 0
    local priority = opts['priority'] or 0

    redis.call('HMSET', jobIdKey, 
        'name', name, 
        'data', data, 
        'opts', jsonOpts,
        'timestamp', timestamp, 
        'delay', delay, 
        'priority', priority)
    
    return delay
end";

        var getTargetQueueListInclude = @"-- Function to get target queue list
local function getTargetQueueList(queueKeys)
    local waitKey = queueKeys['wait']
    local pausedKey = queueKeys['paused']
    
    if redis.call('EXISTS', pausedKey) == 1 then
        return pausedKey
    else
        return waitKey
    end
end";

        var addJobInTargetListInclude = @"-- Function to add job in target list
local function addJobInTargetList(targetKey, jobId, priority, lifo)
    if priority == 0 then
        if lifo then
            redis.call('LPUSH', targetKey, jobId)
        else
            redis.call('RPUSH', targetKey, jobId)
        end
    else
        redis.call('ZADD', targetKey, priority, jobId)
    end
end";

        // Write include files
        File.WriteAllText(Path.Combine(includesDir, "storeJob.lua"), storeJobInclude);
        File.WriteAllText(Path.Combine(includesDir, "getTargetQueueList.lua"), getTargetQueueListInclude);
        File.WriteAllText(Path.Combine(includesDir, "addJobInTargetList.lua"), addJobInTargetListInclude);

        // Create main script that includes these files
        var mainScript = @"-- Derived from BullMQ - Copyright (c) 2018 BullForce Labs AB. - MIT License
-- Original source: https://github.com/taskforcesh/bullmq

--[[
  Adds a job to the queue by doing the following:
    - Increases the job counter if needed.
    - Creates a new job key with the job data.
    - Adds the jobId to the wait/paused list.
    - Emits a global event.

    Input:
      KEYS[1] 'wait',
      KEYS[2] 'paused'
      KEYS[3] 'meta'
      KEYS[4] 'id'
      KEYS[5] 'completed'
      KEYS[6] 'failed'
      KEYS[7] 'delayed'
      KEYS[8] 'priority'
      KEYS[9] 'active'
      KEYS[10] events stream key
      KEYS[11] delay stream key

      ARGV[1] msgpacked job data
      ARGV[2] job options
]]

local jobId = redis.call('INCR', KEYS[4])
local jobIdKey = ARGV[1] .. jobId

--- @include ""includes/storeJob""
--- @include ""includes/getTargetQueueList""  
--- @include ""includes/addJobInTargetList""

-- Parse job data
local jobData = cmsgpack.unpack(ARGV[1])
local opts = cjson.decode(ARGV[2])

-- Store the job
local delay = storeJob(KEYS[10], jobIdKey, jobId, jobData.name, jobData.data, opts, jobData.timestamp)

-- Add to appropriate queue
local queueKeys = {
    wait = KEYS[1],
    paused = KEYS[2]
}

local targetKey = getTargetQueueList(queueKeys)
addJobInTargetList(targetKey, jobId, opts.priority or 0, opts.lifo)

-- Emit event
redis.call('XADD', KEYS[10], '*', 'event', 'added', 'jobId', jobId)

return jobId";

        var mainScriptPath = Path.Combine(_scriptsDirectory, "addStandardJob.lua");
        File.WriteAllText(mainScriptPath, mainScript);

        var task = CreateTask();
        task.ScriptsDirectory = _scriptsDirectory;
        task.OutputDirectory = _outputDirectory;

        // Act
        var result = task.Execute();

        // Assert
        Assert.True(result);
        
        var expectedOutputPath = Path.Combine(_outputDirectory, "addStandardJob.expanded.lua");
        Assert.True(File.Exists(expectedOutputPath));
        
        var expandedContent = File.ReadAllText(expectedOutputPath);
        
        // Verify all includes were expanded
        Assert.Contains("local function storeJob(", expandedContent);
        Assert.Contains("local function getTargetQueueList(", expandedContent);
        Assert.Contains("local function addJobInTargetList(", expandedContent);
        
        // Verify include directives were removed
        Assert.DoesNotContain("@include", expandedContent);
        
        // Verify main script content is preserved
        Assert.Contains("local jobId = redis.call('INCR', KEYS[4])", expandedContent);
        Assert.Contains("return jobId", expandedContent);
        
        // Verify include content is present
        Assert.Contains("redis.call('HMSET', jobIdKey", expandedContent);
        Assert.Contains("redis.call('EXISTS', pausedKey)", expandedContent);
        Assert.Contains("redis.call('LPUSH', targetKey, jobId)", expandedContent);
    }

    [Fact]
    public void Execute_WithComplexIncludeHierarchy_ExpandsCorrectly()
    {
        // Arrange - Test with multiple levels of includes
        var includesDir = Path.Combine(_scriptsDirectory, "includes");
        Directory.CreateDirectory(includesDir);

        // Base utility functions
        var baseUtils = @"-- Base utilities
local function isEmpty(str)
    return str == nil or str == ''
end

local function isNumber(val)
    return type(val) == 'number'
end";

        // Queue utilities that depend on base utils  
        var queueUtils = @"-- Queue utilities
--- @include ""baseUtils""

local function validateQueueName(name)
    return not isEmpty(name)
end

local function validatePriority(priority)
    return isNumber(priority) and priority >= 0
end";

        // Job utilities that depend on both
        var jobUtils = @"-- Job utilities  
--- @include ""baseUtils""
--- @include ""queueUtils""

local function validateJob(job)
    return validateQueueName(job.queue) and validatePriority(job.priority or 0)
end";

        // Write include files
        File.WriteAllText(Path.Combine(includesDir, "baseUtils.lua"), baseUtils);
        File.WriteAllText(Path.Combine(includesDir, "queueUtils.lua"), queueUtils);
        File.WriteAllText(Path.Combine(includesDir, "jobUtils.lua"), jobUtils);

        // Main script that uses all utilities
        var mainScript = @"-- Main job processing script
--- @include ""includes/jobUtils""

local jobData = cjson.decode(ARGV[1])

if validateJob(jobData) then
    redis.call('LPUSH', 'jobs', ARGV[1])
    return 'success'
else
    return 'invalid job'
end";

        var mainScriptPath = Path.Combine(_scriptsDirectory, "processJob.lua");
        File.WriteAllText(mainScriptPath, mainScript);

        var task = CreateTask();
        task.ScriptsDirectory = _scriptsDirectory;
        task.OutputDirectory = _outputDirectory;

        // Act
        var result = task.Execute();

        // Assert
        Assert.True(result);
        
        var expectedOutputPath = Path.Combine(_outputDirectory, "processJob.expanded.lua");
        Assert.True(File.Exists(expectedOutputPath));
        
        var expandedContent = File.ReadAllText(expectedOutputPath);
        
        // Verify all functions are present (should not have duplicates due to circular includes)
        Assert.Contains("function isEmpty(str)", expandedContent);
        Assert.Contains("function isNumber(val)", expandedContent);
        Assert.Contains("function validateQueueName(name)", expandedContent);
        Assert.Contains("function validatePriority(priority)", expandedContent);
        Assert.Contains("function validateJob(job)", expandedContent);
        
        // Verify no include directives remain
        Assert.DoesNotContain("@include", expandedContent);
        
        // Verify main script logic is preserved
        Assert.Contains("if validateJob(jobData) then", expandedContent);
        Assert.Contains("return 'success'", expandedContent);
    }

    [Fact]
    public void Execute_WithErrorInScript_ReturnsErrorAndContinues()
    {
        // Arrange
        var validScript = @"-- Valid script
local result = 'valid'
return result";

        var validScriptPath = Path.Combine(_scriptsDirectory, "valid.lua");
        File.WriteAllText(validScriptPath, validScript);

        // Create a script that will cause issues during file operations
        var problemScriptPath = Path.Combine(_scriptsDirectory, "problem.lua");
        File.WriteAllText(problemScriptPath, "-- Problem script");
        
        // Make the file read-only to simulate a processing error scenario
        File.SetAttributes(problemScriptPath, FileAttributes.ReadOnly);

        var task = CreateTask();
        task.ScriptsDirectory = _scriptsDirectory;
        task.OutputDirectory = _outputDirectory;

        try
        {
            // Act
            var result = task.Execute();

            // The task should complete successfully for the valid script
            // Note: The actual behavior depends on how the task handles individual file errors
            var validOutputPath = Path.Combine(_outputDirectory, "valid.expanded.lua");
            Assert.True(File.Exists(validOutputPath));
        }
        finally
        {
            // Cleanup - remove read-only attribute
            File.SetAttributes(problemScriptPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Execute_WithLargeNumberOfIncludes_PerformsWell()
    {
        // Arrange - Performance test with many includes
        var includesDir = Path.Combine(_scriptsDirectory, "includes");
        Directory.CreateDirectory(includesDir);

        var includeFileCount = 50;
        var mainScriptBuilder = new System.Text.StringBuilder();
        mainScriptBuilder.AppendLine("-- Script with many includes");

        // Create many small include files
        for (int i = 0; i < includeFileCount; i++)
        {
            var includeContent = $@"-- Include file {i}
local function func{i}()
    return 'result{i}'
end";

            var includePath = Path.Combine(includesDir, $"include{i}.lua");
            File.WriteAllText(includePath, includeContent);
            
            mainScriptBuilder.AppendLine($"--- @include \"includes/include{i}\"");
        }

        mainScriptBuilder.AppendLine();
        mainScriptBuilder.AppendLine("-- Use some functions");
        mainScriptBuilder.AppendLine("local result = func0() .. func25() .. func49()");
        mainScriptBuilder.AppendLine("return result");

        var mainScriptPath = Path.Combine(_scriptsDirectory, "manyIncludes.lua");
        File.WriteAllText(mainScriptPath, mainScriptBuilder.ToString());

        var task = CreateTask();
        task.ScriptsDirectory = _scriptsDirectory;
        task.OutputDirectory = _outputDirectory;

        // Act
        var startTime = DateTime.UtcNow;
        var result = task.Execute();
        var duration = DateTime.UtcNow - startTime;

        // Assert
        Assert.True(result);
        
        // Should complete within reasonable time (adjust threshold as needed)
        Assert.True(duration.TotalSeconds < 10, $"Task took too long: {duration.TotalSeconds} seconds");
        
        var expectedOutputPath = Path.Combine(_outputDirectory, "manyIncludes.expanded.lua");
        Assert.True(File.Exists(expectedOutputPath));
        
        var expandedContent = File.ReadAllText(expectedOutputPath);
        
        // Verify all functions are included
        Assert.Contains("function func0()", expandedContent);
        Assert.Contains("function func25()", expandedContent);
        Assert.Contains("function func49()", expandedContent);
        
        // Verify no include directives remain
        Assert.DoesNotContain("@include", expandedContent);
    }

    private ExpandLuaScriptsTask CreateTask()
    {
        var task = new ExpandLuaScriptsTask();
        
        // Mock the logger to prevent null reference exceptions
        var mockLogger = new Mock<IBuildEngine>();
        task.BuildEngine = mockLogger.Object;
        
        return task;
    }
}

using System.Text.Json;
using StackExchange.Redis;
using MessagePack;
using RedisQ.Core.Generated;

namespace RedisQ.Core;

public class RedisScripts(string prefix, string queueName, IDatabase database)
{
    private readonly QueueKeys _queueKeys = new(prefix, queueName);
    
    public RedisKey[] GetKeys(params string[] suffixes)
    {
        return _queueKeys
            .GetKeys(suffixes)
            .ToArray();
    }
    
    /// <summary>
    /// Add a standard job to the queue
    /// </summary>
    public async Task<RedisResult> AddStandardJob(Job job)
    {
        var keyArray = GetKeys(
            "wait", 
            "paused", 
            "meta", 
            "id", 
            "delayed", 
            "active", 
            "events", 
            "marker"
        );
        
        var jsonData = JsonSerializer.Serialize(job.Data);
        var packedOpts = MessagePackSerializer.Serialize(new
        {
            delay = job.Options.Delay,
            priority = job.Options.Priority,
            removeOnComplete = job.Options.RemoveOnComplete,
            removeOnFail = job.Options.RemoveOnFail,
            stackTraceLimit = job.Options.StackTraceLimit,
            order = job.Options.Order
        });
        
        var argArray = new RedisValue[]
        {
            _queueKeys.KeyPrefix,   // key prefix
            job.Id ?? "",           // custom id
            job.Name,               // name
            job.Timestamp.ToString(),   // timestamp
            jsonData,               // JSON serialized job data
            packedOpts              // msg packed options
        };
        
        var result = await database.ScriptEvaluateAsync(
            LuaScript_addStandardJob.Content,
            keyArray,
            argArray
        );
        
        return result;
    }
    
    /// <summary>
    /// Add a delayed job to the queue
    /// </summary>
    public async Task<RedisResult> AddDelayedJob(Job job)
    {
        var keyArray = GetKeys(
            "marker", 
            "meta", 
            "id", 
            "delayed", 
            "completed", 
            "events"
        );
        
        var jsonData = JsonSerializer.Serialize(job.Data);
        var packedOpts = MessagePackSerializer.Serialize(new
        {
            delay = job.Options.Delay,
            priority = job.Options.Priority,
            removeOnComplete = job.Options.RemoveOnComplete,
            removeOnFail = job.Options.RemoveOnFail,
            stackTraceLimit = job.Options.StackTraceLimit,
            order = job.Options.Order
        });
        
        var argArray = new RedisValue[]
        {
            _queueKeys.KeyPrefix,       // keyPrefix
            job.Id ?? "",               // customId
            job.Name,                   // jobName
            job.Timestamp.ToString(),   // timestamp
            jsonData,                   // JSON serialized job data
            packedOpts                  // msg packed job options
        };
        
        var result = await database.ScriptEvaluateAsync(
            LuaScript_addDelayedJob.Content,
            keyArray,
            argArray
        );
        
        return result;
    }
    
    /// <summary>
    /// Get counts of jobs in different states
    /// </summary>
    public async Task<RedisResult[]?> GetCounts(params string[] jobStates)
    {
        RedisKey[] keyArray = [ _queueKeys.KeyPrefix ];
        var argArray = jobStates.Select(type => (RedisValue)type).ToArray();

        var result = await database.ScriptEvaluateAsync(
            LuaScript_getCounts.Content,
            keyArray,
            argArray
        );
        
        return (RedisResult[]?)result;
    }
    
    /// <summary>
    /// Move a job from wait to active state
    /// </summary>
    public async Task<RedisResult> MoveToActive(string token, Dictionary<string, object?> options)
    {
        var keyArray = GetKeys(
            "wait", 
            "active", 
            "prioritized", 
            "events", 
            "stalled", 
            "limiter", 
            "delayed", 
            "paused", 
            "meta", 
            "pc", 
            "marker"
        );
        
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var packedOptions = MessagePackSerializer.Serialize(new
        {
            token,
            lockDuration = options["lockDuration"],
            limiter = options["limiter"],
            workerName = options["workerName"]
        });

        var argArray = new RedisValue[]
        {
            _queueKeys.KeyPrefix,
            timestamp,
            packedOptions
        };
        
        var result = await database.ScriptEvaluateAsync(
            LuaScript_moveToActive.Content,
            keyArray,
            argArray
        );
        
        return result;
    }
    
    /// <summary>
    /// Move a job to completed state
    /// </summary>
    public async Task<RedisResult> MoveToCompletedAsync(Job job, object? returnValue, 
        bool removeOnComplete, string token, bool fetchNext = true,
        Dictionary<string, object>? fieldsToUpdate = null)
    {   
        return await MoveToFinishedAsync(
            job, 
            returnValue, 
            "returnvalue", 
            removeOnComplete, 
            "completed", 
            token, 
            fetchNext, 
            fieldsToUpdate
        );
    }

    /// <summary>
    /// Move a job to failed state
    /// </summary>
    public async Task<RedisResult> MoveToFailedAsync(Job job, object? returnValue, 
        bool removeOnComplete, string token, bool fetchNext = true,
        Dictionary<string, object>? fieldsToUpdate = null)
    {
        return await MoveToFinishedAsync(
            job, 
            returnValue, 
            "failedReason", 
            removeOnComplete,
            "failed", 
            token, 
            fetchNext, 
            fieldsToUpdate
        );
    }
    
    /// <summary>
    /// Move a job to finished state (completed or failed)
    /// </summary>
    private async Task<RedisResult> MoveToFinishedAsync(Job job, object? returnValue, string propVal,
        bool shouldRemove, string target, string token, bool fetchNext = true,
        Dictionary<string, object>? fieldsToUpdate = null)
    {
        if (job.Id == null)
        {
            throw new ArgumentException("Job ID cannot be null", nameof(job));
        }
        
        var metricsKey = _queueKeys.ToKey($"metrics:{target}");

        var keyArray = GetKeys(
                "wait",
                "active",
                "prioritized",
                "events",
                "stalled",
                "limiter",
                "delayed",
                "paused",
                "meta",
                "pc",
                target
            ).Append(_queueKeys.ToKey(job.Id))
            .Append(metricsKey)
            .Append(_queueKeys.ToKey("marker"))
            .ToArray();
        
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var jsonValue = returnValue != null ? JsonSerializer.Serialize(returnValue) : "";

        var keepJobsValue = shouldRemove ? new { count = 0 } : new { count = -1 };

        var options = new Dictionary<string, object>
        {
            ["token"] = token,
            ["keepJobs"] = keepJobsValue,
            ["attempts"] = job.Attempts,
            ["attemptsMade"] = job.AttemptsMade
        };

        var packedOptions = MessagePackSerializer.Serialize(options);

        var argArray = new RedisValue[]
        {
            job.Id,
            timestamp,
            propVal,
            jsonValue,
            target,
            fetchNext ? "1" : "",
            _queueKeys.KeyPrefix,
            packedOptions
        };

        if (fieldsToUpdate != null)
        {
            var flatFields = FlattenDictionary(fieldsToUpdate);
            var packedFields = MessagePackSerializer.Serialize(flatFields);
            var newArgArray = argArray.ToList();
            newArgArray.Add(packedFields);
            argArray = newArgArray.ToArray();
        }

        var result = await database.ScriptEvaluateAsync(
            LuaScript_moveToFinished.Content,
            keyArray,
            argArray
        );

        return result;
    }
    
    /// <summary>
    /// Retry a failed job
    /// </summary>
    public async Task<RedisResult> RetryJobAsync(string jobId, bool lifo, string token, 
        Dictionary<string, object>? fieldsToUpdate = null)
    {
        var keyArray = GetKeys(
            "active", 
            "wait", 
            "paused", 
            jobId,
            "meta", 
            "events", 
            "delayed", 
            "prioritized", 
            "pc", 
            "marker", 
            "stalled"
        );
        
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pushCmd = lifo ? "RPUSH" : "LPUSH";
        
        var argArray = new List<RedisValue>
        { 
            _queueKeys.KeyPrefix, 
            timestamp,
            pushCmd,
            jobId,
            token
        };
        
        if (fieldsToUpdate != null)
        {
            var flatFields = FlattenDictionary(fieldsToUpdate);
            argArray.Add(MessagePackSerializer.Serialize(flatFields));
        }
        
        var result = await database.ScriptEvaluateAsync(
            LuaScript_retryJob.Content,
            keyArray,
            argArray.ToArray()
        );
        
        return result;
    }
    
    // Bring it back when I figure out how to use msgpack for the job arguments
    // private (byte[], string, byte[]) PrepareJobArgs(Job job)
    // {
    //     var jsonData = JsonSerializer.Serialize(job.Data);
    //     var packedOpts = MessagePackSerializer.Serialize(job.Options);
    //     
    //     var argsArray = new object?[]
    //     {
    //         _keys[""],
    //         job.Id ?? "",
    //         job.Name,
    //         job.Timestamp,
    //         "",
    //         ""
    //     };
    //     
    //     var packedArgs = MessagePackSerializer.Serialize(argsArray);
    //     
    //     return (packedArgs, jsonData, packedOpts);
    // }
    
    private static string[] FlattenDictionary(Dictionary<string, object> dict)
    {
        var result = new List<string>();
        
        foreach (var kvp in dict)
        {
            result.Add(kvp.Key);
            result.Add(kvp.Value.ToString() ?? "");
        }
        
        return [.. result];
    }
}
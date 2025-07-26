using System.Text.Json;
using StackExchange.Redis;
using MessagePack;
using RedisQ.Core.Generated;

namespace RedisQ.Core;

public class RedisScripts
{
    private readonly QueueKeys _queueKeys;
    private Dictionary<string, string> _keys;
    private readonly string _queueName;
    private readonly IDatabase _database;
    
    public RedisScripts(string prefix, string queueName, IDatabase database)
    {
        _queueKeys = new QueueKeys(prefix);
        _keys = _queueKeys.GetKeys(queueName);
        _queueName = queueName;
        _database = database;
    }

    public RedisKey[] GetKeys(params string[] keyNames)
    {
        return [.. keyNames.Select(key => (RedisKey)_keys[key])];
    }

    public void ResetQueueKeys(string queueName)
    {
        _keys = _queueKeys.GetKeys(queueName);
    }

    private RedisKey ToKey(string? suffix)
    {
        return (RedisKey)_queueKeys.ToKey(_queueName, suffix ?? string.Empty);
    }
    
    /// <summary>
    /// Add a standard job to the queue
    /// </summary>
    public async Task<RedisResult> AddStandardJobAsync(Job job, long timestamp)
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
        var packedOpts = MessagePackSerializer.Serialize(job.Options);
        
        var argArray = new RedisValue[]
        {
            _keys[""],           // key prefix
            job.Id ?? "",        // custom id
            job.Name,            // name
            timestamp.ToString(), // timestamp
            "",                  // repeat job key (optional)
            "",                  // deduplication key (optional)
            jsonData,            // JSON stringified job data
            packedOpts           // msgpacked options
        };
        
        var result = await _database.ScriptEvaluateAsync(
            LuaScript_addStandardJob.Content,
            keyArray,
            argArray
        );
        
        return result;
    }
    
    /// <summary>
    /// Add a delayed job to the queue
    /// </summary>
    public async Task<RedisResult> AddDelayedJobAsync(Job job, long timestamp)
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
        var packedOpts = MessagePackSerializer.Serialize(job.Options);
        
        var argArray = new RedisValue[]
        {
            _keys[""],           // keyPrefix
            job.Id ?? "",        // customId
            job.Name,            // jobName
            timestamp.ToString(), // timestamp
            jsonData,            // JSON stringified job data
            packedOpts           // msgpacked job options
        };
        
        var result = await _database.ScriptEvaluateAsync(
            LuaScript_addDelayedJob.Content,
            keyArray,
            argArray
        );
        
        return result;
    }
    
    /// <summary>
    /// Get counts of jobs in different states
    /// </summary>
    public async Task<RedisResult[]?> GetCountsAsync(params string[] types)
    {
        var keyArray = new RedisKey[] { _keys[""] };
        var argArray = types.Select(type => (RedisValue)(type == "waiting" ? "wait" : type)).ToArray();

        var result = await _database.ScriptEvaluateAsync(
            LuaScript_getCounts.Content,
            keyArray,
            argArray
        );
        
        return (RedisResult[]?)result;
    }
    
    /// <summary>
    /// Move a job from wait to active state
    /// </summary>
    public async Task<RedisResult> MoveToActiveAsync(string token, Dictionary<string, object?> options)
    {
        var keys = GetKeys();

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
        var packedOptions = MessagePackSerializer.Serialize(new Dictionary<string, object?>
        {
            { "token", token },
            { "lockDuration", options["lockDuration"] },
            { "limiter", options["limiter"] },
            { "workerName", options["workerName"] }
        });

        var argArray = new RedisValue[]
        {
            _keys[""],
            timestamp,
            packedOptions
        };
        
        var result = await _database.ScriptEvaluateAsync(
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
        
        var metricsKey = ToKey($"metrics:{target}");

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
            ).Append(ToKey(job.Id))
            .Append(metricsKey)
            .Append(_keys["marker"])
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
            _keys[""],
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

        var result = await _database.ScriptEvaluateAsync(
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
        var keys = GetKeys();

        var keyArray = GetKeys(
            "active", 
            "wait", 
            "paused", 
            ToKey(jobId),
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
            _keys[""],
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
        
        var result = await _database.ScriptEvaluateAsync(
            LuaScript_retryJob.Content,
            keyArray,
            argArray.ToArray()
        );
        
        return result;
    }
    
    private (byte[], string, byte[]) PrepareJobArgs(Job job)
    {
        var jsonData = JsonSerializer.Serialize(job.Data);
        var packedOpts = MessagePackSerializer.Serialize(job.Options);
        
        var argsArray = new object?[]
        {
            _keys[""],
            job.Id ?? "",
            job.Name,
            job.Timestamp,
            "",
            ""
        };
        
        var packedArgs = MessagePackSerializer.Serialize(argsArray);
        
        return (packedArgs, jsonData, packedOpts);
    }
    
    private string[] FlattenDictionary(Dictionary<string, object> dict)
    {
        var result = new List<string>();
        
        foreach (var kvp in dict)
        {
            result.Add(kvp.Key);
            result.Add(kvp.Value?.ToString() ?? "");
        }
        
        return [.. result];
    }
}
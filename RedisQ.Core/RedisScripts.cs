using System.Text.Json;
using StackExchange.Redis;
using MessagePack;
using RedisQ.Core.Generated;

namespace RedisQ.Core;

public class RedisScripts
{
    private readonly string _prefix;
    private readonly string _queueName;
    private readonly IDatabase _database;
    private readonly QueueKeys _queueKeys;
    private Dictionary<string, string> _keys;

    public RedisScripts(string prefix, string queueName, IDatabase database)
    {
        _prefix = prefix;
        _queueName = queueName;
        _database = database;
        _queueKeys = new QueueKeys(prefix);
        _keys = _queueKeys.GetKeys(queueName);
    }

    public void ResetQueueKeys(string queueName)
    {
        _keys = _queueKeys.GetKeys(queueName);
    }

    public RedisKey[] GetKeys(params string[] keyNames)
    {
        return keyNames.Select(key => (RedisKey)_keys[key]).ToArray();
    }

    // Add a standard job to the queue
    public async Task<RedisResult> AddStandardJobAsync(Job job, long timestamp)
    {
        // Serialize job data with compact JSON
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        var jsonData = JsonSerializer.Serialize(job.Data, jsonOptions);

        // Pack job options using MessagePack
        var packedOpts = MessagePackSerializer.Serialize(job.Options);

        var parameters = new
        {
            waitKey = _keys["wait"],
            pausedKey = _keys["paused"],
            metaKey = _keys["meta"],
            idKey = _keys["id"],
            completedKey = _keys["completed"],
            delayedKey = _keys["delayed"],
            activeKey = _keys["active"],
            eventsKey = _keys["events"],
            markerKey = _keys["marker"],
            keyPrefix = _keys[""],
            customId = job.Id ?? "",
            jobName = job.Name,
            timestamp,
            repeatJobKey = "", // TODO: Add support for repeat jobs
            deduplicationKey = "", // TODO: Add support for deduplication
            jobData = jsonData,
            jobOptions = packedOpts
        };

        var preparedScript = LuaScript.Prepare(LuaScript_addStandardJob.Content);

        return await _database.ScriptEvaluateAsync(preparedScript, parameters);
    }

    // Add a delayed job to the queue
    public async Task<RedisResult> AddDelayedJobAsync(Job job, long timestamp)
    {
        // Serialize job data with compact JSON
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        var jsonData = JsonSerializer.Serialize(job.Data, jsonOptions);

        // Pack job options using MessagePack
        var packedOpts = MessagePackSerializer.Serialize(job.Options);

        var parameters = new
        {
            markerKey = _keys["marker"],
            metaKey = _keys["meta"],
            idKey = _keys["id"],
            delayedKey = _keys["delayed"],
            completedKey = _keys["completed"],
            eventsKey = _keys["events"],
            keyPrefix = _keys[""],
            customId = job.Id ?? "",
            jobName = job.Name,
            timestamp,
            repeatJobKey = "", // TODO: Add support for repeat jobs
            deduplicationKey = "", // TODO: Add support for deduplication
            jobData = jsonData,
            jobOptions = packedOpts
        };

        var preparedScript = LuaScript.Prepare(LuaScript_addDelayedJob.Content);
        return await _database.ScriptEvaluateAsync(preparedScript, parameters);
    }
    
    // Get job counts by type
    public async Task<RedisResult> GetCountsAsync(params string[] types)
    {
        var transformedTypes = types.Select(type => type == "waiting" ? "wait" : type).ToArray();
        
        // Convert to RedisValue array for StackExchange.Redis compatibility
        var redisValueTypes = transformedTypes.Select(t => (RedisValue)t).ToArray();

        var parameters = new
        {
            prefix = _keys[""],
            types = redisValueTypes
        };

        var preparedScript = LuaScript.Prepare(LuaScript_getCounts.Content);
        var result = await _database.ScriptEvaluateAsync(preparedScript, parameters);

        return result;
    }
    
    // Retry a failed job
    public async Task<RedisResult> RetryJobAsync(string jobId, bool lifo, string token = "0",
        Dictionary<string, object>? options = null)
    {
        var jobKey = _queueKeys.ToKey(_queueName, jobId);
        var pushCmd = lifo ? "RPUSH" : "LPUSH";

        var fieldsToUpdate = options?.ContainsKey("fieldsToUpdate") == true 
            ? MessagePackSerializer.Serialize(ObjectToFlatArray(options["fieldsToUpdate"]))
            : [];

        var parameters = new
        {
            activeKey = _keys["active"],
            waitKey = _keys["wait"],
            pausedKey = _keys["paused"],
            jobKey,
            metaKey = _keys["meta"],
            eventsKey = _keys["events"],
            delayedKey = _keys["delayed"],
            prioritizedKey = _keys["prioritized"],
            pcKey = _keys["pc"],
            markerKey = _keys["marker"],
            stalledKey = _keys["stalled"],
            keyPrefix = _keys[""],
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            pushCmd,
            jobId,
            token,
            fieldsToUpdate
        };

        var preparedScript = LuaScript.Prepare(LuaScript_retryJob.Content);
        var result = await _database.ScriptEvaluateAsync(preparedScript, parameters);

        if (result.Resp2Type == ResultType.Integer && (int)result < 0)
        {
            throw CreateFinishedError((int)result, jobId, "retryJob", "active");
        }

        return result;
    }

    // Move next job to active state for processing
    public async Task<RedisResult> MoveToActiveAsync(string workerToken, int lockDuration, 
        string workerName, Dictionary<string, object>? limiter = null)
    {
        var optsData = new Dictionary<string, object>
        {
            ["token"] = workerToken,
            ["lockDuration"] = lockDuration,
            ["name"] = workerName
        };

        if (limiter != null)
        {
            optsData["limiter"] = limiter;
        }

        var packedOpts = MessagePackSerializer.Serialize(optsData);

        var parameters = new
        {
            waitKey = _keys["wait"],
            activeKey = _keys["active"],
            prioritizedKey = _keys["prioritized"],
            eventStreamKey = _keys["events"],
            stalledKey = _keys["stalled"],
            rateLimiterKey = _keys["limiter"],
            delayedKey = _keys["delayed"],
            pausedKey = _keys["paused"],
            metaKey = _keys["meta"],
            pcKey = _keys["pc"],
            markerKey = _keys["marker"],
            keyPrefix = _keys[""],
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            opts = packedOpts
        };

        var preparedScript = LuaScript.Prepare(LuaScript_moveToActive.Content);
        return await _database.ScriptEvaluateAsync(preparedScript, parameters);
    }
    
    // Move job to completed state
    public async Task<RedisResult> MoveToCompletedAsync(Job job, object returnValue,
        bool removeOnComplete, string token, bool fetchNext = true)
    {
        var jobKey = _queueKeys.ToKey(_queueName, job.Id ?? string.Empty);
        var metricsKey = _queueKeys.ToKey(_queueName, "metrics:completed");

        var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
        var serializedValue = JsonSerializer.Serialize(returnValue, jsonOptions);

        var packedOpts = MessagePackSerializer.Serialize(new
        {
            token,
            keepJobs = GetKeepJobs(removeOnComplete),
            attempts = job.Attempts,
            attemptsMade = job.AttemptsMade
        });

        var parameters = new
        {
            waitKey = _keys["wait"],
            activeKey = _keys["active"],
            prioritizedKey = _keys["prioritized"],
            eventStreamKey = _keys["events"],
            stalledKey = _keys["stalled"],
            rateLimiterKey = _keys["limiter"],
            delayedKey = _keys["delayed"],
            pausedKey = _keys["paused"],
            metaKey = _keys["meta"],
            pcKey = _keys["pc"],
            finishedKey = _keys["completed"],
            jobIdKey = jobKey,
            metricsKey,
            markerKey = _keys["marker"],
            jobId = job.Id ?? string.Empty,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgProperty = "returnvalue",
            returnValue = serializedValue,
            target = "completed",
            fetchNext = fetchNext ? "1" : "",
            keysPrefix = _keys[""],
            opts = packedOpts,
            jobFields = new byte[0] // Empty job fields for now
        };

        var preparedScript = LuaScript.Prepare(LuaScript_moveToFinished.Content);
        return await _database.ScriptEvaluateAsync(preparedScript, parameters);
    }

    private object GetKeepJobs(object shouldRemove)
    {
        return shouldRemove switch
        {
            int count => new { count },
            Dictionary<string, object> dict => dict,
            bool remove when remove => new { count = 0 },
            _ => new { count = -1 }
        };
    }

    private Exception CreateFinishedError(int code, string jobId, string command, string state)
    {
        return code switch
        {
            -1 => new InvalidOperationException($"Missing key for job {jobId}. {command}"),
            -2 => new InvalidOperationException($"Missing lock for job {jobId}. {command}"),
            -3 => new InvalidOperationException($"Job {jobId} is not in the {state} state. {command}"),
            _ => new InvalidOperationException($"Unknown error code {code} for job {jobId}. {command}")
        };
    }

    private static object[] ObjectToFlatArray(object obj)
    {
        // Convert object properties to flat array [key1, value1, key2, value2, ...]
        if (obj is Dictionary<string, object> dict)
        {
            return dict.SelectMany(kvp => new object[] { kvp.Key, kvp.Value }).ToArray();
        }

        var properties = obj.GetType().GetProperties();
        return properties.SelectMany(prop => new object[]
        {
            prop.Name,
            prop.GetValue(obj)?.ToString() ?? ""
        }).ToArray();
    }
}
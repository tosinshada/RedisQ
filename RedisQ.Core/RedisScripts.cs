using System.Text.Json;
using StackExchange.Redis;
using MessagePack;
using RedisQ.Core.Loader;

namespace RedisQ.Core;

public class RedisScripts
{
    private readonly string _prefix;
    private readonly string _queueName;
    private readonly IDatabase _database;
    private readonly Dictionary<string, Command> _commands;
    private readonly QueueKeys _queueKeys;
    private readonly ScriptLoader _scriptLoader;
    private Dictionary<string, string> _keys;

    public RedisScripts(string prefix, string queueName, IDatabase database)
    {
        _prefix = prefix;
        _queueName = queueName;
        _database = database;
        _queueKeys = new QueueKeys(prefix);
        _keys = _queueKeys.GetKeys(queueName);
        _scriptLoader = new ScriptLoader();
        _commands = new Dictionary<string, Command>();
    }

    public void ResetQueueKeys(string queueName)
    {
        _keys = _queueKeys.GetKeys(queueName);
    }

    private string GetScript(string scriptName)
    {   
        if (_commands.TryGetValue(scriptName.Replace(".lua", ""), out var command))
        {
            return command.Options.Lua;
        }
        
        // Fallback to file reading if script not loaded yet
        var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", scriptName);
        if (!File.Exists(scriptPath))
        {
            scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "commands", scriptName);
        }
        return File.Exists(scriptPath) ? File.ReadAllText(scriptPath) : string.Empty;
    }

    public RedisKey[] GetKeys(params string[] keyNames)
    {
        return keyNames.Select(key => (RedisKey)_keys[key]).ToArray();
    }

    public RedisValue[] AddJobArgs(Job job)
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

        // Create packed arguments array
        var argsArray = new object[]
        {
            _keys[""],
            job.Id ?? "",
            job.Name,
            job.Timestamp
        };

        var packedArgs = MessagePackSerializer.Serialize(argsArray);

        return [packedArgs, jsonData, packedOpts];
    }

    // Add a standard job to the queue
    public async Task<RedisResult> AddStandardJobAsync(Job job, long timestamp)
    {
        var keys = GetKeys("wait", "paused", "meta", "id", "completed",
                          "delayed", "active", "events", "marker");
        var args = AddJobArgs(job).Concat([timestamp]).ToArray();

        var script = GetScript("addStandardJob");
        return await _database.ScriptEvaluateAsync(script, keys, args);
    }

    // Add a delayed job to the queue
    public async Task<RedisResult> AddDelayedJobAsync(Job job, long timestamp)
    {
        var keys = GetKeys("marker", "meta", "id", "delayed", "completed", "events");
        var args = AddJobArgs(job).Concat([timestamp]).ToArray();

        var script = GetScript("addDelayedJob");
        return await _database.ScriptEvaluateAsync(script, keys, args);
    }

    // Get job counts by type
    public async Task<RedisResult> GetCountsAsync(params string[] types)
    {
        var keys = GetKeys("");
        var transformedTypes = types.Select(type => type == "waiting" ? "wait" : type).ToArray();
        var args = transformedTypes.Select(t => (RedisValue)t).ToArray();

        var script = GetScript("getCounts");
        return await _database.ScriptEvaluateAsync(script, keys, args);
    }

    // Retry a failed job
    public async Task<RedisResult> RetryJobAsync(string jobId, bool lifo, string token = "0",
        Dictionary<string, object>? options = null)
    {
        var keys = GetKeys("active", "wait", "paused");
        var jobKey = _queueKeys.ToKey(_queueName, jobId);
        var allKeys = keys.Concat(new RedisKey[]
        {
            jobKey,
            _keys["meta"],
            _keys["events"],
            _keys["delayed"],
            _keys["prioritized"],
            _keys["pc"],
            _keys["marker"],
            _keys["stalled"]
        }).ToArray();

        var pushCmd = lifo ? "RPUSH" : "LPUSH";
        var args = new List<RedisValue>
        {
            _keys[""],
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            pushCmd,
            jobId,
            token
        };

        if (options?.ContainsKey("fieldsToUpdate") == true)
        {
            var packedFields = MessagePackSerializer.Serialize(
                ObjectToFlatArray(options["fieldsToUpdate"]));
            args.Add(packedFields);
        }

        var script = GetScript("retryJob");
        var result = await _database.ScriptEvaluateAsync(script, allKeys, args.ToArray());

        if (result.Resp2Type == ResultType.Integer && (int)result < 0)
        {
            throw CreateFinishedError((int)result, jobId, "retryJob", "active");
        }

        return result;
    }

    // Move job to completed state
    public async Task<RedisResult> MoveToCompletedAsync(Job job, object returnValue,
        bool removeOnComplete, string token, bool fetchNext = true)
    {
        var keys = GetKeys("wait", "active", "prioritized", "events", "stalled",
                          "limiter", "delayed", "paused", "meta", "pc", "completed");

        var jobKey = _queueKeys.ToKey(_queueName, job.Id ?? string.Empty);
        var metricsKey = _queueKeys.ToKey(_queueName, "metrics:completed");

        var allKeys = keys.Concat(new RedisKey[] { jobKey, metricsKey, _keys["marker"] }).ToArray();

        var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
        var serializedValue = JsonSerializer.Serialize(returnValue, jsonOptions);

        var packedOpts = MessagePackSerializer.Serialize(new
        {
            token = token,
            keepJobs = GetKeepJobs(removeOnComplete),
            attempts = job.Attempts,
            attemptsMade = job.AttemptsMade
        });

        var args = new RedisValue[]
        {
            job.Id ?? string.Empty,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "returnvalue",
            serializedValue,
            "completed",
            fetchNext ? "1" : "",
            _keys[""],
            packedOpts
        };

        var script = GetScript("moveToFinished");
        return await _database.ScriptEvaluateAsync(script, allKeys, args);
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
namespace RedisQ.Core;

public class QueueKeys
{
    private readonly string _prefix;

    public QueueKeys(string prefix)
    {
        _prefix = prefix;
    }

    public Dictionary<string, string> GetKeys(string queueName)
    {
        // [todo] - remove unnecessary keys if not used
        return new Dictionary<string, string>
        {
            [""] = $"{_prefix}:{queueName}:",
            ["wait"] = $"{_prefix}:{queueName}:wait",
            ["active"] = $"{_prefix}:{queueName}:active",
            ["paused"] = $"{_prefix}:{queueName}:paused",
            ["completed"] = $"{_prefix}:{queueName}:completed",
            ["failed"] = $"{_prefix}:{queueName}:failed",
            ["delayed"] = $"{_prefix}:{queueName}:delayed",
            ["prioritized"] = $"{_prefix}:{queueName}:prioritized",
            ["events"] = $"{_prefix}:{queueName}:events",
            ["stalled"] = $"{_prefix}:{queueName}:stalled",
            ["meta"] = $"{_prefix}:{queueName}:meta",
            ["id"] = $"{_prefix}:{queueName}:id",
            ["marker"] = $"{_prefix}:{queueName}:marker",
            ["pc"] = $"{_prefix}:{queueName}:pc",
            ["limiter"] = $"{_prefix}:{queueName}:limiter",
            ["repeat"] = $"{_prefix}:{queueName}:repeat"
        };
    }

    public string ToKey(string queueName, string suffix)
    {
        return $"{_prefix}:{queueName}:{suffix}";
    }
}
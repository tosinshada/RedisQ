using StackExchange.Redis;

namespace RedisQ.Core;

public class QueueKeys(string prefix)
{
    public Dictionary<string, RedisKey> GetInitKeys(string queueName)
    {
        List<string> initKeys = [
            "", "wait", "active", "paused", "completed", "failed", "delayed",
            "prioritized", "events", "stalled", "meta", "id", "marker", "pc",
            "limiter", "repeat"
        ];
        
        return initKeys.ToDictionary(
            key => key,
            key => ToKey(queueName, key)
        );
    }
    
    // use a list of suffixes to generate keys
    public IEnumerable<RedisKey> GetKeys(string queueName, params string[] suffixes)
    {
        return suffixes.Select(suffix => ToKey(queueName, suffix));
    }

    public RedisKey ToKey(string queueName, string suffix)
    {
        return $"{prefix}:{queueName}:{suffix}";
    }
}
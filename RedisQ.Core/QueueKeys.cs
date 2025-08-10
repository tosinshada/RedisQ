using StackExchange.Redis;

namespace RedisQ.Core;

public class QueueKeys(string prefix, string queueName)
{
    public string KeyPrefix => $"{prefix}:{queueName}:";
    
    // use a list of suffixes to generate keys
    public IEnumerable<RedisKey> GetKeys(params string[] suffixes)
    {
        return suffixes.Select(ToKey);
    }

    public RedisKey ToKey(string suffix)
    {
        return $"{prefix}:{queueName}:{suffix}";
    }
}
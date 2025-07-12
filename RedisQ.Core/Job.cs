namespace RedisQ.Core;

public class Job
{
    public string? Id { get; set; }
    public string Name { get; set; }
    public object? Data { get; set; }
    public Dictionary<string, object> Options { get; set; } = new();
    public long Timestamp { get; set; }
    public int Attempts { get; set; }
    public int AttemptsMade { get; set; }
}
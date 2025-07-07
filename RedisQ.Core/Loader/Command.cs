namespace RedisQ.Core.Loader;

public class Command
{
    public string Name { get; set; } = string.Empty;
    public CommandOptions Options { get; set; } = new();
}

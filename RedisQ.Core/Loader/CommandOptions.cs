namespace RedisQ.Core.Loader;

public class CommandOptions
{
    public int NumberOfKeys { get; set; }
    public string Lua { get; set; } = string.Empty;
}
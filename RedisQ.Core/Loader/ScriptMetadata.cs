namespace RedisQ.Core.Loader;

public class ScriptMetadata
{
    public string Name { get; set; } = string.Empty;
    public int? NumberOfKeys { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public List<ScriptMetadata> Includes { get; set; } = [];
}
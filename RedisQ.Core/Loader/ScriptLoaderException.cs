namespace RedisQ.Core.Loader;

public class ScriptLoaderException : Exception
{
    public string[] Includes { get; }
    public int Line { get; }
    public int Position { get; }

    public ScriptLoaderException(string message, string path, string[]? stack = null, int line = 0, int position = 0)
        : base(message)
    {
        Includes = stack ?? [];
        Line = line;
        Position = position;
    }
}
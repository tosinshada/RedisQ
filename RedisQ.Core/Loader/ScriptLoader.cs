using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RedisQ.Core.Loader;

public class ScriptLoader
{
    private readonly Dictionary<string, string> _pathMapper = new();
    private readonly string _rootPath;

    private static readonly Regex IncludeRegex = new(@"^[-]{2,3}[ \t]*@include[ \t]+([""'])(.+?)\1[; \t\n]*$", RegexOptions.Multiline);
    private static readonly Regex EmptyLineRegex = new(@"^\s*[\r\n]", RegexOptions.Multiline);

    public ScriptLoader()
    {
        _rootPath = GetProjectRoot();
        _pathMapper["~"] = _rootPath;
        _pathMapper["rootDir"] = _rootPath;
        _pathMapper["base"] = AppDomain.CurrentDomain.BaseDirectory;
    }

    /// <summary>
    /// Load all lua scripts from a directory and save them to an "exported" subdirectory.
    /// </summary>
    /// <param name="dir">Directory containing the scripts</param>
    /// <param name="cache">Cache for script metadata</param>
    /// <returns></returns>
    public async Task LoadScriptsAsync(string? dir = null, Dictionary<string, ScriptMetadata>? cache = null)
    {
        dir = Path.GetFullPath(dir ?? AppDomain.CurrentDomain.BaseDirectory);

        if (!Directory.Exists(dir))
        {
            throw new ScriptLoaderException($"Directory not found: {dir}", dir);
        }

        var files = Directory.GetFiles(dir, "*.lua");

        if (files.Length == 0)
        {
            throw new ScriptLoaderException("No .lua files found!", dir);
        }

        cache ??= new Dictionary<string, ScriptMetadata>();

        // Create exported directory
        var exportedDir = Path.Combine(dir, "exported");
        Directory.CreateDirectory(exportedDir);

        foreach (var file in files)
        {
            var command = await LoadCommandAsync(file, cache);

            // Save the expanded script to the exported directory
            var fileName = Path.GetFileName(file);
            var exportedFilePath = Path.Combine(exportedDir, fileName);
            await File.WriteAllTextAsync(exportedFilePath, command);
        }
    }

    /// <summary>
    /// Load a single command from a file
    /// </summary>
    /// <param name="filename">The filename to load</param>
    /// <param name="cache">Cache for script metadata</param>
    /// <returns>The loaded command</returns>
    private async Task<string> LoadCommandAsync(string filename, Dictionary<string, ScriptMetadata>? cache = null)
    {
        filename = Path.GetFullPath(filename);

        var (scriptName, _) = SplitFilename(filename);
        var script = cache?.GetValueOrDefault(scriptName);
        if (script == null)
        {
            var content = await File.ReadAllTextAsync(filename);
            script = await ParseScriptAsync(filename, content, cache);
        }

        var luaCommand = RemoveEmptyLines(Interpolate(script));

        return luaCommand;
    }

    /// <summary>
    /// Resolve the script path considering path mappings
    /// </summary>
    /// <param name="scriptName">The name of the script</param>
    /// <param name="stack">The include stack, for nicer errors</param>
    /// <returns>The resolved path</returns>
    private string ResolvePath(string scriptName, string[]? stack = null)
    {
        stack ??= [];

        if (scriptName.StartsWith("~"))
        {
            scriptName = Path.Combine(_rootPath, scriptName.Substring(2));
        }
        else if (scriptName.StartsWith("<"))
        {
            var endIndex = scriptName.IndexOf('>');
            if (endIndex > 0)
            {
                var name = scriptName.Substring(1, endIndex - 1);
                if (!_pathMapper.TryGetValue(name, out var mappedPath))
                {
                    throw new ScriptLoaderException($"No path mapping found for \"{name}\"", scriptName, stack);
                }
                scriptName = Path.Combine(mappedPath, scriptName.Substring(endIndex + 1));
            }
        }

        return Path.GetFullPath(scriptName);
    }

    /// <summary>
    /// Parse a (top-level) lua script
    /// </summary>
    /// <param name="filename">The full path to the script</param>
    /// <param name="content">The content of the script</param>
    /// <param name="cache">Cache for file metadata</param>
    /// <returns>Script metadata</returns>
    private async Task<ScriptMetadata> ParseScriptAsync(string filename, string content, Dictionary<string, ScriptMetadata>? cache = null)
    {
        var (name, numberOfKeys) = SplitFilename(filename);
        var meta = cache?.GetValueOrDefault(name);
        if (meta?.Content == content)
        {
            return meta;
        }

        var fileInfo = new ScriptMetadata
        {
            Path = filename,
            Token = GetPathHash(filename),
            Content = content,
            Name = name,
            NumberOfKeys = numberOfKeys,
            Includes = []
        };

        await ResolveDependenciesAsync(fileInfo, cache, false, []);
        return fileInfo;
    }

    /// <summary>
    /// Construct the final version of a file by interpolating its includes in dependency order
    /// </summary>
    /// <param name="file">The file whose content we want to construct</param>
    /// <param name="processed">A cache to keep track of which includes have already been processed</param>
    /// <returns>The interpolated content</returns>
    private string Interpolate(ScriptMetadata file, HashSet<string>? processed = null)
    {
        processed ??= [];
        var content = file.Content;

        foreach (var child in file.Includes)
        {
            var emitted = processed.Contains(child.Path);
            var fragment = Interpolate(child, processed);
            var replacement = emitted ? string.Empty : fragment;

            if (string.IsNullOrEmpty(replacement))
            {
                content = content.Replace(child.Token, string.Empty);
            }
            else
            {
                // Replace the first instance with the dependency
                var index = content.IndexOf(child.Token, StringComparison.Ordinal);
                if (index >= 0)
                {
                    content = content.Substring(0, index) + replacement + content.Substring(index + child.Token.Length);
                }
                // Remove the rest
                content = content.Replace(child.Token, string.Empty);
            }

            processed.Add(child.Path);
        }

        return content;
    }

    /// <summary>
    /// Recursively collect all scripts included in a file
    /// </summary>
    /// <param name="file">The parent file</param>
    /// <param name="cache">A cache for file metadata</param>
    /// <param name="isInclude">Whether this is an include file</param>
    /// <param name="stack">Internal stack to prevent circular references</param>
    private async Task ResolveDependenciesAsync(ScriptMetadata file, Dictionary<string, ScriptMetadata>? cache, bool isInclude, List<string> stack)
    {
        cache ??= new Dictionary<string, ScriptMetadata>();

        if (stack.Contains(file.Path))
        {
            throw new ScriptLoaderException($"Circular reference: \"{file.Path}\"", file.Path, stack.ToArray());
        }

        stack.Add(file.Path);

        var content = file.Content;
        var matches = IncludeRegex.Matches(content);

        foreach (Match match in matches)
        {
            var reference = match.Groups[2].Value;
            var includeFilename = IsPossiblyMappedPath(reference)
                ? ResolvePath(EnsureExtension(reference), stack.ToArray())
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file.Path)!, EnsureExtension(reference)));

            var includePaths = new List<string>();

            // For simplicity, we're not implementing glob patterns here
            // In a full implementation, you'd add glob support
            if (File.Exists(includeFilename) && Path.GetExtension(includeFilename) == ".lua")
            {
                includePaths.Add(includeFilename);
            }

            if (includePaths.Count == 0)
            {
                var pos = FindPosition(file.Content, match.Value);
                throw new ScriptLoaderException($"Include not found: \"{reference}\"", file.Path, stack.ToArray(), pos.Line, pos.Column);
            }

            var tokens = new List<string>();

            foreach (var includePath in includePaths)
            {
                var hasInclude = file.Includes.Any(x => x.Path == includePath);
                if (hasInclude)
                {
                    var pos = FindPosition(file.Content, match.Value);
                    throw new ScriptLoaderException($"File \"{reference}\" already included in \"{file.Path}\"", file.Path, stack.ToArray(), pos.Line, pos.Column);
                }

                string token;

                if (!cache.TryGetValue(includePath, out var includeMetadata))
                {
                    var (name, numberOfKeys) = SplitFilename(includePath);
                    string childContent;
                    try
                    {
                        childContent = await File.ReadAllTextAsync(includePath);
                    }
                    catch (FileNotFoundException)
                    {
                        var pos = FindPosition(file.Content, match.Value);
                        throw new ScriptLoaderException($"Include not found: \"{reference}\"", file.Path, stack.ToArray(), pos.Line, pos.Column);
                    }

                    token = GetPathHash(includePath);
                    includeMetadata = new ScriptMetadata
                    {
                        Name = name,
                        NumberOfKeys = numberOfKeys,
                        Path = includePath,
                        Content = childContent,
                        Token = token,
                        Includes = new List<ScriptMetadata>()
                    };
                    cache[includePath] = includeMetadata;
                }
                else
                {
                    token = includeMetadata.Token;
                }

                tokens.Add(token);
                file.Includes.Add(includeMetadata);
                await ResolveDependenciesAsync(includeMetadata, cache, true, stack);
            }

            // Replace @includes with normalized path hashes
            var substitution = string.Join("\n", tokens);
            content = content.Replace(match.Value, substitution);
        }

        file.Content = content;

        if (isInclude)
        {
            cache[file.Path] = file;
        }
        else
        {
            cache[file.Name] = file;
        }

        stack.RemoveAt(stack.Count - 1);
    }

    private static bool IsPossiblyMappedPath(string path)
    {
        return !string.IsNullOrEmpty(path) && (path[0] == '~' || path[0] == '<');
    }

    private static string EnsureExtension(string filename, string ext = "lua")
    {
        var foundExt = Path.GetExtension(filename);
        if (!string.IsNullOrEmpty(foundExt) && foundExt != ".")
        {
            return filename;
        }

        if (!string.IsNullOrEmpty(ext) && ext[0] != '.')
        {
            ext = $".{ext}";
        }

        return $"{filename}{ext}";
    }

    private static (string Name, int? NumberOfKeys) SplitFilename(string filePath)
    {
        var longName = Path.GetFileNameWithoutExtension(filePath);
        var parts = longName.Split('-');
        var name = parts[0];
        var numberOfKeys = parts.Length > 1 && int.TryParse(parts[1], out var num) ? num : (int?)null;
        return (name, numberOfKeys);
    }

    private static string GetProjectRoot()
    {
        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (directory != null)
        {
            // Look for solution files, project files, or other indicators
            if (Directory.GetFiles(directory.FullName, "*.sln").Length > 0 || 
                Directory.GetFiles(directory.FullName, "*.csproj").Length > 0 ||
                Directory.GetFiles(directory.FullName, "package.json").Length > 0)
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return AppDomain.CurrentDomain.BaseDirectory;
    }

    private static string Sha1(string data)
    {
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetPathHash(string normalizedPath)
    {
        return $"@@{Sha1(normalizedPath)}";
    }

    private static string RemoveEmptyLines(string str)
    {
        return EmptyLineRegex.Replace(str, string.Empty);
    }

    private static (int Line, int Column) FindPosition(string content, string match)
    {
        var pos = content.IndexOf(match, StringComparison.Ordinal);
        if (pos == -1) return (0, 0);
        
        var lines = content.Substring(0, pos).Split('\n');
        var line = lines.Length;
        var column = lines[^1].Length + match.IndexOf("@include", StringComparison.Ordinal) + 1;
        return (line, column);
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace RedisQ.SourceGenerators;

[Generator]
public class LuaScriptGenerator : IIncrementalGenerator
{
    private static readonly Regex IncludeRegex = new(@"^[-]{2,3}[ \t]*@include[ \t]+([""'])(.+?)\1[; \t\n]*$", RegexOptions.Multiline);
    private static readonly Regex EmptyLineRegex = new(@"^\s*[\r\n]", RegexOptions.Multiline);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Get all .lua files
        var luaFiles = context.AdditionalTextsProvider
            .Where(file => file.Path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Path.Contains("/exported/") && !file.Path.Contains("\\exported\\"));

        // Collect all lua files and their content
        var allLuaFiles = luaFiles.Collect();

        // Process scripts with access to all files
        var processedScripts = allLuaFiles
            .Select((files, ct) => ProcessAllLuaScripts(files, ct))
            .SelectMany((scripts, ct) => scripts);

        // Generate the expanded scripts
        context.RegisterSourceOutput(processedScripts, (srcContext, script) =>
        {
            if (script != null)
                GenerateExpandedScript(srcContext, script);
        });
    }

    private static IEnumerable<ProcessedScript?> ProcessAllLuaScripts(ImmutableArray<AdditionalText> files, CancellationToken cancellationToken)
    {
        // Create a lookup of file paths to content
        var fileContent = new Dictionary<string, string>();
        
        foreach (var file in files)
        {
            var content = file.GetText(cancellationToken)?.ToString();
            if (!string.IsNullOrEmpty(content))
            {
                fileContent[file.Path] = content;
            }
        }

        // Process each main lua file (not in includes directory)
        foreach (var file in files)
        {
            if (file.Path.Contains("/includes/") || file.Path.Contains("\\includes\\"))
                continue;

            var content = file.GetText(cancellationToken)?.ToString();
            
            if (string.IsNullOrEmpty(content))
                continue;

            var fileName = Path.GetFileName(file.Path);
            var directory = Path.GetDirectoryName(file.Path) ?? "";
            
            var (scriptName, numberOfKeys) = SplitFilename(fileName);
            
            // Process includes using the file lookup
            var expandedContent = ProcessIncludes(content!, directory, fileContent, new HashSet<string>());
            var cleanContent = RemoveEmptyLines(expandedContent);

            yield return new ProcessedScript
            {
                OriginalPath = file.Path,
                FileName = fileName,
                ScriptName = scriptName,
                NumberOfKeys = numberOfKeys,
                Content = cleanContent
            };
        }
    }

    private static string ProcessIncludes(string content, string baseDirectory, Dictionary<string, string> fileContent, HashSet<string> processedFiles)
    {
        var matches = IncludeRegex.Matches(content);
        
        foreach (Match match in matches)
        {
            var reference = match.Groups[2].Value;
            var includeFile = EnsureExtension(reference);
            
            string includePath;
            if (Path.IsPathRooted(includeFile))
            {
                includePath = includeFile;
            }
            else
            {
                includePath = Path.GetFullPath(Path.Combine(baseDirectory, includeFile));
            }

            // Normalize path separators for lookup
            var normalizedPath = includePath.Replace('\\', '/');
            var foundPath = fileContent.Keys.FirstOrDefault(k => k.Replace('\\', '/').EndsWith(normalizedPath.Replace(baseDirectory.Replace('\\', '/'), "").TrimStart('/')));
            
            if (foundPath == null)
            {
                // Try relative path from base directory
                var relativePath = Path.Combine(baseDirectory, includeFile).Replace('\\', '/');
                foundPath = fileContent.Keys.FirstOrDefault(k => k.Replace('\\', '/') == relativePath);
            }

            if (processedFiles.Contains(foundPath ?? includePath))
            {
                // Already processed, remove the include directive
                content = content.Replace(match.Value, "");
                continue;
            }

            if (foundPath != null && fileContent.TryGetValue(foundPath, out var includeContent))
            {
                processedFiles.Add(foundPath);
                var includeDirectory = Path.GetDirectoryName(foundPath) ?? "";
                
                // Recursively process includes in the included file
                var processedInclude = ProcessIncludes(includeContent, includeDirectory, fileContent, processedFiles);
                
                // Replace the include directive with the processed content
                content = content.Replace(match.Value, processedInclude);
            }
            else
            {
                // Include file not found, remove the directive
                content = content.Replace(match.Value, $"-- Include not found: {reference}");
            }
        }

        return content;
    }

    private static void GenerateExpandedScript(SourceProductionContext context, ProcessedScript script)
    {
        // Generate a C# class that contains the expanded Lua script as a string constant
        var className = $"LuaScript_{script.ScriptName}";
        
        // Use verbatim string literals to avoid escaping issues
        var luaContent = script.Content.Replace("\"", "\"\""); // Escape quotes for verbatim string
        
        var sourceCode = $@"// <auto-generated />
namespace RedisQ.Core.Generated
{{
    public static class {className}
    {{
        public const string Content = @""{luaContent}"";
        public const string Name = ""{script.ScriptName}"";
        public const int NumberOfKeys = {script.NumberOfKeys ?? 0};
    }}
}}";

        var hintName = $"{className}.g.cs";
        var sourceText = SourceText.From(sourceCode, Encoding.UTF8);
        
        context.AddSource(hintName, sourceText);
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

    private static string RemoveEmptyLines(string str)
    {
        return EmptyLineRegex.Replace(str, string.Empty);
    }

    private class ProcessedScript
    {
        public string OriginalPath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string ScriptName { get; set; } = string.Empty;
        public int? NumberOfKeys { get; set; }
        public string Content { get; set; } = string.Empty;
    }
}

using Microsoft.Build.Framework;
using System.Text.RegularExpressions;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace RedisQ.Core.BuildTasks;

public partial class ExpandLuaScriptsTask : MSBuildTask
{
    [Required]
    public string ScriptsDirectory { get; set; } = string.Empty;

    [Required]
    public string OutputDirectory { get; set; } = string.Empty;

    private static readonly Regex IncludeRegex = MyRegex();

    public override bool Execute()
    {
        try
        {
            Log.LogMessage(MessageImportance.Normal, $"Expanding Lua scripts from {ScriptsDirectory} to {OutputDirectory}");

            if (!Directory.Exists(ScriptsDirectory))
            {
                Log.LogError($"Scripts directory does not exist: {ScriptsDirectory}");
                return false;
            }

            // Create output directory if it doesn't exist
            Directory.CreateDirectory(OutputDirectory);

            // Find all Lua script files (not in includes directory)
            var luaFiles = Directory.GetFiles(ScriptsDirectory, "*.lua", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/includes/") && !f.Contains("\\includes\\"))
                .Where(f => !Path.GetFileName(f).StartsWith("_")); // Skip include files that start with _

            foreach (var luaFile in luaFiles)
            {
                try
                {
                    Log.LogMessage(MessageImportance.Normal, $"Processing script: {luaFile}");

                    // Load and expand the script
                    var expandedContent = LoadAndExpandScript(luaFile);
                    
                    // Get the relative path to preserve directory structure
                    var relativePath = Path.GetRelativePath(ScriptsDirectory, luaFile);
                    var outputPath = Path.Combine(OutputDirectory, relativePath);
                    var outputFileName = Path.ChangeExtension(outputPath, ".expanded.lua");

                    // Ensure output directory exists
                    var outputDir = Path.GetDirectoryName(outputFileName);
                    if (!string.IsNullOrEmpty(outputDir))
                    {
                        Directory.CreateDirectory(outputDir);
                    }

                    // Write the expanded script
                    File.WriteAllText(outputFileName, expandedContent);
                    
                    Log.LogMessage(MessageImportance.Normal, $"Expanded script written to: {outputFileName}");
                }
                catch (Exception ex)
                {
                    Log.LogError($"Failed to process script {luaFile}: {ex.Message}");
                    return false;
                }
            }

            Log.LogMessage(MessageImportance.High, "Lua script expansion completed successfully");
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError($"Lua script expansion failed: {ex.Message}");
            return false;
        }
    }

    private string LoadAndExpandScript(string filename)
    {
        // Read the file content
        var content = File.ReadAllText(filename);
        
        // Process includes
        var processedFiles = new HashSet<string>();
        var baseDirectory = Path.GetDirectoryName(filename) ?? "";
        
        return ProcessIncludes(content, baseDirectory, processedFiles);
    }

    private string ProcessIncludes(string content, string baseDirectory, HashSet<string> processedFiles)
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

            if (processedFiles.Contains(includePath))
            {
                // Already processed, remove the include directive
                content = content.Replace(match.Value, "");
                continue;
            }

            if (File.Exists(includePath))
            {
                processedFiles.Add(includePath);
                var includeDirectory = Path.GetDirectoryName(includePath) ?? "";
                
                // Read and recursively process includes in the included file
                var includeContent = File.ReadAllText(includePath);
                var processedInclude = ProcessIncludes(includeContent, includeDirectory, processedFiles);
                
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

    [GeneratedRegex(@"^[-]{2,4}[ \t]*@include[ \t]+([""'])(.+?)\1[; \t\n]*$", RegexOptions.Multiline)]
    private static partial Regex MyRegex();
}
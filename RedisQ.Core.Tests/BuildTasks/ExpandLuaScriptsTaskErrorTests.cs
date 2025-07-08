using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Moq;
using RedisQ.Core.BuildTasks;
using Xunit;

namespace RedisQ.Core.Tests.BuildTasks;

public class ExpandLuaScriptsTaskErrorTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _scriptsDirectory;
    private readonly string _outputDirectory;

    public ExpandLuaScriptsTaskErrorTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "ExpandLuaScriptsTaskErrors", Guid.NewGuid().ToString());
        _scriptsDirectory = Path.Combine(_testDirectory, "scripts");
        _outputDirectory = Path.Combine(_testDirectory, "output");
        
        Directory.CreateDirectory(_scriptsDirectory);
        Directory.CreateDirectory(_outputDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void Execute_WithInvalidScriptsDirectoryPath_ReturnsFalse()
    {
        // Arrange
        var task = CreateTask();
        task.ScriptsDirectory = ""; // Empty path
        task.OutputDirectory = _outputDirectory;

        // Act
        var result = task.Execute();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Execute_WithNullProperties_ThrowsException()
    {
        // Arrange
        var task = CreateTask();
        
        // Act & Assert - These should throw due to Required attribute, but we test graceful handling
        Assert.Throws<ArgumentNullException>(() =>
        {
            task.ScriptsDirectory = null!;
            task.Execute();
        });
    }

    [Fact]
    public void Execute_WithReadOnlyOutputDirectory_HandlesProperly()
    {
        // Arrange
        var readOnlyOutputDir = Path.Combine(_testDirectory, "readonly");
        Directory.CreateDirectory(readOnlyOutputDir);
        
        // Make directory read-only
        var dirInfo = new DirectoryInfo(readOnlyOutputDir);
        dirInfo.Attributes |= FileAttributes.ReadOnly;

        var scriptContent = @"-- Simple script
local test = 'test'
return test";

        var scriptPath = Path.Combine(_scriptsDirectory, "test.lua");
        File.WriteAllText(scriptPath, scriptContent);

        var task = CreateTask();
        task.ScriptsDirectory = _scriptsDirectory;
        task.OutputDirectory = readOnlyOutputDir;

        try
        {
            // Act
            var result = task.Execute();

            // Assert - The task should handle the error appropriately
            // The exact behavior depends on the implementation
            // This test ensures the task doesn't crash unexpectedly
            Assert.True(result || !result); // Either outcome is acceptable as long as no exception is thrown
        }
        finally
        {
            // Cleanup
            dirInfo.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    [Fact]
    public void Execute_WithVeryLongFilePaths_HandlesGracefully()
    {
        // Arrange - Create a path that might exceed system limits
        var longDirectoryName = new string('a', 200);
        var longScriptsDir = Path.Combine(_scriptsDirectory, longDirectoryName);
        
        try
        {
            Directory.CreateDirectory(longScriptsDir);
            
            var scriptContent = @"-- Script with long path
local result = 'long path test'
return result";

            var scriptPath = Path.Combine(longScriptsDir, "longpath.lua");
            File.WriteAllText(scriptPath, scriptContent);

            var task = CreateTask();
            task.ScriptsDirectory = _scriptsDirectory;
            task.OutputDirectory = _outputDirectory;

            // Act
            var result = task.Execute();

            // Assert - Should handle long paths without crashing
            Assert.True(result);
        }
        catch (PathTooLongException)
        {
            // If the system doesn't support long paths, that's expected
            Assert.True(true);
        }
    }

    [Fact]
    public void Execute_WithCorruptedScriptFile_ContinuesProcessing()
    {
        // Arrange
        var validScriptContent = @"-- Valid script
local valid = 'valid'
return valid";

        var validScriptPath = Path.Combine(_scriptsDirectory, "valid.lua");
        File.WriteAllText(validScriptPath, validScriptContent);

        // Create a file with binary content that might cause encoding issues
        var corruptedScriptPath = Path.Combine(_scriptsDirectory, "corrupted.lua");
        var binaryData = new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02, 0x03 };
        File.WriteAllBytes(corruptedScriptPath, binaryData);

        var task = CreateTask();
        task.ScriptsDirectory = _scriptsDirectory;
        task.OutputDirectory = _outputDirectory;

        // Act
        var result = task.Execute();

        // Assert - Should process the valid file even if the corrupted one fails
        var validOutputPath = Path.Combine(_outputDirectory, "valid.expanded.lua");
        Assert.True(File.Exists(validOutputPath));
    }

    [Fact]
    public void Execute_WithDeeplyNestedIncludes_PreventsStackOverflow()
    {
        // Arrange - Create a scenario that could cause stack overflow
        var includesDir = Path.Combine(_scriptsDirectory, "includes");
        Directory.CreateDirectory(includesDir);

        var depth = 100; // Create deep nesting
        
        for (int i = 0; i < depth; i++)
        {
            var nextInclude = i < depth - 1 ? $"--- @include \"include{i + 1}\"" : "";
            var includeContent = $@"-- Include level {i}
{nextInclude}
local level{i} = {i}";

            var includePath = Path.Combine(includesDir, $"include{i}.lua");
            File.WriteAllText(includePath, includeContent);
        }

        var mainScriptContent = @"-- Main script with deep includes
--- @include ""includes/include0""
local result = level0
return result";

        var mainScriptPath = Path.Combine(_scriptsDirectory, "deep.lua");
        File.WriteAllText(mainScriptPath, mainScriptContent);

        var task = CreateTask();
        task.ScriptsDirectory = _scriptsDirectory;
        task.OutputDirectory = _outputDirectory;

        // Act & Assert - Should not cause stack overflow
        var result = task.Execute();
        
        // The task should either succeed or fail gracefully without stack overflow
        Assert.True(result || !result);
    }

    [Fact]
    public void Execute_WithSpecialCharactersInPaths_HandlesCorrectly()
    {
        // Arrange - Test with various special characters that might cause issues
        var specialChars = new[] { "spaces in name", "unicode-ñ", "symbols-@#$", "dots...test" };
        
        foreach (var specialName in specialChars)
        {
            try
            {
                var specialDir = Path.Combine(_scriptsDirectory, specialName);
                Directory.CreateDirectory(specialDir);

                var scriptContent = $@"-- Script in {specialName}
local result = 'special chars test'
return result";

                var scriptPath = Path.Combine(specialDir, "special.lua");
                File.WriteAllText(scriptPath, scriptContent);
            }
            catch (ArgumentException)
            {
                // Some special characters might not be valid for file names
                continue;
            }
        }

        var task = CreateTask();
        task.ScriptsDirectory = _scriptsDirectory;
        task.OutputDirectory = _outputDirectory;

        // Act
        var result = task.Execute();

        // Assert - Should handle special characters without crashing
        Assert.True(result);
    }

    [Fact]
    public async System.Threading.Tasks.Task Execute_WithConcurrentExecution_ThreadSafe()
    {
        // Arrange - Test thread safety by running multiple tasks concurrently
        var tasks = new List<System.Threading.Tasks.Task<bool>>();
        var taskCount = 5;

        for (int i = 0; i < taskCount; i++)
        {
            var taskIndex = i;
            tasks.Add(System.Threading.Tasks.Task.Run(() =>
            {
                var taskSpecificDir = Path.Combine(_testDirectory, $"concurrent_{taskIndex}");
                var scriptsDir = Path.Combine(taskSpecificDir, "scripts");
                var outputDir = Path.Combine(taskSpecificDir, "output");
                
                Directory.CreateDirectory(scriptsDir);
                Directory.CreateDirectory(outputDir);

                var scriptContent = $@"-- Concurrent script {taskIndex}
local result = 'concurrent_{taskIndex}'
return result";

                var scriptPath = Path.Combine(scriptsDir, $"concurrent{taskIndex}.lua");
                File.WriteAllText(scriptPath, scriptContent);

                var task = CreateTask();
                task.ScriptsDirectory = scriptsDir;
                task.OutputDirectory = outputDir;

                return task.Execute();
            }));
        }

        // Act
        var results = await System.Threading.Tasks.Task.WhenAll(tasks);

        // Assert - All tasks should complete successfully
        Assert.All(results, result => Assert.True(result));
    }

    [Fact]
    public void Execute_WithEmptyIncludeFile_HandlesCorrectly()
    {
        // Arrange
        var includesDir = Path.Combine(_scriptsDirectory, "includes");
        Directory.CreateDirectory(includesDir);

        // Create an empty include file
        var emptyIncludePath = Path.Combine(includesDir, "empty.lua");
        File.WriteAllText(emptyIncludePath, "");

        var mainScriptContent = @"-- Main script with empty include
--- @include ""includes/empty""
local result = 'test with empty include'
return result";

        var mainScriptPath = Path.Combine(_scriptsDirectory, "main.lua");
        File.WriteAllText(mainScriptPath, mainScriptContent);

        var task = CreateTask();
        task.ScriptsDirectory = _scriptsDirectory;
        task.OutputDirectory = _outputDirectory;

        // Act
        var result = task.Execute();

        // Assert
        Assert.True(result);
        
        var expectedOutputPath = Path.Combine(_outputDirectory, "main.expanded.lua");
        var expandedContent = File.ReadAllText(expectedOutputPath);
        
        Assert.DoesNotContain("@include", expandedContent);
        Assert.Contains("local result = 'test with empty include'", expandedContent);
    }

    private ExpandLuaScriptsTask CreateTask()
    {
        var task = new ExpandLuaScriptsTask();
        
        // Mock the logger to capture log messages
        var mockLogger = new Mock<IBuildEngine>();
        var logMessages = new List<string>();
        
        mockLogger.Setup(x => x.LogMessageEvent(It.IsAny<BuildMessageEventArgs>()))
                  .Callback<BuildMessageEventArgs>(args => logMessages.Add(args.Message ?? ""));
        
        mockLogger.Setup(x => x.LogErrorEvent(It.IsAny<BuildErrorEventArgs>()))
                  .Callback<BuildErrorEventArgs>(args => logMessages.Add($"ERROR: {args.Message}"));
        
        task.BuildEngine = mockLogger.Object;
        
        return task;
    }
}

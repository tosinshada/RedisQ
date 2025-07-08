using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Moq;
using RedisQ.Core.BuildTasks;
using System.Text;
using Xunit;

namespace RedisQ.Core.Tests.BuildTasks;

public class ExpandLuaScriptsTaskTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _scriptsDirectory;
    private readonly string _outputDirectory;

    public ExpandLuaScriptsTaskTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "ExpandLuaScriptsTaskTests", Guid.NewGuid().ToString());
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
    public void Execute_WithNonExistentScriptsDirectory_ReturnsFalse()
    {
        // Arrange
        var task = CreateTask();
        task.ScriptsDirectory = Path.Combine(_testDirectory, "nonexistent");
        task.OutputDirectory = _outputDirectory;

        // Act
        var result = task.Execute();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Execute_WithEmptyScriptsDirectory_ReturnsTrue()
    {
        // Arrange
        var task = CreateTask();
        task.ScriptsDirectory = _scriptsDirectory;
        task.OutputDirectory = _outputDirectory;

        // Act
        var result = task.Execute();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void Execute_WithSimpleLuaScript_CreatesExpandedFile()
    {
        // Arrange
        var scriptContent = @"-- Simple Lua script
local jobId = ARGV[1]
return jobId";

        var scriptPath = Path.Combine(_scriptsDirectory, "simple.lua");
        File.WriteAllText(scriptPath, scriptContent);

        var task = CreateTask();
        task.ScriptsDirectory = _scriptsDirectory;
        task.OutputDirectory = _outputDirectory;

        // Act
        var result = task.Execute();

        // Assert
        Assert.True(result);
        
        var expectedOutputPath = Path.Combine(_outputDirectory, "simple.expanded.lua");
        Assert.True(File.Exists(expectedOutputPath));
        
        var expandedContent = File.ReadAllText(expectedOutputPath);
        Assert.Equal(scriptContent, expandedContent);
    }

    [Fact]
    public void Execute_WithScriptContainingInclude_ExpandsInclude()
    {
        // Arrange
        var includesDir = Path.Combine(_scriptsDirectory, "includes");
        Directory.CreateDirectory(includesDir);

        var includeContent = @"-- Include file content
local function helperFunction()
    return 'helper'
end";

        var includePath = Path.Combine(includesDir, "helper.lua");
        File.WriteAllText(includePath, includeContent);

        var mainScriptContent = @"-- Main script
--- @include ""includes/helper""
local result = helperFunction()
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
        Assert.True(File.Exists(expectedOutputPath));
        
        var expandedContent = File.ReadAllText(expectedOutputPath);
        Assert.Contains("helper", expandedContent);
        Assert.Contains("local function helperFunction()", expandedContent);
        Assert.DoesNotContain("@include", expandedContent);
    }

    [Fact]
    public void Execute_WithNestedIncludes_ExpandsAllIncludes()
    {
        // Arrange
        var includesDir = Path.Combine(_scriptsDirectory, "includes");
        Directory.CreateDirectory(includesDir);

        // Create nested include files
        var baseIncludeContent = @"-- Base include
local baseValue = 'base'";

        var baseIncludePath = Path.Combine(includesDir, "base.lua");
        File.WriteAllText(baseIncludePath, baseIncludeContent);

        var helperIncludeContent = @"-- Helper include
--- @include ""base""
local function helperFunction()
    return baseValue .. '_helper'
end";

        var helperIncludePath = Path.Combine(includesDir, "helper.lua");
        File.WriteAllText(helperIncludePath, helperIncludeContent);

        var mainScriptContent = @"-- Main script
--- @include ""includes/helper""
local result = helperFunction()
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
        
        Assert.Contains("local baseValue = 'base'", expandedContent);
        Assert.Contains("local function helperFunction()", expandedContent);
        Assert.DoesNotContain("@include", expandedContent);
    }

    [Fact]
    public void Execute_WithMissingInclude_AddsCommentForMissingFile()
    {
        // Arrange
        var mainScriptContent = @"-- Main script
--- @include ""includes/missing""
local result = 'test'
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
        
        Assert.Contains("-- Include not found: includes/missing", expandedContent);
        Assert.DoesNotContain("@include", expandedContent);
    }

    [Fact]
    public void Execute_WithCircularIncludes_HandlesDuplicatesCorrectly()
    {
        // Arrange
        var includesDir = Path.Combine(_scriptsDirectory, "includes");
        Directory.CreateDirectory(includesDir);

        var firstIncludeContent = @"-- First include
--- @include ""second""
local firstValue = 'first'";

        var firstIncludePath = Path.Combine(includesDir, "first.lua");
        File.WriteAllText(firstIncludePath, firstIncludeContent);

        var secondIncludeContent = @"-- Second include
--- @include ""first""
local secondValue = 'second'";

        var secondIncludePath = Path.Combine(includesDir, "second.lua");
        File.WriteAllText(secondIncludePath, secondIncludeContent);

        var mainScriptContent = @"-- Main script
--- @include ""includes/first""
--- @include ""includes/second""
local result = 'test'
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
        
        // Should contain both includes but handle circularity
        Assert.Contains("firstValue", expandedContent);
        Assert.Contains("secondValue", expandedContent);
    }

    [Fact]
    public void Execute_SkipsIncludeFilesInIncludesDirectory()
    {
        // Arrange
        var includesDir = Path.Combine(_scriptsDirectory, "includes");
        Directory.CreateDirectory(includesDir);

        var includeContent = @"-- This should not be processed as a main script
local helperValue = 'helper'";

        var includePath = Path.Combine(includesDir, "helper.lua");
        File.WriteAllText(includePath, includeContent);

        var mainScriptContent = @"-- Main script
local result = 'test'
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
        
        // Should only have expanded main.lua, not the include file
        var mainExpandedPath = Path.Combine(_outputDirectory, "main.expanded.lua");
        var includeExpandedPath = Path.Combine(_outputDirectory, "includes", "helper.expanded.lua");
        
        Assert.True(File.Exists(mainExpandedPath));
        Assert.False(File.Exists(includeExpandedPath));
    }

    [Fact]
    public void Execute_SkipsFilesStartingWithUnderscore()
    {
        // Arrange
        var underscoreContent = @"-- This should not be processed
local privateHelper = 'private'";

        var underscorePath = Path.Combine(_scriptsDirectory, "_private.lua");
        File.WriteAllText(underscorePath, underscoreContent);

        var mainScriptContent = @"-- Main script
local result = 'test'
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
        
        // Should only have expanded main.lua, not the underscore file
        var mainExpandedPath = Path.Combine(_outputDirectory, "main.expanded.lua");
        var underscoreExpandedPath = Path.Combine(_outputDirectory, "_private.expanded.lua");
        
        Assert.True(File.Exists(mainExpandedPath));
        Assert.False(File.Exists(underscoreExpandedPath));
    }

    [Fact]
    public void Execute_PreservesDirectoryStructure()
    {
        // Arrange
        var subDir = Path.Combine(_scriptsDirectory, "subdirectory");
        Directory.CreateDirectory(subDir);

        var subScriptContent = @"-- Sub directory script
local subResult = 'sub'
return subResult";

        var subScriptPath = Path.Combine(subDir, "sub.lua");
        File.WriteAllText(subScriptPath, subScriptContent);

        var task = CreateTask();
        task.ScriptsDirectory = _scriptsDirectory;
        task.OutputDirectory = _outputDirectory;

        // Act
        var result = task.Execute();

        // Assert
        Assert.True(result);
        
        var expectedOutputPath = Path.Combine(_outputDirectory, "subdirectory", "sub.expanded.lua");
        Assert.True(File.Exists(expectedOutputPath));
        
        var expandedContent = File.ReadAllText(expectedOutputPath);
        Assert.Equal(subScriptContent, expandedContent);
    }

    [Fact]
    public void Execute_WithDifferentIncludeQuoteStyles_HandlesAllStyles()
    {
        // Arrange
        var includesDir = Path.Combine(_scriptsDirectory, "includes");
        Directory.CreateDirectory(includesDir);

        var includeContent = @"-- Include content
local includeValue = 'included'";

        var includePath = Path.Combine(includesDir, "helper.lua");
        File.WriteAllText(includePath, includeContent);

        var mainScriptContent = @"-- Main script with different quote styles
--- @include ""includes/helper""
-- @include 'includes/helper'
local result = includeValue
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
        
        Assert.Contains("local includeValue = 'included'", expandedContent);
        Assert.DoesNotContain("@include", expandedContent);
    }

    [Fact]
    public void EnsureExtension_WithNoExtension_AddsLuaExtension()
    {
        // This tests the private EnsureExtension method indirectly through the include functionality
        // Arrange
        var includesDir = Path.Combine(_scriptsDirectory, "includes");
        Directory.CreateDirectory(includesDir);

        var includeContent = @"-- Include without extension
local value = 'test'";

        var includePath = Path.Combine(includesDir, "noext.lua");
        File.WriteAllText(includePath, includeContent);

        var mainScriptContent = @"-- Main script
--- @include ""includes/noext""
local result = value
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
        
        Assert.Contains("local value = 'test'", expandedContent);
    }

    [Fact]
    public void Execute_WithMultipleScripts_ProcessesAllScripts()
    {
        // Arrange
        var script1Content = @"-- First script
local first = 'first'
return first";

        var script2Content = @"-- Second script  
local second = 'second'
return second";

        var script1Path = Path.Combine(_scriptsDirectory, "script1.lua");
        var script2Path = Path.Combine(_scriptsDirectory, "script2.lua");
        
        File.WriteAllText(script1Path, script1Content);
        File.WriteAllText(script2Path, script2Content);

        var task = CreateTask();
        task.ScriptsDirectory = _scriptsDirectory;
        task.OutputDirectory = _outputDirectory;

        // Act
        var result = task.Execute();

        // Assert
        Assert.True(result);
        
        var output1Path = Path.Combine(_outputDirectory, "script1.expanded.lua");
        var output2Path = Path.Combine(_outputDirectory, "script2.expanded.lua");
        
        Assert.True(File.Exists(output1Path));
        Assert.True(File.Exists(output2Path));
        
        Assert.Equal(script1Content, File.ReadAllText(output1Path));
        Assert.Equal(script2Content, File.ReadAllText(output2Path));
    }

    [Fact]
    public void Execute_WithAbsoluteIncludePath_ProcessesCorrectly()
    {
        // Arrange
        var includesDir = Path.Combine(_scriptsDirectory, "includes");
        Directory.CreateDirectory(includesDir);

        var includeContent = @"-- Absolute path include
local absoluteValue = 'absolute'";

        var includePath = Path.Combine(includesDir, "absolute.lua");
        File.WriteAllText(includePath, includeContent);

        var mainScriptContent = $@"-- Main script with absolute path
--- @include ""{includePath}""
local result = absoluteValue
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
        
        Assert.Contains("local absoluteValue = 'absolute'", expandedContent);
    }

    private ExpandLuaScriptsTask CreateTask()
    {
        var task = new ExpandLuaScriptsTask();
        
        // Mock the logger to prevent null reference exceptions
        var mockLogger = new Mock<IBuildEngine>();
        task.BuildEngine = mockLogger.Object;
        
        return task;
    }
}

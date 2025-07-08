using System.Text.RegularExpressions;
using Xunit;

namespace RedisQ.Core.Tests.BuildTasks;

public class LuaIncludeRegexTests
{
    // We need to test the regex pattern indirectly since MyRegex() is private
    // This recreates the same regex pattern for testing purposes
    private static readonly Regex TestIncludeRegex = new(@"^[-]{2,4}[ \t]*@include[ \t]+([""'])(.+?)\1[; \t\n]*$", RegexOptions.Multiline);

    [Theory]
    [InlineData("--- @include \"helper\"", true, "helper")]
    [InlineData("-- @include \"helper\"", true, "helper")]
    [InlineData("--- @include 'helper'", true, "helper")]
    [InlineData("-- @include 'helper'", true, "helper")]
    [InlineData("--- @include \"includes/helper\"", true, "includes/helper")]
    [InlineData("--- @include \"includes/helper.lua\"", true, "includes/helper.lua")]
    [InlineData("--- @include \"../includes/helper\"", true, "../includes/helper")]
    [InlineData("---  @include  \"helper\"  ", true, "helper")]
    [InlineData("--  @include  'helper'  ", true, "helper")]
    [InlineData("--- @include \"helper\";", true, "helper")]
    [InlineData("--- @include \"helper\"\n", true, "helper")]
    [InlineData("---- @include \"helper\"", true, "helper")]
    public void IncludeRegex_WithValidIncludeDirectives_MatchesCorrectly(string input, bool shouldMatch, string expectedReference)
    {
        // Act
        var match = TestIncludeRegex.Match(input);

        // Assert
        Assert.Equal(shouldMatch, match.Success);
        
        if (shouldMatch && match.Success)
        {
            Assert.Equal(expectedReference, match.Groups[2].Value);
        }
    }

    [Theory]
    [InlineData("- @include \"helper\"")]  // Only one dash
    [InlineData("@include \"helper\"")]    // No dashes
    [InlineData("--- include \"helper\"")]  // Missing @
    [InlineData("--- @include helper")]     // Missing quotes
    [InlineData("    --- @include \"helper\"")]  // Leading spaces
    [InlineData("code --- @include \"helper\"")]  // Not at line start
    [InlineData("--- @include \"helper")]   // Missing closing quote
    [InlineData("--- @include 'helper\"")]  // Mismatched quotes
    public void IncludeRegex_WithInvalidIncludeDirectives_DoesNotMatch(string input)
    {
        // Act
        var match = TestIncludeRegex.Match(input);

        // Assert
        Assert.False(match.Success);
    }

    [Fact]
    public void IncludeRegex_WithMultipleIncludesInText_MatchesAll()
    {
        // Arrange
        var input = @"-- Script with multiple includes
--- @include ""first""
local firstFunction = function() end

--- @include 'second'
local secondFunction = function() end

-- Some other code
--- @include ""third""";

        // Act
        var matches = TestIncludeRegex.Matches(input);

        // Assert
        Assert.Equal(3, matches.Count);
        Assert.Equal("first", matches[0].Groups[2].Value);
        Assert.Equal("second", matches[1].Groups[2].Value);
        Assert.Equal("third", matches[2].Groups[2].Value);
    }

    [Fact]
    public void IncludeRegex_WithComplexFilePaths_MatchesCorrectly()
    {
        // Arrange
        var testCases = new[]
        {
            ("--- @include \"includes/helpers/utility.lua\"", "includes/helpers/utility.lua"),
            ("--- @include 'modules/core/base'", "modules/core/base"),
            ("--- @include \"../../shared/common\"", "../../shared/common"),
            ("--- @include '/absolute/path/script'", "/absolute/path/script"),
            ("--- @include \"file with spaces\"", "file with spaces"),
            ("--- @include 'under_score_file'", "under_score_file"),
            ("--- @include \"kebab-case-file\"", "kebab-case-file"),
            ("--- @include 'file.with.dots'", "file.with.dots")
        };

        foreach (var (input, expectedPath) in testCases)
        {
            // Act
            var match = TestIncludeRegex.Match(input);

            // Assert
            Assert.True(match.Success, $"Failed to match: {input}");
            Assert.Equal(expectedPath, match.Groups[2].Value);
        }
    }

    [Fact]
    public void IncludeRegex_WithVariousWhitespacePatterns_MatchesCorrectly()
    {
        // Arrange
        var testCases = new[]
        {
            "--- @include \"file\"",         // Single spaces
            "---  @include  \"file\"",       // Multiple spaces
            "---\t@include\t\"file\"",       // Tabs
            "--- \t@include \t\"file\"",     // Mixed spaces and tabs
            "---   @include   \"file\"   ",  // Trailing spaces
        };

        foreach (var input in testCases)
        {
            // Act
            var match = TestIncludeRegex.Match(input);

            // Assert
            Assert.True(match.Success, $"Failed to match: {input}");
            Assert.Equal("file", match.Groups[2].Value);
        }
    }
}

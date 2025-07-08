# RedisQ.Core.Tests

This project contains comprehensive tests for the RedisQ.Core library, specifically focusing on the `ExpandLuaScriptsTask` build task.

## Test Coverage

### ExpandLuaScriptsTaskTests
Basic functionality tests covering:
- Empty scripts directory handling
- Simple Lua script processing
- Include directive expansion
- Nested includes processing
- Missing include file handling
- Circular include detection
- Directory structure preservation
- Multiple scripts processing
- Files starting with underscore (skipped)
- Files in includes directory (skipped)
- Different quote styles in include directives
- Absolute path includes

### LuaIncludeRegexTests
Regex pattern validation tests covering:
- Valid include directive patterns
- Invalid include directive patterns
- Multiple includes in single file
- Complex file paths
- Various whitespace patterns
- Quote style variations

### ExpandLuaScriptsTaskIntegrationTests
Real-world scenario tests covering:
- RedisQ-style Lua script expansion
- Complex include hierarchies
- Error handling during processing
- Performance with large numbers of includes

### ExpandLuaScriptsTaskErrorTests
Error condition and edge case tests covering:
- Invalid directory paths
- Read-only directories
- Very long file paths
- Corrupted script files
- Deeply nested includes (stack overflow prevention)
- Special characters in paths
- Concurrent execution (thread safety)
- Empty include files

## Running the Tests

To run all tests:

```bash
dotnet test RedisQ.Core.Tests
```

To run specific test classes:

```bash
dotnet test RedisQ.Core.Tests --filter "ClassName=ExpandLuaScriptsTaskTests"
```

To run with coverage:

```bash
dotnet test RedisQ.Core.Tests --collect:"XPlat Code Coverage"
```

## Test Structure

The tests are organized into logical groupings:

1. **Basic Functionality** - Core features work as expected
2. **Regex Validation** - The include pattern matching works correctly
3. **Integration Scenarios** - Real-world usage patterns
4. **Error Handling** - Edge cases and error conditions

Each test uses temporary directories that are automatically cleaned up, ensuring tests don't interfere with each other or leave artifacts on the system.

## Key Test Scenarios

### Include Processing
- Tests verify that `@include` directives are properly expanded
- Include files are embedded into the main script
- Include directives are removed from the final output
- Circular includes are handled without infinite loops
- Missing includes result in appropriate comments

### File Discovery
- Only `.lua` files outside of `includes/` directories are processed
- Files starting with `_` are skipped
- Directory structure is preserved in output
- Multiple scripts are processed independently

### Error Resilience
- Invalid paths don't crash the task
- Corrupted files don't prevent other files from processing
- Deeply nested includes don't cause stack overflow
- Concurrent execution is thread-safe

## Dependencies

- xUnit for testing framework
- Moq for mocking dependencies
- Microsoft.Build.* for MSBuild task testing
- .NET 8 runtime

## Notes

The tests mock the MSBuild logger to prevent dependency on the actual build system during testing. This allows the tests to run independently and focus on the logic of the `ExpandLuaScriptsTask` itself.

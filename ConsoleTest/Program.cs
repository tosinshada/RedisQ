using RedisQ.Core.Loader;

var scriptLoader = new ScriptLoader();

// Determine the correct commands directory
var commandsDir = args.Length > 0 ? args[0] : (Directory.Exists("Commands") ? "Commands" : "ConsoleTest/Commands");

// Test the LoadScriptsAsync method which now exports expanded scripts
await scriptLoader.LoadScriptsAsync(commandsDir);

Console.WriteLine($"\nExpanded scripts have been saved to {commandsDir}/exported/");
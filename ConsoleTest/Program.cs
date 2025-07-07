using RedisQ.Core.Loader;

var scriptLoader = new ScriptLoader();
var script = await scriptLoader.LoadCommandAsync("Commands/addStandardJob.lua");

Console.WriteLine("Script Name: " + script.Name);
Console.WriteLine("Script Content: " + script.Options.Lua);
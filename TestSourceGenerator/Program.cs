using RedisQ.Core.Generated;

Console.WriteLine("Testing source generator...");

try 
{
    Console.WriteLine($"Script: {LuaScript_addStandardJob.Name}");
    Console.WriteLine($"Keys: {LuaScript_addStandardJob.NumberOfKeys}");
    Console.WriteLine($"Content length: {LuaScript_addStandardJob.Content.Length}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine("Generated class not found - source generator may not be running");
}

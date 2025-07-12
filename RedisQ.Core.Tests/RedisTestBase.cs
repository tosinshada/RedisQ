using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace RedisQ.Core.Tests;

public abstract class RedisTestBase : IAsyncLifetime
{
    private RedisContainer? _redisContainer;
    protected IDatabase Database { get; private set; } = null!;
    protected IConnectionMultiplexer Connection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _redisContainer = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .WithPortBinding(0, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(6379))
            .Build();

        await _redisContainer.StartAsync();
        
        var connectionString = _redisContainer.GetConnectionString();
        Connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        Database = Connection.GetDatabase();
    }

    public async Task DisposeAsync()
    {
        Connection?.Dispose();
        if (_redisContainer != null)
        {
            await _redisContainer.StopAsync();
            await _redisContainer.DisposeAsync();
        }
    }
}

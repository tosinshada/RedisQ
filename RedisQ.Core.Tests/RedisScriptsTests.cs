using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace RedisQ.Core.Tests;

public class RedisScriptsTests : RedisTestBase
{
    private const string TestPrefix = "test";
    private const string TestQueueName = "testqueue";

    private RedisScripts CreateRedisScripts()
    {
        return new RedisScripts(TestPrefix, TestQueueName, Database);
    }

    private static Job CreateTestJob(string? id = null, string name = "TestJob", object? data = null)
    {
        return new Job
        {
            Id = id,
            Name = name,
            Data = data ?? new { message = "test data" },
            Options = new Dictionary<string, object>(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Attempts = 3,
            AttemptsMade = 0
        };
    }

    [Fact]
    public void Constructor_ShouldInitializeCorrectly()
    {
        // Act
        var redisScripts = CreateRedisScripts();

        // Assert
        redisScripts.Should().NotBeNull();
    }

    [Fact]
    public void GetKeys_ShouldReturnCorrectKeys()
    {
        // Arrange
        var redisScripts = CreateRedisScripts();

        // Act
        var keys = redisScripts.GetKeys("wait", "active", "completed");

        // Assert
        keys.Should().HaveCount(3);
        keys[0].ToString().Should().Be($"{TestPrefix}:{TestQueueName}:wait");
        keys[1].ToString().Should().Be($"{TestPrefix}:{TestQueueName}:active");
        keys[2].ToString().Should().Be($"{TestPrefix}:{TestQueueName}:completed");
    }

    [Fact]
    public async Task AddStandardJobAsync_WithoutCustomId_ShouldGenerateJobId()
    {
        // Arrange
        var redisScripts = CreateRedisScripts();
        var job = CreateTestJob();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        var result = await redisScripts.AddStandardJobAsync(job, timestamp);

        // Assert
        result.Should().NotBeNull();
        var jobId = result.ToString();
        jobId.Should().NotBeNullOrEmpty();
        jobId.Should().MatchRegex(@"^\d+$"); // Should be a numeric ID
    }

    [Fact]
    public async Task AddStandardJobAsync_WithCustomId_ShouldUseCustomId()
    {
        // Arrange
        var redisScripts = CreateRedisScripts();
        var customId = "custom-job-123";
        var job = CreateTestJob(id: customId);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        var result = await redisScripts.AddStandardJobAsync(job, timestamp);

        // Assert
        result.ToString().Should().Be(customId);
    }

    [Fact]
    public async Task AddStandardJobAsync_WithExistingCustomId_ShouldReturnError()
    {
        // Arrange
        var redisScripts = CreateRedisScripts();
        var customId = "duplicate-job-123";
        var job1 = CreateTestJob(id: customId);
        var job2 = CreateTestJob(id: customId);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        await redisScripts.AddStandardJobAsync(job1, timestamp);
        var result = await redisScripts.AddStandardJobAsync(job2, timestamp);

        // Assert
        ((int)result).Should().Be(-1); // Job already exists
    }

    [Fact]
    public async Task AddDelayedJobAsync_ShouldAddJobToDelayedQueue()
    {
        // Arrange
        var redisScripts = CreateRedisScripts();
        var job = CreateTestJob();
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();

        // Act
        var result = await redisScripts.AddDelayedJobAsync(job, timestamp);

        // Assert
        result.Should().NotBeNull();
        var jobId = result.ToString();
        jobId.Should().NotBeNullOrEmpty();

        // Verify job is in delayed queue
        var delayedKey = $"{TestPrefix}:{TestQueueName}:delayed";
        var delayedJobs = await Database.SortedSetRangeByScoreAsync(delayedKey);
        delayedJobs.Should().Contain(jobId);
    }

    [Fact]
    public async Task GetCountsAsync_ShouldReturnCorrectCounts()
    {
        // Arrange
        var redisScripts = CreateRedisScripts();
        var job1 = CreateTestJob();
        var job2 = CreateTestJob();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Add jobs to different queues
        await redisScripts.AddStandardJobAsync(job1, timestamp);
        await redisScripts.AddStandardJobAsync(job2, timestamp);
        
        // Add a delayed job
        var delayedTimestamp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();
        await redisScripts.AddDelayedJobAsync(CreateTestJob(), delayedTimestamp);

        // Act
        var result = await redisScripts.GetCountsAsync("wait", "delayed", "completed");

        // Assert
        result.Should().NotBeNull();
        var counts = result!;
        counts.Should().HaveCount(3);
        ((int)counts[0]).Should().Be(2); // waiting jobs
        ((int)counts[1]).Should().Be(1); // delayed jobs
        ((int)counts[2]).Should().Be(0); // completed jobs
    }

    [Fact]
    public async Task GetCountsAsync_WithEmptyQueues_ShouldReturnZeros()
    {
        // Arrange
        var redisScripts = CreateRedisScripts();

        // Act
        var result = await redisScripts.GetCountsAsync("wait", "active", "completed");

        // Assert
        var counts = result!;
        counts.Should().HaveCount(3);
        counts.Should().AllSatisfy(count => ((int)count).Should().Be(0));
    }

    [Fact]
    public async Task MoveToActiveAsync_WithJobsInWait_ShouldMoveJobToActive()
    {
        // Arrange
        var redisScripts = CreateRedisScripts();
        var job = CreateTestJob();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // Add a job to wait queue
        await redisScripts.AddStandardJobAsync(job, timestamp);

        // Act
        var result = await redisScripts.MoveToActiveAsync(
            token: "worker-123",
            new Dictionary<string, object?>()
            {
                { "lockDuration", 3000 },
                { "limiter", null },
                { "workerName", "TestWorker" }
            }
        );

        // Assert
        result.Should().NotBeNull();
        if (result.Resp2Type == ResultType.Array)
        {
            var resultArray = (RedisResult[])result!;
            resultArray.Should().NotBeEmpty();
        }
        
        // Verify job moved from wait to active
        var waitCount = await Database.ListLengthAsync($"{TestPrefix}:{TestQueueName}:wait");
        var activeCount = await Database.ListLengthAsync($"{TestPrefix}:{TestQueueName}:active");
        
        waitCount.Should().Be(0);
        activeCount.Should().Be(1);
    }

    [Fact]
    public async Task MoveToActiveAsync_WithEmptyQueue_ShouldReturnNoJob()
    {
        // Arrange
        var redisScripts = CreateRedisScripts();

        // Act
        var result = await redisScripts.MoveToActiveAsync(
            token: "worker-123",
            new Dictionary<string, object?>()
            {
                { "lockDuration", 3000 },
                { "limiter", null },
                { "workerName", "TestWorker" }
            }
        );

        // Assert
        result.Should().NotBeNull();
        var resultArray = (RedisResult[])result!;
        resultArray.Should().HaveCount(4);
        resultArray.Should().AllSatisfy(value => ((int)value).Should().Be(0));
    }

    [Fact]
    public async Task RetryJobAsync_WithValidJob_ShouldMoveJobBackToWait()
    {
        // Arrange
        var redisScripts = CreateRedisScripts();
        var job = CreateTestJob(id: "retry-job-123");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // Add job and move to active
        await redisScripts.AddStandardJobAsync(job, timestamp);
        await redisScripts.MoveToActiveAsync(
            token: "worker-123",
            new Dictionary<string, object?>()
            {
                { "lockDuration", 3000 },
                { "limiter", null },
                { "workerName", "TestWorker" }
            });

        // Act
        var result = await redisScripts.RetryJobAsync(
            jobId: job.Id!,
            lifo: false,
            token: "worker-123"
        );

        // Assert
        ((int)result).Should().Be(0); // Success
        
        // Verify job moved back to wait queue
        var waitCount = await Database.ListLengthAsync($"{TestPrefix}:{TestQueueName}:wait");
        var activeCount = await Database.ListLengthAsync($"{TestPrefix}:{TestQueueName}:active");
        
        waitCount.Should().Be(1);
        activeCount.Should().Be(0);
    }

    [Fact]
    public async Task RetryJobAsync_WithInvalidJobId_ShouldThrowException()
    {
        // Arrange
        var redisScripts = CreateRedisScripts();

        // Act
        var result = await redisScripts.RetryJobAsync(
            jobId: "non-existent-job",
            lifo: false,
            token: "worker-123"
        );
        
        // Assert
        ((int)result).Should().Be(-1); // Job not found
    }

    [Fact]
    public async Task MoveToCompletedAsync_WithValidJob_ShouldMoveJobToCompleted()
    {
        // Arrange
        var redisScripts = CreateRedisScripts();
        var job = CreateTestJob(id: "complete-job-123");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var returnValue = new { status = "success", result = "completed" };
        
        // Add job and move to active
        await redisScripts.AddStandardJobAsync(job, timestamp);
        await redisScripts.MoveToActiveAsync(
            token: "worker-123",
            new Dictionary<string, object?>()
            {
                { "lockDuration", 3000 },
                { "limiter", null },
                { "workerName", "TestWorker" }
            });

        // Act
        var result = await redisScripts.MoveToCompletedAsync(
            job: job,
            returnValue: returnValue,
            removeOnComplete: false,
            token: "worker-123",
            fetchNext: false
        );

        // Assert
        ((int)result).Should().Be(0); // Success
        
        // Verify job moved to completed queue
        var activeCount = await Database.ListLengthAsync($"{TestPrefix}:{TestQueueName}:active");
        var completedCount = await Database.SortedSetLengthAsync($"{TestPrefix}:{TestQueueName}:completed");
        
        activeCount.Should().Be(0);
        completedCount.Should().Be(1);
    }

    [Fact]
    public async Task MoveToCompletedAsync_WithRemoveOnComplete_ShouldRemoveJob()
    {
        // Arrange
        var redisScripts = CreateRedisScripts();
        var job = CreateTestJob(id: "remove-job-123");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var returnValue = new { status = "success" };
        
        // Add job and move to active
        await redisScripts.AddStandardJobAsync(job, timestamp);
        
        await redisScripts.MoveToActiveAsync(
            token: "worker-123",
            new Dictionary<string, object?>()
            {
                { "lockDuration", 30000 },
                { "limiter", null },
                { "workerName", "TestWorker" }
            });

        // Act
        var result = await redisScripts.MoveToCompletedAsync(
            job: job,
            returnValue: returnValue,
            removeOnComplete: true,
            token: "worker-123",
            fetchNext: false
        );

        // Assert
        ((int)result).Should().Be(0); // Success
        
        // Verify job was removed (not in completed queue)
        var completedCount = await Database.SortedSetLengthAsync($"{TestPrefix}:{TestQueueName}:completed");
        var jobKey = $"{TestPrefix}:{TestQueueName}:{job.Id}";
        var jobExists = await Database.KeyExistsAsync(jobKey);
        
        completedCount.Should().Be(0);
        jobExists.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryJobAsync_WithLifoOption_ShouldUseCorrectPushCommand(bool lifo)
    {
        // Arrange
        var redisScripts = CreateRedisScripts();
        var job = CreateTestJob(id: "lifo-job-123");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // Add job and move to active
        await redisScripts.AddStandardJobAsync(job, timestamp);
        await redisScripts.MoveToActiveAsync(
            token: "worker-123",
            new Dictionary<string, object?>()
            {
                { "lockDuration", 3000 },
                { "limiter", null },
                { "workerName", "TestWorker" }
            });

        // Act
        var result = await redisScripts.RetryJobAsync(
            jobId: job.Id!,
            lifo: lifo,
            token: "worker-123"
        );

        // Assert
        ((int)result).Should().Be(0); // Success
        
        // Verify job is back in wait queue
        var waitCount = await Database.ListLengthAsync($"{TestPrefix}:{TestQueueName}:wait");
        waitCount.Should().Be(1);
    }

    [Fact]
    public async Task MoveToActiveAsync_WithMultipleJobs_ShouldProcessJobs()
    {
        // Arrange
        var redisScripts = CreateRedisScripts();

        // Add multiple jobs
        await redisScripts.AddStandardJobAsync(CreateTestJob(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await redisScripts.AddStandardJobAsync(CreateTestJob(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // Act - Move first job
        var result1 = await redisScripts.MoveToActiveAsync(
            token: "worker-1",
            new Dictionary<string, object?>()
            {
                { "lockDuration", 30000 },
                { "limiter", null },
                { "workerName", "Worker1" }
            });

        // Act - Move second job
        var result2 = await redisScripts.MoveToActiveAsync(
            token: "worker-2",
            new Dictionary<string, object?>()
            {
                { "lockDuration", 30000 },
                { "limiter", null },
                { "workerName", "Worker2" }
            });

        // Assert
        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
        
        // Both should have successfully moved jobs
        var activeCount = await Database.ListLengthAsync($"{TestPrefix}:{TestQueueName}:active");
        activeCount.Should().Be(2);
    }
}

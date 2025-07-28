using RedisQ.Core.Options;

namespace RedisQ.Core;

/// <summary>
/// Represents a job to be processed by RedisQ.
/// </summary>
public class Job
{
    /// <summary>
    /// Unique identifier for the job.
    ///
    /// When provided, it should be a unique string as non-unique IDs will return an error.
    /// </summary>
    public string? Id { get; set; }
    
    /// <summary>
    /// The name of the job, used to identify the job type.
    /// This property is required.
    /// </summary>
    public required string Name { get; set; }
    
    /// <summary>
    /// Optional data payload for the job.
    /// Can contain any serializable object needed for job processing.
    /// </summary>
    public object? Data { get; set; }
    
    /// <summary>
    /// Configuration options for the job.
    /// </summary>
    public JobOptions Options { get; set; } = new();

    /// <summary>
    /// Timestamp when the job was created.
    /// Default: Current time in milliseconds
    /// </summary>
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// The total number of attempts to try the job until it completes.
    /// 
    /// Default: 3
    /// </summary>
    public int Attempts { get; set; } = 3;
    
    /// <summary>
    /// The number of attempts made so far.
    /// </summary>
    public int AttemptsMade { get; set; }
}
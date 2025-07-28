using MessagePack;

namespace RedisQ.Core.Options;

/// <summary>
/// Options for configuring a job.
/// </summary>
[MessagePackObject]
public class JobOptions
{
    /// <summary>
    /// An amount of milliseconds to wait until this job can be processed.
    /// Note that for accurate delays, worker and producers
    /// should have their clocks synchronized.
    /// 
    /// Default: 0
    /// </summary>
    [Key("delay")]
    public int Delay { get; set; }

    /// <summary>
    /// If true, removes the job when it successfully completes.
    /// 
    /// Default behavior is to keep the job in the completed set.
    /// </summary>
    [Key("removeOnComplete")]
    public bool RemoveOnComplete { get; set; }

    /// <summary>
    /// If true, removes the job when it fails after all attempts.
    /// 
    /// Default behavior is to keep the job in the failed set.
    /// </summary>
    [Key("removeOnFail")]
    public bool RemoveOnFail { get; set; }

    /// <summary>
    /// Limits the amount of stack trace lines that will be recorded in the stacktrace.
    ///
    /// Default: 10
    /// </summary>
    [Key("stackTraceLimit")]
    public int StackTraceLimit { get; set; } = 10;
}
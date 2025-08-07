using MessagePack;

namespace RedisQ.Core.Options;

/// <summary>
/// Options for configuring a job.
/// </summary>
public class JobOptions
{
    /// <summary>
    /// An amount of milliseconds to wait until this job can be processed.
    /// Note that for accurate delays, worker and producers
    /// should have their clocks synchronized.
    /// 
    /// Default: 0
    /// </summary>
    public int Delay { get; set; }
    
    /// <summary>
    /// This option allows you to determine if the job should be processed immediately
    /// A higher value means that the job will be processed earlier.
    /// 
    /// Default: 0
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// If true, removes the job when it successfully completes.
    /// 
    /// Default behavior is to keep the job in the completed set.
    /// </summary>
    public bool RemoveOnComplete { get; set; }

    /// <summary>
    /// If true, removes the job when it fails after all attempts.
    /// 
    /// Default behavior is to keep the job in the failed set.
    /// </summary>
    public bool RemoveOnFail { get; set; }

    /// <summary>
    /// Limits the amount of stack trace lines that will be recorded in the stacktrace.
    ///
    /// Default: 10
    /// </summary>
    public int StackTraceLimit { get; set; } = 10;

    /// <summary>
    /// The order in which jobs are processed.
    /// Possible values are "lifo" (last in, first out) or "fifo" (first in, first out).
    ///
    /// Default: "lifo"
    /// </summary>
    public string Order { get; set; } = "lifo";
}
using Wymcmd.Core.Model;

namespace Wymcmd.Core.Capture;

public sealed record RawStart(
    int Pid,
    int ParentPid,
    ulong StartKey,
    string ImageName,
    string ImagePath,
    string CommandLine,
    int SessionId,
    DateTime TimeStamp,
    EvidenceSource Source);

public sealed record RawStop(int Pid, DateTime TimeStamp, int? ExitCode);

public interface ICollector : IDisposable
{
    EvidenceSource Source { get; }

    /// <summary>False when the OS refuses this collector - no admin rights, missing provider.</summary>
    bool Available { get; }

    /// <summary>True when short-lived processes cannot be missed.</summary>
    bool Lossless { get; }

    event Action<RawStart>? Started;
    event Action<RawStop>? Stopped;

    void Start();
    void Stop();
}

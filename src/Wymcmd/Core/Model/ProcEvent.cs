namespace Wymcmd.Core.Model;

public sealed class SignatureInfo
{
    public SignatureStatus Status { get; init; } = SignatureStatus.Unknown;
    public string? Publisher { get; init; }
    public string? Thumbprint { get; init; }

    public static readonly SignatureInfo Unknown = new();
}

public sealed class LaunchSource
{
    public LaunchSourceKind Kind { get; init; } = LaunchSourceKind.Unknown;

    /// <summary>Task path, registry value name, service name, shortcut name…</summary>
    public string? Name { get; init; }

    /// <summary>Where the entry lives, so the user can go remove it.</summary>
    public string? Location { get; init; }

    public Confidence Confidence { get; init; } = Confidence.Inferred;
    public EvidenceSource FoundVia { get; init; } = EvidenceSource.None;
}

public sealed class RiskFactor(string key, int weight, string? detail = null)
{
    public string Key { get; } = key;
    public int Weight { get; } = weight;
    public string? Detail { get; } = detail;
}

public sealed class ProcEvent
{
    public long RowId { get; set; }
    public int Pid { get; set; }
    public int ParentPid { get; set; }

    /// <summary>ETW ProcessStartKey when available - survives pid reuse.</summary>
    public ulong StartKey { get; set; }

    public DateTime StartTime { get; set; }
    public DateTime? ExitTime { get; set; }
    public int? ExitCode { get; set; }

    public string ImageName { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public string? WorkingDirectory { get; set; }

    public string? UserName { get; set; }
    public string? UserSid { get; set; }
    public int SessionId { get; set; }
    public bool Elevated { get; set; }
    public string? IntegrityLevel { get; set; }

    public string ParentImageName { get; set; } = "";
    public string? ParentCommandLine { get; set; }

    public WindowVisibility Window { get; set; } = WindowVisibility.Unknown;
    public SignatureInfo Signature { get; set; } = SignatureInfo.Unknown;
    public LaunchSource? Source { get; set; }

    public EvidenceSource Sources { get; set; } = EvidenceSource.None;
    public Confidence Confidence { get; set; } = Confidence.Inferred;

    public int Risk { get; set; }
    public List<RiskFactor> RiskFactors { get; } = [];

    /// <summary>Ancestors, nearest parent first. Dead parents included.</summary>
    public List<AncestorLink> Chain { get; } = [];

    /// <summary>Decoded payload for encoded/wrapped command lines.</summary>
    public string? DecodedCommand { get; set; }

    public string? Sha256 { get; set; }

    public TimeSpan? Lifetime => ExitTime is { } exit ? exit - StartTime : null;

    public bool IsConsoleHost => ConsoleImages.Contains(ImageName);

    /// <summary>
    /// Shells and script engines - the things a person means by "a console opened".
    /// conhost.exe and OpenConsole.exe are deliberately absent: they are the window host for
    /// these processes, so listing them would double every single event.
    /// </summary>
    public static readonly HashSet<string> ConsoleImages = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wt.exe",
        "wscript.exe", "cscript.exe", "mshta.exe", "bash.exe", "wsl.exe", "sh.exe"
    };
}

public sealed class AncestorLink
{
    public int Pid { get; init; }
    public string ImageName { get; init; } = "";
    public string? ImagePath { get; init; }
    public string? CommandLine { get; init; }
    public DateTime? StartTime { get; init; }
    public bool Alive { get; init; }
    public SignatureInfo Signature { get; init; } = SignatureInfo.Unknown;
}

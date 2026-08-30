namespace Wymcmd.Core.Model;

public enum WindowVisibility
{
    Unknown,
    Visible,
    Hidden,
    Embedded
}

/// <summary>How much of a record is measured versus reconstructed.</summary>
public enum Confidence
{
    Inferred,
    High,
    Certain
}

[Flags]
public enum EvidenceSource
{
    None = 0,
    Etw = 1 << 0,
    BlackBox = 1 << 1,
    Wmi = 1 << 2,
    Sysmon = 1 << 3,
    SecurityLog = 1 << 4,
    ScriptBlockLog = 1 << 5,
    TaskLog = 1 << 6,
    WmiActivityLog = 1 << 7,
    Prefetch = 1 << 8,
    Bam = 1 << 9,
    AmCache = 1 << 10,
    Srum = 1 << 11,
    UserAssist = 1 << 12,
    LiveSnapshot = 1 << 13
}

public enum LaunchSourceKind
{
    Unknown,
    UserShell,
    ScheduledTask,
    RunKey,
    StartupFolder,
    Service,
    WmiSubscription,
    Installer,
    OfficeMacro,
    BrowserOrDownload,
    ImageFileExecutionOptions,
    ActiveSetup,
    LogonScript,
    Terminal,
    DeveloperTool,
    SystemComponent,
    RemoteAccess,

    // Appended, never reordered: the numbers are in the database.
    WinlogonHook,
    ComServer
}

public enum SignatureStatus
{
    Unknown,
    Unsigned,
    Valid,
    Invalid,
    Expired
}

public enum CaptureMode
{
    Forensic,
    BlackBox,
    Live,
    Trap,
    Watchdog
}

public enum RuleAction
{
    Allow,
    Log,
    Notify,
    Hide,
    Suspend,
    Kill,
    KillTree
}

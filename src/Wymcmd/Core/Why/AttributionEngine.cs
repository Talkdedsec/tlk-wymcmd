using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;

namespace Wymcmd.Core.Why;

/// <summary>
/// Answers the only question the tool exists for: what made this process start.
/// Works from the ancestor chain first (strongest evidence), then falls back to matching
/// the command line against everything on the machine that can launch a program on its own.
/// </summary>
public sealed class AttributionEngine(AutostartIndex index)
{
    private static readonly HashSet<string> ExplorerShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "shellexperiencehost.exe", "searchapp.exe", "startmenuexperiencehost.exe"
    };

    private static readonly HashSet<string> Terminals = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wt.exe", "windowsterminal.exe", "openconsole.exe",
        "conhost.exe", "bash.exe", "wsl.exe", "mintty.exe", "alacritty.exe", "hyper.exe"
    };

    private static readonly HashSet<string> DeveloperTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "code.exe", "devenv.exe", "rider64.exe", "idea64.exe", "webstorm64.exe", "pycharm64.exe",
        "sublime_text.exe", "node.exe", "npm.cmd", "git.exe", "dotnet.exe", "msbuild.exe", "claude.exe"
    };

    private static readonly HashSet<string> OfficeApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe", "msaccess.exe", "onenote.exe"
    };

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe"
    };

    private static readonly HashSet<string> Installers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msiexec.exe", "setup.exe", "install.exe", "trustedinstaller.exe", "tiworker.exe",
        "wusa.exe", "winget.exe", "choco.exe"
    };

    private static readonly HashSet<string> RemoteAccess = new(StringComparer.OrdinalIgnoreCase)
    {
        "sshd.exe", "winrshost.exe", "wsmprovhost.exe", "psexesvc.exe", "rundll32.exe.tsclient"
    };

    public void Attribute(ProcEvent evt)
    {
        index.EnsureFresh();

        evt.Source = FromChain(evt) ?? FromIndex(evt) ?? new LaunchSource
        {
            Kind = LaunchSourceKind.Unknown,
            Confidence = Confidence.Inferred
        };
    }

    private LaunchSource? FromChain(ProcEvent evt)
    {
        var parent = evt.Chain.FirstOrDefault();
        var parentName = parent?.ImageName ?? evt.ParentImageName;
        if (string.IsNullOrEmpty(parentName)) return null;

        if (parentName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var host = parent?.CommandLine ?? "";
            if (host.Contains("Schedule", StringComparison.OrdinalIgnoreCase) || host.Contains("netsvcs", StringComparison.OrdinalIgnoreCase))
            {
                var task = index.Match(evt.ImagePath, evt.CommandLine);
                if (task is { Kind: LaunchSourceKind.ScheduledTask })
                    return Source(task, Confidence.Certain);

                return new LaunchSource
                {
                    Kind = LaunchSourceKind.ScheduledTask,
                    Location = "Task Scheduler",
                    Confidence = Confidence.High,
                    FoundVia = evt.Sources
                };
            }
        }

        if (parentName.Equals("taskeng.exe", StringComparison.OrdinalIgnoreCase))
            return new LaunchSource { Kind = LaunchSourceKind.ScheduledTask, Confidence = Confidence.High };

        if (parentName.Equals("services.exe", StringComparison.OrdinalIgnoreCase))
        {
            var service = index.Match(evt.ImagePath, evt.CommandLine);
            return service is { Kind: LaunchSourceKind.Service }
                ? Source(service, Confidence.Certain)
                : new LaunchSource { Kind = LaunchSourceKind.Service, Confidence = Confidence.High };
        }

        if (parentName.Equals("wmiprvse.exe", StringComparison.OrdinalIgnoreCase) ||
            parentName.Equals("scrcons.exe", StringComparison.OrdinalIgnoreCase))
        {
            var consumer = index.Match(evt.ImagePath, evt.CommandLine);
            return consumer is { Kind: LaunchSourceKind.WmiSubscription }
                ? Source(consumer, Confidence.Certain)
                : new LaunchSource { Kind = LaunchSourceKind.WmiSubscription, Confidence = Confidence.High };
        }

        if (parentName.Equals("userinit.exe", StringComparison.OrdinalIgnoreCase) ||
            parentName.Equals("winlogon.exe", StringComparison.OrdinalIgnoreCase))
            return new LaunchSource { Kind = LaunchSourceKind.LogonScript, Confidence = Confidence.High };

        if (ExplorerShells.Contains(parentName))
        {
            // At logon explorer also starts Run-key and Startup-folder entries.
            var autostart = index.Match(evt.ImagePath, evt.CommandLine);
            if (autostart is { Kind: LaunchSourceKind.RunKey or LaunchSourceKind.StartupFolder })
                return Source(autostart, Confidence.High);

            return new LaunchSource
            {
                Kind = LaunchSourceKind.UserShell,
                Name = parentName,
                Confidence = Confidence.High
            };
        }

        if (Installers.Contains(parentName))
            return new LaunchSource { Kind = LaunchSourceKind.Installer, Name = parentName, Confidence = Confidence.High };

        if (OfficeApps.Contains(parentName))
            return new LaunchSource { Kind = LaunchSourceKind.OfficeMacro, Name = parentName, Confidence = Confidence.High };

        if (Browsers.Contains(parentName))
            return new LaunchSource { Kind = LaunchSourceKind.BrowserOrDownload, Name = parentName, Confidence = Confidence.High };

        if (RemoteAccess.Contains(parentName))
            return new LaunchSource { Kind = LaunchSourceKind.RemoteAccess, Name = parentName, Confidence = Confidence.High };

        if (DeveloperTools.Contains(parentName))
            return new LaunchSource { Kind = LaunchSourceKind.DeveloperTool, Name = parentName, Confidence = Confidence.High };

        if (Terminals.Contains(parentName))
            return new LaunchSource { Kind = LaunchSourceKind.Terminal, Name = parentName, Confidence = Confidence.High };

        if (parentName.Equals("system", StringComparison.OrdinalIgnoreCase) ||
            parentName.Equals("wininit.exe", StringComparison.OrdinalIgnoreCase) ||
            parentName.Equals("smss.exe", StringComparison.OrdinalIgnoreCase))
            return new LaunchSource { Kind = LaunchSourceKind.SystemComponent, Name = parentName, Confidence = Confidence.High };

        return null;
    }

    private LaunchSource? FromIndex(ProcEvent evt)
    {
        var match = index.Match(evt.ImagePath, evt.CommandLine);
        return match is null ? null : Source(match, Confidence.Inferred);
    }

    private static LaunchSource Source(AutostartEntry entry, Confidence confidence) => new()
    {
        Kind = entry.Kind,
        Name = entry.Name,
        Location = entry.Location,
        Confidence = confidence
    };

    /// <summary>One sentence a human can act on, in the active language.</summary>
    public static string Verdict(ProcEvent evt)
    {
        var source = evt.Source;
        var parentName = evt.Chain.FirstOrDefault()?.ImageName ?? evt.ParentImageName;

        var sentence = source?.Kind switch
        {
            LaunchSourceKind.ScheduledTask => Loc.T("verdict.started_by_task", source.Name ?? Loc.T("source.scheduledtask")),
            LaunchSourceKind.RunKey => Loc.T("verdict.started_by_runkey", source.Name ?? ""),
            LaunchSourceKind.StartupFolder => Loc.T("verdict.started_by_startup", source.Name ?? ""),
            LaunchSourceKind.Service => Loc.T("verdict.started_by_service", source.Name ?? Loc.T("source.service")),
            LaunchSourceKind.WmiSubscription => Loc.T("verdict.started_by_wmi", source.Name ?? ""),
            LaunchSourceKind.UserShell => Loc.T("verdict.started_by_user", source.Name ?? parentName),
            LaunchSourceKind.Installer => Loc.T("verdict.started_by_installer", source.Name ?? parentName),
            LaunchSourceKind.Unknown or null => string.IsNullOrEmpty(parentName)
                ? Loc.T("verdict.unattributed")
                : Loc.T("verdict.started_by_parent", parentName),
            _ => Loc.T("verdict.started_by_parent", source.Name ?? parentName)
        };

        var chain = ChainText(evt);
        return chain.Length == 0 ? sentence : Loc.T("verdict.line", sentence, chain);
    }

    public static string ChainText(ProcEvent evt, int maxLinks = 4)
    {
        var separator = Loc.T("verdict.chain_separator");
        var links = evt.Chain
            .Take(maxLinks)
            .Select(link => link.ImageName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Reverse()
            .ToList();

        links.Add(evt.ImageName);
        return string.Join(separator, links);
    }
}

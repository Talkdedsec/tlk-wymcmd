using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Why;

namespace Wymcmd.Gui;

/// <summary>
/// Tray presence and the one notification that matters: a console just opened without a
/// window, or with a risk score high enough that you would want to look now.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Window _window;
    private DateTime _lastNotification = DateTime.MinValue;

    public TrayIcon(Window window, Action onPanic)
    {
        _window = window;

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "wymcmd",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };

        _icon.ContextMenuStrip.Items.Add(Loc.T("gui.tray_show"), null, (_, _) => Show());
        _icon.ContextMenuStrip.Items.Add(Loc.T("gui.panic"), null, (_, _) => onPanic());
        _icon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _icon.ContextMenuStrip.Items.Add(Loc.T("gui.tray_quit"), null, (_, _) => System.Windows.Application.Current.Shutdown());

        _icon.DoubleClick += (_, _) => Show();
    }

    public bool NotificationsEnabled { get; set; } = true;

    public void Notify(ProcEvent evt)
    {
        if (!NotificationsEnabled) return;
        if (evt.Risk < RiskScorer.WarnThreshold && evt.Window != WindowVisibility.Hidden) return;

        // One balloon at a time; a burst of events should not turn into a burst of popups.
        if (DateTime.Now - _lastNotification < TimeSpan.FromSeconds(20)) return;
        _lastNotification = DateTime.Now;

        _icon.BalloonTipTitle = Loc.T("gui.alert_title", evt.ImageName);
        _icon.BalloonTipText = AttributionEngine.Verdict(evt);
        _icon.BalloonTipIcon = evt.Risk >= RiskScorer.AlertThreshold ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _icon.ShowBalloonTip(6000);
    }

    private void Show()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private static Icon LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "brand", "wymcmd.ico");
        if (File.Exists(path)) return new Icon(path);

        var executable = Environment.ProcessPath;
        return executable is not null ? Icon.ExtractAssociatedIcon(executable) ?? SystemIcons.Application : SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

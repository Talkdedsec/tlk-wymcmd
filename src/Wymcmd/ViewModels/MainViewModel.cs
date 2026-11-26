using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wymcmd.Core.Actions;
using Wymcmd.Core.Capture;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Ipc;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Rules;
using Wymcmd.Core.Setup;
using Wymcmd.Core.Store;
using Wymcmd.Core.Tree;
using Wymcmd.Core.Why;

namespace Wymcmd.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly EventStore _store = new();
    private readonly ProcessTree _tree = new();
    private readonly AutostartIndex _autostart = new();
    private readonly Queue<ProcEvent> _pending = new();
    private readonly DispatcherTimer _flush;
    private CaptureEngine? _engine;
    private PipeClient? _feed;

    public MainViewModel()
    {
        _flush = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _flush.Tick += (_, _) => Drain();
        _flush.Start();

        LoadHistory();
        _ = CatchUpAsync();
    }

    private bool _catchingUp;
    private DateTime? _caughtUpTo;

    // The view model is built before the application starts pumping messages, so there is no
    // synchronization context for an await to come back on. The list belongs to this thread.
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    /// <summary>
    /// The list comes from the database, and the database only knows what was captured while a
    /// window or the service was running. Whatever Windows itself recorded in between - the black
    /// box from the moment the machine booted, the Security log, Sysmon - is read here, off the
    /// interface thread, and the list is loaded again once it is in.
    /// </summary>
    private async Task CatchUpAsync()
    {
        if (_catchingUp) return;
        _catchingUp = true;

        try
        {
            // The first run reads the whole window; later ones only what came after it.
            var now = DateTime.Now;
            var from = _caughtUpTo is { } last ? last.AddMinutes(-1) : now - HistoryWindow;

            var added = await Task.Run(() => new Core.Forensic.ForensicHarvester(_store).CatchUp(from, now)).ConfigureAwait(false);
            _caughtUpTo = now;
            if (added == 0) return;

            await _store.FlushAsync().ConfigureAwait(false);
            await _dispatcher.InvokeAsync(LoadHistory);
        }
        catch (Exception ex)
        {
            Log.Warn("could not catch up on what was recorded while closed: " + ex.Message);
        }
        finally
        {
            _catchingUp = false;
        }
    }

    /// <summary>How far back the list goes, and so how far back catching up has to reach.</summary>
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(3);

    /// <summary>Shared with the statistics window so both read the same database handle.</summary>
    public EventStore Store => _store;

    public ObservableCollection<ProcEvent> Events { get; } = [];

    /// <summary>Raised for launches worth interrupting the user for.</summary>
    public event Action<ProcEvent>? Alert;

    [ObservableProperty] private ProcEvent? _selected;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _consoleOnly = true;
    [ObservableProperty] private bool _hiddenOnly;
    [ObservableProperty] private bool _unsignedOnly;
    [ObservableProperty] private int _minRisk;
    [ObservableProperty] private bool _watching;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _captureText = "";

    public IReadOnlyList<Loc.LanguageOption> Languages => Loc.Available;

    /// <summary>
    /// Bound as SelectedItem rather than SelectedValue: WPF pushes a null selection through a
    /// two-way SelectedValue binding during load, which would silently reset the language.
    /// </summary>
    public bool EnglishActive => Loc.Language == "en";
    public bool TurkishActive => Loc.Language == "tr";

    [RelayCommand]
    private void SetLanguage(string? code)
    {
        if (code is null || code == Loc.Language) return;

        Loc.Use(code);
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(EnglishActive));
        OnPropertyChanged(nameof(TurkishActive));
        OnPropertyChanged(nameof(Watching));

        // The watch button above, and the verdict, the dates and the fact cards are built by converters from the launch
        // itself, so nothing tells them the language changed. Reload and pick the same launch.
        var keep = Selected;
        Selected = null;
        LoadHistory();
        if (keep is not null)
            Selected = Events.FirstOrDefault(e => e.Pid == keep.Pid && e.StartTime == keep.StartTime) ?? Selected;
    }

    public Loc.LanguageOption SelectedLanguage
    {
        get => Loc.Available.FirstOrDefault(option => option.Code == Loc.Language) ?? Loc.Available[0];
        set
        {
            if (value is null || value.Code == Loc.Language) return;

            Loc.Use(value.Code);
            OnPropertyChanged(nameof(SelectedLanguage));
            RefreshTexts();
        }
    }

    private void LoadHistory()
    {
        Events.Clear();
        foreach (var evt in _store.Query(new EventFilter
        {
            From = DateTime.Now - HistoryWindow,
            ConsoleOnly = ConsoleOnly,
            HiddenOnly = HiddenOnly,
            UnsignedOnly = UnsignedOnly,
            MinRisk = MinRisk,
            Text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
            Limit = 500
        }))
        {
            Events.Add(evt);
        }

        // An empty detail pane next to a full list looks broken; show the newest launch.
        Selected ??= Events.FirstOrDefault();

        RefreshTexts();
    }

    private void RefreshTexts()
    {
        var admin = SourceInspector.IsAdministrator();
        CaptureText = Watching
            ? (_feed is not null ? Loc.T("gui.watching_service") : admin ? Loc.T("watch.started") : Loc.T("watch.degraded"))
            : Loc.T("gui.not_watching");
        StatusText = Loc.T("gui.event_count", Events.Count, _store.CountAll());
    }

    [RelayCommand]
    private void ToggleWatch()
    {
        if (Watching)
        {
            _engine?.Stop();
            _feed?.Dispose();
            _feed = null;
            Watching = false;
            RefreshTexts();
            return;
        }

        try
        {
            // If the watchdog service is already capturing, listen to it instead of opening
            // a second kernel session that would compete for the same events.
            if (PipeClient.ServiceIsListening())
            {
                _feed = new PipeClient();
                _feed.Received += OnObserved;
                _feed.Start();
            }
            else
            {
                if (_engine is null)
                {
                    _engine = new CaptureEngine(_store, _tree, new AttributionEngine(_autostart), RuleSet.LoadCurrent());
                    _engine.Observed += OnObserved;
                }
                _engine.Start();
            }

            Watching = true;
        }
        catch (Exception ex)
        {
            Log.Error("could not start live capture", ex);
            StatusText = ex.Message;
        }

        RefreshTexts();
    }

    private void OnObserved(ProcEvent evt)
    {
        lock (_pending) _pending.Enqueue(evt);
    }

    private void Drain()
    {
        List<ProcEvent> batch;
        lock (_pending)
        {
            if (_pending.Count == 0) return;
            batch = [.. _pending];
            _pending.Clear();
        }

        foreach (var evt in batch)
        {
            if (!Passes(evt)) continue;

            var existing = Events.FirstOrDefault(e => e.Pid == evt.Pid && e.StartTime == evt.StartTime);
            if (existing is not null)
            {
                // The engine reports the same object again once the window state is known.
                // Assigning an item over itself changes nothing the list can see, so the row and
                // the card above it kept saying "unknown": take it out and put it back.
                var index = Events.IndexOf(existing);
                var wasSelected = ReferenceEquals(Selected, existing);
                Events.RemoveAt(index);
                Events.Insert(index, evt);
                if (wasSelected)
                {
                    Selected = null;
                    Selected = evt;
                }
                continue;
            }

            Events.Insert(0, evt);

            if (Watching && (evt.Risk >= Core.Why.RiskScorer.WarnThreshold ||
                             (evt.IsConsoleHost && evt.Window == WindowVisibility.Hidden)))
            {
                Alert?.Invoke(evt);
            }
        }

        while (Events.Count > 2000) Events.RemoveAt(Events.Count - 1);
        RefreshTexts();
    }

    private bool Passes(ProcEvent evt)
    {
        if (ConsoleOnly && !evt.IsConsoleHost) return false;
        if (HiddenOnly && evt.Window != WindowVisibility.Hidden) return false;
        if (UnsignedOnly && evt.Signature.Status != SignatureStatus.Unsigned) return false;
        if (evt.Risk < MinRisk) return false;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var needle = SearchText.Trim();
            var haystack = evt.ImageName + " " + evt.CommandLine + " " + evt.ImagePath + " " + (evt.Source?.Name ?? "");
            if (!haystack.Contains(needle, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    [RelayCommand]
    private void Refresh()
    {
        LoadHistory();
        _ = CatchUpAsync();
    }

    /// <summary>Writes what the current filter shows, in the format the chosen extension implies.</summary>
    public void Export()
    {
        if (Events.Count == 0)
        {
            StatusText = Loc.T("gui.export_empty");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"wymcmd-{DateTime.Now:yyyyMMdd-HHmmss}",
            DefaultExt = ".csv",
            Filter = "CSV (*.csv)|*.csv|JSON lines (*.jsonl)|*.jsonl|Report (*.md)|*.md",
            InitialDirectory = AppPaths.ExportDirectory
        };

        Directory.CreateDirectory(AppPaths.ExportDirectory);
        if (dialog.ShowDialog() != true) return;

        var events = Events.ToList();
        var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();

        var content = extension switch
        {
            ".jsonl" => string.Join(Environment.NewLine, events.Select(Exporter.Json)),
            ".md" => string.Join(Environment.NewLine + Environment.NewLine,
                events.OrderByDescending(evt => evt.Risk).Take(50).Select(ReportBuilder.Markdown)),
            _ => Exporter.Csv(events)
        };

        File.WriteAllText(dialog.FileName, content, new System.Text.UTF8Encoding(false));
        StatusText = Loc.T("gui.export_done", events.Count, dialog.FileName);
    }

    /// <summary>
    /// A row captured live carries a start time on the system clock and is matched tightly. A row
    /// that came from a trace or a log was stamped by another clock, seconds off on a machine
    /// that has been up for days - and a console from boot that is still running is exactly the
    /// one somebody opens the window to end.
    /// </summary>
    private static TimeSpan? SlackFor(ProcEvent evt)
        => (evt.Sources & (EvidenceSource.Etw | EvidenceSource.Wmi)) != 0 ? null : TimeSpan.FromSeconds(30);

    [RelayCommand]
    private void KillSelected()
    {
        if (Selected is null) return;
        var result = ProcessActions.Kill(Selected.Pid, Selected.ImageName, Selected.StartTime, SlackFor(Selected));
        StatusText = result.Outcome switch
        {
            ActionOutcome.Done => Loc.T("kill.done", Selected.Pid, result.Affected),
            ActionOutcome.Protected => Loc.T("kill.protected", Selected.Pid),
            ActionOutcome.AccessDenied => Loc.T("cli.error.needs_admin"),
            ActionOutcome.NotFound => Loc.T("cli.error.not_found"),
            _ => Loc.T("kill.failed", Selected.Pid, result.Detail ?? "")
        };
    }

    [RelayCommand]
    private void KillTreeSelected()
    {
        if (Selected is null) return;
        var result = ProcessActions.KillTree(_tree, Selected.Pid, Selected.StartTime, SlackFor(Selected));
        StatusText = Loc.T("kill.done", Selected.Pid, result.Affected);
    }

    [RelayCommand]
    private void OpenLocation()
    {
        if (Selected is { ImagePath.Length: > 0 }) ProcessActions.OpenLocation(Selected.ImagePath);
    }

    /// <summary>Kills every console the current user owns. Protected processes are never touched.</summary>
    [RelayCommand]
    private void Panic()
    {
        _tree.Seed();
        var killed = 0;
        foreach (var record in _tree.LiveRecords()
                     .Where(r => ProcEvent.ConsoleImages.Contains(r.ImageName))
                     .Where(r => r.Pid != Environment.ProcessId))
        {
            if (ProcessActions.Kill(record.Pid, record.ImageName, record.StartTime).Success) killed++;
        }

        StatusText = Loc.T("gui.panic_done", killed);
    }

    partial void OnSearchTextChanged(string value) => LoadHistory();
    partial void OnConsoleOnlyChanged(bool value) => LoadHistory();
    partial void OnHiddenOnlyChanged(bool value) => LoadHistory();
    partial void OnUnsignedOnlyChanged(bool value) => LoadHistory();
    partial void OnMinRiskChanged(int value) => LoadHistory();

    /// <summary>Closing the window must not be able to wedge it - every wait here is bounded.</summary>
    public void Dispose()
    {
        _flush.Stop();
        _feed?.Dispose();
        _engine?.Stop();
        _engine?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _store.Dispose();
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Rules;
using Wymcmd.Core.Store;

namespace Wymcmd.ViewModels;

public sealed partial class RuleRow(Rule rule, int wouldMatch) : ObservableObject
{
    public Rule Rule { get; } = rule;
    public int WouldMatch { get; } = wouldMatch;

    public string Title => Rule.Name.Length > 0 ? Rule.Name : Rule.Id;
    public string Action => Rule.Action.ToString().ToLowerInvariant();
    public string Conditions => Describe(Rule);
    public string DryRun => Loc.T("rules.dry_run", WouldMatch);

    public bool Enabled
    {
        get => Rule.Enabled;
        set
        {
            if (Rule.Enabled == value) return;
            Rule.Enabled = value;
            OnPropertyChanged();
        }
    }

    private static string Describe(Rule rule)
    {
        var parts = new List<string>();
        if (rule.Image is not null) parts.Add("image=" + rule.Image);
        if (rule.ImagePath is not null) parts.Add("path=" + rule.ImagePath);
        if (rule.CommandLine is not null) parts.Add("cmdline~" + rule.CommandLine);
        if (rule.Parent is not null) parts.Add("parent=" + rule.Parent);
        if (rule.Ancestor is not null) parts.Add("ancestor=" + rule.Ancestor);
        if (rule.Signer is not null) parts.Add("signer=" + rule.Signer);
        if (rule.User is not null) parts.Add("user=" + rule.User);
        if (rule.Unsigned == true) parts.Add("unsigned");
        if (rule.HiddenWindow == true) parts.Add("hidden");
        if (rule.Elevated == true) parts.Add("elevated");
        if (rule.InTempPath == true) parts.Add("temp-path");
        if (rule.MinRisk > 0) parts.Add("risk>=" + rule.MinRisk);
        return parts.Count == 0 ? Loc.T("rules.matches_everything") : string.Join(", ", parts);
    }
}

public sealed partial class RulesViewModel : ObservableObject
{
    private readonly EventStore _store;
    private RuleSet _set;

    public RulesViewModel(EventStore store, ProcEvent? seed)
    {
        _store = store;
        _set = RuleSet.Load(AppPaths.Rules);
        Seed = seed;
        Load();
    }

    public ObservableCollection<RuleRow> Rules { get; } = [];

    /// <summary>The launch the main window had selected, used to prefill a new rule.</summary>
    public ProcEvent? Seed { get; }

    [ObservableProperty] private RuleRow? _selected;
    [ObservableProperty] private string _message = "";

    public bool CanSeed => Seed is not null;

    public string SeedDescription => Seed is null
        ? ""
        : Loc.T("rules.seed_from", Seed.ImageName);

    private void Load()
    {
        Rules.Clear();

        var recent = _store.Query(new EventFilter { From = DateTime.Now.AddDays(-7), Limit = 5000 });
        foreach (var rule in _set.Rules.OrderBy(rule => rule.Priority))
            Rules.Add(new RuleRow(rule, recent.Count(rule.Matches)));

        Message = Rules.Count == 0 ? Loc.T("rules.empty") : "";
    }

    [RelayCommand]
    private void Save()
    {
        _set.Save(AppPaths.Rules);
        Message = Loc.T("gui.action_done");
        Load();
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is null) return;

        _set.Rules.Remove(Selected.Rule);
        _set.Save(AppPaths.Rules);
        Load();
    }

    /// <summary>
    /// Builds a rule from the selected launch: the image, and the parts of the command line
    /// that are stable enough to match on. It starts as a log rule - nothing gets armed here.
    /// </summary>
    [RelayCommand]
    private void AddFromSelection()
    {
        if (Seed is null) return;

        var rule = new Rule
        {
            Name = Seed.ImageName,
            Image = Seed.ImageName,
            Action = RuleAction.Log
        };

        if (Seed.Window == WindowVisibility.Hidden) rule.HiddenWindow = true;
        if (Seed.Signature.Status == SignatureStatus.Unsigned) rule.Unsigned = true;
        if (Seed.Chain.FirstOrDefault()?.ImageName is { Length: > 0 } parent) rule.Parent = parent;

        _set.Rules.Add(rule);
        _set.Save(AppPaths.Rules);
        Load();

        Selected = Rules.FirstOrDefault(row => row.Rule.Id == rule.Id);
        Message = Loc.T("rules.added", rule.Id, rule.Action.ToString().ToLowerInvariant());
    }

    [RelayCommand]
    private void Reload()
    {
        _set = RuleSet.Load(AppPaths.Rules);
        Load();
    }
}

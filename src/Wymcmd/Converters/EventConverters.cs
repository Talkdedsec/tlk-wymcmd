using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Why;

namespace Wymcmd.Converters;

public sealed class VerdictConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ProcEvent evt ? AttributionEngine.Verdict(evt) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SentenceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ProcEvent evt ? AttributionEngine.Sentence(evt) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>"24 August 12:27:59 · 3 minutes ago · lived 42 ms", in the active language.</summary>
public sealed class WhenLineConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ProcEvent evt) return "";

        var line = evt.StartTime.ToString("d MMMM HH:mm:ss", Loc.Culture) + "  ·  " + Loc.Ago(evt.StartTime);
        return evt.Lifetime is { } lifetime
            ? line + "  ·  " + Loc.T("gui.lived", Loc.Duration(lifetime))
            : line;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed record ChainStep(string Name, bool First, bool Last);

/// <summary>The ancestry oldest first, ending with the process itself, one chip per step.</summary>
public sealed class ChainStepsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ProcEvent evt) return Array.Empty<ChainStep>();

        var names = evt.Chain
            .Select(link => link.ImageName.Length > 0 ? link.ImageName : Loc.T("chain.exited"))
            .Reverse()
            .Append(evt.ImageName)
            .ToList();

        return names.Select((name, i) => new ChainStep(name, i == 0, i == names.Count - 1)).ToList();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ConfidenceTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Loc.T("confidence." + ((value as LaunchSource)?.Confidence ?? Confidence.Inferred).ToString().ToLowerInvariant());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class RiskBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Calm = new(Color.FromRgb(0x3D, 0x7F, 0x6B));
    private static readonly SolidColorBrush Warn = new(Color.FromRgb(0xF2, 0xC1, 0x4E));
    private static readonly SolidColorBrush Alert = new(Color.FromRgb(0xFF, 0x5C, 0x5C));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var risk = value as int? ?? 0;
        return risk >= RiskScorer.AlertThreshold ? Alert : risk >= RiskScorer.WarnThreshold ? Warn : Calm;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class WindowStateTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is WindowVisibility visibility
            ? Loc.T("window." + visibility.ToString().ToLowerInvariant())
            : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SignatureTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SignatureInfo signature) return "";
        return signature.Status switch
        {
            SignatureStatus.Valid => Loc.T("signature.valid", signature.Publisher ?? "?"),
            SignatureStatus.Unsigned => Loc.T("signature.unsigned"),
            SignatureStatus.Invalid => Loc.T("signature.invalid"),
            SignatureStatus.Expired => Loc.T("signature.expired"),
            _ => Loc.T("signature.unknown")
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SourceTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not LaunchSource source) return Loc.T("source.unknown");
        var kind = Loc.T("source." + source.Kind.ToString().ToLowerInvariant());
        return source.Name is { Length: > 0 } ? $"{kind}: {source.Name}" : kind;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class TimeAgoConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime when ? Loc.Ago(when) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || (value is string text && text.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class RiskFactorTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string key ? Loc.T("risk." + key) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>A parent that exited before we looked has a pid but no name of its own.</summary>
public sealed class AncestorNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string name && name.Length > 0 ? name : Loc.T("chain.exited");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class EmptyToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value switch
        {
            null => true,
            int count => count == 0,
            System.Collections.ICollection collection => collection.Count == 0,
            _ => false
        };
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToWatchTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Loc.T(value is true ? "gui.stop_watching" : "gui.start_watching");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

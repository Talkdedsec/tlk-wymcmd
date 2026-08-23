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

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace AstraCat;

public sealed class EditorSubtitleCue : INotifyPropertyChanged
{
    private long _startMilliseconds;
    private long _endMilliseconds;
    private string _original = string.Empty;
    private string _translated = string.Empty;
    private int _trackIndex;
    private string? _groupId;
    private string _groupName = string.Empty;
    private bool _isEditing;
    private bool _isActive;

    public int Index { get; set; }

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowTranslatedEditor));
            OnPropertyChanged(nameof(ShowOriginalEditor));
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IndicatorColor));
            OnPropertyChanged(nameof(IndicatorOpacity));
            OnPropertyChanged(nameof(IndicatorWidth));
            OnPropertyChanged(nameof(LanguageLabel));
            OnPropertyChanged(nameof(LanguageForeground));
            OnPropertyChanged(nameof(LanguageBackground));
            OnPropertyChanged(nameof(DisplayForeground));
            OnPropertyChanged(nameof(DisplayFontSize));
            OnPropertyChanged(nameof(DisplayFontWeight));
            OnPropertyChanged(nameof(TimeForeground));
            OnPropertyChanged(nameof(LanguageOpacity));
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(UsesTranslatedText));
            OnPropertyChanged(nameof(UsesOriginalText));
            OnPropertyChanged(nameof(ShowTranslatedEditor));
            OnPropertyChanged(nameof(ShowOriginalEditor));
        }
    }

    public int TrackIndex
    {
        get => _trackIndex;
        set
        {
            if (_trackIndex == value) return;
            _trackIndex = Math.Max(0, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrackLabel));
            OnPropertyChanged(nameof(TrackColor));
            OnPropertyChanged(nameof(IndicatorColor));
            OnPropertyChanged(nameof(LanguageLabel));
            OnPropertyChanged(nameof(LanguageForeground));
            OnPropertyChanged(nameof(LanguageBackground));
            OnPropertyChanged(nameof(DisplayForeground));
            OnPropertyChanged(nameof(DisplayFontSize));
            OnPropertyChanged(nameof(DisplayFontWeight));
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(UsesTranslatedText));
            OnPropertyChanged(nameof(UsesOriginalText));
            OnPropertyChanged(nameof(ShowTranslatedEditor));
            OnPropertyChanged(nameof(ShowOriginalEditor));
        }
    }

    public string? GroupId
    {
        get => _groupId;
        set
        {
            if (_groupId == value) return;
            _groupId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGrouped));
        }
    }

    public string GroupName
    {
        get => _groupName;
        set
        {
            if (_groupName == value) return;
            _groupName = value;
            OnPropertyChanged();
        }
    }

    public long StartMilliseconds
    {
        get => _startMilliseconds;
        set
        {
            if (_startMilliseconds == value) return;
            _startMilliseconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StartLabel));
            OnPropertyChanged(nameof(DurationLabel));
        }
    }

    public long EndMilliseconds
    {
        get => _endMilliseconds;
        set
        {
            if (_endMilliseconds == value) return;
            _endMilliseconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EndLabel));
            OnPropertyChanged(nameof(DurationLabel));
        }
    }

    public string Original
    {
        get => _original;
        set
        {
            if (_original == value) return;
            _original = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(UsesTranslatedText));
            OnPropertyChanged(nameof(UsesOriginalText));
            OnPropertyChanged(nameof(ShowTranslatedEditor));
            OnPropertyChanged(nameof(ShowOriginalEditor));
        }
    }

    public string Translated
    {
        get => _translated;
        set
        {
            if (_translated == value) return;
            _translated = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(UsesTranslatedText));
            OnPropertyChanged(nameof(UsesOriginalText));
            OnPropertyChanged(nameof(ShowTranslatedEditor));
            OnPropertyChanged(nameof(ShowOriginalEditor));
        }
    }

    public static readonly string[] TrackColors =
    [
        "#0089FF", // 1: Blue (Track 1)
        "#B45AF6", // 2: Purple (Track 2)
        "#10B981", // 3: Emerald
        "#F59E0B", // 4: Amber
        "#EC4899", // 5: Pink
        "#6366F1", // 6: Indigo
        "#14B8A6", // 7: Teal
        "#8B5CF6"  // 8: Violet
    ];
    private static readonly string[] TrackBackgroundColors =
    [
        "#E7F3FD", "#F1ECFC", "#E8F8F2", "#FFF4DE",
        "#FDEBF3", "#ECECFE", "#E7F8F6", "#F0ECFD"
    ];

    public string TrackColor => TrackColors[Math.Abs(TrackIndex) % TrackColors.Length];
    public string IndicatorColor => TrackColor;
    public double IndicatorOpacity => IsActive ? 1 : 0.34;
    public double IndicatorWidth => IsActive ? 3 : 2;
    // A cue keeps its language/content identity when it is moved between tracks.
    // TrackIndex controls layout and styling only; it must not decide which text
    // field is rendered.
    public bool UsesTranslatedText => !string.IsNullOrWhiteSpace(Translated);
    public bool UsesOriginalText => !UsesTranslatedText;
    public bool ShowTranslatedEditor => IsEditing && UsesTranslatedText;
    public bool ShowOriginalEditor => IsEditing && UsesOriginalText;
    public string DisplayText => UsesTranslatedText ? Translated : Original;
    public string LanguageLabel => $"L{TrackIndex + 1}";
    public string LanguageForeground => TrackColor;
    public string LanguageBackground => TrackBackgroundColors[Math.Abs(TrackIndex) % TrackBackgroundColors.Length];
    public string DisplayForeground => IsActive ? TrackColor : "#8D97A2";
    public double DisplayFontSize => 12;
    public FontWeight DisplayFontWeight => FontWeight.Bold;
    public string TimeForeground => IsActive ? "#465563" : "#A0A8B1";
    public double LanguageOpacity => IsActive ? 1 : 0.58;

    public string StartLabel => FormatTime(StartMilliseconds);
    public string EndLabel => FormatTime(EndMilliseconds);
    public string DurationLabel => $"{Math.Max(0, EndMilliseconds - StartMilliseconds) / 1000d:0.00}s";
    public string TrackLabel => $"轨道 {TrackIndex + 1}";
    public bool IsGrouped => !string.IsNullOrWhiteSpace(GroupId);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string FormatTime(long milliseconds) =>
        TimeSpan.FromMilliseconds(Math.Max(0, milliseconds))
            .ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
}

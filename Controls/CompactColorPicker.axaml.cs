using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace AstraCat;

public partial class CompactColorPicker : UserControl
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<CompactColorPicker, Color>(nameof(Color), Colors.White);

    private bool _syncing;

    public CompactColorPicker()
    {
        InitializeComponent();
        SyncVisuals(Color);
    }

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public event EventHandler<ColorChangedEventArgs>? ColorChanged;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ColorProperty) return;

        var oldColor = change.GetOldValue<Color>();
        var newColor = change.GetNewValue<Color>();
        SyncVisuals(newColor);
        ColorChanged?.Invoke(this, new ColorChangedEventArgs(oldColor, newColor));
    }

    private void SyncVisuals(Color color)
    {
        if (ColorSwatch == null || Spectrum == null || HexDisplay == null || HexEditor == null) return;
        var hex = ToRgbHex(color);
        ColorSwatch.Background = new SolidColorBrush(color);
        HexDisplay.Text = hex;
        HexEditor.Text = hex[1..];

        if (_syncing) return;
        _syncing = true;
        Spectrum.HsvColor = color.ToHsv();
        _syncing = false;
    }

    private void Spectrum_OnColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (_syncing) return;
        SetCurrentValue(ColorProperty, Color.FromArgb(255, e.NewColor.R, e.NewColor.G, e.NewColor.B));
    }

    private void HexEditor_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyHexEditor();
        PickerButton.Focus();
        e.Handled = true;
    }

    private void HexEditor_OnLostFocus(object? sender, RoutedEventArgs e) => ApplyHexEditor();

    private void ApplyHexEditor()
    {
        var value = HexEditor.Text?.Trim() ?? string.Empty;
        if (!value.StartsWith('#')) value = "#" + value;
        if (Color.TryParse(value, out var parsed))
            SetCurrentValue(ColorProperty, Color.FromArgb(255, parsed.R, parsed.G, parsed.B));
        else
            HexEditor.Text = ToRgbHex(Color)[1..];
    }

    private static string ToRgbHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}

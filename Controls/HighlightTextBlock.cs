using System;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace AstraCat;

public class HighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> HighlightTextProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(HighlightText));

    public static readonly StyledProperty<string?> ReplaceTextProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(ReplaceText));

    public static readonly StyledProperty<bool> IsReplaceActiveProperty =
        AvaloniaProperty.Register<HighlightTextBlock, bool>(nameof(IsReplaceActive));

    public static readonly StyledProperty<IBrush?> HighlightBackgroundProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(HighlightBackground), Brush.Parse("#BAE6FD"));

    public static readonly StyledProperty<IBrush?> HighlightForegroundProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(HighlightForeground), Brush.Parse("#0369A1"));

    public static readonly StyledProperty<IBrush?> ReplaceOldBackgroundProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(ReplaceOldBackground), Brush.Parse("#FEE2E2"));

    public static readonly StyledProperty<IBrush?> ReplaceOldForegroundProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(ReplaceOldForeground), Brush.Parse("#DC2626"));

    public static readonly StyledProperty<IBrush?> ReplaceNewBackgroundProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(ReplaceNewBackground), Brush.Parse("#DCFCE7"));

    public static readonly StyledProperty<IBrush?> ReplaceNewForegroundProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(ReplaceNewForeground), Brush.Parse("#16A34A"));

    public string? HighlightText
    {
        get => GetValue(HighlightTextProperty);
        set => SetValue(HighlightTextProperty, value);
    }

    public string? ReplaceText
    {
        get => GetValue(ReplaceTextProperty);
        set => SetValue(ReplaceTextProperty, value);
    }

    public bool IsReplaceActive
    {
        get => GetValue(IsReplaceActiveProperty);
        set => SetValue(IsReplaceActiveProperty, value);
    }

    public IBrush? HighlightBackground
    {
        get => GetValue(HighlightBackgroundProperty);
        set => SetValue(HighlightBackgroundProperty, value);
    }

    public IBrush? HighlightForeground
    {
        get => GetValue(HighlightForegroundProperty);
        set => SetValue(HighlightForegroundProperty, value);
    }

    public IBrush? ReplaceOldBackground
    {
        get => GetValue(ReplaceOldBackgroundProperty);
        set => SetValue(ReplaceOldBackgroundProperty, value);
    }

    public IBrush? ReplaceOldForeground
    {
        get => GetValue(ReplaceOldForegroundProperty);
        set => SetValue(ReplaceOldForegroundProperty, value);
    }

    public IBrush? ReplaceNewBackground
    {
        get => GetValue(ReplaceNewBackgroundProperty);
        set => SetValue(ReplaceNewBackgroundProperty, value);
    }

    public IBrush? ReplaceNewForeground
    {
        get => GetValue(ReplaceNewForegroundProperty);
        set => SetValue(ReplaceNewForegroundProperty, value);
    }

    static HighlightTextBlock()
    {
        HighlightTextProperty.Changed.AddClassHandler<HighlightTextBlock>((x, _) => x.UpdateInlines());
        ReplaceTextProperty.Changed.AddClassHandler<HighlightTextBlock>((x, _) => x.UpdateInlines());
        IsReplaceActiveProperty.Changed.AddClassHandler<HighlightTextBlock>((x, _) => x.UpdateInlines());
        TextProperty.Changed.AddClassHandler<HighlightTextBlock>((x, _) => x.UpdateInlines());
    }

    public HighlightTextBlock()
    {
        UpdateInlines();
    }

    private void UpdateInlines()
    {
        var text = Text;
        var query = HighlightText;
        var replace = ReplaceText;
        var hasReplace = IsReplaceActive && !string.IsNullOrEmpty(replace);

        Inlines ??= new InlineCollection();
        Inlines.Clear();

        if (string.IsNullOrEmpty(text)) return;

        if (string.IsNullOrWhiteSpace(query))
        {
            Inlines.Add(new Run(text));
            return;
        }

        try
        {
            var pattern = Regex.Escape(query.Trim());
            var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase);
            if (matches.Count == 0)
            {
                Inlines.Add(new Run(text));
                return;
            }

            var lastIndex = 0;
            foreach (Match match in matches)
            {
                if (match.Index > lastIndex)
                {
                    Inlines.Add(new Run(text.Substring(lastIndex, match.Index - lastIndex)));
                }

                if (hasReplace)
                {
                    // 1. Light red old text (overwritten / replaced)
                    var oldBorder = new Border
                    {
                        Background = ReplaceOldBackground ?? Brush.Parse("#FEE2E2"),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(2, 0),
                        Margin = new Thickness(0, 0, 2, 0),
                        Child = new TextBlock
                        {
                            Text = match.Value,
                            Foreground = ReplaceOldForeground ?? Brush.Parse("#DC2626"),
                            FontWeight = FontWeight.SemiBold,
                            FontSize = FontSize,
                            FontFamily = FontFamily
                        }
                    };
                    Inlines.Add(new InlineUIContainer(oldBorder));

                    // 2. Light green new text (replacement) immediately following
                    var newBorder = new Border
                    {
                        Background = ReplaceNewBackground ?? Brush.Parse("#DCFCE7"),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(2, 0),
                        Margin = new Thickness(0, 0, 1, 0),
                        Child = new TextBlock
                        {
                            Text = replace,
                            Foreground = ReplaceNewForeground ?? Brush.Parse("#16A34A"),
                            FontWeight = FontWeight.Bold,
                            FontSize = FontSize,
                            FontFamily = FontFamily
                        }
                    };
                    Inlines.Add(new InlineUIContainer(newBorder));
                }
                else
                {
                    // Standard light blue highlighter
                    var highlightBorder = new Border
                    {
                        Background = HighlightBackground ?? Brush.Parse("#BAE6FD"),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(2, 0),
                        Margin = new Thickness(0, 0),
                        Child = new TextBlock
                        {
                            Text = match.Value,
                            Foreground = HighlightForeground ?? Brush.Parse("#0369A1"),
                            FontWeight = FontWeight.SemiBold,
                            FontSize = FontSize,
                            FontFamily = FontFamily
                        }
                    };
                    Inlines.Add(new InlineUIContainer(highlightBorder));
                }

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < text.Length)
            {
                Inlines.Add(new Run(text.Substring(lastIndex)));
            }
        }
        catch
        {
            Inlines.Add(new Run(text));
        }
    }
}

namespace AstraCat;

public sealed class SubtitleStyleDefinition
{
    public string Id { get; set; } = "main";
    public string Name { get; set; } = "中文默认样式";
    public string AccentColor { get; set; } = "#3A9BF4";
    public string FontFamily { get; set; } = "Microsoft YaHei";
    public double FontSize { get; set; } = 70;
    public string TextColor { get; set; } = "#FFFFFF";
    public string OutlineColor { get; set; } = "#22263B";
    public double OutlineWidth { get; set; } = 3;
    public double ShadowDistance { get; set; } = 1.5;
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Boxed { get; set; }
    public string BoxColor { get; set; } = "#000000";
    public double BoxOpacity { get; set; } = 53;
    public double BoxCornerRadius { get; set; } = 10;
    public double BoxPadding { get; set; } = 12;
    public string Alignment { get; set; } = "底部居中";
    public double VerticalMargin { get; set; } = 120;
    public double HorizontalMargin { get; set; } = 40;

    public string Summary => $"{FontFamily} · {FontSize:0} pt · {Alignment}";

    public SubtitleStyleDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        AccentColor = AccentColor,
        FontFamily = FontFamily,
        FontSize = FontSize,
        TextColor = TextColor,
        OutlineColor = OutlineColor,
        OutlineWidth = OutlineWidth,
        ShadowDistance = ShadowDistance,
        Bold = Bold,
        Italic = Italic,
        Underline = Underline,
        Boxed = Boxed,
        BoxColor = BoxColor,
        BoxOpacity = BoxOpacity,
        BoxCornerRadius = BoxCornerRadius,
        BoxPadding = BoxPadding,
        Alignment = Alignment,
        VerticalMargin = VerticalMargin,
        HorizontalMargin = HorizontalMargin
    };

    public static SubtitleStyleDefinition MainDefault() => new();

    public static SubtitleStyleDefinition SecondaryDefault() => new()
    {
        Id = "secondary",
        Name = "英文默认样式",
        AccentColor = "#8B72E8",
        FontSize = 50,
        TextColor = "#FFFFFF",
        OutlineColor = "#22263B",
        OutlineWidth = 2.2,
        Bold = true,
        VerticalMargin = 70,
        HorizontalMargin = 40
    };
}

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Accord.App;

/// <summary>
/// Centralized Windows 11 Fluent color palette that dynamically adapts every
/// code-behind card, border, badge, and status pill to the active Light/Dark theme
/// and Windows 11 system accent color.
/// </summary>
public sealed class ThemePalette
{
    public bool IsDark { get; }
    public Color Accent { get; }
    public Color AccentTextOnAccent { get; }

    public SolidColorBrush CardBackground { get; }
    public SolidColorBrush CardBorder { get; }
    public SolidColorBrush SubtleCardBackground { get; }
    public SolidColorBrush ItemBackground { get; }
    public SolidColorBrush ItemHoverBackground { get; }
    public SolidColorBrush ItemSelectedBackground { get; }
    public SolidColorBrush ItemSelectedBorder { get; }

    public SolidColorBrush TextPrimary { get; }
    public SolidColorBrush TextSecondary { get; }
    public SolidColorBrush TextMuted { get; }

    public SolidColorBrush DrawerSurface { get; }
    public SolidColorBrush DrawerBackdrop { get; }
    public SolidColorBrush DrawerBorder { get; }
    public SolidColorBrush PreviewBackground { get; }

    public SolidColorBrush AccentFill { get; }
    public SolidColorBrush AccentSoftBackground { get; }
    public SolidColorBrush AccentSoftBorder { get; }
    public SolidColorBrush AccentText { get; }

    public SolidColorBrush SuccessBackground { get; }
    public SolidColorBrush SuccessForeground { get; }
    public SolidColorBrush SuccessDot { get; }

    public SolidColorBrush WarningBackground { get; }
    public SolidColorBrush WarningForeground { get; }
    public SolidColorBrush WarningDot { get; }

    public SolidColorBrush ErrorBackground { get; }
    public SolidColorBrush ErrorForeground { get; }

    public SolidColorBrush NeutralPillBackground { get; }
    public SolidColorBrush NeutralPillForeground { get; }
    public SolidColorBrush NeutralDot { get; }

    public SolidColorBrush IconTileBackground { get; }
    public SolidColorBrush IconTileSelectedBackground { get; }

    public Color TitleBarButtonFg { get; }
    public Color TitleBarButtonHoverFg { get; }
    public Color TitleBarButtonHoverBg { get; }
    public Color TitleBarButtonPressedBg { get; }
    public Color TitleBarButtonInactiveFg { get; }

    private ThemePalette(bool isDark)
    {
        IsDark = isDark;

        var ui = new UISettings();
        Color sysAccent = ui.GetColorValue(UIColorType.Accent);
        Color accentLight2 = ui.GetColorValue(UIColorType.AccentLight2);
        Color accentDark1 = ui.GetColorValue(UIColorType.AccentDark1);

        Accent = isDark ? accentLight2 : sysAccent;
        AccentTextOnAccent = isDark ? Color.FromArgb(255, 16, 16, 16) : Colors.White;

        if (isDark)
        {
            CardBackground = Brush(0xB8, 0x2B, 0x2B, 0x2E);
            CardBorder = Brush(0x28, 0xFF, 0xFF, 0xFF);
            SubtleCardBackground = Brush(0x88, 0x23, 0x23, 0x26);
            ItemBackground = Brush(0x0D, 0xFF, 0xFF, 0xFF);
            ItemHoverBackground = Brush(0x17, 0xFF, 0xFF, 0xFF);
            ItemSelectedBackground = Brush(0x28, Accent.R, Accent.G, Accent.B);
            ItemSelectedBorder = Brush(0xAA, Accent.R, Accent.G, Accent.B);

            TextPrimary = Brush(0xFF, 0xF5, 0xF5, 0xF7);
            TextSecondary = Brush(0xCC, 0xD4, 0xD4, 0xD8);
            TextMuted = Brush(0x99, 0xB8, 0xB8, 0xC0);

            DrawerSurface = Brush(0xFF, 0x20, 0x20, 0x24);
            DrawerBackdrop = Brush(0x8A, 0x00, 0x00, 0x00);
            DrawerBorder = Brush(0x36, 0xFF, 0xFF, 0xFF);
            PreviewBackground = Brush(0x66, 0x16, 0x16, 0x18);

            AccentFill = new SolidColorBrush(Accent);
            AccentSoftBackground = Brush(0x2E, Accent.R, Accent.G, Accent.B);
            AccentSoftBorder = Brush(0x99, Accent.R, Accent.G, Accent.B);
            AccentText = new SolidColorBrush(Accent);

            SuccessBackground = Brush(0x2E, 0x3D, 0xD6, 0x8C);
            SuccessForeground = Brush(0xFF, 0x6E, 0xE7, 0xA8);
            SuccessDot = Brush(0xFF, 0x4A, 0xDE, 0x80);

            WarningBackground = Brush(0x2E, 0xF5, 0x9E, 0x0B);
            WarningForeground = Brush(0xFF, 0xFC, 0xD3, 0x4D);
            WarningDot = Brush(0xFF, 0xFB, 0xBF, 0x24);

            ErrorBackground = Brush(0x30, 0xEF, 0x44, 0x44);
            ErrorForeground = Brush(0xFF, 0xFC, 0xA5, 0xA5);

            NeutralPillBackground = Brush(0x1C, 0xFF, 0xFF, 0xFF);
            NeutralPillForeground = Brush(0xDD, 0xD4, 0xD4, 0xD8);
            NeutralDot = Brush(0x99, 0xA1, 0xA1, 0xAA);

            IconTileBackground = Brush(0x1A, 0xFF, 0xFF, 0xFF);
            IconTileSelectedBackground = Brush(0x38, Accent.R, Accent.G, Accent.B);

            TitleBarButtonFg = Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF7);
            TitleBarButtonHoverFg = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            TitleBarButtonHoverBg = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
            TitleBarButtonPressedBg = Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF);
            TitleBarButtonInactiveFg = Color.FromArgb(0x88, 0xD4, 0xD4, 0xD8);
        }
        else
        {
            CardBackground = Brush(0xCC, 0xFF, 0xFF, 0xFF);
            CardBorder = Brush(0x1F, 0x00, 0x00, 0x00);
            SubtleCardBackground = Brush(0x99, 0xF6, 0xF7, 0xFA);
            ItemBackground = Brush(0x85, 0xFF, 0xFF, 0xFF);
            ItemHoverBackground = Brush(0xD8, 0xF3, 0xF5, 0xF9);
            ItemSelectedBackground = Brush(0x1E, sysAccent.R, sysAccent.G, sysAccent.B);
            ItemSelectedBorder = Brush(0xB8, sysAccent.R, sysAccent.G, sysAccent.B);

            TextPrimary = Brush(0xFF, 0x1A, 0x1A, 0x1F);
            TextSecondary = Brush(0xFF, 0x4A, 0x4A, 0x55);
            TextMuted = Brush(0xFF, 0x6B, 0x6B, 0x76);

            DrawerSurface = Brush(0xFF, 0xF8, 0xF9, 0xFB);
            DrawerBackdrop = Brush(0x52, 0x00, 0x00, 0x00);
            DrawerBorder = Brush(0x2A, 0x00, 0x00, 0x00);
            PreviewBackground = Brush(0xAA, 0xF1, 0xF3, 0xF7);

            AccentFill = new SolidColorBrush(sysAccent);
            AccentSoftBackground = Brush(0x1F, sysAccent.R, sysAccent.G, sysAccent.B);
            AccentSoftBorder = Brush(0x88, sysAccent.R, sysAccent.G, sysAccent.B);
            AccentText = new SolidColorBrush(accentDark1);

            SuccessBackground = Brush(0x22, 0x10, 0x7C, 0x10);
            SuccessForeground = Brush(0xFF, 0x0E, 0x6B, 0x0E);
            SuccessDot = Brush(0xFF, 0x16, 0xA3, 0x4A);

            WarningBackground = Brush(0x26, 0xC2, 0x6D, 0x00);
            WarningForeground = Brush(0xFF, 0x9A, 0x4B, 0x00);
            WarningDot = Brush(0xFF, 0xD9, 0x77, 0x06);

            ErrorBackground = Brush(0x22, 0xC4, 0x2B, 0x1C);
            ErrorForeground = Brush(0xFF, 0xB4, 0x23, 0x18);

            NeutralPillBackground = Brush(0x12, 0x00, 0x00, 0x00);
            NeutralPillForeground = Brush(0xFF, 0x4A, 0x4A, 0x55);
            NeutralDot = Brush(0xFF, 0x8A, 0x8A, 0x94);

            IconTileBackground = Brush(0x0E, 0x00, 0x00, 0x00);
            IconTileSelectedBackground = Brush(0x22, sysAccent.R, sysAccent.G, sysAccent.B);

            TitleBarButtonFg = Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1F);
            TitleBarButtonHoverFg = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            TitleBarButtonHoverBg = Color.FromArgb(0x12, 0x00, 0x00, 0x00);
            TitleBarButtonPressedBg = Color.FromArgb(0x20, 0x00, 0x00, 0x00);
            TitleBarButtonInactiveFg = Color.FromArgb(0x80, 0x5A, 0x5A, 0x64);
        }
    }

    public static ThemePalette For(ElementTheme actualTheme) =>
        new(actualTheme == ElementTheme.Dark);

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) =>
        new(Color.FromArgb(a, r, g, b));
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Qalam.App;

/// <summary>Palette and building blocks borrowed from the studio's Oddi tool.</summary>
internal static class Ui
{
    public static readonly Color OrangeC = Color.Parse("#F25C19");
    public static readonly Color YellowC = Color.Parse("#FED800");
    public static readonly IBrush Bg = Brush.Parse("#0A0A0D");
    public static readonly IBrush Card = Brush.Parse("#121217");
    public static readonly IBrush Field = Brush.Parse("#0A0A0D");
    public static readonly IBrush Border = Brush.Parse("#212128");
    public static readonly IBrush Text = Brush.Parse("#EDEDF2");
    public static readonly IBrush Muted = Brush.Parse("#8A8A99");
    public static readonly IBrush Dim = Brush.Parse("#4C4C59");
    public static readonly IBrush Orange = new SolidColorBrush(OrangeC);
    public static readonly IBrush Yellow = new SolidColorBrush(YellowC);
    public static readonly IBrush YellowTint = Brush.Parse("#2F2A14");
    public static readonly IBrush OrangeTint = Brush.Parse("#2E1A12");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, SF Mono, monospace");

    public static Border CardBox(Control content, Thickness? padding = null) => new()
    {
        Background = Card, BorderBrush = Border, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12), Padding = padding ?? new Thickness(18, 16),
        Child = content
    };

    public static TextBlock Caption(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Muted,
        LetterSpacing = 1, Margin = new Thickness(0, 0, 0, 10)
    };

    public static TextBlock Title(string text, double size = 15) => new()
    {
        Text = text, FontSize = size, FontWeight = FontWeight.Bold, Foreground = Text, TextWrapping = TextWrapping.Wrap
    };

    public static TextBlock Hint(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
    };

    /// <summary>Icon tile like Oddi's section badges (tinted square with a glyph).</summary>
    public static Border Badge(string glyph, bool yellow) => new()
    {
        Width = 36, Height = 36, CornerRadius = new CornerRadius(9),
        Background = yellow ? YellowTint : OrangeTint,
        Child = new TextBlock
        {
            Text = glyph, FontSize = 16, FontWeight = FontWeight.Bold,
            Foreground = yellow ? Yellow : Orange,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        }
    };

    public static Button PrimaryButton(string text, bool yellow = false) => new()
    {
        Content = new TextBlock { Text = text, FontWeight = FontWeight.Bold, FontSize = 13 },
        Background = yellow ? Yellow : Orange,
        Foreground = yellow ? Bg : Brushes.White,
        CornerRadius = new CornerRadius(9), Padding = new Thickness(16, 9),
        BorderThickness = new Thickness(0),
    };

    public static Button GhostButton(string text) => new()
    {
        Content = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 13 },
        Background = Card, Foreground = Text, BorderBrush = Border, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 8),
    };

    public static Border Chip(string text, IBrush? fg = null) => new()
    {
        BorderBrush = Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
        Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontFamily = Mono, FontSize = 12, Foreground = fg ?? Muted }
    };

    /// <summary>Row: label (+hint) on the left, control on the right.</summary>
    public static Grid Row(string label, string? hint, Control right)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6) };
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Text, TextWrapping = TextWrapping.Wrap });
        if (hint != null) left.Children.Add(Hint(hint));
        g.Children.Add(left);
        right.VerticalAlignment = VerticalAlignment.Center;
        right.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    public static ToggleSwitch Switch(bool on) => new() { IsChecked = on, OnContent = "", OffContent = "", MinWidth = 0 };
}

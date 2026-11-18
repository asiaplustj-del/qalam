using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Qalam.Core;

namespace Qalam.App;

/// <summary>Small borderless window under the text caret: "ҳамчун  [Tab]". Never takes focus.</summary>
public sealed class SuggestionPopup : Window
{
    private readonly TextBlock _typed = new() { FontSize = 14, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _rest = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Ui.Text, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _alts = new() { FontSize = 12, Foreground = Ui.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly Border _badge;

    public SuggestionPopup()
    {
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Focusable = false;

        var tab = new Border
        {
            Background = Ui.Yellow, CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1), Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "Tab", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Ui.Bg }
        };
        _badge = new Border
        {
            Background = Ui.OrangeTint, CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1), Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
            Child = new TextBlock { Text = "нейро", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Ui.Orange }
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_badge);
        row.Children.Add(_typed);
        row.Children.Add(_rest);
        row.Children.Add(tab);
        row.Children.Add(_alts);
        Content = new Border
        {
            Background = Ui.Card, BorderBrush = Ui.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 6), Child = row,
            BoxShadow = BoxShadows.Parse("0 4 16 0 #80000000")
        };
    }

    public void ShowSuggestion(QalamController.Suggestion s, PixelPoint position)
    {
        if (s.IsPhrase)
        {
            _badge.IsVisible = true;
            _typed.Text = "";
            _rest.Text = s.Text.Length > 60 ? s.Text[..60] + "…" : s.Text;
            _alts.Text = "";
        }
        else
        {
            _badge.IsVisible = false;
            int n = Math.Min(s.Erase, s.Text.Length);
            _typed.Text = s.Text[..n];
            _rest.Text = s.Text[n..];
            _alts.Text = s.Alternatives.Count > 0 ? string.Join(" · ", s.Alternatives) : "";
        }
        Position = position;
        if (!IsVisible) Show();
    }
}

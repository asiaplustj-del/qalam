using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Qalam.Core;

namespace Qalam.App;

/// <summary>
/// Cotypist-style suggestion: semi-transparent text drawn right after the text cursor, no background.
/// A transparent, borderless, never-focused window placed exactly over the caret.
/// </summary>
public sealed class GhostText : Window
{
    private readonly TextBlock _text = new()
    {
        Foreground = new SolidColorBrush(Color.FromArgb(150, 128, 128, 136)),
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
        // the app's embedded Inter: the macOS system UI font gave garbage glyphs in this window
    };
    private readonly TextBlock _more = new()
    {
        Foreground = new SolidColorBrush(Color.FromArgb(110, 128, 128, 136)),
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
        Margin = new Thickness(8, 0, 0, 0),
    };

    public GhostText()
    {
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = false;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _text, _more } };
    }

    /// <param name="caretTop">Caret top-left, physical pixels.</param>
    /// <param name="caretHeight">Caret height, physical pixels (≈ line height).</param>
    /// <param name="typed">What the user typed of the current word (to tell "continue" from "replace").</param>
    public void ShowAt(QalamController.Suggestion s, string typed, PixelPoint caretTop, int caretHeight)
    {
        string ghost;
        if (s.IsPhrase) ghost = s.Text;
        else if (s.Text.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) ghost = s.Text[typed.Length..];
        else ghost = "  → " + s.Text;   // Tajik letters restored: "точик" → "тоҷикистон"
        if (ghost.Length == 0) { Hide(); return; }

        var scaling = Screens.ScreenFromPoint(caretTop)?.Scaling ?? 1.0;
        double lineDip = Math.Clamp(caretHeight / scaling, 10, 60);
        _text.FontSize = Math.Round(lineDip * 0.8, 1);
        _text.LineHeight = lineDip;
        _text.Height = lineDip;
        _text.Text = ghost;
        int n = s.Alternatives.Count;
        _more.IsVisible = n > 0;
        _more.FontSize = Math.Max(9, _text.FontSize * 0.75);
        _more.Text = $"ещё {n} · ⇧Tab";
        Position = new PixelPoint(caretTop.X + (int)Math.Round(1 * scaling), caretTop.Y);
        if (!IsVisible) Show();
    }
}

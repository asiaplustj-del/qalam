using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Qalam.Core;

namespace Qalam.App;

/// <summary>
/// Inline suggestion: semi-transparent text drawn right after the text cursor, on the same line,
/// in the field's own font (family, size, weight as the app's accessibility API reports them), no background.
/// A transparent, borderless, never-focused window placed exactly over the caret.
/// </summary>
public sealed class GhostText : Window
{
    private readonly TextBlock _text = new()
    {
        Foreground = new SolidColorBrush(Color.FromArgb(150, 128, 128, 136)),
        VerticalAlignment = VerticalAlignment.Bottom,
        IsHitTestVisible = false,
    };
    private readonly TextBlock _more = new()
    {
        Foreground = new SolidColorBrush(Color.FromArgb(110, 128, 128, 136)),
        VerticalAlignment = VerticalAlignment.Bottom,
        IsHitTestVisible = false,
        Margin = new Thickness(8, 0, 0, 0),
    };
    private readonly StackPanel _row;
    private PixelPoint? _target;

    public GhostText()
    {
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = false;
        Focusable = false;
        IsHitTestVisible = false;
        // The size is set explicitly BEFORE the window is moved. With SizeToContent the size arrived after the move,
        // and macOS keeps a window's bottom edge when it grows → the hint jumped above the line.
        SizeToContent = SizeToContent.Manual;
        Width = 1; Height = 1;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        _row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Children = { _text, _more } };
        Content = _row;
        // any late resize (layout pass, the OS) → put the window back exactly where it belongs
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ClientSizeProperty && _target is { } t && IsVisible) { Position = t; ApplyMacFrame(); }
        };
    }

    /// <param name="caretTop">Caret top-left, physical pixels.</param>
    /// <param name="caretHeight">Caret height, physical pixels (≈ line height).</param>
    /// <param name="typed">What the user typed of the current word (to tell "continue" from "replace").</param>
    /// <param name="font">The field's font, if the app reports it.</param>
    /// <param name="fontSizePx">Its size in physical pixels (0 = unknown).</param>
    public void ShowAt(QalamController.Suggestion s, string typed, PixelPoint caretTop, int caretHeight, CaretFont? font = null, double fontSizePx = 0)
    {
        string ghost;
        if (s.IsPhrase) ghost = s.Text;
        else if (s.Text.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) ghost = s.Text[typed.Length..];
        else ghost = "  → " + s.Text;   // Tajik letters restored: "точик" → "тоҷикистон"
        if (ghost.Length == 0) { Hide(); return; }

        var scaling = Screens.ScreenFromPoint(caretTop)?.Scaling ?? 1.0;
        double lineDip = Math.Clamp(caretHeight / scaling, 8, 160);

        // size: the field's own font size when it is plausible for this line, else ~80 % of the line height
        double size = lineDip * 0.8;
        double reported = fontSizePx / scaling;
        if (reported >= lineDip * 0.4 && reported <= lineDip * 1.05) size = reported;
        _text.FontSize = Math.Round(size, 1);
        _text.FontFamily = ResolveFamily(font?.Family);
        _text.FontWeight = (FontWeight)Math.Clamp(font?.Weight ?? 400, 100, 950);
        _text.FontStyle = font?.Italic == true ? FontStyle.Italic : FontStyle.Normal;
        _text.Text = ghost;

        int n = s.Alternatives.Count;
        _more.IsVisible = n > 0;
        _more.FontFamily = _text.FontFamily;
        _more.FontSize = Math.Max(9, _text.FontSize * 0.7);
        _more.Text = $"ещё {n} · ⇧Tab";

        // measure first, then size, then move: the text's bottom (≈ its baseline + descent) sits on the caret's bottom
        _row.Measure(Size.Infinity);
        var want = _row.DesiredSize;
        double h = Math.Ceiling(Math.Max(want.Height, 1));
        Width = Math.Ceiling(want.Width) + 2;
        Height = h;
        int hPx = (int)Math.Round(h * scaling);
        int bottom = caretTop.Y + caretHeight;
        // lots of extra line spacing (headings, paragraph spacing): the text is in the middle of the caret, not at its bottom
        if (caretHeight > hPx * 1.45) bottom = caretTop.Y + (caretHeight + hPx) / 2;
        var target = new PixelPoint(caretTop.X + (int)Math.Round(1 * scaling), bottom - hPx);
        _target = target;
        Position = target;
        if (!IsVisible) Show();
        // macOS: set the native frame directly (Avalonia moved the window with a stale height → a line too high)
        double prim = Screens.Primary?.Scaling ?? scaling;
        _macFrame = OperatingSystem.IsMacOS() ? (target.X / prim, target.Y / prim, Width, h) : null;
        ApplyMacFrame();
        // the native window may apply the new size a moment later — place it again after that
        Dispatcher.UIThread.Post(() => { if (_target == target && IsVisible) { Position = target; ApplyMacFrame(); } }, DispatcherPriority.Render);
        LastPlacement = $"caret=({caretTop.X},{caretTop.Y},h{caretHeight}) font={font?.Family ?? "?"} {fontSizePx:F0}px w{font?.Weight} → " +
                        $"text {_text.FontFamily.Name} {_text.FontSize}dip, window {target.X},{target.Y} {Width:F0}x{h:F0}dip @{scaling}{(_macFrame != null ? " (native frame)" : "")}";
    }

    private (double X, double Y, double W, double H)? _macFrame;
    private void ApplyMacFrame()
    {
        if (_macFrame is { } f && !MacWindowFrame.Place(this, f.X, f.Y, f.W, f.H)) _macFrame = null;
    }

    /// <summary>Last position decision (for the diagnostic log).</summary>
    public string LastPlacement { get; private set; } = "";

    public override void Hide() { _target = null; _macFrame = null; base.Hide(); }

    private static readonly Dictionary<string, FontFamily> _families = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The app's font if it is installed here. Hidden macOS system fonts (".AppleSystemUIFont", ".SFNS-…", "SF Pro")
    /// and unknown ones → the embedded Inter, which is close to San Francisco and renders Cyrillic and Tajik correctly
    /// (the system UI font gave garbage glyphs in this window).
    /// </summary>
    private static FontFamily ResolveFamily(string? reported)
    {
        if (string.IsNullOrWhiteSpace(reported)) return FontFamily.Default;
        if (_families.TryGetValue(reported, out var cached)) return cached;
        var result = FontFamily.Default;
        // a CSS stack from a browser: "system-ui, \"Segoe UI\", Roboto" → the first installed one
        foreach (var raw in reported.Split(','))
        {
            var name = raw.Trim().Trim('"', '\'').Trim();
            if (name.Length == 0) continue;
            if (name.StartsWith('.') || name.StartsWith("SF ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("SFNS", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System Font", StringComparison.OrdinalIgnoreCase) || name.Equals("system-ui", StringComparison.OrdinalIgnoreCase)
                || name.Equals("-apple-system", StringComparison.OrdinalIgnoreCase) || name.Equals("BlinkMacSystemFont", StringComparison.OrdinalIgnoreCase))
                break;   // the platform UI font
            try
            {
                var family = new FontFamily(name);
                if (FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var gt)
                    && gt.FamilyName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    result = family;
                    break;
                }
            }
            catch { }
        }
        _families[reported] = result;
        return result;
    }
}

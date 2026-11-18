using Qalam.Core;

/// <summary>Simulated OS: a text field + layout, to test the controller end to end.</summary>
sealed class FakePlatform : IKeyboardPlatform
{
    public string Name => "fake";
    public Func<KeyInput, bool>? KeyHandler { get; set; }
    public event Action? MouseClicked;
    public System.Text.StringBuilder Screen = new();
    public KeyboardLayout Current;
    readonly List<KeyboardLayout> _layouts;
    public FakePlatform(List<KeyboardLayout> l) { _layouts = l; Current = l[0]; }
    public bool HasPermissions() => true;
    public void RequestPermissions() { }
    public void Start() { }
    public IReadOnlyList<KeyboardLayout> GetLayouts() => _layouts;
    public KeyboardLayout? GetCurrentLayout(IReadOnlyList<KeyboardLayout> known) => Current;
    public void SwitchLayout(KeyboardLayout l) => Current = l;
    public void Replace(int erase, string text, Action? then = null) { Screen.Length -= Math.Min(erase, Screen.Length); Screen.Append(text); then?.Invoke(); }
    public void ReplayKey(SpecialKey k) => Screen.Append(k switch { SpecialKey.Space => " ", SpecialKey.Enter => "\n", _ => "\t" });
    public void ConvertSelection(Func<string, string?> convert) { var r = convert(Screen.ToString()); if (r != null) { Screen.Clear(); Screen.Append(r); } }
    public string? ForegroundApp() => "Notepad";
    public (int X, int Y, int W, int H)? GetCaretRect() => (100, 100, 1, 16);
    public void SetStartWithSystem(bool e) { }
    public void Dispose() { }
    public void Click() => MouseClicked?.Invoke();

    // physical typing helpers
    public void Press(KeyInput k) { bool sw = KeyHandler?.Invoke(k) ?? false; if (Environment.GetEnvironmentVariable("DBG") != null) Console.WriteLine($"{k} swallowed={sw}"); if (!sw) Apply(k); KeyHandler?.Invoke(k with { IsDown = false }); }
    void Apply(KeyInput k)
    {
        if (k.Special == SpecialKey.None) { var c = Current.Render(new KeyStroke(k.PhysicalKey, k.Shift, k.Caps)); if (c != '\0') Screen.Append(c); }
        else if (k.Special == SpecialKey.Space) Screen.Append(' ');
        else if (k.Special == SpecialKey.Tab) Screen.Append('\t');
        else if (k.Special == SpecialKey.Backspace && Screen.Length > 0) Screen.Length--;
    }
    /// <summary>Type text as if the user's fingers were on <paramref name="intended"/> layout.</summary>
    public void Type(string text, KeyboardLayout intended)
    {
        foreach (var ch in text)
        {
            if (ch == ' ') { Press(new KeyInput(true, SpecialKey.Space)); continue; }
            intended.TryEncode(ch.ToString(), out var st);
            Press(new KeyInput(true, SpecialKey.None, st[0].Key, st[0].Shift));
        }
    }
    public void PressTab() => Press(new KeyInput(true, SpecialKey.Tab, KeyName: "Tab"));
    public void Tap(SpecialKey mod) { KeyHandler?.Invoke(new KeyInput(true, mod)); KeyHandler?.Invoke(new KeyInput(false, mod)); }
}

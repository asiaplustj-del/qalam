using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qalam.Core;

namespace Qalam.App;

/// <summary>
/// Optional module: continues the phrase with a local language model — either the built-in
/// llama.cpp engine with a GGUF file, or an external Ollama server. Nothing leaves the computer.
/// Off by default.
/// </summary>
public sealed class NeuralService : IDisposable
{
    /// <summary>Where to get a model when the computer has none.</summary>
    public const string ModelsPage = "https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF";

    public sealed record ModelInfo(string Name, string Description);

    public static readonly ModelInfo[] Recommended =
    {
        new("gemma3:1b", "≈0,8 ГБ · быстрая, для любого компьютера"),
        new("qwen2.5:1.5b", "≈1 ГБ · быстрая"),
        new("llama3.2:3b", "≈2 ГБ · средняя"),
        new("gemma3:4b", "≈3,3 ГБ · лучше пишет, нужен мощный компьютер или Mac на M-чипе"),
    };

    private readonly QalamSettings _s;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private CancellationTokenSource? _cts;

    public bool? OllamaFound { get; private set; }
    public List<string> InstalledModels { get; private set; } = new();
    public string? LastError { get; private set; }

    private readonly IKeyboardPlatform? _platform;

    /// <summary>The built-in engine (llama.cpp's llama-server as a child process).</summary>
    public LlamaEngine Engine { get; } = new();
    public bool UseBuiltin => _s.NeuralBackend != "ollama";

    public NeuralService(QalamSettings settings, IKeyboardPlatform? platform = null)
    {
        _s = settings;
        _platform = platform;
        Engine.UnloadAfterMinutes = settings.NeuralUnloadMinutes;
        if (OperatingSystem.IsWindows())
            Engine.OnProcessStarted = p => Qalam.Platform.Windows.WindowsPlatform.KillWithQalam(p);
    }

    /// <summary>Apply changed settings. <paramref name="userAction"/>: the user just switched the module
    /// on or picked a model — load it right away so the first suggestion is quick.</summary>
    public void ApplySettings(bool userAction = false)
    {
        Engine.UnloadAfterMinutes = _s.NeuralUnloadMinutes;
        if (!_s.NeuralEnabled || !UseBuiltin) { if (Engine.State != LlamaEngine.EngineState.Stopped) Engine.Stop(); return; }
        if (userAction && ModelFiles.IsUsable(_s.NeuralModelPath)) Engine.Preload(_s.NeuralModelPath!);
    }

    public void Dispose() => Engine.Dispose();

    private string Url(string path) => _s.NeuralUrl.TrimEnd('/') + path;

    public async Task RefreshAsync()
    {
        try
        {
            var json = await _http.GetFromJsonAsync<JsonObject>(Url("/api/tags"));
            InstalledModels = json?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>() ?? "").Where(n => n != "").ToList() ?? new();
            OllamaFound = true; LastError = null;
        }
        catch (Exception e) { OllamaFound = false; InstalledModels = new(); LastError = e.Message; }
    }

    public bool ModelInstalled(string name) =>
        InstalledModels.Any(m => m == name || m == name + ":latest" || (name.Contains(':') ? false : m.StartsWith(name + ":")));

    /// <summary>Downloads a model through Ollama; progress 0..1.</summary>
    public async Task<bool> PullAsync(string model, IProgress<(double Fraction, string Status)> progress, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var req = new HttpRequestMessage(HttpMethod.Post, Url("/api/pull"))
            { Content = new StringContent(JsonSerializer.Serialize(new { name = model, stream = true }), Encoding.UTF8, "application/json") };
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (line.Length == 0) continue;
                var o = JsonNode.Parse(line);
                if (o?["error"] is { } err) { LastError = err.ToString(); return false; }
                double total = o?["total"]?.GetValue<double>() ?? 0, done = o?["completed"]?.GetValue<double>() ?? 0;
                progress.Report((total > 0 ? done / total : 0, o?["status"]?.ToString() ?? ""));
            }
            await RefreshAsync();
            return ModelInstalled(model);
        }
        catch (Exception e) { LastError = e.Message; return false; }
    }

    private DateTime _lastSkipLog;
    private int _asked, _answered, _empty;

    /// <summary>
    /// Called after each space and on pauses inside a word: waits a little (typing on cancels it),
    /// then asks the model to continue the text. The controller decides what to show.
    /// </summary>
    public void OnPhraseOpportunity(QalamController controller, QalamController.PhraseRequest req)
    {
        long version = req.Version;
        var fieldText = req.Text;
        if (!_s.NeuralEnabled) return;
        if (UseBuiltin && !ModelFiles.IsUsable(_s.NeuralModelPath)) { LogSkip("no model file chosen"); return; }
        if (_s.NeuralSkipOnBattery && _platform?.IsOnBattery() == true) { LogSkip("on battery"); return; }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var context = fieldText.Length > 800 ? fieldText[^800..] : fieldText;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!req.Urgent) await Task.Delay(Math.Clamp(_s.NeuralPauseMs, 0, 2000), cts.Token);
                // the real text of the field (also what was there before Qalam saw you type): the model
                // understands much more from a whole paragraph than from the last few words
                context = RealContext(context, fieldText);
                if (cts.IsCancellationRequested) return;
                if (context.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2) return;
                if (UseBuiltin && Engine.State != LlamaEngine.EngineState.Ready) LogSkip($"engine {Engine.State} — loading the model, suggestions start when it is ready");
                // inside a word a short answer is enough (and quicker): the word and a few more
                int tokens = req.MidWord ? Math.Min(10, _s.PhraseMaxTokens) : _s.PhraseMaxTokens;
                var raw = await CompleteRawAsync(context, cts.Token, tokens);
                if (cts.IsCancellationRequested) return;
                _asked++;
                if (string.IsNullOrWhiteSpace(raw)) _empty++; else _answered++;
                if (_asked % 20 == 1) Log.Info($"neural: {_asked} requests, {_answered} answers, {_empty} empty (engine {Engine.State})");
                if (raw != null) controller.OfferPhrase(version, raw, context);
            }
            catch { }
        });
    }

    private string RealContext(string context, string typed)
    {
        try
        {
            var real = _platform?.GetTextBeforeCaret(800);
            if (string.IsNullOrEmpty(real)) return context;
            // only if it really is the field being typed in: it must end with what Qalam saw typed
            var tail = typed.Length > 12 ? typed[^12..] : typed;
            if (tail.Length > 0 && !real.EndsWith(tail, StringComparison.Ordinal)) return context;
            return real.Replace("\r\n", "\n").Replace('\r', '\n');
        }
        catch { return context; }
    }

    private void LogSkip(string why)
    {
        if ((DateTime.UtcNow - _lastSkipLog).TotalSeconds < 60) return;
        _lastSkipLog = DateTime.UtcNow;
        Log.Info("neural: skipped — " + why);
    }

    /// <summary>For the "check" button: a cleaned continuation of <paramref name="context"/>.</summary>
    public async Task<string?> CompleteAsync(string context, CancellationToken ct) =>
        PhraseText.Clean(await CompleteRawAsync(context, ct), context, _s.PhraseMaxWords, _s.NeuralLength >= 3 ? 2 : 1);

    public async Task<string?> CompleteRawAsync(string context, CancellationToken ct, int maxTokens = 0)
    {
        if (maxTokens <= 0) maxTokens = _s.PhraseMaxTokens;
        if (UseBuiltin)
            return await Engine.CompleteAsync(_s.NeuralModelPath!, context, maxTokens, ct, LlamaEngine.InstructionFor(_s.NeuralLength));
        var body = new
        {
            model = _s.NeuralModel,
            prompt = context,
            raw = true,
            stream = false,
            keep_alive = "30m",
            options = new { num_predict = maxTokens, temperature = 0.2, top_p = 0.9, stop = new[] { "\n" } }
        };
        using var resp = await _http.PostAsJsonAsync(Url("/api/generate"), body, ct);
        if (!resp.IsSuccessStatusCode) { LastError = $"HTTP {(int)resp.StatusCode}"; return null; }
        var o = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        return o?["response"]?.GetValue<string>();
    }

    /// <summary>Keeps the first few words / up to the end of the sentence.</summary>
    public static string? Clean(string? raw, bool afterSpace)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = afterSpace ? raw.TrimStart() : raw;
        int cut = t.IndexOfAny(new[] { '.', '!', '?', '…' });
        if (cut >= 0) t = t[..(cut + 1)];
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 7) t = string.Join(' ', words.Take(7));
        t = t.TrimEnd();
        if (t.Length < 2 || t.Contains('<') || t.Contains('*') || t.Contains('#')) return null;
        return t;
    }

    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}

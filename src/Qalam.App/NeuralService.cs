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

    /// <summary>Called after each space: waits a little, then asks the model for a short continuation.</summary>
    public void OnPhraseOpportunity(QalamController controller, long version, string fieldText)
    {
        if (!_s.NeuralEnabled) return;
        if (UseBuiltin && !ModelFiles.IsUsable(_s.NeuralModelPath)) return;
        if (_s.NeuralSkipOnBattery && _platform?.IsOnBattery() == true) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var context = fieldText.Length > 800 ? fieldText[^800..] : fieldText;
        if (context.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_s.NeuralDelayMs, cts.Token);
                var text = await CompleteAsync(context, cts.Token);
                if (text != null && !cts.IsCancellationRequested) controller.OfferPhrase(version, text);
            }
            catch { }
        });
    }

    public async Task<string?> CompleteAsync(string context, CancellationToken ct)
    {
        if (UseBuiltin)
        {
            var raw = await Engine.CompleteAsync(_s.NeuralModelPath!, context, 14, ct);
            return Clean(raw, context.EndsWith(' '));
        }
        var body = new
        {
            model = _s.NeuralModel,
            prompt = context,
            raw = true,
            stream = false,
            keep_alive = "30m",
            options = new { num_predict = 14, temperature = 0.2, top_p = 0.9, stop = new[] { "\n" } }
        };
        var sw = Stopwatch.StartNew();
        using var resp = await _http.PostAsJsonAsync(Url("/api/generate"), body, ct);
        if (!resp.IsSuccessStatusCode) { LastError = $"HTTP {(int)resp.StatusCode}"; return null; }
        var o = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        return Clean(o?["response"]?.GetValue<string>(), context.EndsWith(' '));
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

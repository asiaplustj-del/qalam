using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Qalam.Core;

/// <summary>
/// Built-in model engine: runs llama.cpp's <c>llama-server</c> (shipped inside Qalam) as a
/// separate child process with a GGUF file. Loaded on demand, unloaded after idle time.
/// If the engine crashes or hangs, Qalam itself keeps working.
/// </summary>
public sealed class LlamaEngine : IDisposable
{
    public enum EngineState { Stopped, Loading, Ready, Failed }

    public EngineState State { get; private set; } = EngineState.Stopped;
    public string? Error { get; private set; }
    public string? LoadedModel { get; private set; }
    public event Action? StateChanged;

    /// <summary>Called right after the server process starts (Windows: tie it to Qalam's lifetime).</summary>
    public Action<Process>? OnProcessStarted { get; set; }
    /// <summary>Minutes without requests before the model is unloaded (0 = never).</summary>
    public int UnloadAfterMinutes { get; set; } = 10;
    public int ContextSize { get; set; } = 1024;

    private Process? _proc;
    private int _port;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private DateTime _lastUse = DateTime.UtcNow;
    private readonly Timer _idleTimer;
    private readonly LinkedList<string> _logTail = new();
    private static string PidFile => Path.Combine(QalamSettings.DataDir, "llama-server.pid");

    private static int _staleChecked;

    public LlamaEngine()
    {
        _idleTimer = new Timer(_ => CheckIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        if (Interlocked.Exchange(ref _staleChecked, 1) == 0) KillStale();   // once per Qalam start
    }

    /// <summary>The llama-server executable bundled with Qalam (or QALAM_LLAMA_SERVER for tests).</summary>
    public static string? ServerPath
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("QALAM_LLAMA_SERVER");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            var exe = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
            var p = Path.Combine(AppContext.BaseDirectory, "llama", exe);
            return File.Exists(p) ? p : null;
        }
    }

    public static bool Available => ServerPath != null;

    /// <summary>Resident memory of the engine process, bytes (0 if not running).</summary>
    public long MemoryBytes
    {
        get { try { var p = _proc; if (p == null || p.HasExited) return 0; p.Refresh(); return p.WorkingSet64; } catch { return 0; } }
    }

    /// <summary>Start loading the model in the background (no-op if already loaded/loading).</summary>
    public void Preload(string modelPath) => _ = EnsureStartedAsync(modelPath);

    /// <summary>
    /// Continue <paramref name="prompt"/>. Returns null while the model is still loading
    /// (typing is never blocked waiting for it), on errors, or when cancelled.
    /// </summary>
    public async Task<string?> CompleteAsync(string modelPath, string prompt, int maxTokens, CancellationToken ct, string? instruction = null)
    {
        _lastUse = DateTime.UtcNow;
        if (State != EngineState.Ready || LoadedModel != modelPath)
        {
            Preload(modelPath);
            return null;
        }
        try
        {
            // Chat models (Gemma, Qwen…) answer a request better than they continue raw text:
            // ask for the continuation through the model's own chat template.
            var chat = new
            {
                messages = new object[]
                {
                    new { role = "system", content = instruction ?? Instruction },
                    new { role = "user", content = prompt },
                },
                max_tokens = maxTokens,
                temperature = 0.2,
                top_k = 40,
                top_p = 0.9,
                stop = new[] { "\n" },
                cache_prompt = true,
            };
            using (var resp = await _http.PostAsJsonAsync($"http://127.0.0.1:{_port}/v1/chat/completions", chat, ct))
            {
                if (resp.IsSuccessStatusCode)
                {
                    var o = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
                    _lastUse = DateTime.UtcNow;
                    var text = o?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                else Log.Info($"llama chat HTTP {(int)resp.StatusCode}, falling back to raw completion");
            }
            // models without a chat template: plain continuation
            var body = new { prompt, n_predict = maxTokens, temperature = 0.2, top_k = 40, top_p = 0.9, stop = new[] { "\n" }, cache_prompt = true };
            using var r2 = await _http.PostAsJsonAsync($"http://127.0.0.1:{_port}/completion", body, ct);
            if (!r2.IsSuccessStatusCode) { Log.Info($"llama /completion HTTP {(int)r2.StatusCode}"); return null; }
            var o2 = await r2.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
            _lastUse = DateTime.UtcNow;
            return o2?["content"]?.GetValue<string>();
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception e)
        {
            if (_proc is { HasExited: true }) SetState(EngineState.Failed, "движок неожиданно закрылся");
            Log.Error("llama completion", e);
            return null;
        }
    }

    /// <summary>The request for a chosen phrase length (1 short, 2 medium, 3 long).</summary>
    public static string InstructionFor(int length) => length switch
    {
        1 => Instruction.Replace("от 2 до 6 слов", "от 1 до 3 слов"),
        3 => Instruction.Replace("от 2 до 6 слов", "до 12 слов; можно закончить предложение и начать следующее"),
        _ => Instruction,
    };

    /// <summary>What we ask the model to do (works for Russian, Tajik and English text).</summary>
    public const string Instruction =
        "Ты — автодополнение текста, как подсказки на клавиатуре телефона. Пользователь присылает текст, который он сейчас печатает. " +
        "Допиши естественное продолжение последней фразы: от 2 до 6 слов, на том же языке и в том же стиле. " +
        "Если последнее слово не допечатано — начни с его окончания. " +
        "Согласуй слова с уже написанными по роду, числу и падежу. " +
        "Выведи ТОЛЬКО само продолжение: без кавычек, пояснений, приветствий и без повторения уже написанного текста.";

    /// <summary>Waits (up to <paramref name="timeout"/>) until the model is loaded. For the "check" button.</summary>
    public async Task<bool> WaitReadyAsync(string modelPath, TimeSpan timeout)
    {
        Preload(modelPath);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (State == EngineState.Ready && LoadedModel == modelPath) return true;
            if (State == EngineState.Failed) return false;
            await Task.Delay(250);
        }
        return false;
    }

    private async Task EnsureStartedAsync(string modelPath)
    {
        if ((State is EngineState.Ready or EngineState.Loading) && LoadedModel == modelPath) return;
        if (!await _startLock.WaitAsync(0)) return;   // a start is already in progress
        try
        {
            if (State == EngineState.Ready && LoadedModel == modelPath) return;
            StopProcess();
            var server = ServerPath;
            if (server == null) { SetState(EngineState.Failed, "движок не найден в этой сборке Qalam"); return; }
            if (!File.Exists(modelPath)) { SetState(EngineState.Failed, "файл модели не найден"); return; }

            if (OperatingSystem.IsMacOS()) Unquarantine(Path.GetDirectoryName(server)!);
            _port = FreePort();
            LoadedModel = modelPath;
            SetState(EngineState.Loading, null);
            var psi = new ProcessStartInfo(server)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(server)!,
            };
            foreach (var a in new[] { "-m", modelPath, "--host", "127.0.0.1", "--port", _port.ToString(),
                                      "-c", ContextSize.ToString(), "-np", "1", "--no-webui", "--reasoning-budget", "0" })
                psi.ArgumentList.Add(a);
            if (OperatingSystem.IsWindows())
            {   // CPU build: leave half of the cores to the user's apps
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add(Math.Max(2, Environment.ProcessorCount / 2).ToString());
            }
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => AddLog(e.Data);
            p.ErrorDataReceived += (_, e) => AddLog(e.Data);
            Log.Info($"llama-server start: {Path.GetFileName(modelPath)} on port {_port}");
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _proc = p;
            OnProcessStarted?.Invoke(p);
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            try { Directory.CreateDirectory(QalamSettings.DataDir); File.WriteAllText(PidFile, p.Id.ToString()); } catch { }

            // wait until the model is loaded (a few seconds for 1–4 GB from SSD)
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromMinutes(3))
            {
                if (p.HasExited)
                {
                    var tail = string.Join(" | ", _logTail.TakeLast(6));
                    Log.Error($"llama-server exited with code {p.ExitCode}: {tail}");
                    SetState(EngineState.Failed, ExplainFailure(tail));
                    _proc = null;
                    return;
                }
                try
                {
                    using var r = await _http.GetAsync($"http://127.0.0.1:{_port}/health");
                    if (r.StatusCode == HttpStatusCode.OK)
                    {
                        _lastUse = DateTime.UtcNow;
                        Log.Info($"llama-server ready in {sw.Elapsed.TotalSeconds:F1}s");
                        SetState(EngineState.Ready, null);
                        return;
                    }
                }
                catch { /* not listening yet */ }
                await Task.Delay(400);
            }
            Log.Error("llama-server: model did not load in 3 minutes");
            StopProcess();
            SetState(EngineState.Failed, "модель не загрузилась за 3 минуты");
        }
        catch (Exception e)
        {
            Log.Error("llama-server start", e);
            StopProcess();
            SetState(EngineState.Failed, e.Message);
        }
        finally { _startLock.Release(); }
    }

    /// <summary>
    /// Files from a downloaded .dmg carry macOS's quarantine flag; the user already approved Qalam,
    /// so clear it on Qalam's own engine folder, otherwise macOS may refuse to start llama-server.
    /// </summary>
    private static bool _unquarantined;
    private static void Unquarantine(string dir)
    {
        if (_unquarantined) return;
        _unquarantined = true;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/xattr", new[] { "-dr", "com.apple.quarantine", dir })
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
            p?.WaitForExit(5000);
        }
        catch { }
    }

    private static string ExplainFailure(string log)
    {
        var l = log.ToLowerInvariant();
        if (l.Contains("unknown model architecture") || l.Contains("unsupported")) return "эта модель не поддерживается движком";
        if (l.Contains("failed to load model") || l.Contains("invalid magic") || l.Contains("gguf_init")) return "файл модели повреждён или это не модель GGUF";
        if (l.Contains("failed to open") || l.Contains("permission") || l.Contains("operation not permitted")) return "нет доступа к файлу модели — выберите его заново или скопируйте к себе";
        if (l.Contains("out of memory") || l.Contains("failed to allocate")) return "не хватает памяти для этой модели";
        return "движок не запустился (подробности в журнале)";
    }

    private void AddLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_logTail)
        {
            _logTail.AddLast(line.Length > 300 ? line[..300] : line);
            while (_logTail.Count > 40) _logTail.RemoveFirst();
        }
    }

    private void CheckIdle()
    {
        if (State != EngineState.Ready || UnloadAfterMinutes <= 0) return;
        if (DateTime.UtcNow - _lastUse < TimeSpan.FromMinutes(UnloadAfterMinutes)) return;
        Log.Info($"llama-server unloaded after {UnloadAfterMinutes} idle minutes");
        Stop();
    }

    /// <summary>Unload the model and stop the engine process.</summary>
    public void Stop()
    {
        StopProcess();
        LoadedModel = null;
        SetState(EngineState.Stopped, null);
    }

    private void StopProcess()
    {
        var p = _proc;
        _proc = null;
        if (p != null)
        {
            try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } } catch { }
            p.Dispose();
        }
        try { File.Delete(PidFile); } catch { }
    }

    /// <summary>An engine left over from a crashed Qalam would hold gigabytes of memory — close it.</summary>
    private static void KillStale()
    {
        try
        {
            if (!File.Exists(PidFile)) return;
            if (int.TryParse(File.ReadAllText(PidFile).Trim(), out var pid))
            {
                using var p = Process.GetProcessById(pid);
                if (p.ProcessName.Contains("llama-server", StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    Log.Info($"killed stale llama-server {pid}");
                }
            }
            File.Delete(PidFile);
        }
        catch { try { File.Delete(PidFile); } catch { } }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private void SetState(EngineState s, string? error)
    {
        State = s; Error = error;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        StopProcess();
    }
}

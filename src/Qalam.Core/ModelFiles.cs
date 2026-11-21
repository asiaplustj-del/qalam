namespace Qalam.Core;

/// <summary>Finding, copying and describing GGUF model files on this computer.</summary>
public static class ModelFiles
{
    public sealed record Found(string Path, long Bytes, string Source)
    {
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
        public override string ToString() => $"{Name} · {Size(Bytes)} · {Source}";
    }

    /// <summary>Qalam's own folder for models.</summary>
    public static string ModelsDir => System.IO.Path.Combine(QalamSettings.DataDir, "models");

    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} ГБ" : $"{bytes / (double)(1L << 20):0} МБ";

    public static bool IsUsable(string? path) =>
        !string.IsNullOrEmpty(path) && File.Exists(path) && path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Folders where other apps keep their models (Cotypist, LM Studio, Hugging Face cache…).</summary>
    public static IEnumerable<(string Dir, int Depth)> SearchRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return (ModelsDir, 2);
        yield return (System.IO.Path.Combine(home, ".lmstudio", "models"), 4);
        yield return (System.IO.Path.Combine(home, ".cache", "huggingface", "hub"), 6);
        yield return (System.IO.Path.Combine(home, ".cache", "llama.cpp"), 3);
        if (OperatingSystem.IsMacOS())
        {
            // Application Support of other apps (not sandbox containers: those trigger a macOS privacy prompt)
            yield return (System.IO.Path.Combine(home, "Library", "Application Support"), 5);
            yield return (System.IO.Path.Combine(home, "Library", "Caches", "llama.cpp"), 3);
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 5);
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 4);
            yield return (System.IO.Path.Combine(home, "Downloads"), 2);
        }
    }

    /// <summary>All GGUF language models found in the usual places (vision projectors and tiny files skipped).</summary>
    public static List<Found> Scan(CancellationToken ct = default)
    {
        var result = new Dictionary<string, Found>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, depth) in SearchRoots())
            Walk(dir, depth, result, ct);
        return result.Values.OrderByDescending(f => f.Source == "Qalam").ThenBy(f => f.Bytes).ToList();
    }

    private static void Walk(string dir, int depth, Dictionary<string, Found> result, CancellationToken ct)
    {
        if (depth < 0 || ct.IsCancellationRequested || !Directory.Exists(dir)) return;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.System };
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.gguf", opts))
            {
                var name = System.IO.Path.GetFileName(f).ToLowerInvariant();
                if (name.Contains("mmproj") || name.Contains("vocab")) continue;
                long len;
                try { len = new FileInfo(f).Length; } catch { continue; }
                if (len < 50L << 20) continue;
                result[f] = new Found(f, len, SourceOf(f));
            }
            foreach (var d in Directory.EnumerateDirectories(dir, "*", opts))
            {
                var n = System.IO.Path.GetFileName(d);
                if (n.StartsWith('.') && !n.Equals(".lmstudio", StringComparison.OrdinalIgnoreCase)) continue;
                if (n is "Containers" or "Group Containers" or "node_modules" or "Microsoft" or "Packages" or "Temp") continue;
                Walk(d, depth - 1, result, ct);
            }
        }
        catch { /* unreadable folder */ }
    }

    public static string SourceOf(string path)
    {
        var p = path.Replace('\\', '/').ToLowerInvariant();
        if (p.StartsWith(ModelsDir.Replace('\\', '/').ToLowerInvariant())) return "Qalam";
        if (p.Contains("cotypist")) return "Cotypist";
        if (p.Contains("/.lmstudio/") || p.Contains("lm studio") || p.Contains("lmstudio")) return "LM Studio";
        if (p.Contains("huggingface")) return "Hugging Face";
        if (p.Contains("llama.cpp")) return "llama.cpp";
        if (p.Contains("/jan/") || p.Contains("jan.ai")) return "Jan";
        if (p.Contains("gpt4all") || p.Contains("nomic.ai")) return "GPT4All";
        if (p.Contains("/downloads/")) return "Загрузки";
        var parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "";
        return parent.Length > 0 ? parent : "другая программа";
    }

    /// <summary>Copies a model into Qalam's folder (so it survives uninstalling the app it came from).</summary>
    public static async Task<string> CopyToQalamAsync(string source, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ModelsDir);
        var dest = System.IO.Path.Combine(ModelsDir, System.IO.Path.GetFileName(source));
        if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(source).Length) return dest;
        var tmp = dest + ".part";
        try
        {
            await using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                var buf = new byte[4 << 20];
                long total = src.Length, done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    progress.Report(total > 0 ? (double)done / total : 0);
                }
            }
            File.Move(tmp, dest, overwrite: true);
            return dest;
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}

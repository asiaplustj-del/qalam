using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Qalam.Core;

/// <summary>
/// Built-in updates from the project's GitHub Releases. Files downloaded by Qalam itself (not by a
/// browser) are not marked "from the internet", so macOS Gatekeeper / Windows SmartScreen don't
/// ask again, and the persistent signature keeps macOS permissions.
/// </summary>
public static class Updater
{
    public const string Repo = "asiaplustj-del/qalam";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";

    public sealed record Release(Version Version, string Tag, string PageUrl, string? AssetUrl, string? AssetName, long AssetBytes);

    public static Version Current
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static bool IsNewer(Version candidate) => candidate.CompareTo(Current) > 0;

    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        var t = tag.TrimStart('v', 'V');
        return Version.TryParse(t, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>Which release file fits this computer.</summary>
    public static bool AssetFits(string name)
    {
        if (OperatingSystem.IsMacOS())
            return name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase) &&
                   name.Contains(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "AppleSilicon" : "Intel", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
            return name.StartsWith("Qalam-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        using var http = NewClient(TimeSpan.FromSeconds(20));
        var json = await http.GetFromJsonAsync<JsonObject>($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        var tag = json?["tag_name"]?.GetValue<string>();
        var version = ParseTag(tag);
        if (json == null || version == null) return null;
        string? url = null, name = null; long size = 0;
        foreach (var a in json["assets"]?.AsArray() ?? new JsonArray())
        {
            var n = a?["name"]?.GetValue<string>() ?? "";
            if (!AssetFits(n)) continue;
            name = n;
            url = a?["browser_download_url"]?.GetValue<string>();
            size = a?["size"]?.GetValue<long>() ?? 0;
            break;
        }
        return new Release(version, tag!, json["html_url"]?.GetValue<string>() ?? ReleasesPage, url, name, size);
    }

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Qalam/{Current}");   // GitHub API requires a User-Agent
        return http;
    }

    public static string UpdatesDir => Path.Combine(QalamSettings.DataDir, "updates");

    public static async Task<string> DownloadAsync(Release r, IProgress<double> progress, CancellationToken ct)
    {
        if (r.AssetUrl == null || r.AssetName == null) throw new InvalidOperationException("в релизе нет файла для этого компьютера");
        Directory.CreateDirectory(UpdatesDir);
        var dest = Path.Combine(UpdatesDir, r.AssetName);
        var tmp = dest + ".part";
        using var http = NewClient(Timeout.InfiniteTimeSpan);
        using var resp = await http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? r.AssetBytes, done = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report((double)done / total);
            }
        }
        if (total > 0 && done != total) throw new IOException("файл скачался не полностью");
        File.Move(tmp, dest, overwrite: true);
        return dest;
    }


    /// <summary>Shell script that swaps the app bundle after Qalam exits (public for tests).</summary>
    public static string MacInstallScript(string downloaded, string app, int pid, string mount, string staging, string log)
    {
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        var old = app + ".old";
            var script = $"""
                #!/bin/sh
                exec >> {Q(log)} 2>&1
                echo "=== update $(date)"
                mkdir -p {Q(mount)}
                if ! hdiutil attach -nobrowse -readonly -noautoopen -mountpoint {Q(mount)} {Q(downloaded)}; then echo "attach failed"; exit 1; fi
                rm -rf {Q(staging)}
                ditto {Q(Path.Combine(mount, "Qalam.app"))} {Q(staging)}
                hdiutil detach {Q(mount)} -quiet || true
                while kill -0 {pid} 2>/dev/null; do sleep 0.3; done
                rm -rf {Q(old)}
                # swap the bundles; if macOS refuses (App Management), keep the old app and show the new one
                if mv {Q(app)} {Q(old)}; then
                  if mv {Q(staging)} {Q(app)}; then
                    rm -rf {Q(old)} || true
                    xattr -dr com.apple.quarantine {Q(app)} || true
                    rm -f {Q(downloaded)}
                    echo "updated"
                  else
                    mv {Q(old)} {Q(app)}
                    echo "replace failed, restored the old version"
                    open {Q(downloaded)}
                  fi
                else
                  echo "cannot replace the app (permission?)"
                  open {Q(downloaded)}
                fi
                open {Q(app)}
                """;
        return script.Replace("\r", "");
    }

    /// <summary>The installed Qalam.app bundle (macOS), or null when running outside a bundle.</summary>
    public static string? MacAppBundle()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/'));   // …/Qalam.app/Contents/MacOS
        var bundle = dir.Parent?.Parent;
        return bundle != null && bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle.FullName : null;
    }

    /// <summary>Installed by the setup program (not the portable zip) — only then can Windows update itself.</summary>
    public static bool WindowsInstalled()
    {
        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        return AppContext.BaseDirectory.StartsWith(programs, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanSelfUpdate => OperatingSystem.IsMacOS() ? MacAppBundle() != null : OperatingSystem.IsWindows() && WindowsInstalled();

    /// <summary>
    /// Starts installing the downloaded file. The caller must quit Qalam right after this returns:
    /// the installer waits for Qalam to exit, replaces it and starts the new version.
    /// </summary>
    public static void StartInstall(string downloaded)
    {
        int pid = Environment.ProcessId;
        if (OperatingSystem.IsWindows())
        {
            // Inno Setup: silent install; the [Run] entry with Check: WizardSilent restarts Qalam
            Process.Start(new ProcessStartInfo(downloaded, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            var app = MacAppBundle() ?? throw new InvalidOperationException("Qalam запущен не из программы Qalam.app");
            var mount = Path.Combine(Path.GetTempPath(), "qalam-update-" + Guid.NewGuid().ToString("N")[..8]);
            var staging = Path.Combine(Path.GetDirectoryName(app)!, ".Qalam-update.app");
            var log = Path.Combine(QalamSettings.DataDir, "update.log");
            static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
            var script = MacInstallScript(downloaded, app, pid, mount, staging, log);
            var scriptPath = Path.Combine(UpdatesDir, "install.sh");
            Directory.CreateDirectory(UpdatesDir);
            File.WriteAllText(scriptPath, script);
            Process.Start(new ProcessStartInfo("/bin/sh", new[] { scriptPath }) { UseShellExecute = false, CreateNoWindow = true });
            return;
        }
        throw new PlatformNotSupportedException();
    }
}

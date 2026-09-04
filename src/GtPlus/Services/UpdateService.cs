using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;

namespace GtPlus.Services;

public static class AppVersion
{
    private static string? _cached;
    public static string Current => _cached ??= Read();

    private static string Read()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://GtPlus/Assets/version.json"));
            using var doc    = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("version", out var value) &&
                value.GetString() is { Length: > 0 } version)
            {
                return version;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"AppVersion: could not read version.json - {ex.Message}");
        }

        // fall back to whatever the assembly was stamped with
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(informational)) return "unknown";

        // strip the "+<commit sha>" source-revision suffix
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational.Substring(0, plus) : informational;
    }
}

public record UpdateInfo(string Version, string Notes, string HtmlUrl, DateTimeOffset? PublishedAt);

public record UpdateCheckResult(bool Success, UpdateInfo? Latest, bool IsNewer, string? Error)
{
    public static UpdateCheckResult Failed(string error) => new(false, null, false, error);
}

public class UpdateService
{
    public const string ReleasesUrl = "https://github.com/Coow/GT-Plus/releases";
    private const string LatestApi  = "https://api.github.com/repos/Coow/GT-Plus/releases/latest";

    // GitHub rejects requests without a User-Agent; the timeout keeps a startup check from hanging the app
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GtPlus", AppVersion.Current));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken token = default)
    {
        try
        {
            using var response = await Http.GetAsync(LatestApi, token);
            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Failed($"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}");

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag))
                return UpdateCheckResult.Failed("The latest release has no version tag");

            var notes = root.TryGetProperty("body", out var bodyValue) ? bodyValue.GetString() ?? "" : "";
            var url   = root.TryGetProperty("html_url", out var urlValue) ? urlValue.GetString() ?? ReleasesUrl : ReleasesUrl;

            DateTimeOffset? published = root.TryGetProperty("published_at", out var pub) &&
                                        pub.TryGetDateTimeOffset(out var when) ? when : null;

            var latest = new UpdateInfo(Normalise(tag), notes.Trim(), url, published);
            var isNewer = Compare(latest.Version, AppVersion.Current) > 0;

            Logger.Info($"UpdateService: running {AppVersion.Current}, latest release {latest.Version}, newer = {isNewer}");
            return new UpdateCheckResult(true, latest, isNewer, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"UpdateService: check failed - {ex.Message}");
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    public static string Normalise(string version)
    {
        var trimmed = version.Trim();
        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? trimmed.Substring(1) : trimmed;
    }

    public static int Compare(string a, string b)
    {
        var left  = Parts(a);
        var right = Parts(b);

        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var l = i < left.Length  ? left[i]  : 0;
            var r = i < right.Length ? right[i] : 0;
            if (l != r) return l.CompareTo(r);
        }
        return 0;
    }

    private static int[] Parts(string version)
    {
        var core = Normalise(version);

        var cut = core.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) core = core.Substring(0, cut);

        var pieces = core.Split('.');
        var parts  = new int[pieces.Length];
        for (int i = 0; i < pieces.Length; i++)
            parts[i] = int.TryParse(pieces[i], out var n) ? n : 0;

        return parts;
    }
}

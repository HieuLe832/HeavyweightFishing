using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chat.Api.Auth;
using Chat.Api.Files;
using Microsoft.Extensions.Options;

namespace Chat.Api.Avatars;

/// <summary>
/// Process 5.0's side of the Avatar Provider, an optional external entity: reads its <c>catalog.json</c> (cached briefly)
/// and fetches images on demand. Images are never cached here, so a provider that is down hides the avatars at once, and
/// nothing a user picked is ever cleared because of it.
/// </summary>
public sealed partial class AvatarCatalog(
    IOptions<AvatarProviderOptions> options, IHttpClientFactory http, TimeProvider time, ILogger<AvatarCatalog> log)
{
    public const string HttpClientName = "avatar-provider";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Snapshot? _snapshot;

    private sealed record Snapshot(DateTimeOffset FetchedAt, IReadOnlyDictionary<string, string> Files);
    private sealed record CatalogBody(List<CatalogEntry>? Avatars);
    private sealed record CatalogEntry(string? Key, string? File);

    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.BaseUrl);

    [GeneratedRegex("^[a-z0-9-]{1,64}$")]
    private static partial Regex KeyPattern();

    // A plain file name: no directories, no "..", so the provider can only be asked for its own files.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex FilePattern();

    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>The keys in the catalog, sorted. Empty when the feature is off.</summary>
    public async Task<Result<IReadOnlyList<string>>> KeysAsync(CancellationToken ct)
    {
        if (!Enabled) return Array.Empty<string>();
        var catalog = await CatalogAsync(ct);
        if (catalog is null) return AuthError.AvatarProviderUnavailable;
        return catalog.Keys.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Null when the key may be picked: it is well formed and the provider lists it right now.</summary>
    public async Task<AuthError?> CheckKeyAsync(string key, CancellationToken ct)
    {
        if (!Enabled) return AuthError.AvatarProviderUnavailable;
        if (!IsValidKey(key)) return AuthError.AvatarNotFound;
        var catalog = await CatalogAsync(ct);
        if (catalog is null) return AuthError.AvatarProviderUnavailable;
        return catalog.ContainsKey(key) ? null : AuthError.AvatarNotFound;
    }

    /// <summary>The image for a key, checked like an upload: its type comes from the bytes, never from the provider.</summary>
    public async Task<Result<MediaDownload>> OpenAsync(string key, CancellationToken ct)
    {
        if (!Enabled) return AuthError.AvatarProviderUnavailable;
        if (!IsValidKey(key)) return AuthError.AvatarNotFound;
        var catalog = await CatalogAsync(ct);
        if (catalog is null) return AuthError.AvatarProviderUnavailable;
        if (!catalog.TryGetValue(key, out var file)) return AuthError.AvatarNotFound;

        byte[] bytes;
        try
        {
            using var response = await Client().GetAsync(Uri.EscapeDataString(file), HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return AuthError.AvatarNotFound;
            if (!response.IsSuccessStatusCode) return Unavailable($"answered {(int)response.StatusCode} for {file}");
            var max = options.Value.MaxBytes;
            if (response.Content.Headers.ContentLength > max) return AuthError.AvatarProviderInvalid;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > max) return AuthError.AvatarProviderInvalid;
            }
            bytes = buffer.ToArray();
        }
        catch (Exception e) when (IsProviderFailure(e, ct))
        {
            return Unavailable(e.Message);
        }

        var sniffed = MediaSniffer.Detect(bytes);
        if (sniffed is not { IsImage: true }) return AuthError.AvatarProviderInvalid;
        return new MediaDownload(bytes, sniffed.MimeType, $"\"{Convert.ToHexStringLower(SHA256.HashData(bytes))}\"", Inline: true);
    }

    /// <summary>The key → file map, from the cache while it is fresh. Null when the provider cannot be read.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> CatalogAsync(CancellationToken ct)
    {
        var maxAge = TimeSpan.FromSeconds(options.Value.CatalogCacheSeconds);
        if (Fresh(_snapshot, maxAge) is { } cached) return cached;

        await _refresh.WaitAsync(ct);
        try
        {
            if (Fresh(_snapshot, maxAge) is { } again) return again; // another request refreshed it meanwhile
            var files = await FetchCatalogAsync(ct);
            if (files is not null) _snapshot = new Snapshot(time.GetUtcNow(), files); // failures are not cached
            return files;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private IReadOnlyDictionary<string, string>? Fresh(Snapshot? snapshot, TimeSpan maxAge) =>
        snapshot is not null && time.GetUtcNow() - snapshot.FetchedAt < maxAge ? snapshot.Files : null;

    private async Task<IReadOnlyDictionary<string, string>?> FetchCatalogAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Client().GetAsync("catalog.json", ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Avatar provider answered {Status} for catalog.json", (int)response.StatusCode);
                return null;
            }
            var body = await response.Content.ReadFromJsonAsync<CatalogBody>(Json, ct);
            if (body?.Avatars is null)
            {
                log.LogWarning("Avatar provider sent a catalog without an avatars list");
                return null;
            }
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in body.Avatars)
            {
                if (IsValidKey(entry.Key) && entry.File is { } file && FilePattern().IsMatch(file) && !file.Contains("..", StringComparison.Ordinal))
                    files[entry.Key!] = file;
                else
                    log.LogWarning("Avatar provider catalog entry skipped: key {Key}, file {File}", entry.Key, entry.File);
            }
            return files;
        }
        catch (Exception e) when (IsProviderFailure(e, ct) || e is JsonException or NotSupportedException)
        {
            log.LogWarning("Avatar provider catalog could not be read: {Error}", e.Message);
            return null;
        }
    }

    private HttpClient Client()
    {
        var client = http.CreateClient(HttpClientName);
        client.BaseAddress ??= new Uri(options.Value.BaseUrl!.TrimEnd('/') + "/");
        return client;
    }

    // A timeout surfaces as TaskCanceledException without the caller having cancelled.
    private static bool IsProviderFailure(Exception e, CancellationToken ct) =>
        e is HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested);

    private AuthError Unavailable(string reason)
    {
        log.LogWarning("Avatar provider unavailable: {Reason}", reason);
        return AuthError.AvatarProviderUnavailable;
    }
}

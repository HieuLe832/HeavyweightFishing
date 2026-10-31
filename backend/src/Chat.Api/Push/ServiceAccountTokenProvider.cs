using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Chat.Api.Push;

public interface IAccessTokenProvider
{
    Task<string> GetAsync(CancellationToken ct);
}

/// <summary>
/// Turns a Google service account into OAuth access tokens without a Google package: an RS256 JWT exchanged at the token URI.
/// The token is cached until five minutes before it expires; one request serves concurrent callers.
/// </summary>
public sealed class ServiceAccountTokenProvider(HttpClient http, ServiceAccount account, TimeProvider time) : IAccessTokenProvider
{
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _renewAt;

    public async Task<string> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (_token is null || now >= _renewAt)
            {
                (_token, var lifetime) = await RequestAsync(now, ct);
                _renewAt = now + lifetime - RenewBefore;
            }
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(string Token, TimeSpan Lifetime)> RequestAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var response = await http.PostAsync(account.TokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = Assertion(now),
        }), ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Google's token endpoint answered {(int)response.StatusCode}", null, response.StatusCode);

        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("access_token", out var token) && token.GetString() is { Length: > 0 } value)
                return (value, TimeSpan.FromSeconds(body.TryGetProperty("expires_in", out var seconds) && seconds.TryGetInt32(out var s) ? s : 3600));
        }
        catch (JsonException)
        {
            // falls through to the error below
        }
        throw new HttpRequestException("Google's token endpoint answered without an access token");
    }

    private string Assertion(DateTimeOffset now)
    {
        var header = Base64UrlTextEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = Base64UrlTextEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = account.ClientEmail, scope = Scope, aud = account.TokenUri, iat = now.ToUnixTimeSeconds(), exp = now.AddHours(1).ToUnixTimeSeconds(),
        }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(account.PrivateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{claims}.{Base64UrlTextEncoder.Encode(signature)}";
    }
}

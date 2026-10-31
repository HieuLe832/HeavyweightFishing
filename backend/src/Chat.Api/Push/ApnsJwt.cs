using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Chat.Api.Push;

/// <summary>The ES256 provider token APNs asks for. Apple wants it renewed between every 20 and 60 minutes, so it is reused for 50.</summary>
public sealed class ApnsJwt(ECDsa key, string keyId, string teamId, TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(50);

    private readonly object _gate = new();
    private string? _token;
    private DateTimeOffset _issuedAt;

    public string Token()
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_token is null || now - _issuedAt >= Lifetime)
            {
                _token = Sign(now);
                _issuedAt = now;
            }
            return _token;
        }
    }

    private string Sign(DateTimeOffset now)
    {
        var header = Base64UrlTextEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = keyId }));
        var claims = Base64UrlTextEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = teamId, iat = now.ToUnixTimeSeconds() }));
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{header}.{claims}.{Base64UrlTextEncoder.Encode(signature)}";
    }
}

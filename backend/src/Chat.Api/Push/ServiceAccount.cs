using System.Text.Json;

namespace Chat.Api.Push;

/// <summary>The parts of a Google service account file that FCM needs.</summary>
public record ServiceAccount(string ProjectId, string ClientEmail, string PrivateKeyPem, string TokenUri)
{
    private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    /// <summary>Reads <c>project_id</c>, <c>client_email</c>, <c>private_key</c> and (optionally) <c>token_uri</c>; names every missing field.</summary>
    public static ServiceAccount Load(string path)
    {
        JsonElement json;
        try
        {
            json = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"Push:Fcm:ServiceAccountJsonPath is not a JSON file: {path}", e);
        }

        string? Read(string name) => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var missing = new[] { "project_id", "client_email", "private_key" }.Where(n => string.IsNullOrWhiteSpace(Read(n))).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"The FCM service account file {path} lacks {string.Join(", ", missing)}.");
        return new ServiceAccount(Read("project_id")!, Read("client_email")!, Read("private_key")!, Read("token_uri") is { Length: > 0 } uri ? uri : DefaultTokenUri);
    }
}

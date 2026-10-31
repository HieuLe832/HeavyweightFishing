namespace Chat.Api.Push;

/// <summary>The <c>Push</c> section. A platform with nothing set uses the logging sender; one that is partly set stops the API at start.</summary>
public class PushOptions
{
    public const string Section = "Push";

    public FcmOptions Fcm { get; set; } = new();
    public ApnsOptions Apns { get; set; } = new();
    public WebOptions Web { get; set; } = new();
}

public class FcmOptions
{
    public string? ServiceAccountJsonPath { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(ServiceAccountJsonPath);
}

public class ApnsOptions
{
    public string? KeyPath { get; set; }
    public string? KeyId { get; set; }
    public string? TeamId { get; set; }
    public string? BundleId { get; set; }

    /// <summary>Use Apple's sandbox host, for development builds of the app.</summary>
    public bool Sandbox { get; set; }

    public bool IsEmpty => Missing().Count == 4;

    public IReadOnlyList<string> Missing() => PushKeys.Missing("Push:Apns", ("KeyPath", KeyPath), ("KeyId", KeyId), ("TeamId", TeamId), ("BundleId", BundleId));
}

public class WebOptions
{
    public string? PublicKey { get; set; }
    public string? PrivateKey { get; set; }

    /// <summary>A <c>mailto:</c> or <c>https:</c> contact for the push services (the VAPID subject).</summary>
    public string? Subject { get; set; }

    public bool IsEmpty => Missing().Count == 3;

    public IReadOnlyList<string> Missing() => PushKeys.Missing("Push:Web", ("PublicKey", PublicKey), ("PrivateKey", PrivateKey), ("Subject", Subject));
}

internal static class PushKeys
{
    public static IReadOnlyList<string> Missing(string section, params (string Key, string? Value)[] keys) =>
        keys.Where(k => string.IsNullOrWhiteSpace(k.Value)).Select(k => $"{section}:{k.Key}").ToList();
}

namespace Chat.Api.Realtime;

public class RealtimeOptions
{
    public const string Section = "Realtime";

    /// <summary>How long a connection counts as live without being renewed. A crashed node's connections expire after this.</summary>
    public int LeaseSeconds { get; set; } = 60;

    /// <summary>How often a node renews the leases of its own connections. Must be well below the lease.</summary>
    public int RefreshSeconds { get; set; } = 20;

    /// <summary>How often a node closes its hub connections whose session has ended (logout, password reset, ban).</summary>
    public int SessionCheckSeconds { get; set; } = 20;

    /// <summary>How long a typing signal lasts without being repeated.</summary>
    public int TypingSeconds { get; set; } = 5;
}

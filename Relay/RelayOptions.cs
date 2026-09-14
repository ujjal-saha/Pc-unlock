namespace PhoneUnlockService.Relay;

public sealed class RelayOptions
{
    /// <summary>
    /// wss:// URL of the relay server. Plain ws:// is rejected at startup
    /// (see RelayClient) - the nonce/signature exchange must not be
    /// interceptable/modifiable in transit even though the payloads
    /// themselves aren't secret-bearing, because tampering with a
    /// "denied" -> "approved" decision is exactly the attack this system
    /// most needs to prevent.
    /// </summary>
    public string Url { get; set; } = "wss://REPLACE_WITH_YOUR_RELAY_HOST/ws?role=pc&pc_id=<PC_ID>";

    /// <summary>Bearer token identifying this PC to the relay (issued at PC registration time - not implemented yet).</summary>
    public string AuthToken { get; set; } = "";

    public int ReconnectDelaySeconds { get; set; } = 5;
}

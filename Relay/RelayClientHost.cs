using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PhoneUnlockService.Relay;

/// <summary>Keeps RelayClient connected, reconnecting with a fixed backoff whenever the socket drops.</summary>
public sealed class RelayClientHost : BackgroundService
{
    private readonly RelayClient _relayClient;
    private readonly RelayOptions _options;
    private readonly ILogger<RelayClientHost> _log;

    public RelayClientHost(RelayClient relayClient, IOptions<RelayOptions> options, ILogger<RelayClientHost> log)
    {
        _relayClient = relayClient;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _relayClient.ConnectAndPumpAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Relay connection failed, will retry in {Delay}s", _options.ReconnectDelaySeconds);
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectDelaySeconds), stoppingToken);
            }
        }
    }
}

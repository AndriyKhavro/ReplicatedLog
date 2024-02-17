using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using Grpc.Net.ClientFactory;
using Google.Protobuf.WellKnownTypes;
using System.Linq;

namespace ReplicatedLog.Services;

public class HeartbeatService
{
    private readonly ILogger<ReplicatedLogService> _logger;
    private readonly ConcurrentDictionary<string, HeartbeatStatus> _statuses;
    private readonly PeriodicTimer _timer;
    private readonly ReplicationLogConfiguration _configuration;
    private readonly GrpcClientFactory? _clientFactory;
    private static readonly Empty EmptyInstance = new();

    public HeartbeatService(IOptions<ReplicationLogConfiguration> options, ILogger<ReplicatedLogService> logger, GrpcClientFactory? clientFactory = null)
    {
        _configuration = options.Value;
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_configuration.HeartbeatIntervalMs));
        _clientFactory = clientFactory;
        _logger = logger;
        var initialStatuses = _configuration.Secondaries.ToDictionary(secondary => secondary, _ => HeartbeatStatus.Unknown);
        _statuses = new ConcurrentDictionary<string, HeartbeatStatus>(initialStatuses);
    }

    public async Task Start()
    {
        if (_clientFactory is null)
        {
            return;
        }

        do
        {
            await Task.WhenAll(_configuration.Secondaries.Select(UpdateHeartbeatStatus));
        } while (await _timer.WaitForNextTickAsync());
    }

    public IReadOnlyDictionary<string, HeartbeatStatus> GetHeartbeatStatuses() => _statuses;

    public bool IsHealthyOrSuspected(string secondary) =>
        _statuses[secondary] is HeartbeatStatus.Healthy or HeartbeatStatus.Suspected;

    public bool HasQuorum() => 1 + _statuses.Keys.Count(IsHealthyOrSuspected) > (1 + _statuses.Count) / 2;

    private async Task UpdateHeartbeatStatus(string secondary)
    {
        _statuses[secondary] = await GetHeartbeatStatus(secondary);
    }

    private async Task<HeartbeatStatus> GetHeartbeatStatus(string secondary)
    {
        var isHealthy = await GetHeartbeat(secondary);
        if (isHealthy)
        {
            return HeartbeatStatus.Healthy;
        }

        return _statuses.GetValueOrDefault(secondary) is HeartbeatStatus.Healthy
            ? HeartbeatStatus.Suspected
            : HeartbeatStatus.Unhealthy;
    }

    private async Task<bool> GetHeartbeat(string secondary)
    {
        if (_clientFactory is null)
        {
            throw new InvalidOperationException("GrpcClient is not configured");
        }

        var client = _clientFactory.CreateClient<ReplicatedLog.ReplicatedLogService.ReplicatedLogServiceClient>(secondary);

        try
        {
            var response = await client.HeartbeatAsync(
                EmptyInstance,
                deadline: DateTime.UtcNow.AddMilliseconds(_configuration.HeartbeatTimeoutMs));
            return response.IsHealthy;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Heartbeat failed for {secondary}", secondary);
            return false;
        }
    }
}
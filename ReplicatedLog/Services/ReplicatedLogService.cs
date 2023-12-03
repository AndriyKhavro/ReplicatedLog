using System.Collections.Concurrent;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.ClientFactory;
using Microsoft.Extensions.Options;

namespace ReplicatedLog.Services;

public class ReplicatedLogService : ReplicatedLog.ReplicatedLogService.ReplicatedLogServiceBase
{
    private readonly ILogger<ReplicatedLogService> _logger;
    private readonly ReplicationLogConfiguration _configuration;
    private readonly IReadOnlyCollection<ReplicatedLog.ReplicatedLogService.ReplicatedLogServiceClient> _secondaryClients;
    private static readonly ConcurrentQueue<Message> Messages = new();
    private static readonly Empty EmptyInstance = new();

    public ReplicatedLogService(ILogger<ReplicatedLogService> logger, IOptions<ReplicationLogConfiguration> configuration, GrpcClientFactory? clientFactory = null)
    {
        _logger = logger;
        _configuration = configuration.Value;
        _secondaryClients = clientFactory is null
            ? Array.Empty<ReplicatedLog.ReplicatedLogService.ReplicatedLogServiceClient>()
            : _configuration.Secondaries
                .Select(clientFactory.CreateClient<ReplicatedLog.ReplicatedLogService.ReplicatedLogServiceClient>)
                .ToArray();
    }

    public override async Task<Empty> Append(Message request, ServerCallContext context)
    {
        if (_configuration.MaxAppendDelayMs > 0)
        {
            var delayMs = new Random().Next(_configuration.MaxAppendDelayMs);
            await Task.Delay(delayMs);
        }

        Messages.Enqueue(request);
        _logger.LogInformation("Message {message} was saved locally", request.Text);

        var secondaryTasks = _secondaryClients.Select(secondaryClient => AppendOnSecondary(request, secondaryClient));
        await Task.WhenAll(secondaryTasks);

        return EmptyInstance;
    }

    public override async Task List(Empty request, IServerStreamWriter<Message> responseStream, ServerCallContext context)
    {
        _logger.LogInformation("Streaming {messageCount} messages started", Messages.Count);

        foreach (var message in Messages)
        {
            await responseStream.WriteAsync(message);
        }

        _logger.LogInformation("Streaming {messageCount} messages finished", Messages.Count);
    }

    private async Task AppendOnSecondary(Message request, ReplicatedLog.ReplicatedLogService.ReplicatedLogServiceClient client)
    {
        await client.AppendAsync(request);
        _logger.LogInformation("Message {message} was sent to secondary", request.Text);
    }
}
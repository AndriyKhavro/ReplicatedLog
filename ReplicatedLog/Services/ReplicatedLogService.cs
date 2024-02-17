using System.Collections.Concurrent;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.ClientFactory;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using Polly;
using Polly.Retry;

namespace ReplicatedLog.Services;

public class ReplicatedLogService : ReplicatedLog.ReplicatedLogService.ReplicatedLogServiceBase
{
    private readonly ILogger<ReplicatedLogService> _logger;
    private readonly ReplicationLogConfiguration _configuration;
    private readonly IReadOnlyCollection<(string Url, ReplicatedLog.ReplicatedLogService.ReplicatedLogServiceClient Client)> _secondaryClients;
    private readonly HeartbeatService _heartbeatService;
    private static readonly ConcurrentDictionary<int, Message> Messages = new();
    private static readonly Empty EmptyInstance = new();
    private static readonly MessageResponse Accepted = new()
    {
        Accepted = true
    };
    private static readonly MessageResponse NoQuorum = new()
    {
        Accepted = false,
        Error = "The system is in read-only mode and does not accept messages due to missing quorum"
    };

    private static int _sequenceNumber;

    public ReplicatedLogService(ILogger<ReplicatedLogService> logger, IOptions<ReplicationLogConfiguration> configuration, HeartbeatService heartbeatService, GrpcClientFactory? clientFactory = null)
    {
        _logger = logger;
        _heartbeatService = heartbeatService;
        _configuration = configuration.Value;
        _secondaryClients = clientFactory is null
            ? Array.Empty<(string, ReplicatedLog.ReplicatedLogService.ReplicatedLogServiceClient)>()
            : _configuration.Secondaries
                .Select(secondary => (secondary, clientFactory.CreateClient<ReplicatedLog.ReplicatedLogService.ReplicatedLogServiceClient>(secondary)))
                .ToArray();
    }

    public override async Task<MessageResponse> Append(MessageRequest request, ServerCallContext context)
    {
        if (request.Message?.Text is null)
        {
            throw new ArgumentNullException(nameof(request.Message));
        }

        if (!_heartbeatService.HasQuorum())
        {
            return NoQuorum;
        }

        int sequenceNumber = Interlocked.Increment(ref _sequenceNumber);
        Messages[sequenceNumber] = request.Message;

        _logger.LogInformation("Message {message} was saved locally", request.Message.Text);

        var secondaryTasks = _secondaryClients.Select(secondary => AppendOnSecondary(request.Message, sequenceNumber, secondary.Client, secondary.Url));

        await Task.WhenAll(secondaryTasks.OrderByCompletion().Take(request.WriteConcern - 1));

        return Accepted;
    }

    public override async Task<Empty> Replicate(ReplicatedMessage request, ServerCallContext context)
    {
        if (_configuration.MaxAppendDelayMs > 0)
        {
            var delayMs = new Random().Next(_configuration.MaxAppendDelayMs);
            await Task.Delay(delayMs);
        }

        Messages[request.SequenceNumber] = request.Message;

        _logger.LogInformation(
            "Saved replicated message {message}. Sequence number: {sequenceNumber}",
            request.Message,
            request.SequenceNumber);

        return EmptyInstance;
    }

    public override async Task List(Empty request, IServerStreamWriter<Message> responseStream, ServerCallContext context)
    {
        _logger.LogInformation("Streaming {messageCount} messages started", Messages.Count);

        var messages = Enumerable.Range(1, Messages.Count)
            .TakeWhile(Messages.ContainsKey)
            .Select(index => Messages[index])
            .ToArray();

        foreach (var message in messages)
        {
            await responseStream.WriteAsync(message);
        }

        _logger.LogInformation("Streaming {messageCount} messages finished", messages.Length);
    }

    public override Task<HeartbeatResponse> Heartbeat(Empty request, ServerCallContext context)
    {
        return Task.FromResult(new HeartbeatResponse { IsHealthy = true });
    }

    public override Task<HealthResponse> Health(Empty request, ServerCallContext context)
    {
        var response = new HealthResponse();
        foreach (var pair in _heartbeatService.GetHeartbeatStatuses())
        {
            response.Status[pair.Key] = pair.Value;
        }

        return Task.FromResult(response);
    }

    private async Task AppendOnSecondary(Message request, int sequenceNumber, ReplicatedLog.ReplicatedLogService.ReplicatedLogServiceClient client, string secondary)
    {
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(1),
                UseJitter = true, // Adds a random factor to the delay
                MaxRetryAttempts = int.MaxValue,
                MaxDelay = TimeSpan.FromMinutes(5),
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "Replication of message {sequenceNumber} failed on Attempt #{retryAttempt}. Will retry after {retryDelay} ms",
                        sequenceNumber, args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                },
            }).Build();
        
        await pipeline.ExecuteAsync(async cancellationToken =>
        {
            while (!_heartbeatService.IsHealthyOrSuspected(secondary))
            {
                _logger.LogWarning("{secondary} is unhealthy. Waiting for it to become healthy to replicate", secondary);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            return await client.ReplicateAsync(
                new ReplicatedMessage { Message = request, SequenceNumber = sequenceNumber },
                deadline: DateTime.UtcNow.AddMilliseconds(_configuration.ReplicationTimeoutMs),
                cancellationToken: cancellationToken);
        });

        _logger.LogInformation("Message {sequenceNumber} was sent to secondary", sequenceNumber);
    }
}
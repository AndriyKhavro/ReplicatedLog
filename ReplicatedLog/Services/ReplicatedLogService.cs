using System.Collections.Concurrent;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.ClientFactory;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;

namespace ReplicatedLog.Services;

public class ReplicatedLogService : ReplicatedLog.ReplicatedLogService.ReplicatedLogServiceBase
{
    private readonly ILogger<ReplicatedLogService> _logger;
    private readonly ReplicationLogConfiguration _configuration;
    private readonly IReadOnlyCollection<ReplicatedLog.ReplicatedLogService.ReplicatedLogServiceClient> _secondaryClients;
    private static readonly ConcurrentDictionary<int, Message> Messages = new();
    private static readonly Empty EmptyInstance = new();
    private static int _sequenceNumber;

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

    public override async Task<Empty> Append(MessageRequest request, ServerCallContext context)
    {
        if (request.Message?.Text is null)
        {
            throw new ArgumentNullException(nameof(request.Message));
        }

        int sequenceNumber = Interlocked.Increment(ref _sequenceNumber);
        Messages[sequenceNumber] = request.Message;

        _logger.LogInformation("Message {message} was saved locally", request.Message.Text);

        var secondaryTasks = _secondaryClients.Select(secondaryClient => AppendOnSecondary(request.Message, sequenceNumber, secondaryClient));

        await Task.WhenAll(secondaryTasks.OrderByCompletion().Take(request.WriteConcern - 1));

        return EmptyInstance;
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

        for (int index = 0, sentCount = 0; sentCount < Messages.Count; index++)
        {
            if (Messages.TryGetValue(index, out var message))
            {
                await responseStream.WriteAsync(message);
                sentCount++;
            }
        }

        _logger.LogInformation("Streaming {messageCount} messages finished", Messages.Count);
    }

    private async Task AppendOnSecondary(Message request, int sequenceNumber, ReplicatedLog.ReplicatedLogService.ReplicatedLogServiceClient client)
    {
        await client.ReplicateAsync(new ReplicatedMessage { Message = request, SequenceNumber = sequenceNumber });
        _logger.LogInformation("Message {message} was sent to secondary", request.Text);
    }
}
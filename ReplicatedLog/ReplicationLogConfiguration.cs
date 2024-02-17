namespace ReplicatedLog;

public class ReplicationLogConfiguration
{
    public int MaxAppendDelayMs { get; init; }
    public int ReplicationTimeoutMs { get; init; } = 30_000;
    public int HeartbeatTimeoutMs { get; init; } = 30_000;
    public int HeartbeatIntervalMs { get; init; } = 30_000;
    public IReadOnlyList<string> Secondaries { get; init; } = Array.Empty<string>();
}
namespace ReplicatedLog;

public class ReplicationLogConfiguration
{
    public int MaxAppendDelayMs { get; init; }
    public IReadOnlyList<string> Secondaries { get; init; } = Array.Empty<string>();
}
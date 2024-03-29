using ReplicatedLog;
using ReplicatedLog.Services;
using ReplicatedLogService = ReplicatedLog.Services.ReplicatedLogService;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();

var configSection = builder.Configuration.GetSection("ReplicationLog");
builder.Services.Configure<ReplicationLogConfiguration>(configSection);

var configuration = configSection.Get<ReplicationLogConfiguration>()!;
foreach (var secondaryAddress in configuration.Secondaries)
{
    builder.Services.AddGrpcClient<ReplicatedLog.ReplicatedLogService.ReplicatedLogServiceClient>(
        secondaryAddress,
        options => options.Address = new Uri(secondaryAddress));
}

builder.Services.AddGrpcReflection();
builder.Services.AddSingleton<HeartbeatService>();

var app = builder.Build();

app.MapGrpcService<ReplicatedLogService>();
app.MapGrpcReflectionService();

var heartbeatTask = app.Services.GetRequiredService<HeartbeatService>().Start();
app.Run();

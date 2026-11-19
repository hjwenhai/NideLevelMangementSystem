using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;

namespace Naide.LevelManager.Host;
public sealed class AlarmService(IConfigurationStore configurations, AcquisitionService acquisition, IAlarmStore store,
    TimeProvider clock, ILogger<AlarmService> logger) : BackgroundService
{
    public string Error { get; private set; } = "";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialized = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!initialized) { await store.ResetPendingAsync(stoppingToken); initialized = true; }
                var config = await configurations.GetAsync(stoppingToken); var view = await acquisition.CurrentAsync(stoppingToken);
                // Do not combine a just-saved configuration with a view from a different revision.
                if (view.ConfigurationRevision == config.Revision)
                {
                    var readings = view.Readings.ToDictionary(r => r.TankId); List<AlarmObservation> inputs = [];
                    foreach (var tank in config.Tanks.Where(t => t.Enabled && config.Channels.Any(c => c.Id == t.ChannelId && c.Enabled)))
                        foreach (var rule in AlarmEngine.Rules(tank))
                            inputs.Add(new(rule, AlarmEngine.Observe(rule, readings[tank.Id], view.Running)));
                    await store.EvaluateAsync(inputs, clock.GetUtcNow(), stoppingToken);
                }
                Error = "";
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { Error = "报警处理异常：" + e.Message; logger.LogError(e, "报警处理失败，下轮重试"); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

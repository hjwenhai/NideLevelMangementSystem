using System.Collections.Concurrent;
using System.Diagnostics;
using Naide.LevelManager.Application;
using Naide.LevelManager.Communication;
using Naide.LevelManager.Domain;

namespace Naide.LevelManager.Host;

public sealed record ChannelRuntime(Guid ChannelId, string Name, bool Connected, long CompletedCycles,
    double LastCycleMs, DateTimeOffset? LastCycleAtUtc, string Error);
public sealed record AcquisitionView(DateTimeOffset ServerAtUtc, bool Running, Guid SessionId, long ConfigurationRevision,
    IReadOnlyList<ReadingSnapshot> Readings, IReadOnlyList<ChannelRuntime> Channels, string HistoryError);

public sealed class AcquisitionService(IConfigurationStore configurations, IRegisterClientFactory clients,
    IHistoryStore history, MeteringService metering, InstrumentFaultTracking faultTracking, WirelessRuntime wireless, RealtimeTrends realtime, ChannelAccess access, IEventWriter events, TimeProvider clock, ILogger<AcquisitionService> logger) : BackgroundService
{
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly object levelSync=new();
    private readonly HashSet<Guid> adjustingLevels=[];
    private readonly Dictionary<Guid,long> levelGenerations=[];
    private sealed class LevelScope(Action release):IDisposable {public void Dispose()=>release();}
    public IDisposable SuspendLevelAdjustment(Guid tankId)
    {
        void Invalidate(){if(readings.TryGetValue(tankId,out var s)){var bad=Measurement.Missing("液位修改后等待重新采集");readings[tankId]=s with{LevelMm=bad,FloatPositionMm=bad,CurrentVolumeM3=bad,NetVolumeM3=bad,MassT=bad,FreshPositionReadConfirmed=false};}}
        lock(levelSync){adjustingLevels.Add(tankId);levelGenerations[tankId]=levelGenerations.GetValueOrDefault(tankId)+1;Invalidate();}
        return new LevelScope(()=>{lock(levelSync){adjustingLevels.Remove(tankId);levelGenerations[tankId]=levelGenerations.GetValueOrDefault(tankId)+1;Invalidate();}});
    }
    private readonly ConcurrentDictionary<Guid, ReadingSnapshot> readings = new();
    private readonly ConcurrentDictionary<Guid, ChannelRuntime> channelStates = new();
    private readonly ConcurrentDictionary<Guid, string> diagnostics = new();
    private static readonly TrendPreferences DefaultTrends = new();
    private static readonly ControlSettings DefaultControls = new();
    private volatile SystemConfiguration runtimeConfiguration = new();
    private CancellationToken hostToken;
    private CancellationTokenSource? active;
    private readonly Dictionary<Guid, Task> channelWorkers = [];
    private Task archiveWorker = Task.CompletedTask;
    private volatile bool running;
    private Guid session;
    private string historyError = "";

    public async Task<AcquisitionView> CurrentAsync(CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(ct); var now = clock.GetUtcNow();
        List<ReadingSnapshot> rows = new(config.Tanks.Count);
        foreach (var t in config.Tanks)
        {
            var sample = readings.GetValueOrDefault(t.Id) ?? new ReadingSnapshot { TankId = t.Id, ConfigurationRevision = config.Revision };
            var channel = config.Channels.First(c => c.Id == t.ChannelId);
            var current = sample.ForCurrentState(now, session, config.Revision, config.ReadingExpirySeconds,
                running, t.Enabled && channel.Enabled, t.TemperatureMode);
            if (t.Metering.WaterHeightMm.HasValue &&
                (current.CalculationRevision == 0 || current.ConfigurationRevision != config.Revision || current.WaterVolumeM3 != sample.WaterVolumeM3))
            {
                try { current = current with { WaterVolumeM3 = await metering.CalculateManualWaterVolumeAsync(t.Metering, ct) }; }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogError(error, "人工水体积计算失败");
                    current = current with { WaterVolumeM3 = new(null, ValueQuality.Fault, "水体积计算暂不可用") };
                }
            }
            rows.Add(wireless.Apply(current,t,channel,false));
        }
        return new(now, running, session, config.Revision, rows, channelStates.Values.OrderBy(c => c.Name).ToArray(), historyError);
    }

    public async Task BeginAsync(AuditActor actor, CancellationToken ct)
    {
        actor.Require(AppPermission.AcquisitionManage);
        await lifecycle.WaitAsync(ct);
        try
        {
            if (running) return;
            var config = await configurations.GetAsync(ct);
            if (!config.Tanks.Any(t => t.Enabled && config.Channels.Any(c => c.Id == t.ChannelId && c.Enabled)))
                throw new InvalidOperationException("请先配置启用的储罐及采集通道");
            await events.WriteEventAsync(new("操作日志", "acquisition.start", actor.AccountId, actor.DisplayName, "启动设备采集", "已受理", Parameters: new { config.Revision }), ct);
            Launch(config);
        }
        finally { lifecycle.Release(); }
    }

    public async Task HaltAsync(AuditActor actor, CancellationToken ct)
    {
        actor.Require(AppPermission.AcquisitionManage);
        await lifecycle.WaitAsync(ct);
        try
        {
            if (!running) return;
            await events.WriteEventAsync(new("操作日志", "acquisition.stop", actor.AccountId, actor.DisplayName, "停止设备采集", "已受理"), ct);
            await End();
        }
        finally { lifecycle.Release(); }
    }

    public async Task ReconfigureAsync(SystemConfiguration config, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            // A later database commit may overtake this caller while it waits for the
            // lifecycle gate. Never rebind a physical channel to an older revision.
            var newest=await configurations.GetAsync(CancellationToken.None);
            if(newest.Revision>config.Revision)config=newest;
            if(running&&runtimeConfiguration.Revision>=config.Revision)return;
            if (running && CanApplyWithoutReopening(runtimeConfiguration, config))
            {
                runtimeConfiguration = config;
                foreach (var pair in readings) readings.TryUpdate(pair.Key, pair.Value with { ConfigurationRevision = config.Revision }, pair.Value);
                StartMissingChannels(config);
                return;
            }
            var restart = running; await End(); readings.Clear(); if (restart) Launch(config);
        }
        finally { lifecycle.Release(); }
    }

    public static bool CanApplyWithoutReopening(SystemConfiguration before, SystemConfiguration after)
    {
        if (before.HistoryIntervalSeconds != after.HistoryIntervalSeconds || before.RetentionMonths != after.RetentionMonths || before.ReadingExpirySeconds != after.ReadingExpirySeconds) return false;
        var channels = after.Channels.ToDictionary(c => c.Id);
        if (before.Channels.Any(c => !channels.TryGetValue(c.Id, out var next) || c with { Name = "" } != next with { Name = "" })) return false;
        TankDefinition Normalize(TankDefinition t) => t with { Code = "", Name = "", ProductNumber = "", Batch = "", DiameterMm = null, NominalCapacityM3 = null, Notes = "", InstrumentTag = "", Medium = "", MediumId = null, Zone = "", Hidden = false, PinnedAtUtc = null, Shape = TankShape.Vertical, Alarms = new AlarmSettings(), Trends = DefaultTrends, Controls = DefaultControls, AtsHeightsMm = Array.Empty<decimal?>() };
        var normalized = after.Tanks.ToDictionary(t => t.Id, Normalize);
        return before.Tanks.All(t => normalized.TryGetValue(t.Id, out var next) && Normalize(t) == next);
    }

    private void Launch(SystemConfiguration config)
    {
        runtimeConfiguration = config;
        session = Guid.NewGuid(); readings.Clear(); channelStates.Clear(); diagnostics.Clear(); historyError = "";
        active = CancellationTokenSource.CreateLinkedTokenSource(hostToken); running = true;
        var token = active.Token; var epoch = session;
        StartMissingChannels(config);
        archiveWorker = Task.Run(() => Archive(config, epoch, token), CancellationToken.None);
    }

    // Called under lifecycle. A saved empty channel gets a worker when its first enabled tank is added.
    private void StartMissingChannels(SystemConfiguration config)
    {
        var token = active!.Token;
        var epoch = session;
        var enabledChannels = config.Tanks.Where(t => t.Enabled).Select(t => t.ChannelId).ToHashSet();
        foreach (var channel in config.Channels)
        {
            if (channel.Enabled && enabledChannels.Contains(channel.Id) && !channelWorkers.ContainsKey(channel.Id))
                channelWorkers.Add(channel.Id, Task.Run(() => Poll(channel, config, epoch, token), CancellationToken.None));
        }
    }
    private async Task End()
    {
        running = false; realtime.GapAll();
        if (active is null) return;
        await active.CancelAsync();
        try { await Task.WhenAll(channelWorkers.Values.Append(archiveWorker)); } catch (OperationCanceledException) { }
        active.Dispose(); active = null; channelWorkers.Clear(); archiveWorker = Task.CompletedTask;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        hostToken = stoppingToken;
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await lifecycle.WaitAsync(); try { await End(); } finally { lifecycle.Release(); }
    }

    private async Task Poll(ChannelDefinition channel, SystemConfiguration config, Guid epoch, CancellationToken ct)
    {
        var tanks = config.Tanks.Where(t => t.ChannelId == channel.Id && t.Enabled).ToArray();
        long cycles = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var client = new PriorityRegisterClient(await clients.CreateAsync(channel, ct));
                using var registration=access.Register(channel,epoch,client,ct);
                var reader = new DeviceReader(channel.Protocol, clock);
                while (!ct.IsCancellationRequested)
                {
                    config = runtimeConfiguration;
                    tanks = config.Tanks.Where(t => t.ChannelId == channel.Id && t.Enabled).ToArray();
                    var watch = Stopwatch.StartNew(); string lastError = "";
                    foreach (var tank in tanks)
                    {
                        long levelGeneration;lock(levelSync){if(adjustingLevels.Contains(tank.Id))continue;levelGeneration=levelGenerations.GetValueOrDefault(tank.Id);}
                        ct.ThrowIfCancellationRequested(); ReadingSnapshot? sample = null; Exception? last = null;
                        for (var retry = 0; retry <= channel.ReadRetries && sample is null; retry++)
                        {
                            try { sample = await reader.ReadAsync(client, new(channel, tank, config.Revision, epoch, config.ReadingExpirySeconds), ct); }
                            catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException)
                            { last = error; if (retry < channel.ReadRetries) await Task.Delay(channel.ReadTimeoutMs, ct); }
                        }
                        sample ??= Failed(tank, config.Revision, epoch, last?.Message ?? "设备未响应");
                        sample=await faultTracking.ApplyAsync(tank,channel,sample,ct);
                        sample=wireless.Apply(sample,tank,channel);
                        try { sample = await metering.CalculateAsync(tank, sample, ct); }
                        catch(Exception error) when(error is not OperationCanceledException)
                        {
                            logger.LogError(error,"计量计算失败");
                            var bad=new Measurement(null,ValueQuality.Fault,"计量计算暂不可用");
                            sample=sample with{CurrentVolumeM3=bad,NetVolumeM3=bad,MassT=bad,Diagnostic=sample.Diagnostic+"；计量计算暂不可用"};
                        }
                        if (sample.Diagnostic.Length > 0) lastError = sample.Diagnostic;
                        lock(levelSync){if(adjustingLevels.Contains(tank.Id)||levelGeneration!=levelGenerations.GetValueOrDefault(tank.Id))continue;
                        if (!ct.IsCancellationRequested && epoch == session) { readings[tank.Id] = sample with { ConfigurationRevision = runtimeConfiguration.Revision }; realtime.Publish(sample); }}
                        await RecordDiagnostic(tank, sample, ct);
                    }
                    cycles++; channelStates[channel.Id] = new(channel.Id, channel.Name, true, cycles, watch.Elapsed.TotalMilliseconds, clock.GetUtcNow(), lastError);
                    await Task.Delay(Math.Max(100, channel.PollIntervalMs - (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds)), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError(error,"采集通道异常，稍后重连");
                channelStates[channel.Id] = new(channel.Id, channel.Name, false, cycles, 0, null, error.Message);
                foreach (var tank in tanks)
                {
                    var sample = Failed(tank, config.Revision, epoch, error.Message);
                    if (!ct.IsCancellationRequested && epoch == session) { readings[tank.Id] = sample; realtime.Publish(sample); }
                    await RecordDiagnostic(tank, sample, ct);
                }
                try { await Task.Delay(Math.Max(1000, channel.PollIntervalMs), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }
    private ReadingSnapshot Failed(TankDefinition tank, long revision, Guid epoch, string reason) => new()
    {
        TankId = tank.Id, ConfigurationRevision = revision, AcquisitionSessionId = epoch, ReceivedAtUtc = clock.GetUtcNow(),
        LevelMm = new(null, ValueQuality.Offline, reason), InstantTemperatureC = new(null, ValueQuality.Offline, reason),
        InstrumentDensityKgM3 = new(null, ValueQuality.Offline, reason), Diagnostic = reason
    };
    private async Task RecordDiagnostic(TankDefinition tank, ReadingSnapshot sample, CancellationToken ct)
    {
        var code = sample.Diagnostic.Length > 0 ? sample.Diagnostic : sample.InstrumentFault is > 0 ? "INSTRUMENT_FAULT:" + sample.InstrumentFault : "";
        if (diagnostics.GetValueOrDefault(tank.Id, "") == code) return;
        try
        {
            await events.WriteEventAsync(new(code.Length == 0 ? "系统事件" : sample.InstrumentFault is > 0 ? "仪表诊断" : "通讯异常",
                code.Length == 0 ? "acquisition.recovered" : "acquisition.error", "system", "系统",
                code.Length == 0 ? "设备采集恢复" : "设备采集异常", code.Length == 0 ? "恢复" : code, tank.Id, tank.Code,
                new { sample.ConfigurationRevision, sample.InstrumentFault, sample.RawQuality, sample.RawValidity }), ct);
            diagnostics[tank.Id] = code;
        }
        catch (Exception error) when (error is not OperationCanceledException) { logger.LogError(error, "采集事件保存失败"); }
    }

    private async Task Archive(SystemConfiguration config, Guid epoch, CancellationToken ct)
    {
        var intervalMs = config.HistoryIntervalSeconds * 1000L;
        var next = (clock.GetUtcNow().ToUnixTimeMilliseconds() / intervalMs + 1) * intervalMs;
        while (!ct.IsCancellationRequested)
        {
            var wait = next - clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(wait, 1000)), clock, ct);
            else
            {
                config = runtimeConfiguration;
                var now = clock.GetUtcNow(); var stamp = now.ToUnixTimeMilliseconds() / intervalMs * intervalMs;
                var samples = config.Tanks.Where(t => t.Enabled && config.Channels.Any(c => c.Id == t.ChannelId && c.Enabled)).Select(t =>
                {
                    var r = (readings.GetValueOrDefault(t.Id) ?? new ReadingSnapshot { TankId = t.Id })
                        .ForCurrentState(now, epoch, config.Revision, config.ReadingExpirySeconds, running, true, t.TemperatureMode);
                    return new HistorySample
                    {
                        TankId = t.Id, AtUtcMs = stamp, ConfigurationRevision = config.Revision, AcquisitionSessionId = epoch, CalculationRevision = r.CalculationRevision,
                        Analog1Ma=r.Analog1Ma,Analog2Ma=r.Analog2Ma,Pressure1KPa=r.Pressure1KPa,Pressure2KPa=r.Pressure2KPa,CurrentVolumeM3 = r.CurrentVolumeM3, NetVolumeM3 = r.NetVolumeM3, MassT = r.MassT,
                        ReceivedAtUtcMs = r.ReceivedAtUtc == default ? null : r.ReceivedAtUtc.ToUnixTimeMilliseconds(), SourceAtUtcMs = r.SourceAtUtc?.ToUnixTimeMilliseconds(),
                        SourceTimeConfirmed = r.SourceTimeConfirmed, DeviceDataAgeMs = r.DeviceDataAgeMs,
                        LevelMm = r.LevelMm, InstantTemperatureC = r.InstantTemperatureC, AverageTemperatureC = r.AverageTemperatureC,AverageCompletedAtUtcMs=r.AverageCompletedAtUtc?.ToUnixTimeMilliseconds(),
                        InstrumentDensityKgM3 = r.InstrumentDensityKgM3, AtsPoints = r.AtsPoints,
                        InstrumentStatus = r.InstrumentStatus, InstrumentFault = r.InstrumentFault, RawQuality = r.RawQuality, RawValidity = r.RawValidity,
                        DiagnosticCode = r.Diagnostic.Length == 0 ? "" : "ACQUISITION_ERROR"
                    };
                }).ToArray();
                try { await history.AppendAsync(samples, ct); historyError = ""; }
                catch (Exception error) when (error is not OperationCanceledException)
                { historyError = "历史保存失败：" + error.Message; logger.LogError(error, "历史归档失败"); }
                next = stamp + intervalMs;
            }
        }
    }
}

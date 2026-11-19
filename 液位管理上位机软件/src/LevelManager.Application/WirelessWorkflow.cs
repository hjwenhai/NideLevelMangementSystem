using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Application;

// The journal commits each stage before any side effect. No stage is resumed after a process restart.
public sealed class WirelessWorkflow(IWirelessTaskStore journal,IWorkflowTime time)
{
    public async Task<WirelessTaskRecord> RunAsync(WirelessTaskRecord initial,IWirelessMeasurementDevice device,CancellationToken ct)
    {
        var task=initial;var movementStarted=false;
        async Task Stage(WirelessStage stage,string message,TimeSpan? due=null)
        {
            var next=task with{Version=task.Version+1,Stage=stage,StageStartedAtUtc=time.UtcNow,DisplayDueAtUtc=due.HasValue?time.UtcNow+due.Value:null,Message=message};await journal.SaveAsync(next,ct);task=next;
        }
        ReadingSnapshot Require(ReadingSnapshot r,InstrumentCommand mode)
        {
            if(!r.LevelMm.IsValid||!r.FreshPositionReadConfirmed||r.InstrumentStatus is null||r.InstrumentFault is null||!CommandEncoding.ModeMatches(r.InstrumentStatus.Value,mode,task.Target.Channel.Protocol))
                throw new InvalidOperationException("实际位置、当前模式或通讯未确认有效；停止自动推进");
            return r;
        }
        async Task<ReadingSnapshot> Read(InstrumentCommand mode)=>Require(await device.ReadPositionAsync(ct),mode);
        async Task Command(InstrumentCommand command,WirelessStage stage,string message)
        {
            task=task with{WriteIntentRecorded=true};await Stage(stage,message);movementStarted=true;
            await device.CommandAsync(task,command,ct);
        }
        async Task WaitDwell(InstrumentCommand mode,decimal? target=null)
        {
            var started=time.Timestamp;
            while(time.Elapsed(started)<WirelessMeasurementRules.Dwell)
            {
                var sample=await Read(mode);if(target.HasValue&&!WirelessMeasurementRules.WithinTolerance(sample.LevelMm.Value!.Value,target.Value))throw new InvalidOperationException("停留过程中位置超出到位容差");
                await time.Delay(TimeSpan.FromSeconds(1),ct);
            }
        }
        async Task<(WirelessTemperatureSample Sample,int Attempts)> Temperature()
        {
            var start=time.Timestamp;var attempts=0;Exception? last=null;
            for(var i=0;i<2;i++)
            {
                var remaining=TimeSpan.FromSeconds(10)-time.Elapsed(start);if(remaining<=TimeSpan.Zero)break;
                using var deadline=new CancellationTokenSource(remaining);using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,deadline.Token);attempts++;
                try{return(await device.ReadTemperatureOnceAsync(linked.Token),attempts);} // 0x8000 is a result, never a reason to retry.
                catch(Exception e)when(e is IOException or TimeoutException or OperationCanceledException&&!ct.IsCancellationRequested){last=e;}
                if(i==0&&time.Elapsed(start)+TimeSpan.FromSeconds(2)<TimeSpan.FromSeconds(10))await time.Delay(TimeSpan.FromSeconds(2),ct);else break;
            }
            return(new(Measurement.Missing("温度读取失败："+(last?.Message??"本点读取总等待达到10秒")),null),attempts);
        }
        try
        {
            await Stage(WirelessStage.Checking,"核对实际位置、模式、故障与本次测温能力");
            if(device.UnsupportedReason is {} unsupported)throw new InvalidOperationException(unsupported);
            var before=await device.ReadPositionAsync(ct);if(!before.LevelMm.IsValid||!before.FreshPositionReadConfirmed||before.InstrumentFault is null||before.InstrumentStatus is null)throw new InvalidOperationException("启动前设备状态未确认有效");
            task=task with{WriteIntentRecorded=true};await Stage(WirelessStage.Authorizing,"记录本任务唯一一次认证写入意图");await device.AuthorizeOnceAsync(task,ct);
            await Command(InstrumentCommand.MeasureI,WirelessStage.EnteringMeasureI,"进入测量Ⅰ并核验实际模式");await Stage(WirelessStage.InitialStability,"起始高度极差≤5mm持续20秒，最多等待10分钟",WirelessMeasurementRules.InitialStableTimeout);
            var window=new InitialStabilityWindow();var stableStart=time.Timestamp;var stable=false;
            while(time.Elapsed(stableStart)<=WirelessMeasurementRules.InitialStableTimeout)
            {
                var r=await Read(InstrumentCommand.MeasureI);var evidence=r.LevelSequence.HasValue?r.GatewayBootId+":"+r.GatewayBindingVersion+":"+r.LevelSequence:r.ReceivedAtUtc.UtcTicks.ToString();
                if(window.Observe(r.LevelMm,r.ReceivedAtUtc,evidence,r.FreshPositionReadConfirmed)){stable=true;break;}await time.Delay(TimeSpan.FromSeconds(1),ct);
            }
            if(!stable)throw new TimeoutException("起始高度10分钟内未达到5mm/20秒稳定条件，未进入下降阶段");
            await Stage(WirelessStage.InitialDwell,"起始稳定后另停留10分钟，基准温度不参与平均",WirelessMeasurementRules.Dwell);await WaitDwell(InstrumentCommand.MeasureI);
            var baseline=await Temperature();var surface=await Read(InstrumentCommand.MeasureI);
            var plan=WirelessMeasurementRules.Plan(surface.LevelMm.Value!.Value,task.RequestedPoints);
            task=task with{BaselineTemperature=baseline.Sample.Temperature,Plan=plan,PlanFrozenAtUtc=time.UtcNow,Points=plan.TargetsMm.Select((h,i)=>new WirelessPoint(i+1,h,null,null,null,Measurement.Missing("尚未测量"),0)).ToArray()};await Stage(WirelessStage.Planning,"冻结本次液面X，生成从高到低的测点");
            if(plan.PlannedPoints==0){task=task with{MeasureIRestored=true,FinishedAtUtc=time.UtcNow};await Stage(WirelessStage.Skipped,"液面低于500mm，不执行平均温度测点，保留旧成功结果");return task;}
            foreach(var point in task.Points.ToArray())
            {
                task=task with{CurrentPoint=point.Number};
                await Command(InstrumentCommand.MeasureIII,WirelessStage.Descending,"测量Ⅲ下降，按真实位置P判断越过目标H");var movingStart=time.Timestamp;var crossed=false;
                while(time.Elapsed(movingStart)<TimeSpan.FromSeconds(task.Target.Tank.Controls.MovementTimeoutSeconds))
                {
                    var r=await Read(InstrumentCommand.MeasureIII);if(WirelessMeasurementRules.ShouldStop(r.LevelMm.Value!.Value,point.TargetMm)){crossed=true;break;}await time.Delay(TimeSpan.FromSeconds(1),ct);
                }
                if(!crossed)throw new TimeoutException("下降到目标的等待超时");
                await Command(InstrumentCommand.Stop,WirelessStage.Stopping,"P−H＜0，发送停止并单独核验停稳");await Stage(WirelessStage.Settling,"核验停止模式、连续实际位置未再变化及最终±100mm容差");
                // Three distinct fresh readings at protocol resolution. This is mechanical evidence,
                // not the initial 5mm/20s rule and not a substitute for field braking-distance tests.
                var settlingStart=time.Timestamp;decimal? previous=null;var unchanged=0;DateTimeOffset? priorAt=null;ReadingSnapshot? stopped=null;
                while(time.Elapsed(settlingStart)<TimeSpan.FromSeconds(task.Target.Tank.Controls.MovementTimeoutSeconds))
                {
                    var r=await Read(InstrumentCommand.Stop);var p=r.LevelMm.Value!.Value;
                    if(priorAt.HasValue&&r.ReceivedAtUtc<=priorAt)throw new InvalidOperationException("停止后的实际位置更新未确认");
                    unchanged=previous.HasValue&&previous==p?unchanged+1:1;previous=p;priorAt=r.ReceivedAtUtc;
                    if(unchanged>=3){if(!WirelessMeasurementRules.WithinTolerance(p,point.TargetMm))throw new InvalidOperationException("停稳后最终位置超出±100mm容差");stopped=r;break;}await time.Delay(TimeSpan.FromSeconds(1),ct);
                }
                if(stopped is null)throw new TimeoutException("停止后的实际停稳未确认");
                var dwellStart=time.Timestamp;var dwellAt=time.UtcNow;await Stage(WirelessStage.PointDwell,"第"+point.Number+"点已停稳到位，停留10分钟",WirelessMeasurementRules.Dwell);await WaitDwell(InstrumentCommand.Stop,point.TargetMm);var dwellElapsed=time.Elapsed(dwellStart).TotalSeconds;
                await Stage(WirelessStage.ReadingTemperature,"停留结束后新发温度读取，失败最多重试一次");var temp=await Temperature();var final=await Read(InstrumentCommand.Stop);
                if(!WirelessMeasurementRules.WithinTolerance(final.LevelMm.Value!.Value,point.TargetMm))throw new InvalidOperationException("采温时实际位置超差");
                var completed=point with{StoppedPositionMm=stopped.LevelMm.Value,DwellStartedAtUtc=dwellAt,SampledAtUtc=time.UtcNow,Temperature=temp.Sample.Temperature,Attempts=temp.Attempts,RawTemperature=temp.Sample.RawValue,DwellElapsedSeconds=dwellElapsed};
                task=task with{Points=task.Points.Select(p=>p.Number==point.Number?completed:p).ToArray()};await Stage(WirelessStage.ReadingTemperature,"第"+point.Number+"点已记录，有效/缺测均保留");
            }
            await Command(InstrumentCommand.MeasureI,WirelessStage.RestoringMeasureI,"全部测点已尝试，恢复测量Ⅰ并核验真实高度和模式");await Read(InstrumentCommand.MeasureI);
            task=task with{MeasureIRestored=true,FinishedAtUtc=time.UtcNow,AverageTemperature=WirelessMeasurementRules.Average(task.Plan!,task.Points.Select(p=>p.Temperature).ToArray(),true,false)};
            await Stage(WirelessStage.Completed,task.AverageTemperature.IsValid?"本次完成，平均仅由有效测点计算":"测点流程与恢复已完成，有效温度点数不足，不发布新平均");return task;
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {
            var stopNote="";
            if(task.Stage==WirelessStage.Descending&&!ct.IsCancellationRequested)
            {
                try{await Stage(WirelessStage.Stopping,"下降过程异常，仅请求独立停止，不重放原动作");await device.CommandAsync(task,InstrumentCommand.Stop,ct);stopNote="已请求独立停止，机械状态仍须核查。";}
                catch(Exception stop)when(stop is not OutOfMemoryException){stopNote="独立停止未确认，需核查："+stop.Message+"。";}
            }
            task=task with{Version=task.Version+1,Stage=movementStarted||task.WriteIntentRecorded?WirelessStage.RequiresReview:WirelessStage.Failed,FinishedAtUtc=time.UtcNow,DisplayDueAtUtc=null,AverageTemperature=Measurement.Missing("本次未完成，不覆盖旧成功平均"),Message=(e is OperationCanceledException?"后续测温已取消；软件取消不等于机械停止，需核对现场。":"自动推进已中断：")+e.Message+stopNote};
            await journal.SaveAsync(task,CancellationToken.None);return task;
        }
    }
}

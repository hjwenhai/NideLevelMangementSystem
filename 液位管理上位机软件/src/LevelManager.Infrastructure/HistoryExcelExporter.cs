using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Infrastructure;

public sealed record HistoryExportResult(string FileName,long Rows,int Parts);
public sealed class HistoryExcelExporter(IHistoryStore history,int rowsPerPart=1_000_000)
{
    private sealed record ColumnSpec(string Name,uint Style,Func<HistorySample,object?> Value);
    private static string Quality(ValueQuality q)=>q switch{ValueQuality.Good=>"正常",ValueQuality.Missing=>"无数据",ValueQuality.Offline=>"通讯中断",ValueQuality.Expired=>"数据过期",ValueQuality.Fault=>"故障",_=>"未配置"};
    private static string Date(long? ms)=>ms.HasValue?DateTimeOffset.FromUnixTimeMilliseconds(ms.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff",CultureInfo.InvariantCulture):"";
    public async Task<HistoryExportResult> ExportAsync(string directory,HistoryExportRequest request,SystemConfiguration config,Func<long,Task>? progress=null,CancellationToken ct=default)
    {
        if(!Path.IsPathFullyQualified(directory)||rowsPerPart is <1 or >1_048_575||request.StartUtcMs>=request.EndUtcMs||request.EndUtcMs-request.StartUtcMs>367L*86400000)throw new ArgumentException("导出目录、分片大小或时间范围无效");
        HistoryExportPolicy.Validate(request.SelectedQuantities);
        Directory.CreateDirectory(directory);if(Directory.EnumerateFileSystemEntries(directory).Any())throw new InvalidOperationException("导出目录必须为空");
        var names=config.Tanks.ToDictionary(t=>t.Id,t=>t.Code);List<ColumnSpec> columns=[new("时间（电脑当地时间）",0,s=>Date(s.AtUtcMs)),new("储罐（当前名称）",0,s=>names.GetValueOrDefault(s.TankId,s.TankId.ToString()))];
        var display=config.Display;var heightFactor=display.HeightUnit switch{HeightUnit.Metre=>.001m,HeightUnit.Centimetre=>.1m,_=>1m};var heightUnit=display.HeightUnit switch{HeightUnit.Metre=>"m",HeightUnit.Centimetre=>"cm",_=>"mm"};
        var volumeFactor=display.VolumeUnit==VolumeUnit.Litre?1000m:1m;var volumeUnit=display.VolumeUnit==VolumeUnit.Litre?"L":"m³";var densityFactor=display.DensityUnit==DensityUnit.GramPerCubicCentimetre?.001m:1m;var densityUnit=display.DensityUnit==DensityUnit.GramPerCubicCentimetre?"g/cm³":"kg/m³";var massFactor=display.MassUnit==MassUnit.Kilogram?1000m:1m;var massUnit=display.MassUnit==MassUnit.Kilogram?"kg":"t";
        void Metric(string name,Func<HistorySample,Measurement> get,decimal factor=1,uint style=2)
        {columns.Add(new(name,style,s=>get(s).IsValid?get(s).Value*factor:null));columns.Add(new(name+" 状态",0,s=>Quality(get(s).Quality)));}
        bool Includes(HistoryDataItem q)=>request.SelectedQuantities is null?q!=HistoryDataItem.Interface:request.SelectedQuantities.Contains(q);
        if(Includes(HistoryDataItem.Level))Metric("液位 "+heightUnit,s=>s.LevelMm,heightFactor);
        if(Includes(HistoryDataItem.Interface))Metric("界位 "+heightUnit,s=>s.InterfaceMm,heightFactor);
        if(Includes(HistoryDataItem.InstantTemperature))Metric("温度 ℃",s=>s.InstantTemperatureC);
        if(Includes(HistoryDataItem.AverageTemperature))Metric("平均温度 ℃",s=>s.AverageTemperatureC);
        if(Includes(HistoryDataItem.Density))Metric("仪表密度 "+densityUnit,s=>s.InstrumentDensityKgM3,densityFactor);
        if(Includes(HistoryDataItem.CurrentVolume))Metric("当前体积 "+volumeUnit,s=>s.CurrentVolumeM3,volumeFactor,3);
        if(Includes(HistoryDataItem.NetVolume))Metric("净体积 "+volumeUnit,s=>s.NetVolumeM3,volumeFactor,3);
        if(Includes(HistoryDataItem.Mass))Metric("质量 "+massUnit,s=>s.MassT,massFactor,4);
        if(Includes(HistoryDataItem.Pressure1))Metric("压力1 kPa",s=>s.Pressure1KPa);
        if(Includes(HistoryDataItem.Pressure2))Metric("压力2 kPa",s=>s.Pressure2KPa);
        if(request.SelectedQuantities is null){Metric("模拟量输入1 mA",s=>s.Analog1Ma);Metric("模拟量输入2 mA",s=>s.Analog2Ma);}
        columns.AddRange([new("配置版本",0,s=>s.ConfigurationRevision),new("计算版本",0,s=>s.CalculationRevision),new("接收时间（电脑当地时间）",0,s=>Date(s.ReceivedAtUtcMs)),new("来源时间（电脑当地时间）",0,s=>Date(s.SourceAtUtcMs)),new("来源时间已确认",0,s=>s.SourceTimeConfirmed?"是":"否"),new("平均测温完成时间",0,s=>Date(s.AverageCompletedAtUtcMs)),new("仪表状态",0,s=>s.InstrumentStatus),new("故障码",0,s=>s.InstrumentFault),new("采集会话",0,s=>s.AcquisitionSessionId.ToString())]);
        if(request.IncludeAtsPoints)for(var i=1;i<=16;i++){var point=i;Metric("ATS第"+point+"点 ℃",s=>s.AtsPoints.FirstOrDefault(p=>p.Index==point)?.TemperatureC??new(null,ValueQuality.Unconfigured));}
        StreamingHistoryWorkbook? document=null;var part=0;var inPart=0;long rows=0;List<string> files=[];
        void Close()
        {
            if(document is null)return;document.Complete(new[]{new[]{"项目","内容"},new[]{"开始时间（电脑当地时间）",Date(request.StartUtcMs)},new[]{"结束时间（不含）",Date(request.EndUtcMs)},new[]{"储罐",request.TankId.HasValue?names.GetValueOrDefault(request.TankId.Value,request.TankId.ToString()!):"全部历史储罐"},new[]{"导出配置版本",config.Revision.ToString(CultureInfo.InvariantCulture)},new[]{"分片序号",part.ToString(CultureInfo.InvariantCulture)},new[]{"本片数据行数",inPart.ToString(CultureInfo.InvariantCulture)},new[]{"ATS点温度",request.IncludeAtsPoints?"包含16点及各点状态":"本次未导出，原历史数据保留"},new[]{"状态筛选",request.QualityQuantity+" / "+request.Quality},new[]{"说明","来自同一次数据库读取快照。保留当时值、质量、配置与计算版本；不按新罐容表重算。正常0和无效空值分开。罐号按导出时名称显示，已删除罐保留内部编号。"}});document=null;
        }
        void Start()
        {
            Close();part++;inPart=0;var file="历史数据_"+part.ToString("D3")+".xlsx";files.Add(file);document=new(Path.Combine(directory,file),columns.Count,display);document.Row(columns.Select(c=>((object?)c.Name,1u)));
        }
        try
        {
            Start();await foreach(var sample in history.ReadForExportAsync(new(request.StartUtcMs,request.EndUtcMs,request.TankId,QualityQuantity:request.QualityQuantity,Quality:request.Quality),ct))
            {
                ct.ThrowIfCancellationRequested();if(inPart>=rowsPerPart)Start();document!.Row(columns.Select(c=>(c.Value(sample),c.Style)));rows++;inPart++;
                if(rows%2048==0){if(progress is not null)await progress(rows);await Task.Yield();}
            }
            Close();ct.ThrowIfCancellationRequested();
            if(files.Count==1)return new(files[0],rows,part);
            var metadata=new{Query=request,ExportConfigurationRevision=config.Revision,Rows=rows,Files=files,Note="各文件为同一次数据库读取快照的连续分片。空值与正常0分开，数值保持当时结果，不按新罐容表重算。罐号采用导出时名称，原配置和计算版本另列。",Display=config.Display};
            await File.WriteAllTextAsync(Path.Combine(directory,"导出说明.json"),JsonSerializer.Serialize(metadata,new JsonSerializerOptions{WriteIndented=true}),ct);
            var archive=Path.Combine(directory,"历史数据分片.zip.partial");
            using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create))
            {
                foreach(var file in files.Append("导出说明.json")){ct.ThrowIfCancellationRequested();zip.CreateEntryFromFile(Path.Combine(directory,file),file,CompressionLevel.NoCompression);}
            }
            var final=Path.Combine(directory,"历史数据分片.zip");File.Move(archive,final);return new(Path.GetFileName(final),rows,part);
        }
        finally{document?.Dispose();}
    }
}

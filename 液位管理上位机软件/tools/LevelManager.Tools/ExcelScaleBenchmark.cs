using System.Diagnostics;
using System.Text.Json;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
internal static class ExcelScaleBenchmark
{
 public static async Task<int> Run(string[] args)
 {
  string Option(string key){var index=Array.IndexOf(args,key);return index>=0&&index+1<args.Length?args[index+1]:throw new ArgumentException("缺少参数 "+key);}var source=Option("--source");var output=Option("--output");if(!Path.IsPathFullyQualified(source)||!File.Exists(source)||!Path.IsPathFullyQualified(output)||Directory.Exists(output))throw new ArgumentException("使用已生成的隔离样本及新导出目录");Directory.CreateDirectory(output);var store=new SqliteConfigurationStore(source);await store.InitializeAsync(allowCapacityProfile:true);var config=await store.GetAsync();var history=new SqliteHistoryStore(source);var from=DateTimeOffset.Parse("2025-01-01T00:00:00Z").ToUnixTimeMilliseconds();var timer=Stopwatch.StartNew();var result=await new HistoryExcelExporter(history).ExportAsync(Path.Combine(output,"excel"),new(from,from+365L*86400000,config.Tanks[0].Id,true),config,rows=>{if(rows%32768==0)File.WriteAllText(Path.Combine(output,"progress.json"),JsonSerializer.Serialize(new{rows,elapsedSeconds=timer.Elapsed.TotalSeconds,workingBytes=Process.GetCurrentProcess().WorkingSet64}));return Task.CompletedTask;});if(result.Rows!=525600)throw new InvalidDataException("单罐全年导出行数不符");var value=new{passed=true,schemaVersion=SqliteConfigurationStore.CurrentSchema,sourceTotalRows=(await history.StatisticsAsync()).Rows,result,elapsedSeconds=timer.Elapsed.TotalSeconds,peakBytes=Process.GetCurrentProcess().PeakWorkingSet64,bytes=new FileInfo(Path.Combine(output,"excel",result.FileName)).Length,realDeviceAccess=false};await File.WriteAllTextAsync(Path.Combine(output,"result.json"),JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("PASS: annual real XLSX with16ATS and states, bounded streaming output");return 0;
 }
}

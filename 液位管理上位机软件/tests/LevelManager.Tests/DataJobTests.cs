using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Spreadsheet;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Application;
using Naide.LevelManager.Infrastructure;
using Xunit;
namespace Naide.LevelManager.Tests;
public class DataJobTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-10-28T01:00:00Z");
    private static DataJob Job()=>new(){Kind=DataJobKind.Snapshot,ActorId="a",ActorName="管理员",CreatedAtUtc=Now,RequestJson="{}"};
    [Fact]public async Task QueueBoundClaimAndCrashRecoveryNeverReplayQueuedOrRunningJobs()
    {
        await using var f=new StoreFixture();await f.Initialize();var jobs=new SqliteDataJobStore(f.PathToDb);var first=Job();await jobs.CreateAsync(first);await jobs.CreateAsync(Job());await jobs.CreateAsync(Job());await Assert.ThrowsAsync<InvalidOperationException>(()=>jobs.CreateAsync(Job()));
        Assert.Equal(DataJobState.Running,(await jobs.ClaimAsync(Now))!.State);await jobs.RecoverInterruptedAsync(Now.AddSeconds(1));Assert.Null(await jobs.ClaimAsync(Now.AddSeconds(2)));Assert.All(await jobs.ListAsync(),j=>Assert.Equal(DataJobState.Interrupted,j.State));
    }
    [Fact]public async Task SnapshotAndJobCompletionAuditAreOneTransactionAndValuesRemainFrozen()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),new("a","管理员",AccountRole.Administrator));var jobs=new SqliteDataJobStore(f.PathToDb);var job=Job();await jobs.CreateAsync(job);job=(await jobs.ClaimAsync(Now))!;
        var snapshot=new DataSnapshot(Guid.NewGuid(),"交班",Now,c.Revision,"管理员",[new(){TankId=c.Tanks[0].Id,LevelMm=Measurement.Good(0),InstantTemperatureC=new(null,ValueQuality.Offline)}]);var done=job with{State=DataJobState.Completed,SnapshotId=snapshot.Id,Rows=1,FinishedAtUtc=Now};
        await f.Scalar("CREATE TRIGGER fail_snapshot_audit BEFORE INSERT ON operation_events BEGIN SELECT RAISE(ABORT,'test failure'); END;");await Assert.ThrowsAnyAsync<Exception>(()=>jobs.CompleteSnapshotAsync(done,snapshot));Assert.Empty(await jobs.SnapshotsAsync());Assert.Equal(DataJobState.Running,(await jobs.GetAsync(job.Id))!.State);
        await f.Scalar("DROP TRIGGER fail_snapshot_audit");await jobs.CompleteSnapshotAsync(done,snapshot);Assert.Equal(0,(await jobs.SnapshotAsync(snapshot.Id))!.Readings[0].LevelMm.Value);Assert.Null((await jobs.SnapshotAsync(snapshot.Id))!.Readings[0].InstantTemperatureC.Value);Assert.Equal(1,Assert.Single(await jobs.SnapshotsAsync()).ValidLevels);
    }
    [Fact]public async Task StreamingExcelPreservesZeroQualityAndEscapesFormulaText()
    {
        await using var f=new StoreFixture();await f.Initialize();var input=Fixtures.Configuration();input=input with{Tanks=[input.Tanks[0] with{Code="=1+1"}]};var c=await f.Store.SaveAsync(input,new("a","管理员",AccountRole.Administrator));var history=new SqliteHistoryStore(f.PathToDb);var session=Guid.NewGuid();
        await history.AppendAsync([new(){TankId=c.Tanks[0].Id,AtUtcMs=1000,ConfigurationRevision=c.Revision,AcquisitionSessionId=session,LevelMm=Measurement.Good(0)},new(){TankId=c.Tanks[0].Id,AtUtcMs=2000,ConfigurationRevision=c.Revision,AcquisitionSessionId=session,LevelMm=new(null,ValueQuality.Offline)}]);
        var directory=Path.Combine(f.Root,"export");var result=await new HistoryExcelExporter(history).ExportAsync(directory,new(0,3000),c);Assert.Equal(2,result.Rows);
        using var doc=SpreadsheetDocument.Open(Path.Combine(directory,result.FileName),false);Assert.Empty(new OpenXmlValidator().Validate(doc).Take(10));var rows=doc.WorkbookPart!.WorksheetParts.First().Worksheet!.Descendants<Row>().ToArray();Assert.Equal(3,rows.Length);Assert.Equal("=1+1",rows[1].Elements<Cell>().ElementAt(1).InnerText);Assert.Empty(doc.WorkbookPart!.WorksheetParts.SelectMany(p=>p.Worksheet!.Descendants<CellFormula>()));Assert.Equal("0",rows[1].Elements<Cell>().ElementAt(2).InnerText);Assert.Equal("",rows[2].Elements<Cell>().ElementAt(2).InnerText);Assert.Equal("通讯中断",rows[2].Elements<Cell>().ElementAt(3).InnerText);
    }
    [Fact]public async Task ExcelPreservesLiteralEscapeLookingTankNamesAndEncodesControlCharacters()
    {
        await using var f=new StoreFixture();await f.Initialize();var original=Fixtures.Configuration();var c=await f.Store.SaveAsync(original with{Tanks=[original.Tanks[0] with{Code="_x0041_\u0001"}]},new("a","管理员",AccountRole.Administrator));var history=new SqliteHistoryStore(f.PathToDb);await history.AppendAsync([new(){TankId=c.Tanks[0].Id,AtUtcMs=1000,ConfigurationRevision=c.Revision,AcquisitionSessionId=Guid.NewGuid(),LevelMm=Measurement.Good(0)}]);var directory=Path.Combine(f.Root,"escaped");var result=await new HistoryExcelExporter(history).ExportAsync(directory,new(0,2000),c);using var zip=ZipFile.OpenRead(Path.Combine(directory,result.FileName));using var stream=zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();var xml=System.Xml.Linq.XDocument.Load(stream);Assert.Contains("_x005F_x0041__x0001_",xml.DescendantNodes().OfType<System.Xml.Linq.XText>().Select(t=>t.Value));
    }
    [Fact]public async Task SplitExcelContainsEverySourceRowOnceInOrderedParts()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),new("a","管理员",AccountRole.Administrator));var history=new SqliteHistoryStore(f.PathToDb);var session=Guid.NewGuid();
        await history.AppendAsync(Enumerable.Range(0,7).Select(i=>new HistorySample{TankId=c.Tanks[0].Id,AtUtcMs=i*1000,ConfigurationRevision=c.Revision,AcquisitionSessionId=session,LevelMm=Measurement.Good(i)}).ToArray());
        var directory=Path.Combine(f.Root,"split");var result=await new HistoryExcelExporter(history,3).ExportAsync(directory,new(0,10000),c);Assert.Equal(7,result.Rows);Assert.Equal(3,result.Parts);
        using var zip=ZipFile.OpenRead(Path.Combine(directory,result.FileName));Assert.Equal(4,zip.Entries.Count);List<decimal> values=[];
        foreach(var entry in zip.Entries.Where(e=>e.Name.EndsWith(".xlsx")).OrderBy(e=>e.Name))
        {using var bytes=new MemoryStream();using(var stream=entry.Open())stream.CopyTo(bytes);bytes.Position=0;using var doc=SpreadsheetDocument.Open(bytes,false);Assert.Empty(new OpenXmlValidator().Validate(doc).Take(10));values.AddRange(doc.WorkbookPart!.WorksheetParts.First().Worksheet!.Descendants<Row>().Skip(1).Select(r=>decimal.Parse(r.Elements<Cell>().ElementAt(2).InnerText)));}
        Assert.Equal(Enumerable.Range(0,7).Select(i=>(decimal)i),values);
    }
    [Fact]public async Task ExportStreamHoldsOneDatabaseSnapshotWhileNewRowsAreInserted()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),new("a","管理员",AccountRole.Administrator));var history=new SqliteHistoryStore(f.PathToDb);var session=Guid.NewGuid();HistorySample Sample(long at)=>new(){TankId=c.Tanks[0].Id,AtUtcMs=at,ConfigurationRevision=c.Revision,AcquisitionSessionId=session,LevelMm=Measurement.Good(at)};
        await history.AppendAsync([Sample(1),Sample(2)]);await using var stream=history.ReadForExportAsync(new(0,100)).GetAsyncEnumerator();Assert.True(await stream.MoveNextAsync());await history.AppendAsync([Sample(3)]);List<long> read=[stream.Current.AtUtcMs];while(await stream.MoveNextAsync())read.Add(stream.Current.AtUtcMs);Assert.Equal(new long[]{1,2},read);
    }
    [Fact]public async Task ScheduledMeasurementDispatchIsClaimedOnceAndNeverConsumedAsSnapshotOrReplayed()
    {
        await using var f=new StoreFixture();await f.Initialize();var store=new SqliteDataJobStore(f.PathToDb);var id=Guid.NewGuid();var job=Job() with{Kind=DataJobKind.AverageTemperatureDispatch,State=DataJobState.Running};Assert.True(await store.CreateScheduledAsync(id,"2026-10-28",job));Assert.False(await store.CreateScheduledAsync(id,"2026-10-28",job with{Id=Guid.NewGuid()}));Assert.Null(await store.ClaimAsync(Now));await store.RecoverInterruptedAsync(Now);Assert.Equal(DataJobState.Interrupted,(await store.GetAsync(job.Id))!.State);Assert.False(await store.CreateScheduledAsync(id,"2026-10-28",job with{Id=Guid.NewGuid()}));Assert.Null(await store.ClaimAsync(Now));
    }
}

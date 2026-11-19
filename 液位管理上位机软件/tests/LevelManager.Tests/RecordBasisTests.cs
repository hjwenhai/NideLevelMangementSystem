using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Xunit;
namespace Naide.LevelManager.Tests;
public class RecordBasisTests
{
    private static readonly AuditActor Admin=new("basisqa","记录依据验证",AccountRole.Administrator);
    [Fact]public async Task HistoricalBasisKeepsOriginalTankSettingsAndTableVersionAfterCurrentChanges()
    {
        await using var fixture=new StoreFixture();await fixture.Initialize();
        var table=await fixture.Store.SaveTableAsync(new(){Number="CT-BASIS",Name="原始罐容表",Points=[new(0,0),new(12000,3000)]},Admin);
        var proposed=Fixtures.Configuration(2);proposed=proposed with{Tanks=proposed.Tanks.Select(t=>t with{Metering=new(){Enabled=true,MainTableId=table.Id,WaterVolumeM3=1,StandardDensity20KgM3=850}}).ToArray()};
        var original=await fixture.Store.SaveAsync(proposed,Admin);var tank=original.Tanks[0];var metering=new MeteringService(fixture.Store);
        var sample=new ReadingSnapshot{TankId=tank.Id,LevelMm=Measurement.Good(6000),InstrumentDensityKgM3=Measurement.Good(830)};
        var first=await metering.CalculateAsync(tank,sample,default);
        await fixture.Store.SaveTableAsync(table with{Name="当前新版表",Points=[new(0,0),new(12000,4000)]},Admin);
        var changed=await fixture.Store.SaveAsync(original with{Tanks=original.Tanks.Select(t=>t with{Name="当前名称",Metering=t.Metering with{WaterVolumeM3=8,StandardDensity20KgM3=900}}).ToArray()},Admin);
        var second=await metering.CalculateAsync(changed.Tanks[0],sample,default);
        var old=await fixture.Store.GetRecordBasisAsync(tank.Id,original.Revision,first.CalculationRevision);
        Assert.Equal(tank.Name,old.Tank.Name);Assert.Equal(1,old.Metering!.WaterVolumeM3);Assert.Equal(850,old.Metering.StandardDensity20KgM3);Assert.Equal(1,Assert.Single(old.Tables).Revision);Assert.Equal("原始罐容表",old.Tables[0].Name);
        var current=await fixture.Store.GetRecordBasisAsync(tank.Id,changed.Revision,second.CalculationRevision);
        Assert.Equal("当前名称",current.Tank.Name);Assert.Equal(8,current.Metering!.WaterVolumeM3);Assert.Equal(2,Assert.Single(current.Tables).Revision);
        await Assert.ThrowsAsync<ArgumentException>(()=>fixture.Store.GetRecordBasisAsync(original.Tanks[1].Id,original.Revision,first.CalculationRevision));
        await Assert.ThrowsAsync<ArgumentException>(()=>fixture.Store.GetRecordBasisAsync(tank.Id,999999,first.CalculationRevision));
        await Assert.ThrowsAsync<ArgumentException>(()=>fixture.Store.GetRecordBasisAsync(tank.Id,original.Revision,999999));
    }
    [Fact]public async Task ZeroCalculationVersionIsShownAsUnconfiguredRatherThanCurrentMetering()
    {
        await using var fixture=new StoreFixture();await fixture.Initialize();var config=await fixture.Store.SaveAsync(Fixtures.Configuration(1),Admin);
        var basis=await fixture.Store.GetRecordBasisAsync(config.Tanks[0].Id,config.Revision,0);
        Assert.Null(basis.Metering);Assert.Empty(basis.Tables);Assert.Contains("没有启用",basis.Formula);
    }
    [Theory][InlineData("capacity-main.xlsx",3000)][InlineData("capacity-static-pressure.xlsx",12)]
    public void SuppliedXlsxTemplatesImportFiveRowsIncludingTheFirstZeroAndIgnoreNotes(string name,double lastVolume)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"LevelManager.slnx")))directory=directory.Parent;
        Assert.NotNull(directory);var path=Path.Combine(directory!.FullName,"assets","capacity-templates",name);
        var preview=CapacityExcel.Preview(File.ReadAllBytes(path),0,2,1,2);
        Assert.Equal(5,preview.ImportedRows);Assert.Equal(0,preview.Points[0].Height);Assert.Equal(0,preview.Points[0].Volume);Assert.Equal((decimal)lastVolume,preview.Points[^1].Volume);
    }
}


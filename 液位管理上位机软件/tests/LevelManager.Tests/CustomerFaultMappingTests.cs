using Naide.LevelManager.Domain;
using Xunit;
namespace Naide.LevelManager.Tests;
public class CustomerFaultMappingTests
{
    [Fact]public void IfxMappingsFollowCustomerRedTextAndRetainFirstDetection()
    {
        string?[] codes=[null,"7","9","A","1","8",null,null,null,null,"5","6","G","B","3","2"];
        string[] corrected=["温度计超量程","模拟量输入超量程","参数错误","电机错误","浮子控制错误"];
        var first=DateTimeOffset.Parse("2026-11-18T00:00:00Z");
        var old=InstrumentFaultCatalog.Observe(0xffff,AcquisitionProtocol.IfxL25,[],first);
        var actual=InstrumentFaultCatalog.Observe(0xffff,AcquisitionProtocol.IfxL25,old,first.AddDays(1));
        foreach(var fault in actual){Assert.Equal(codes[fault.Bit],fault.DisplayCode);Assert.Equal(first,fault.FirstDetectedAtUtc);}
        for(var bit=1;bit<=5;bit++)Assert.Equal(corrected[bit-1],actual.Single(f=>f.Bit==bit).Description);
        var pair=InstrumentFaultCatalog.Observe(0x0802,AcquisitionProtocol.IfxL25,[],first);
        Assert.Equal(new[]{"6","7"},pair.Select(f=>f.DisplayCode));
    }
    [Theory][InlineData(AcquisitionProtocol.DirectRtu)][InlineData(AcquisitionProtocol.GatewayTcp)][InlineData(AcquisitionProtocol.GatewayRtu)]
    public void FwMappingsRemainUnchangedIncludingUnassignedCodes(AcquisitionProtocol protocol)
    {
        string?[] codes=["1","2","3",null,"5","6","7","8","9","A","B","C",null,"E",null,"G"];
        var actual=InstrumentFaultCatalog.Observe(0xffff,protocol,[],DateTimeOffset.UtcNow);
        foreach(var fault in actual)Assert.Equal(codes[fault.Bit],fault.DisplayCode);
    }
}

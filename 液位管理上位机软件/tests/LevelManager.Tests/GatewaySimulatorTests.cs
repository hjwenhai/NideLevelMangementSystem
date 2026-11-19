using Naide.LevelManager.Communication;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Simulator;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Naide.LevelManager.Tests;
public sealed class GatewaySimulatorTests
{
    [Fact]
    public async Task ElevenSlotsReadByProductionReaderWithAtsAndPressure()
    {
        var model=new GatewayModel(11);await using var server=new LoopbackGateway(model);server.Start(0);
        Assert.Equal(IPAddress.Loopback,new IPEndPoint(IPAddress.Loopback,server.Port).Address);
        await using var client=new ModbusTcpRegisterClient("127.0.0.1",server.Port,2000);
        var channel=new ChannelDefinition{Protocol=AcquisitionProtocol.GatewayTcp,Host="127.0.0.1",TcpPort=server.Port,UnitId=1};
        foreach(var slot in Enumerable.Range(1,11))
        {
            var tank=new TankDefinition{ChannelId=channel.Id,Address=slot,Enabled=true,TemperatureMode=TemperatureMode.Ats,AtsPointCount=16,Pressure1=new(){Enabled=true},Pressure2=new(){Enabled=true}};
            var reading=await new DeviceReader(channel.Protocol).ReadAsync(client,new(channel,tank,1,Guid.NewGuid()),default);
            Assert.True(reading.LevelMm.IsValid,reading.LevelMm.Reason);Assert.True(reading.InstantTemperatureC.IsValid);Assert.True(reading.InstrumentDensityKgM3.IsValid);Assert.Equal(16,reading.AtsPoints.Count);Assert.All(reading.AtsPoints,p=>Assert.True(p.TemperatureC.IsValid));Assert.True(reading.AverageTemperatureC.IsValid);Assert.True(reading.Analog1Ma.IsValid);Assert.True(reading.Analog2Ma.IsValid);
        }
        Assert.Equal(0,server.Writes);
    }
    [Fact]
    public void ControlLatchesAndIndependentStopAndResetAffectSimulationOnly()
    {
        var model=new GatewayModel(11);model.Varying=false;model.Write(30000,5);
        Assert.Equal((ushort)2,model.Snapshot()[0].Mode);Assert.Equal((ushort)3,model.Read(30000,12)[1]);
        Assert.Equal((byte)6,Assert.Throws<SimulatorProtocolException>(()=>model.Write(30000,6)).Code);
        model.Write(30007,1);Assert.Equal((ushort)4,model.Snapshot()[0].Mode);
        model.Write(30000,0);model.Write(30007,0);Assert.Equal((ushort)4,model.Snapshot()[0].Mode);
        model.Write(30000,1);Assert.Equal((ushort)1,model.Snapshot()[0].Mode);Assert.Equal((ushort)2,model.Read(30000,12)[5]);
    }
    [Fact]
    public void RemoteLevelStagingAndSlotIsolation()
    {
        var model=new GatewayModel(11){Varying=false};var other=model.Snapshot()[1].PositionMm;
        model.Stage(31000,[1,1234]);Assert.Equal((ushort)1,model.Read(31000,4)[2]);model.Write(30000,11);
        Assert.Equal(6677m,model.Snapshot()[0].PositionMm);Assert.Equal(other,model.Snapshot()[1].PositionMm);Assert.Equal((ushort)0,model.Read(31000,4)[2]);Assert.Equal((ushort)3,model.Read(30000,12)[1]);
        Assert.Throws<SimulatorProtocolException>(()=>model.Stage(31000,[65535,65535]));
    }
    [Fact]
    public async Task OfflineAndFaultAreVisibleToProductionReader()
    {
        var model=new GatewayModel(11);await using var server=new LoopbackGateway(model);server.Start(0);await using var client=new ModbusTcpRegisterClient("127.0.0.1",server.Port,2000);
        var c=new ChannelDefinition{Protocol=AcquisitionProtocol.GatewayTcp,UnitId=1};var t=new TankDefinition{ChannelId=c.Id,Address=1};var binding=new DeviceBinding(c,t,1,Guid.NewGuid());var reader=new DeviceReader(c.Protocol);
        model.SetCondition(1,true,0);Assert.Equal(ValueQuality.Offline,(await reader.ReadAsync(client,binding,default)).LevelMm.Quality);
        model.SetCondition(1,false,1);Assert.Equal((ushort)1,(await reader.ReadAsync(client,binding,default)).InstrumentFault);
        model.SetCondition(1,false,0);Assert.True((await reader.ReadAsync(client,binding,default)).LevelMm.IsValid);
    }
    [Fact]
    public void PduRejectsUnsupportedFunctionsAndInvalidReadsAndSlots()
    {
        var model=new GatewayModel(11);var server=new LoopbackGateway(model);
        Assert.Equal(new byte[]{0x85,1},server.Handle([5,0,0,0,0]));Assert.Equal(new byte[]{0x84,3},server.Handle([4,0,0,0,0]));
        Assert.Equal(new byte[]{0x84,2},server.Handle([4,0xff,0xff,0,1]));Assert.Equal(new byte[]{0x90,3},server.Handle([16,0,0,0,2,4]));
        Assert.Equal((ushort)16,model.Read((ushort)(1000+24*11),24)[0]);Assert.Throws<SimulatorProtocolException>(()=>model.Write((ushort)(30000+12*11),1));
    }
    [Fact]
    public async Task PortConflictDoesNotTakeOverAndDisposeClosesSockets()
    {
        var model=new GatewayModel();var server=new LoopbackGateway(model);server.Start(0);var port=server.Port;
        await using(var other=new LoopbackGateway(new GatewayModel()))Assert.Throws<SocketException>(()=>other.Start(port));
        await server.DisposeAsync();var listener=new TcpListener(IPAddress.Loopback,port);listener.Start();listener.Stop();
    }
    [Fact]
    public async Task TcpHandlesFragmentedAndBackToBackFrames()
    {
        await using var server=new LoopbackGateway(new GatewayModel());server.Start(0);using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,server.Port);using var stream=client.GetStream();
        byte[] request=[0,1,0,0,0,6,1,4,0,0,0,4];await stream.WriteAsync(request.AsMemory(0,3));await stream.WriteAsync(request.AsMemory(3));var response=new byte[17];await stream.ReadExactlyAsync(response);Assert.Equal((byte)4,response[7]);Assert.Equal((byte)8,response[8]);
        await stream.WriteAsync(request.Concat(request).ToArray());var pair=new byte[34];await stream.ReadExactlyAsync(pair);Assert.Equal(response,pair[..17]);Assert.Equal(response,pair[17..]);
    }
    [Fact]
    public void CountsAndValuesAreBoundedAndRebootIdentityChanges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(()=>new GatewayModel(0));Assert.Throws<ArgumentOutOfRangeException>(()=>new GatewayModel(65));
        var a=new GatewayModel();var b=new GatewayModel();Assert.NotEqual(a.BootId,b.BootId);Assert.Throws<ArgumentException>(()=>a.SetValues(1,12001,20,800));
        a.SetValues(11,1000,0,900);a.Varying=false;Assert.Equal(0,a.Snapshot()[10].TemperatureC);Assert.Equal(1000,a.Snapshot()[10].PositionMm);
        a.Varying=true;a.SetValues(11,0,0,900);Assert.InRange(a.Snapshot()[10].PositionMm,0,12000);Assert.Equal(24,a.Read(1240,24).Length);
        a.SetValues(1,12000,20,800);Assert.InRange(a.Snapshot()[0].PositionMm,0,12000);Assert.Equal(24,a.Read(1000,24).Length);
    }
    [Fact]
    public void GatewaySlotOneAndDirectAddressOneAreSeparateBindings()
    {
        var gateway=new ChannelDefinition{Name="模拟",Protocol=AcquisitionProtocol.GatewayTcp,Host="127.0.0.1",TcpPort=15020,UnitId=1};
        var direct=new ChannelDefinition{Name="客户真实仪表",Protocol=AcquisitionProtocol.DirectRtu,PortName="COM2",BaudRate=4800,UnitId=1};
        var tanks=Enumerable.Range(1,11).Select(i=>new TankDefinition{Code=$"SIM-{i:00}",ChannelId=gateway.Id,Address=i}).Append(new TankDefinition{Code="REAL-01",ChannelId=direct.Id,Address=1}).ToArray();
        ConfigurationRules.RequireValid(new(){Channels=[gateway,direct],Tanks=tanks});
    }
}

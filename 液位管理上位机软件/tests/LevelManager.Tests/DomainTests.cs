using Naide.LevelManager.Domain;
using Naide.LevelManager.Communication;
using Xunit;
namespace Naide.LevelManager.Tests;
public static class Fixtures
{
 public static SystemConfiguration Configuration(int count=1)
 {
  var channels=Enumerable.Range(0,(count+31)/32).Select(i=>new ChannelDefinition{Name="采集通道"+(i+1),PortName="COM"+(i+1)}).ToArray();
  return new(){Channels=channels,Tanks=Enumerable.Range(0,count).Select(i=>new TankDefinition{Code="T-"+(i+1).ToString("000"),ChannelId=channels[i/32].Id,Address=i%32+1}).ToArray()};
 }
}
public class DomainTests
{
 [Fact]public void DefaultsAndAnnualCapacityMatchApprovedBaseline()
 {var c=new SystemConfiguration();Assert.Equal(60,c.HistoryIntervalSeconds);Assert.Equal(12,c.RetentionMonths);Assert.Equal(128,ConfigurationRules.MaximumTanks);Assert.Equal(67_276_800,ConfigurationRules.EstimateRows(128,60,365));Assert.Equal(67_461_120,ConfigurationRules.EstimateRows(128,60,366));}
 [Fact]public void NewSerialChannelRequiresAnExplicitPortInsteadOfAssumingCom1()
 {var channel=new ChannelDefinition{Name="新采集通道",Protocol=AcquisitionProtocol.DirectRtu};Assert.Empty(channel.PortName);Assert.Contains(ConfigurationRules.Validate(new(){Channels=[channel]}),e=>e.Field.EndsWith("/portName"));Assert.Empty(ConfigurationRules.Validate(new(){Channels=[channel with{PortName="COM3"}]}));}
 [Fact]public void Supports128TanksAcrossFourSerialLines()=>Assert.Empty(ConfigurationRules.Validate(Fixtures.Configuration(128)));
 [Fact]public void Rejects129IncludingDisabledTanks()
 {var c=Fixtures.Configuration(129);c=c with{Tanks=c.Tanks.Select(t=>t with{Enabled=false}).ToArray()};Assert.Contains(ConfigurationRules.Validate(c),e=>e.Field=="tanks");}
 [Fact]public void CrossLineSameAddressIsAllowedButDuplicateBindingIsNot()
 {var c=Fixtures.Configuration(33);Assert.Empty(ConfigurationRules.Validate(c));c=c with{Tanks=c.Tanks.Select((t,i)=>i==1?t with{Address=1}:t).ToArray()};Assert.Contains(ConfigurationRules.Validate(c),e=>e.Message.Contains("重复"));}
 [Theory][InlineData(243,true)][InlineData(244,false)][InlineData(0,false)]
 public void DirectStationRangeFollowsInstrumentProtocol(int address,bool allowed)
 {var c=Fixtures.Configuration();c=c with{Tanks=[c.Tanks[0] with{Address=address}]};Assert.Equal(allowed,ConfigurationRules.Validate(c).Count==0);}
 [Fact]public void IfxOneComAndOneStationContainsAll32Slots()
 {var c=Fixtures.Configuration(32);c=c with{Channels=[c.Channels[0] with{Protocol=AcquisitionProtocol.IfxL25,BaudRate=38400,Parity=SerialParity.Even,UnitId=1}]};Assert.Empty(ConfigurationRules.Validate(c));c=c with{Channels=[c.Channels[0] with{UnitId=2,BaudRate=9600,Parity=SerialParity.Odd,StopBits=2}]};Assert.Empty(ConfigurationRules.Validate(c));}
 [Fact]public void GatewayUnitIdIsSeparateFromInternalSlot()
 {var g=new ChannelDefinition{Name="网关",Protocol=AcquisitionProtocol.GatewayTcp,Host="127.0.0.1",UnitId=1};var c=new SystemConfiguration{Channels=[g],Tanks=Enumerable.Range(1,64).Select(i=>new TankDefinition{Code="G-"+i,ChannelId=g.Id,Address=i}).ToArray()};Assert.Empty(ConfigurationRules.Validate(c));Assert.All(c.Channels,x=>Assert.Equal(1,x.UnitId));}
 [Fact]public void PreventsAliasSerialPortsHavingTwoOwners()
 {var c=Fixtures.Configuration(33);c=c with{Channels=[c.Channels[0],c.Channels[1] with{PortName=" com01 "}]};Assert.Contains(ConfigurationRules.Validate(c),e=>e.Message.Contains("一个采集通道"));}
 [Theory][InlineData(0,false)][InlineData(1,true)][InlineData(16,true)][InlineData(17,false)]
 public void AtsPointCountDoesNotAcceptOutOfRange(int count,bool allowed)
 {var c=Fixtures.Configuration();c=c with{Tanks=[c.Tanks[0] with{TemperatureMode=TemperatureMode.Ats,AtsPointCount=count}]};Assert.Equal(allowed,ConfigurationRules.Validate(c).Count==0);}
 [Fact]public void ZeroTemperatureIsValidAndMissingIsNotZero()
 {Assert.True(Measurement.Good(0).IsValid);Assert.Null(Measurement.Missing("未读取").Value);}
 [Fact]public void NewSessionOrRevisionCannotReuseOldValidReading()
 {var session=Guid.NewGuid();var now=DateTimeOffset.UtcNow;var r=new ReadingSnapshot{AcquisitionSessionId=session,ConfigurationRevision=1,ReceivedAtUtc=now,LevelMm=Measurement.Good(1000),InstantTemperatureC=Measurement.Good(0)};Assert.Equal(1000,r.ForCurrentState(now,session,1,30,true,true,TemperatureMode.Ats).LevelMm.Value);Assert.Null(r.ForCurrentState(now,Guid.NewGuid(),1,30,true,true,TemperatureMode.Ats).LevelMm.Value);Assert.Null(r.ForCurrentState(now,session,2,30,true,true,TemperatureMode.Ats).LevelMm.Value);Assert.Null(r.ForCurrentState(now.AddSeconds(31),session,1,30,true,true,TemperatureMode.Ats).LevelMm.Value);Assert.Null(r.ForCurrentState(now,session,1,30,false,true,TemperatureMode.Ats).LevelMm.Value);}
 [Fact]public void NoThermometerNeverShowsZeroTemperature()
 {var s=Guid.NewGuid();var now=DateTimeOffset.UtcNow;var r=new ReadingSnapshot{AcquisitionSessionId=s,ReceivedAtUtc=now,InstantTemperatureC=Measurement.Good(0)};Assert.Null(r.ForCurrentState(now,s,0,30,true,true,TemperatureMode.None).InstantTemperatureC.Value);}
 [Fact]public void RtuCrcUsesKnownModbusVectorAndBadFrameIsRejected()
 {Assert.Equal(0xcdc5,ModbusReadCodec.Crc16([1,3,0,0,0,10]));byte[] frame=[1,4,2,0,123,0,0];var crc=ModbusReadCodec.Crc16(frame.AsSpan(0,5));frame[5]=(byte)crc;frame[6]=(byte)(crc>>8);Assert.Equal((ushort)123,ModbusReadCodec.DecodeRtuInputResponse(frame,1,1)[0]);frame[3]^=1;Assert.Throws<InvalidDataException>(()=>ModbusReadCodec.DecodeRtuInputResponse(frame,1,1));}
 [Fact]public void DecodesSignedLevelNegativeTemperatureAndProtocolDensityUnits()
 {Assert.Equal(-1m,ModbusReadCodec.LevelMm(65535,65526));Assert.Equal(-12.50m,ModbusReadCodec.TemperatureC(0xfb1e));Assert.Equal(851m,ModbusReadCodec.DirectOrGatewayDensityKgM3(8510));Assert.Equal(851m,ModbusReadCodec.IfxDensityKgM3(851));}
}

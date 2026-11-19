using System.Buffers.Binary;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Communication;
public interface IReadOnlyRegisterClient : IAsyncDisposable
{
 ValueTask<ushort[]> ReadInputRegistersAsync(byte unit,ushort start,ushort count,CancellationToken cancellationToken);
}
public sealed record DeviceBinding(ChannelDefinition Channel,TankDefinition Tank,long ConfigurationRevision,Guid AcquisitionSessionId,int ExpirySeconds=30);
public interface IDeviceReader
{
 AcquisitionProtocol Protocol {get;}
 ValueTask<ReadingSnapshot> ReadAsync(IReadOnlyRegisterClient client,DeviceBinding binding,CancellationToken cancellationToken);
}
public static class ModbusReadCodec
{
 public static ushort Crc16(ReadOnlySpan<byte> data)
 {
  ushort crc=0xffff;foreach(var value in data){crc^=value;for(var n=0;n<8;n++)crc=(ushort)((crc&1)!=0?(crc>>1)^0xa001:crc>>1);}return crc;
 }
 public static byte[] BuildRtuInputRead(byte unit,ushort start,ushort count)
 {
  if(unit is <1 or >247||count is <1 or >125||(uint)start+count>65536)throw new ArgumentOutOfRangeException(nameof(count));
  byte[] data=[unit,4,0,0,0,0,0,0];BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2),start);BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4),count);BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6),Crc16(data.AsSpan(0,6)));return data;
 }
 public static ushort[] DecodeRtuInputResponse(ReadOnlySpan<byte> frame,byte unit,ushort count)
 {
  if(count is <1 or >125||frame.Length<5||frame.Length>256)throw new InvalidDataException("RTU长度或数量无效");
  if(frame[0]!=unit||Crc16(frame[..^2])!=BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]))throw new InvalidDataException("RTU站号或CRC不符");
  if(frame[1]==0x84&&frame.Length==5)throw new InvalidDataException("Modbus异常码"+frame[2]);
  if(frame[1]!=4||frame[2]!=count*2||frame.Length!=5+count*2)throw new InvalidDataException("功能码、字节数或响应长度不符");
  var words=new ushort[count];for(var i=0;i<count;i++)words[i]=BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(3+i*2,2));return words;
 }
 public static decimal LevelMm(ushort high,ushort low)=>unchecked((int)(((uint)high<<16)|low))/10m;
 public static decimal TemperatureC(ushort raw)=>unchecked((short)raw)/100m;
 public static decimal DirectOrGatewayDensityKgM3(ushort raw)=>raw/10m;
 public static decimal IfxDensityKgM3(ushort raw)=>unchecked((short)raw);
}

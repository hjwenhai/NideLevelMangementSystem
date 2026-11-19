using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Naide.LevelManager.Simulator;

public sealed class SimulatorProtocolException(byte code) : Exception("Modbus模拟异常"+code)
{ public byte Code {get;}=code; }
public sealed record SimulatedTank(int Slot,decimal PositionMm,decimal TemperatureC,decimal DensityKgM3,string Condition,ushort Mode,ushort NormalState,ushort StopState);

// This model has no serial, outbound connection, application database or
// authorization dependency. All writes affect these in-memory demo slots only.
public sealed class GatewayModel
{
    private sealed class Slot
    {
        public decimal Position,Temperature,Density;
        public bool Offline;
        public ushort Fault,Mode=1,NormalValue,NormalState,StopValue,StopState;
        public uint NormalCount,StopCount;
        public ushort[] Target=[0,0];
        public bool Ready;
    }
    private readonly object sync=new();
    private readonly Slot[] slots;
    private readonly Stopwatch watch=Stopwatch.StartNew();
    private long step=-1;
    private double lastSeconds;
    private uint sequence=1;
    private bool varying=true;
    public int Count=>slots.Length;
    public ulong BootId {get;}=BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));
    public GatewayModel(int count=11)
    {
        if(count is <1 or >64)throw new ArgumentOutOfRangeException(nameof(count));
        slots=Enumerable.Range(1,count).Select(i=>new Slot{Position=1600+(i%12)*700,Temperature=24+i*.35m,Density=800+i*2}).ToArray();
    }
    private Slot At(int slot){if(slot<1||slot>Count)throw new ArgumentOutOfRangeException(nameof(slot));return slots[slot-1];}
    public bool Varying {get{lock(sync)return varying;}set{lock(sync)varying=value;}}
    private void Advance()
    {
        var seconds=watch.Elapsed.TotalSeconds;var tick=(long)seconds;
        if(tick==step)return;
        var delta=Math.Clamp(seconds-lastSeconds,0,5);lastSeconds=seconds;step=tick;sequence++;
        foreach(var s in slots)if(!s.Offline&&s.Fault==0)
        {
            if(s.Mode==2)s.Position=Math.Min(11900,s.Position+(decimal)(15*delta));
            if(s.Mode==256)s.Position=Math.Max(100,s.Position-(decimal)(15*delta));
        }
    }
    private decimal Position(Slot s,int i)=>decimal.Round(Math.Clamp(s.Position+(varying&&s.Mode==1?(decimal)(3*Math.Sin(watch.Elapsed.TotalSeconds/35+i)):0),0m,12000m),1);
    public IReadOnlyList<SimulatedTank> Snapshot()
    {
        lock(sync){Advance();return slots.Select((s,i)=>new SimulatedTank(i+1,Position(s,i),s.Temperature,s.Density,s.Offline?"模拟离线":s.Fault!=0?"模拟故障":"正常",s.Mode,s.NormalState,s.StopState)).ToArray();}
    }
    public void SetValues(int slot,decimal position,decimal temperature,decimal density)
    {
        if(position is <0 or >12000||temperature is <-100 or >200||density is <1 or >2000)throw new ArgumentException("模拟值超出范围");
        lock(sync){var s=At(slot);s.Position=position;s.Temperature=temperature;s.Density=density;sequence++;}
    }
    public void SetCondition(int slot,bool offline,ushort fault){lock(sync){var s=At(slot);s.Offline=offline;s.Fault=fault;sequence++;}}
    private static void Put32(ushort[] a,int index,uint value){a[index]=(ushort)(value>>16);a[index+1]=(ushort)value;}
    private static void Put64(ushort[] a,int index,ulong value){Put32(a,index,(uint)(value>>32));Put32(a,index+2,(uint)value);}
    private ushort[] Header()
    {
        var a=new ushort[31];a[2]=3;a[3]=64;Put64(a,6,BootId);a[27]=16;a[28]=3;a[29]=12;a[30]=1;
        // Header[19] is kept compatible with r22's normal device framing.
        // Simulation is explicitly identified in the tool and SIM-* tank names.
        return a;
    }
    private ushort[] Summary(int slot)
    {
        var a=new ushort[24];if(slot>Count){a[0]=16;return a;}
        var s=At(slot);a[0]=(ushort)(128|(s.Offline?0:3));a[1]=s.Offline?(ushort)0:(ushort)127;Put32(a,2,sequence);
        Put32(a,4,(uint)(Position(s,slot-1)*10));a[6]=unchecked((ushort)(short)(s.Temperature*100));a[7]=(ushort)(s.Density*10);a[9]=s.Mode;a[10]=s.Fault;Put32(a,20,1);
        return a;
    }
    private ushort[] Detail(int slot)
    {
        var a=new ushort[144];if(slot>Count)return a;
        var s=At(slot);Put32(a,0,(uint)(Position(s,slot-1)*10));
        for(var i=0;i<16;i++)a[6+i]=unchecked((ushort)(short)((s.Temperature+(i-7.5m)*.08m)*100));
        a[23]=65535;a[26]=unchecked((ushort)(short)(s.Temperature*100));a[27]=12000;a[28]=14000;a[50]=(ushort)(s.Density*10);a[51]=s.Mode;a[52]=s.Fault;Put32(a,54,1);Put32(a,56,sequence);
        for(var i=0;i<4;i++)a[58+i]=65535;
        for(var i=0;i<6;i++){var n=64+i*12;a[n]=s.Offline?(ushort)0:(ushort)9;Put32(a,n+2,sequence);}
        return a;
    }
    private ushort[] Control(int slot)
    {
        if(slot>Count)throw new SimulatorProtocolException(2);
        var s=At(slot);var a=new ushort[12];a[0]=s.NormalValue;a[1]=s.NormalState;Put32(a,4,s.NormalCount);a[7]=s.StopValue;a[8]=s.StopState;Put32(a,10,s.StopCount);return a;
    }
    public ushort[] Read(ushort start,ushort count)
    {
        if(count is <1 or >125||(uint)start+count>65536)throw new SimulatorProtocolException(3);
        lock(sync)
        {
            Advance();ushort[] a;int offset;
            if(start<31){a=Header();offset=start;}
            else if(start is >=1000 and <2536){var i=(start-1000)/24+1;a=Summary(i);offset=(start-1000)%24;}
            else if(start is >=3000 and <12216){var i=(start-3000)/144+1;a=Detail(i);offset=(start-3000)%144;}
            else if(start is >=30000 and <30768){var i=(start-30000)/12+1;a=Control(i);offset=(start-30000)%12;}
            else if(start is >=31000 and <31256){var i=(start-31000)/4+1;if(i>Count)throw new SimulatorProtocolException(2);var s=At(i);a=[s.Target[0],s.Target[1],s.Ready?(ushort)1:(ushort)0,0];offset=(start-31000)%4;}
            else throw new SimulatorProtocolException(2);
            if(offset+count>a.Length)throw new SimulatorProtocolException(2);
            return a.AsSpan(offset,count).ToArray();
        }
    }
    public void Stage(ushort address,ushort[] words)
    {
        lock(sync)
        {
            if(address is <31000 or >=31256||(address-31000)%4!=0||words.Length!=2)throw new SimulatorProtocolException(2);
            var slot=(address-31000)/4+1;if(slot>Count)throw new SimulatorProtocolException(2);
            var raw=unchecked((int)(((uint)words[0]<<16)|words[1]));if(raw is <0 or >120000)throw new SimulatorProtocolException(3);
            var s=At(slot);s.Target=words.ToArray();s.Ready=true;
        }
    }
    public void Write(ushort address,ushort value)
    {
        lock(sync)
        {
            if(address is <30000 or >=30768)throw new SimulatorProtocolException(2);
            var slot=(address-30000)/12+1;var offset=(address-30000)%12;
            if(slot>Count||offset is not(0 or 7))throw new SimulatorProtocolException(2);
            var s=At(slot);
            if(value==0){if(offset==0){if(s.NormalState is 1 or 2)throw new SimulatorProtocolException(6);s.NormalValue=0;s.NormalState=0;}else{if(s.StopState is 1 or 2)throw new SimulatorProtocolException(6);s.StopValue=0;s.StopState=0;}return;}
            if(s.Offline||s.Fault!=0)throw new SimulatorProtocolException(6);
            if(offset==7)
            {
                if(value!=1)throw new SimulatorProtocolException(3);
                if(s.StopState!=0)throw new SimulatorProtocolException(6);
                s.Mode=4;s.StopValue=1;s.StopState=3;s.StopCount++;sequence++;return;
            }
            if(s.NormalState!=0||s.StopState!=0)throw new SimulatorProtocolException(6);
            ushort mode=value switch{1=>1,2=>16,3=>32,4=>4,5=>2,6=>256,7=>64,9=>8,10=>128,11=>s.Mode,_=>throw new SimulatorProtocolException(3)};
            if(value==11){if(!s.Ready)throw new SimulatorProtocolException(3);s.Position=(((uint)s.Target[0]<<16)|s.Target[1])/10m;s.Ready=false;}
            s.Mode=mode;s.NormalValue=value;s.NormalState=3;s.NormalCount++;sequence++;
        }
    }
}

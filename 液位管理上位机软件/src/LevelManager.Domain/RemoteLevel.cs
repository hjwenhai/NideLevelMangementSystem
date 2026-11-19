namespace Naide.LevelManager.Domain;

public sealed record LevelAdjustmentEvidence(decimal TargetLevelMm)
{
    public decimal? CurrentLevelMm {get;init;}
    public decimal? PreviousOffsetMm {get;init;}
    public decimal? NewOffsetMm {get;init;}
    public decimal? ReadbackOffsetMm {get;init;}
    public bool AuthorizationAttempted {get;init;}
    public bool OffsetWriteMayHaveBeenSent {get;init;}
    public bool? AuthorizationClearConfirmed {get;init;}
    public ushort? GatewayState {get;init;}
    public ushort? GatewayError {get;init;}
}
public static class RemoteLevelRules
{
    public const decimal MinimumMm=-9999.9m,MaximumMm=99999.9m;
    public static int ToRaw(decimal mm)
    {
        if(mm<MinimumMm||mm>MaximumMm||decimal.Truncate(mm*10)!=mm*10)
            throw new ArgumentException("目标液位须为−9999.9～99999.9mm，最多1位小数");
        return checked((int)(mm*10));
    }
    public static int Offset(int target,int current,int previous)
    {
        var value=(long)target-current+previous;
        if(value < -99999 || value > 999999)throw new ArgumentException("计算后的液位偏移超出−9999.9～99999.9mm，未修改仪表");
        return checked((int)value);
    }
    public static ushort[] Words(int value)=>[(ushort)(unchecked((uint)value)>>16),unchecked((ushort)value)];
    public static int Signed(ushort[] values)=>values.Length==2?unchecked((int)(((uint)values[0]<<16)|values[1])):throw new InvalidDataException("液位或偏移回读长度错误");
}

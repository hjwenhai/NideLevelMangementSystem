param([ValidateRange(0,128)][int]$Tanks=128,[ValidateRange(1,86400)][int]$IntervalSeconds=60,[ValidateRange(1,366)][int]$Days=365)
$rows=[long]$Tanks*[long][Math]::Ceiling(86400.0*$Days/$IntervalSeconds)
[PSCustomObject]@{Tanks=$Tanks;IntervalSeconds=$IntervalSeconds;Days=$Days;RegularHistoryRows=$rows;IncludesEventsAndTasks=$false;Type='数量估算，不代表生成数据或容量验收'} | ConvertTo-Json

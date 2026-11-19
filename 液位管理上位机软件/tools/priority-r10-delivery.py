from pathlib import Path
root=Path(__file__).resolve().parents[1]
def edit(name,old,new):
 p=root/name;s=p.read_text(encoding='utf-8-sig');assert old in s,(name,old[:100]);p.write_text(s.replace(old,new),encoding='utf-8')
edit('src/LevelManager.Infrastructure/TankConfigurationExcel.cs','static TankConfigurationExcel()=>','private static (string Name,Field[] Fields)[] ForDelivery(bool average)=>Sheets.Select(s=>(s.Name,Fields:average?s.Fields:s.Fields.Where(f=>f.Path!="controls.wirelessMeasurementEnabled").ToArray())).ToArray();\n    static TankConfigurationExcel()=>')
edit('src/LevelManager.Infrastructure/TankConfigurationExcel.cs','bool template=false)','bool template=false,bool includeAverage=true)')
edit('src/LevelManager.Infrastructure/TankConfigurationExcel.cs','foreach(var (name,fields) in Sheets)Sheet(name,','foreach(var (name,fields) in ForDelivery(includeAverage))Sheet(name,')
edit('src/LevelManager.Infrastructure/TankConfigurationExcel.cs','fields.Select(f=>CellValue(t,f,config)).ToArray()','fields.Select(f=>!includeAverage&&f.Path=="temperatureMode"&&t.TemperatureMode==TemperatureMode.Wireless?(object?)"Instrument":CellValue(t,f,config)).ToArray()')
edit('src/LevelManager.Infrastructure/TankConfigurationExcel.cs','["温度类型","None=不带温度计；Ats=ATS固定点；Wireless=无线浮子；ATS与无线浮子不能同时使用。"]','["温度类型",includeAverage?"None=不带温度计；Ats=ATS固定点；Wireless=无线浮子；ATS与无线浮子不能同时使用。":"None=不带温度计；Ats=ATS固定点；Instrument=仪表瞬时温度IR26。"]')
edit('src/LevelManager.Infrastructure/TankConfigurationExcel.cs','CancellationToken ct=default)\n    {\n        if(file.Length','CancellationToken ct=default,bool includeAverage=true)\n    {\n        var activeSheets=ForDelivery(includeAverage);\n        if(file.Length')
edit('src/LevelManager.Infrastructure/TankConfigurationExcel.cs','foreach(var (name,fields) in Sheets){if(!data','foreach(var (name,fields) in activeSheets){if(!data')
edit('src/LevelManager.Infrastructure/TankConfigurationExcel.cs','foreach(var (name,fields) in Sheets.Skip(1))','foreach(var (name,fields) in activeSheets.Skip(1))')
edit('src/LevelManager.Infrastructure/TankConfigurationExcel.cs','foreach(var (name,fields) in Sheets)\n                {','foreach(var (name,fields) in activeSheets)\n                {')
edit('src/LevelManager.Infrastructure/TankConfigurationExcel.cs','"temperature"=>JsonValue.Create(Enum.Parse<TemperatureMode>(text).ToString())','"temperature"=>JsonValue.Create(!includeAverage&&text=="Instrument"?TemperatureMode.Wireless.ToString():Enum.Parse<TemperatureMode>(text).ToString())')
edit('src/LevelManager.Host/TankConfigurationTransfer.cs','template??false),','template??false,authorization.HasAverageModule),')
edit('src/LevelManager.Host/TankConfigurationTransfer.cs','await tables.ListTablesAsync(ct),ct);','await tables.ListTablesAsync(ct),ct,authorization.HasAverageModule);')
edit('web/src/App.vue','中文联调测试版 / V0.3.0','中文联调测试版 / V0.3.0 · r10')
edit('src/LevelManager.Host/Program.cs','version="0.3.0-zh-test.1",stage=','version="0.3.0-zh-test.1",revision="20261028-r10",stage=')
print('r10 optional-delivery export adapted')

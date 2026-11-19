from pathlib import Path
r=Path(__file__).resolve().parents[1];p=r/'src/LevelManager.Infrastructure/TankConfigurationExcel.cs';s=p.read_text(encoding='utf-8-sig')
old='if(!choices.TryGetValue(field.Path,out var list))return value;'
new='if(!choices.TryGetValue(field.Path,out var list)){var raw=Convert.ToString(value,CultureInfo.InvariantCulture);return EnumChoices(field.Kind,includeAverage).FirstOrDefault(c=>c.Value==raw).Label??value;}'
assert old in s;s=s.replace(old,new)
a=s.index('var values=kind switch{');b=s.index(';if(choices.TryGetValue',a)
s=s[:a]+'var enumChoices=EnumChoices(kind,includeAverage);var values=kind=="bool"?"是,否":enumChoices.Length>0?string.Join(\',\',enumChoices.Select(c=>c.Label)):null'+s[b:]
s=s.replace('(object?)"Instrument":Friendly(t,f)','(object?)"仪表温度":Friendly(t,f)')
for kind,enum in [('densitySource','MeteringDensitySource'),('baseVolume','BaseVolumeMethod'),('fraction','FractionalMillimetreRule'),('shape','TankShape'),('pressureUnit','PressureUnit')]:
 s=s.replace(f'Enum.Parse<{enum}>(text)',f'Enum.Parse<{enum}>(EnumValue(text,"{kind}",includeAverage))')
s=s.replace('!includeAverage&&text=="Instrument"?TemperatureMode.Wireless.ToString():Enum.Parse<TemperatureMode>(text).ToString()', '!includeAverage&&EnumValue(text,"temperature",includeAverage)=="Instrument"?TemperatureMode.Wireless.ToString():Enum.Parse<TemperatureMode>(EnumValue(text,"temperature",includeAverage)).ToString()')
s=s.replace('Vertical=立式罐；Sphere=球罐；FloatingRoof=浮顶罐','选择立式罐、球罐或浮顶罐；兼容原英文值。')
s=s.replace('None=不带温度计；Ats=ATS固定点；Wireless=无线浮子；ATS与无线浮子不能同时使用。','选择不带温度计、多点温度计或无线浮子；多点温度计与无线浮子不能同时使用。')
s=s.replace('None=不带温度计；Ats=ATS固定点；Instrument=仪表瞬时温度IR26。','选择不带温度计、多点温度计或仪表温度（IR26）。')
s=s.replace('KPa=kPa；MPa=MPa；Bar=bar。','kPa、MPa、bar为工程单位符号，不翻译；')
p.write_text(s,encoding='utf-8')
p=r/'tests/LevelManager.Tests/TankConfigurationExcelTests.cs';s=p.read_text(encoding='utf-8-sig').replace('Assert.Contains("Instrument",texts);','Assert.Contains("仪表温度",texts);');p.write_text(s,encoding='utf-8')
print('Chinese enum choices exported; old English input values remain compatible')

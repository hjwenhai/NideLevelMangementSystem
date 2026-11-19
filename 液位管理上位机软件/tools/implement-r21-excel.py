from pathlib import Path
p=Path(__file__).resolve().parents[1]/'src/LevelManager.Infrastructure/TankConfigurationExcel.cs'
s=p.read_text(encoding='utf-8-sig')
s=s.replace('private static readonly Field[] Meter', '''private static readonly HashSet<string> AddedPaths=["metering.manualOperatingDensityKgM3","metering.densitySource","metering.showInstrumentDensity","metering.baseVolumeMethod","metering.fractionalMillimetreRule"];
    private static Dictionary<string,(Guid Id,string Label)[]> Choices(SystemConfiguration config,IReadOnlyList<CapacityTableSummary> tables)
    {
        string Label(string name,Guid id)=>name+" ["+id.ToString()[..8]+"]";
        return new(){["channelId"]=config.Channels.Select(c=>(c.Id,Label(c.Name,c.Id))).ToArray(),["mediumId"]=config.Media.Select(c=>(c.Id,Label(c.Name,c.Id))).ToArray(),["$groups"]=config.Groups.Select(c=>(c.Id,Label(c.Name,c.Id))).ToArray(),
            ["metering.mainTableId"]=tables.Where(t=>t.Kind==CapacityTableKind.Main&&t.Enabled).Select(t=>(t.Id,Label(t.Number+" · "+t.Name,t.Id))).ToArray(),
            ["metering.staticPressureTableId"]=tables.Where(t=>t.Kind==CapacityTableKind.StaticPressure&&t.Enabled).Select(t=>(t.Id,Label(t.Number+" · "+t.Name,t.Id))).ToArray(),
            ["metering.decimalTableId"]=tables.Where(t=>t.Kind==CapacityTableKind.Decimal&&t.Enabled).Select(t=>(t.Id,Label(t.Number+" · "+t.Name,t.Id))).ToArray()};
    }
    private static Guid Reference(string text,string field,Dictionary<string,(Guid Id,string Label)[]> choices)
    {
        if(Guid.TryParse(text,out var id))return id;
        var found=choices.GetValueOrDefault(field,[]).Where(c=>c.Label==text).ToArray();
        return found.Length==1?found[0].Id:throw new ArgumentException("选项不存在或已变更，请重新下载模板并选择");
    }
    private static readonly Field[] Meter''')
s=s.replace('return fields.ToArray();\n    }\n    private static Field[] BuildTemperature()', '''fields.AddRange([new("人工工况密度 kg/m³（可空）","metering.manualOperatingDensityKgM3","number?"),new("计算密度来源","metering.densitySource","densitySource"),new("显示仪表实测密度","metering.showInstrumentDensity","bool"),new("基础体积算法","metering.baseVolumeMethod","baseVolume"),new("不足1mm处理","metering.fractionalMillimetreRule","fraction")]);
        return fields.ToArray();
    }
    private static Field[] BuildTemperature()''')
s=s.replace('ATS点数','温度计点数').replace('ATS第','温度计第')
s=s.replace('var part=doc.AddWorkbookPart();part.Workbook=new Workbook();var sheets=part.Workbook.AppendChild(new Sheets());','''var part=doc.AddWorkbookPart();part.Workbook=new Workbook();var sheets=part.Workbook.AppendChild(new Sheets());
            var choices=Choices(config,tables);var choiceNames=choices.Keys.Select((k,i)=>(k,n:"TankChoice"+(i+1))).ToDictionary(x=>x.k,x=>x.n);
            object? Friendly(TankDefinition tank,Field field){var value=CellValue(tank,field,config);if(!choices.TryGetValue(field.Path,out var list))return value;
                if(field.Path=="$groups")return string.Join(';',config.Groups.Where(g=>g.TankIds.Contains(tank.Id)).Select(g=>list.Single(x=>x.Id==g.Id).Label));
                return Guid.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),out var id)?list.FirstOrDefault(x=>x.Id==id).Label??value:value;}
            var names=new DefinedNames();foreach(var (key,list) in choices)if(list.Length>0){var column=ColumnName(Array.IndexOf(choices.Keys.ToArray(),key)+1);names.Append(new DefinedName{Name=choiceNames[key],Text="'选择列表'!$"+column+"$2:$"+column+"$"+(list.Length+1)});}
            // Defined names must follow Sheets in the workbook element.
            part.Workbook.Append(names);''')
s=s.replace('"pressureUnit"=>"KPa,MPa,Bar",_=>null','"pressureUnit"=>"KPa,MPa,Bar","densitySource"=>"Instrument,Manual","baseVolume"=>"Interpolate,MainPlusDecimal","fraction"=>"Linear,IntegerOnly",_=>null')
s=s.replace('if(values is not null)validations.Append', '''if(choices.TryGetValue(definitions[i].Path,out var options)&&options.Length>0){validations.Append(new DataValidation{Type=DataValidationValues.List,AllowBlank=definitions[i].Kind=="guid?"||definitions[i].Path=="$groups",ShowErrorMessage=true,ErrorTitle="请选择可用名称",Error="请使用本模板下拉选项；原GUID格式仍可由系统导入",SequenceOfReferences=new ListValue<StringValue>{InnerText=ColumnName(i+1)+"2:"+ColumnName(i+1)+"129"},Formula1=new Formula1(choiceNames[definitions[i].Path])});}
                if(values is not null)validations.Append''')
s=s.replace('CellValue(t,f,config)).ToArray()', 'Friendly(t,f)).ToArray()')
s=s.replace('            Sheet("参考编号",', '''            Sheet("选择列表",new[]{choices.Keys.Select(k=>(object?)k).ToArray()}.Concat(Enumerable.Range(0,Math.Max(1,choices.Values.Max(v=>v.Length))).Select(i=>choices.Values.Select(v=>(object?)(i<v.Length?v[i].Label:"")).ToArray())));
            sheets.Elements<Sheet>().Single(x=>x.Name=="选择列表").State=SheetStateValues.Hidden;
            Sheet("参考编号",''')
s=s.replace('["模板格式","TANK-CONFIG-1"]','["模板格式","TANK-CONFIG-2"]')
s=s.replace('通道、介质、罐组与罐容表使用参考编号页的编号。','通道、介质、罐组与罐容表可直接下拉选择名称；也兼容原GUID。选择列表来自导出时的现有配置。')
s=s.replace('多个罐组编号使用英文分号分隔','下拉可选择一个罐组；多个罐组选项使用英文分号分隔')
s=s.replace('var activeSheets=ForDelivery(includeAverage);','var activeSheets=ForDelivery(includeAverage);var choices=Choices(current,tables);var absent=new HashSet<string>();')
start=s.index('        foreach(var (name,fields) in activeSheets){if(!data.TryGetValue')
finish=s.index('        if(!data.TryGetValue("填写说明"',start)
s=s[:start]+'''        foreach(var (name,fields) in activeSheets)
        {
            if(!data.TryGetValue(name,out var rows)||rows.Count==0){issues.Add(name+"：工作表不存在");continue;}
            string Normalize(string text)=>text.Replace("ATS点数","温度计点数").Replace("ATS第","温度计第");
            var headers=rows[0].Select(v=>Normalize(Text(v))).ToArray();
            var indexes=fields.Select(f=>Array.IndexOf(headers,Normalize(f.Label))).ToArray();
            for(var i=0;i<fields.Length;i++)if(indexes[i]<0){if(AddedPaths.Contains(fields[i].Path))absent.Add(fields[i].Path);else issues.Add(name+"：缺少表头 "+fields[i].Label);}
            if(headers.Where(h=>h!="").Distinct().Count()!=headers.Count(h=>h!="")||headers.Any(h=>h!=""&&!fields.Any(f=>Normalize(f.Label)==h)))issues.Add(name+"：表头重复或含未知列");
            data[name]=new[]{fields.Select(f=>(object?)f.Label).ToArray()}.Concat(rows.Skip(1).Select(r=>indexes.Select(i=>i<0?null:r.ElementAtOrDefault(i)).ToArray())).ToList();
        }
''' + s[finish:]
s=s.replace('Text(row[1])=="TANK-CONFIG-1"','(Text(row[1])=="TANK-CONFIG-1"||Text(row[1])=="TANK-CONFIG-2")')
s=s.replace('if(field.Path=="id"||field.Path=="code")continue;', 'if(field.Path=="id"||field.Path=="code"||absent.Contains(field.Path))continue;')
s=s.replace('.Select(Guid.Parse).ToArray()', '.Select(v=>Reference(v,"$groups",choices)).ToArray()')
s=s.replace('JsonValue.Create(Guid.Parse(text).ToString())','JsonValue.Create(Reference(text,field.Path,choices).ToString())')
s=s.replace('"shape"=>JsonValue.Create', '''"densitySource"=>JsonValue.Create(Enum.Parse<MeteringDensitySource>(text).ToString()),"baseVolume"=>JsonValue.Create(Enum.Parse<BaseVolumeMethod>(text).ToString()),"fraction"=>JsonValue.Create(Enum.Parse<FractionalMillimetreRule>(text).ToString()),
                                "shape"=>JsonValue.Create''')
p.write_text(s,encoding='utf-8')

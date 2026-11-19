using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ExcelDataReader;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Infrastructure;

public sealed record TankConfigurationDifference(string TankCode,string Field,string Before,string After);
public sealed record TankConfigurationPreview(SystemConfiguration Configuration,IReadOnlyList<string> Issues,int Added,int Updated,IReadOnlyList<string> ChangedTanks,IReadOnlyList<TankConfigurationDifference>? Differences=null);
public static class TankConfigurationExcel
{
    private sealed record Field(string Label,string Path,string Kind="text");
    private static (string Value,string Label)[] EnumChoices(string kind,bool average=true)=>kind switch
    {
        "shape"=>[("Vertical","立式罐"),("Sphere","球罐"),("FloatingRoof","浮顶罐")],
        "temperature"=>average?[("None","不带温度计"),("Ats","多点温度计"),("Wireless","无线浮子")]:[("None","不带温度计"),("Ats","多点温度计"),("Instrument","仪表温度")],
        "pressureUnit"=>[("KPa","kPa"),("MPa","MPa"),("Bar","bar")],
        "densitySource"=>[("Instrument","仪表实测密度"),("Manual","人工工况密度")],
        "baseVolume"=>[("Interpolate","主表插值（不叠加小数表）"),("MainPlusDecimal","主表下节点＋小数表增量")],
        "fraction"=>[("Linear","相邻毫米线性插值"),("IntegerOnly","仅整数毫米"),("Unconfirmed","待确认（不计算不足1mm）")],
        _=>[]
    };
    private static string EnumValue(string text,string kind,bool average)
    {
        // New workbooks use Chinese labels. Old English values remain importable.
        var choices=EnumChoices(kind,average);
        var match=choices.Where(c=>c.Label==text||c.Value==text).ToArray();
        return match.Length==1?match[0].Value:throw new ArgumentException("请使用对应的中文下拉选项或原英文值");
    }
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new JsonStringEnumConverter(allowIntegerValues:false)},UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    private static readonly Field[] Archive=[new("内部编号（新增留空）","id"),new("罐号","code"),new("名称","name"),new("仪表位号","instrumentTag"),new("产品编号","productNumber"),new("设备批次","batch"),new("罐区","zone"),new("介质名称","medium"),new("介质编号（可空）","mediumId","guid?"),new("罐型","shape","shape"),new("罐高 mm","heightMm","number"),new("罐径 mm（可空）","diameterMm","number?"),new("公称容量 m³（可空）","nominalCapacityM3","number?"),new("采集通道编号","channelId","guid"),new("表地址（IFX按站号范围）","address","integer"),new("启用","enabled","bool"),new("隐藏","hidden","bool"),new("所属罐组编号（分号分隔）","$groups"),new("备注","notes")];
    private static readonly HashSet<string> AddedPaths=["$command.MeasureII","$command.MeasureIII","$command.Lower","$command.Density","$command.RemoteLevel","metering.waterHeightMm","metering.manualOperatingDensityKgM3","metering.densitySource","metering.showInstrumentDensity","metering.baseVolumeMethod","metering.fractionalMillimetreRule"];
    private static Dictionary<string,(Guid Id,string Label)[]> Choices(SystemConfiguration config,IReadOnlyList<CapacityTableSummary> tables)
    {
        string Label(string name,Guid id)=>name+" ["+id.ToString()[..8]+"]";
        return new(){["$tankCodes"]=config.Tanks.Select(t=>(t.Id,t.Code)).ToArray(),["channelId"]=config.Channels.Select(c=>(c.Id,Label(c.Name,c.Id))).ToArray(),["mediumId"]=config.Media.Select(c=>(c.Id,Label(c.Name,c.Id))).ToArray(),["$groups"]=config.Groups.Select(c=>(c.Id,Label(c.Name,c.Id))).ToArray(),
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
    private static readonly Field[] Meter=BuildMeter();
    private static readonly Field[] Temperature=BuildTemperature();
    private static readonly (string Name,Field[] Fields)[] Sheets=[("罐体与通讯",Archive),("报警与计量",Meter),("操作与温度",Temperature)];
    private static (string Name,Field[] Fields)[] ForDelivery(bool average,bool separateGroups=false)=>Sheets.Select(s=>(s.Name,Fields:s.Fields.Where(f=>(average||f.Path!="controls.wirelessMeasurementEnabled")&&(!separateGroups||f.Path!="$groups")).ToArray())).ToArray();
    static TankConfigurationExcel()=>Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    private static string ColumnName(int index)
    {var result="";while(index>0){index--;result=(char)('A'+index%26)+result;index/=26;}return result;}
    private static Columns ColumnsFor(string name,Field[]? fields)
    {
        var count=fields?.Length??(name is "填写说明" or "罐组关联"?2:4);var columns=new Columns();
        for(var i=0;i<count;i++)
        {
            var field=fields?.ElementAt(i);double width;
            if(name=="填写说明")width=i==1?110:24;
            else if(name=="参考编号")width=i==1?44:i>=2?36:18;
            else width=field?.Path=="id"||field?.Kind.StartsWith("guid",StringComparison.Ordinal)==true?44:field?.Path=="$groups"?72:field?.Path=="notes"?55:field?.Kind=="bool"?18:24;
            columns.Append(new Column{Min=(uint)i+1,Max=(uint)i+1,Width=width,CustomWidth=true});
        }return columns;
    }
    private static Field[] BuildMeter()
    {
        List<Field> fields=[new("罐号","code"),new("启用体积计算","metering.enabled","bool"),new("主罐容表编号（可空）","metering.mainTableId","guid?"),new("静压力修正表编号（可空）","metering.staticPressureTableId","guid?"),new("小数表编号（可空）","metering.decimalTableId","guid?"),new("人工水体积 m³","metering.waterVolumeM3","number"),new("20℃标准密度 kg/m³（可空）","metering.standardDensity20KgM3","number?")];
        foreach(var (key,name) in new[]{("level","液位 mm"),("temperature","温度 ℃"),("density","密度 kg/m³"),("pressure1","压力1 kPa"),("pressure2","压力2 kPa")})
            foreach(var (limit,label) in new[]{("lowLow","低低限"),("low","低限"),("high","高限"),("highHigh","高高限"),("hysteresis","回差"),("delaySeconds","延时 秒")})fields.Add(new(name+" "+label,"alarms."+key+"."+limit,limit=="delaySeconds"?"integer":limit=="hysteresis"?"number":"number?"));
        fields.AddRange([new("监测通讯异常","alarms.communicationEnabled","bool"),new("监测仪表故障","alarms.instrumentEnabled","bool")]);
        for(var n=1;n<=2;n++)fields.AddRange([new("压力"+n+"启用","pressure"+n+".enabled","bool"),new("压力"+n+" 4mA对应值","pressure"+n+".at4Ma","number"),new("压力"+n+" 20mA对应值","pressure"+n+".at20Ma","number"),new("压力"+n+"单位","pressure"+n+".unit","pressureUnit")]);
        fields.AddRange([new("水位 mm（可空）","metering.waterHeightMm","number?"),new("人工工况密度 kg/m³（可空）","metering.manualOperatingDensityKgM3","number?"),new("计算密度来源","metering.densitySource","densitySource"),new("显示仪表实测密度","metering.showInstrumentDensity","bool"),new("基础体积算法","metering.baseVolumeMethod","baseVolume"),new("不足1mm处理","metering.fractionalMillimetreRule","fraction")]);
        return fields.ToArray();
    }
    private static Field[] BuildTemperature()
    {
        List<Field> fields=[new("罐号","code"),new("温度类型","temperatureMode","temperature"),new("温度计点数","atsPointCount","integer")];
        for(var n=1;n<=16;n++)fields.Add(new("温度计第"+n+"点高度 mm（可空）","atsHeightsMm."+(n-1),"number?"));
        fields.AddRange([new("启用无线平均测温","controls.wirelessMeasurementEnabled","bool"),new("操作确认超时 秒","controls.confirmationTimeoutSeconds","integer"),new("移动超时 秒","controls.movementTimeoutSeconds","integer")]);
        foreach(var command in Enum.GetValues<InstrumentCommand>())fields.Add(new("允许"+CommandEncoding.Name(command),"$command."+command,"bool"));
        return fields.ToArray();
    }
    private static JsonNode? Get(JsonNode node,string path)
    {foreach(var part in path.Split('.')){if(node is JsonArray array){if(!int.TryParse(part,out var at)||at>=array.Count)return null;node=array[at]!;}else node=node[part]!;if(node is null)return null;}return node;}
    private static void Set(JsonNode node,string path,JsonNode? value)
    {var parts=path.Split('.');for(var i=0;i<parts.Length-1;i++){if(node is JsonArray a){var at=int.Parse(parts[i],CultureInfo.InvariantCulture);while(a.Count<=at)a.Add(null);node=a[at]!;}else node=node[parts[i]]!;}if(node is JsonArray array){var at=int.Parse(parts[^1],CultureInfo.InvariantCulture);while(array.Count<=at)array.Add(null);array[at]=value;}else node[parts[^1]]=value;}
    private static object? CellValue(TankDefinition tank,Field field,SystemConfiguration config)
    {
        if(field.Path=="$groups")return string.Join(';',config.Groups.Where(g=>g.TankIds.Contains(tank.Id)).Select(g=>g.Id));
        if(field.Path.StartsWith("$command.",StringComparison.Ordinal))return tank.Controls.AllowedCommands.Contains(Enum.Parse<InstrumentCommand>(field.Path[9..]))?"是":"否";
        var value=Get(JsonSerializer.SerializeToNode(tank,Json)!,field.Path);if(value is null)return null;
        if(field.Kind=="bool")return value.GetValue<bool>()?"是":"否";
        if(field.Kind.StartsWith("number",StringComparison.Ordinal)||field.Kind=="integer")return value.GetValue<decimal>();
        return value.GetValue<string>();
    }
    public static byte[] Export(SystemConfiguration config,IReadOnlyList<CapacityTableSummary> tables,bool template=false,bool includeAverage=true,IReadOnlyList<string>? operationModules=null)
    {
        (string Name,Field[] Fields)[] ExportFields()=>ForDelivery(includeAverage,true).Select(s=>(s.Name,Fields:s.Fields.Where(f=>operationModules is null||!f.Path.StartsWith("$command.",StringComparison.Ordinal)||ProductTrust.CommandAvailable(Enum.Parse<InstrumentCommand>(f.Path[9..]),operationModules)).ToArray())).ToArray();

        using var stream=new MemoryStream();using(var doc=SpreadsheetDocument.Create(stream,SpreadsheetDocumentType.Workbook,true))
        {
            var part=doc.AddWorkbookPart();part.Workbook=new Workbook();var sheets=part.Workbook.AppendChild(new Sheets());
            var choices=Choices(config,tables);var choiceNames=choices.Keys.Select((k,i)=>(k,n:"TankChoice"+(i+1))).ToDictionary(x=>x.k,x=>x.n);
            object? Friendly(TankDefinition tank,Field field){var channel=config.Channels.FirstOrDefault(c=>c.Id==tank.ChannelId);var value=field.Path=="address"&&channel?.Protocol==AcquisitionProtocol.IfxL25?IfxAddressing.TableAddress(tank.Address,channel.UnitId):CellValue(tank,field,config);if(!choices.TryGetValue(field.Path,out var list)){var raw=Convert.ToString(value,CultureInfo.InvariantCulture);return EnumChoices(field.Kind,includeAverage).FirstOrDefault(c=>c.Value==raw).Label??value;}
                if(field.Path=="$groups")return string.Join(';',config.Groups.Where(g=>g.TankIds.Contains(tank.Id)).Select(g=>list.Single(x=>x.Id==g.Id).Label));
                return Guid.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),out var id)?list.FirstOrDefault(x=>x.Id==id).Label??value:value;}
            var names=new DefinedNames();foreach(var (key,list) in choices)if(list.Length>0){var column=ColumnName(Array.IndexOf(choices.Keys.ToArray(),key)+1);names.Append(new DefinedName{Name=choiceNames[key],Text="'选择列表'!$"+column+"$2:$"+column+"$"+(list.Length+1)});}
            // Defined names must follow Sheets in the workbook element.
            part.Workbook.Append(names);
            var styles=part.AddNewPart<WorkbookStylesPart>();styles.Stylesheet=new Stylesheet(new Fonts(new Font{FontSize=new FontSize{Val=11},FontName=new FontName{Val="微软雅黑"}},new Font{Bold=new Bold(),Color=new Color{Rgb="FFFFFFFF"},FontSize=new FontSize{Val=11},FontName=new FontName{Val="微软雅黑"}}),new Fills(new Fill(new PatternFill{PatternType=PatternValues.None}),new Fill(new PatternFill{PatternType=PatternValues.Gray125}),new Fill(new PatternFill(new ForegroundColor{Rgb="FF22515E"}){PatternType=PatternValues.Solid})),new Borders(new Border()),new CellStyleFormats(new CellFormat()),new CellFormats(new CellFormat{Alignment=new Alignment{Vertical=VerticalAlignmentValues.Center}},new CellFormat{FontId=1,FillId=2,ApplyFont=true,ApplyFill=true,Alignment=new Alignment{WrapText=true,Vertical=VerticalAlignmentValues.Center}},new CellFormat{Alignment=new Alignment{WrapText=true,Vertical=VerticalAlignmentValues.Top}}));WorkbookBorders.Apply(styles.Stylesheet);styles.Stylesheet.Save();
            void Sheet(string name,IEnumerable<object?[]> rows)
            {
                var ws=part.AddNewPart<WorksheetPart>();var data=new SheetData();var worksheet=new Worksheet(new SheetViews(new SheetView(new Pane{State=PaneStateValues.Frozen,VerticalSplit=1,HorizontalSplit=name=="罐体与通讯"?2:1,TopLeftCell=name=="罐体与通讯"?"C2":"B2",ActivePane=PaneValues.BottomRight}){WorkbookViewId=0,ShowGridLines=false}),ColumnsFor(name,ExportFields().FirstOrDefault(s=>s.Name==name).Fields),data);uint at=0;
                foreach(var values in rows){var row=new Row{RowIndex=++at,Height=at==1?42:name=="填写说明"?Math.Max(22,Math.Ceiling(values.Select(v=>Convert.ToString(v,CultureInfo.InvariantCulture)?.Length??0).Max()/55d)*18+6):22,CustomHeight=true};var column=0;foreach(var value in values){var numeric=value is decimal or int or long or double;var cell=new Cell{CellReference=ColumnName(++column)+at.ToString(CultureInfo.InvariantCulture),DataType=numeric?CellValues.Number:CellValues.InlineString,StyleIndex=at==1?1u:name=="填写说明"?2u:0u};if(numeric)cell.CellValue=new CellValue(Convert.ToString(value,CultureInfo.InvariantCulture)!);else cell.InlineString=new InlineString(new Text(value?.ToString()??""){Space=SpaceProcessingModeValues.Preserve});row.Append(cell);}data.Append(row);}
                var definitions=ExportFields().FirstOrDefault(s=>s.Name==name).Fields;
                if(definitions is not null){var validations=new DataValidations();for(var i=0;i<definitions.Length;i++){var kind=definitions[i].Kind;var enumChoices=EnumChoices(kind,includeAverage);var values=kind=="bool"?"是,否":enumChoices.Length>0?string.Join(',',enumChoices.Select(c=>c.Label)):null;if(choices.TryGetValue(definitions[i].Path,out var options)&&options.Length>0){validations.Append(new DataValidation{Type=DataValidationValues.List,AllowBlank=definitions[i].Kind=="guid?"||definitions[i].Path=="$groups",ShowErrorMessage=true,ErrorTitle="请选择可用名称",Error="请使用本模板下拉选项；原GUID格式仍可由系统导入",SequenceOfReferences=new ListValue<StringValue>{InnerText=ColumnName(i+1)+"2:"+ColumnName(i+1)+"129"},Formula1=new Formula1(choiceNames[definitions[i].Path])});}
                if(values is not null)validations.Append(new DataValidation{Type=DataValidationValues.List,AllowBlank=false,ShowErrorMessage=true,ErrorTitle="请选择模板选项",Error="请使用下拉列表中的值",SequenceOfReferences=new ListValue<StringValue>{InnerText=ColumnName(i+1)+"2:"+ColumnName(i+1)+"129"},Formula1=new Formula1("\""+values+"\"")});}if(validations.HasChildren)worksheet.Append(validations);}
                if(name=="罐组关联"){
                    var validations=new DataValidations();
                    foreach(var (column,key) in new[]{("A","$tankCodes"),("B","$groups")})if(choices[key].Length>0)validations.Append(new DataValidation{Type=DataValidationValues.List,AllowBlank=column=="B",ShowInputMessage=true,PromptTitle="一行一个关联",Prompt=column=="A"?"选择已有罐号；新增罐号可直接填写，须与主配置页一致":"每行选择一个罐组。同罐可多行；仅保留罐号且罐组留空表示清除全部关联",ShowErrorMessage=true,ErrorStyle=column=="A"?DataValidationErrorStyleValues.Warning:DataValidationErrorStyleValues.Stop,ErrorTitle="请核对关联",Error=column=="A"?"新增罐号可以继续填写；应与主配置页一致":"请从下拉选择一个罐组，不在同一格拼接多个选项",SequenceOfReferences=new ListValue<StringValue>{InnerText=column+"2:"+column+"32769"},Formula1=new Formula1(choiceNames[key])});
                    if(validations.HasChildren)worksheet.Append(validations);
                }
                ws.Worksheet=worksheet;ws.Worksheet.Save();sheets.Append(new Sheet{Name=name,Id=part.GetIdOfPart(ws),SheetId=(uint)sheets.Count()+1});
            }
            foreach(var (name,fields) in ExportFields())Sheet(name,new[]{fields.Select(f=>(object?)f.Label).ToArray()}.Concat(template?[]:config.Tanks.Select(t=>fields.Select(f=>!includeAverage&&f.Path=="temperatureMode"&&t.TemperatureMode==TemperatureMode.Wireless?(object?)"仪表温度":Friendly(t,f)).ToArray())));
            Sheet("罐组关联",new[]{new object?[]{"罐号","所属罐组"}}.Concat(template?[]:config.Tanks.SelectMany(t=>{var groups=config.Groups.Where(g=>g.TankIds.Contains(t.Id)).ToArray();return groups.Length==0?new[]{new object?[]{t.Code,""}}:groups.Select(g=>new object?[]{t.Code,choices["$groups"].Single(c=>c.Id==g.Id).Label});})));
            Sheet("选择列表",new[]{choices.Keys.Select(k=>(object?)k).ToArray()}.Concat(Enumerable.Range(0,Math.Max(1,choices.Values.Max(v=>v.Length))).Select(i=>choices.Values.Select(v=>(object?)(i<v.Length?v[i].Label:"")).ToArray())));
            sheets.Elements<Sheet>().Single(x=>x.Name=="选择列表").State=SheetStateValues.Hidden;
            Sheet("参考编号",new[]{new object?[]{"类别","编号（填入配置）","名称","说明"}}.Concat(config.Channels.Select(c=>new object?[]{"采集通道",c.Id,c.Name,c.Protocol.ToString()})).Concat(config.Media.Select(m=>new object?[]{"介质",m.Id,m.Name,m.Code})).Concat(config.Groups.Select(g=>new object?[]{"罐组",g.Id,g.Name,g.Kind.ToString()})).Concat(tables.Select(t=>new object?[]{"罐容表",t.Id,t.Number+" "+t.Name,t.Kind+" / "+(t.Enabled?"启用":"停用")})));
            Sheet("填写说明",new object?[][]{["项目","内容"],["模板格式","TANK-CONFIG-4"],["导出配置版本",config.Revision],["保存方式","三个配置页按罐号对应，须保留表头。导入仅新增或更新所列储罐；未列出的原储罐保留，不批量删除。"],["编号","修改或恢复已删除的原储罐，请保留导出的内部编号，三个配置页的罐号须一致；恢复后继续关联该编号的历史记录。全新储罐内部编号留空；同罐号已属于另一编号时先核对，不能直接覆盖。通道、介质、罐组与罐容表可下拉选择名称，也兼容原GUID；引用资料须仍存在。"],["罐型","选择立式罐、球罐或浮顶罐；兼容原英文值。"],["温度类型",includeAverage?"选择不带温度计、多点温度计或无线浮子；多点温度计与无线浮子不能同时使用。":"选择不带温度计、多点温度计或仪表温度（IR26）。"],["压力单位","kPa、MPa、bar为工程单位符号，不翻译；4mA与20mA填写所选单位的工程量。"],["是否与空值","是/否（或true/false）；可空数值留空表示未配置，0与空值不同。所有数值填写实际值，不填写公式。"],["关联罐组","在“罐组关联”页，每行一个罐号、一个罐组；同罐可多行。导入按该罐所列全部行替换关联。清除全部关联：保留罐号，罐组留空且仅此一行。未列出的罐保留原关联；关联页罐号必须在主配置页列出。"],["IFX表地址","IFX30000：表地址范围为(站号-1)*32～站号*32-1，对应内部序号1～32。旧版本模板的内部罐号仍按原规则导入。"],["水位","手工填写水层高度mm，自动按所选基础罐容算法计算水体积；水位留空时沿用人工水体积。静压力修正只对整体液体容积应用一次。"],["安全与范围","Excel不含账号、口令、岗位权限、供方授权、接入令牌、罐容表数据或定时任务。允许操作列只修改配置，不立即发送动作。"],["导入流程","上传预览→检查错误与变更→确认保存。通道须预先创建；罐容表须预先导入。绑定变化可能重新建立采集会话，当前有未完成操作时由后台拒绝。"],["ATS高度","单位mm，从罐底起。未明确高度留空；正常温度点参与平均，不按液位剔除。"]});part.Workbook.Save();
        }return stream.ToArray();
    }
    public static TankConfigurationPreview Preview(byte[] file,SystemConfiguration current,IReadOnlyList<CapacityTableSummary> tables,CancellationToken ct=default,bool includeAverage=true)
    {
        var activeSheets=ForDelivery(includeAverage);var choices=Choices(current,tables);var absent=new HashSet<string>();
        if(file.Length is <8 or >4*1024*1024||file[0]!='P'||file[1]!='K')throw new ArgumentException("请使用本系统导出的xlsx模板，文件不超过4MiB");
        using(var archive=new ZipArchive(new MemoryStream(file),ZipArchiveMode.Read)){if(archive.Entries.Count>500||archive.Entries.Sum(e=>e.Length)>24L*1024*1024)throw new ArgumentException("配置Excel解压后过大");if(archive.Entries.Any(e=>e.FullName.EndsWith("vbaProject.bin",StringComparison.OrdinalIgnoreCase)||e.FullName.Contains("externalLinks/",StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("配置Excel不能含宏或外部链接");using var document=SpreadsheetDocument.Open(new MemoryStream(file),false);if(document.WorkbookPart!.WorksheetParts.Any(p=>p.Worksheet?.Descendants<CellFormula>().Any()==true))throw new ArgumentException("配置表请填写实际值，不使用公式");}
        Dictionary<string,List<object?[]>> data=new();using(var reader=ExcelReaderFactory.CreateOpenXmlReader(new MemoryStream(file))){do{if(reader.FieldCount>100)throw new ArgumentException("配置表列数超出模板范围");List<object?[]> rows=[];while(reader.Read()){ct.ThrowIfCancellationRequested();if(rows.Count>(reader.Name=="罐组关联"?32769:Sheets.Any(s=>s.Name==reader.Name)?129:2048))throw new ArgumentException("配置表行数超出128罐模板范围");var row=Enumerable.Range(0,reader.FieldCount).Select(reader.GetValue).ToArray();if(row.Any(v=>!string.IsNullOrWhiteSpace(Convert.ToString(v,CultureInfo.InvariantCulture))))rows.Add(row);}data.Add(reader.Name,rows);}while(reader.NextResult());}
        List<string> issues=[];string Text(object? value)=>Convert.ToString(value,CultureInfo.InvariantCulture)?.Trim()??"";
        var format=data.GetValueOrDefault("填写说明")?.FirstOrDefault(row=>Text(row.ElementAtOrDefault(0))=="模板格式")?.ElementAtOrDefault(1)?.ToString();
        var tableAddressFormat=format=="TANK-CONFIG-4";var separateGroups=format is "TANK-CONFIG-3" or "TANK-CONFIG-4";activeSheets=ForDelivery(includeAverage,separateGroups);
        if(separateGroups&&(!data.TryGetValue("罐组关联",out var associationHeaderRows)||associationHeaderRows.Count==0||associationHeaderRows[0].Length!=2||Text(associationHeaderRows[0][0])!="罐号"||Text(associationHeaderRows[0][1])!="所属罐组"))issues.Add("罐组关联：缺少工作表或表头不符");

        foreach(var (name,fields) in activeSheets)
        {
            if(!data.TryGetValue(name,out var rows)||rows.Count==0){issues.Add(name+"：工作表不存在");continue;}
            string Normalize(string text)=>text.Replace("ATS点数","温度计点数").Replace("ATS第","温度计第").Replace("仪表地址或内部罐号","表地址（IFX按站号范围）");
            var headers=rows[0].Select(v=>Normalize(Text(v))).ToArray();
            var indexes=fields.Select(f=>Array.IndexOf(headers,Normalize(f.Label))).ToArray();
            for(var i=0;i<fields.Length;i++)if(indexes[i]<0){if(AddedPaths.Contains(fields[i].Path))absent.Add(fields[i].Path);else issues.Add(name+"：缺少表头 "+fields[i].Label);}
            if(headers.Where(h=>h!="").Distinct().Count()!=headers.Count(h=>h!="")||headers.Any(h=>h!=""&&!fields.Any(f=>Normalize(f.Label)==h)))issues.Add(name+"：表头重复或含未知列");
            data[name]=new[]{fields.Select(f=>(object?)f.Label).ToArray()}.Concat(rows.Skip(1).Select(r=>indexes.Select(i=>i<0?null:r.ElementAtOrDefault(i)).ToArray())).ToList();
        }
        if(!data.TryGetValue("填写说明",out var info)||!info.Any(row=>row.Length>1&&Text(row[0])=="模板格式"&&(Text(row[1])=="TANK-CONFIG-1"||Text(row[1])=="TANK-CONFIG-2"||Text(row[1])=="TANK-CONFIG-3"||Text(row[1])=="TANK-CONFIG-4")))issues.Add("不是本系统支持的储罐配置模板");
        if(issues.Count>0)return new(current,issues,0,0,[]);
        var main=data["罐体与通讯"].Skip(1).ToArray();if(main.Length is <1 or >128)return new(current,["请填写1～128个储罐"],0,0,[]);
        var baseByCode=current.Tanks.ToDictionary(t=>t.Code,StringComparer.OrdinalIgnoreCase);var baseById=current.Tanks.ToDictionary(t=>t.Id);var result=current.Tanks.ToDictionary(t=>t.Id);var importedByCode=new Dictionary<string,TankDefinition>(StringComparer.OrdinalIgnoreCase);var seenCodes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var seenIds=new HashSet<Guid>();var changed=new List<string>();var groupMemberships=new Dictionary<Guid,HashSet<Guid>>();int added=0,updated=0;
        var supplemental=new Dictionary<string,Dictionary<string,object?[]>>();
        foreach(var (name,fields) in activeSheets.Skip(1)){var byCode=new Dictionary<string,object?[]>(StringComparer.OrdinalIgnoreCase);foreach(var row in data[name].Skip(1)){var code=Text(row.ElementAtOrDefault(0));if(!byCode.TryAdd(code,row))issues.Add(name+"：罐号重复 "+code);}supplemental[name]=byCode;}
        foreach(var row in main)
        {
            var code=Text(row.ElementAtOrDefault(1));try
            {
                if(string.IsNullOrWhiteSpace(code)||!seenCodes.Add(code))throw new ArgumentException("罐号为空或重复");
                var idText=Text(row.ElementAtOrDefault(0));TankDefinition? before=null;Guid? restoredId=null;
                if(idText!="")
                {
                    if(!Guid.TryParse(idText,out var id)||id==Guid.Empty)throw new ArgumentException("内部编号格式不正确；新建储罐请留空，恢复导出配置请保留原编号");
                    if(baseByCode.TryGetValue(code,out var sameCode)&&sameCode.Id!=id)throw new ArgumentException("罐号已属于另一内部编号；请核对原储罐，按罐号更新时可将本行内部编号留空");
                    baseById.TryGetValue(id,out before);restoredId=id;
                }
                else baseByCode.TryGetValue(code,out before);
                // Retain exported identities when restoring deleted tanks, so their history stays linked.
                var tank=before??new TankDefinition{Id=restoredId??Guid.NewGuid()};if(!seenIds.Add(tank.Id))throw new ArgumentException("同一储罐内部编号被重复使用");var node=JsonSerializer.SerializeToNode(tank,Json)!;
                foreach(var (name,fields) in activeSheets)
                {
                    var cells=name=="罐体与通讯"?row:supplemental[name].TryGetValue(code,out var extra)?extra:throw new ArgumentException(name+"缺少同罐号记录");
                    for(var i=0;i<fields.Length;i++)
                    {
                        var field=fields[i];if(field.Path=="id"||field.Path=="code"||absent.Contains(field.Path))continue;var text=Text(cells.ElementAtOrDefault(i));
                        try
                        {
                            if(text.Length>(field.Path=="$groups"?20000:2000))throw new ArgumentException("文本过长");
                            if(field.Path=="$groups"){var ids=text.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(v=>Reference(v,"$groups",choices)).ToArray();if(ids.Distinct().Count()!=ids.Length||ids.Any(id=>!current.Groups.Any(g=>g.Id==id)))throw new ArgumentException("罐组编号不存在或重复");groupMemberships[tank.Id]=ids.ToHashSet();continue;}
                            JsonNode? value=field.Kind switch
                            {
                                "number?" when text==""=>null,"guid?" when text==""=>null,
                                "number" or "number?"=>JsonValue.Create(decimal.Parse(text,NumberStyles.Float,CultureInfo.InvariantCulture)),
                                "integer"=>JsonValue.Create(int.Parse(text,NumberStyles.Integer,CultureInfo.InvariantCulture)),
                                "bool"=>JsonValue.Create(text is "是" or "true" or "True" or "1"?true:text is "否" or "false" or "False" or "0"?false:throw new ArgumentException("填写是或否")),
                                "guid" or "guid?"=>JsonValue.Create(Reference(text,field.Path,choices).ToString()),
                                "densitySource"=>JsonValue.Create(Enum.Parse<MeteringDensitySource>(EnumValue(text,"densitySource",includeAverage)).ToString()),"baseVolume"=>JsonValue.Create(Enum.Parse<BaseVolumeMethod>(EnumValue(text,"baseVolume",includeAverage)).ToString()),"fraction"=>JsonValue.Create(Enum.Parse<FractionalMillimetreRule>(EnumValue(text,"fraction",includeAverage)).ToString()),
                                "shape"=>JsonValue.Create(Enum.Parse<TankShape>(EnumValue(text,"shape",includeAverage)).ToString()),"temperature"=>JsonValue.Create(!includeAverage&&EnumValue(text,"temperature",includeAverage)=="Instrument"?TemperatureMode.Wireless.ToString():Enum.Parse<TemperatureMode>(EnumValue(text,"temperature",includeAverage)).ToString()),"pressureUnit"=>JsonValue.Create(Enum.Parse<PressureUnit>(EnumValue(text,"pressureUnit",includeAverage)).ToString()),_=>JsonValue.Create(text)
                            };
                            if(field.Path.StartsWith("$command.",StringComparison.Ordinal)){var command=field.Path[9..];var commands=(JsonArray)node["controls"]!["allowedCommands"]!;foreach(var existing in commands.Where(c=>c!.GetValue<string>()==command).ToArray())commands.Remove(existing);if(value!.GetValue<bool>())commands.Add(command);}
                            else Set(node,field.Path,value);
                        }catch(Exception e)when(e is FormatException or ArgumentException or OverflowException){throw new ArgumentException(name+" / "+field.Label+"："+e.Message);}
                    }
                }
                node["code"]=code;var next=node.Deserialize<TankDefinition>(Json)!;
                if(tableAddressFormat){var channel=current.Channels.FirstOrDefault(c=>c.Id==next.ChannelId);if(channel?.Protocol==AcquisitionProtocol.IfxL25)next=next with{Address=IfxAddressing.InternalSlot(next.Address,channel.UnitId)};}
                // Trailing empty heights are presentation defaults, not fake positions.
                next=next with{AtsHeightsMm=next.AtsHeightsMm.Reverse().SkipWhile(h=>h is null).Reverse().ToArray()};
                result[next.Id]=next;importedByCode.Add(code,next);if(before is null)added++;else updated++;changed.Add(code);
            }catch(Exception e)when(e is ArgumentException or FormatException or OverflowException or JsonException){issues.Add((code==""?"未填罐号":code)+"："+e.Message);}
        }
        foreach(var (name,rows) in supplemental)if(rows.Keys.Any(code=>!seenCodes.Contains(code)))issues.Add(name+"包含主配置页没有的罐号");
        if(separateGroups){
            var associationRows=data["罐组关联"].Skip(1).ToArray();
            foreach(var row in associationRows)if(Text(row.ElementAtOrDefault(0))=="")issues.Add("罐组关联：有罐组但未填写罐号");
            foreach(var rows in associationRows.Where(row=>Text(row.ElementAtOrDefault(0))!="").GroupBy(row=>Text(row[0]),StringComparer.OrdinalIgnoreCase)){
                var code=rows.Key;
                if(!seenCodes.Contains(code)){issues.Add("罐组关联："+code+"未在罐体与通讯页列出");continue;}
                // A rejected main row already has a precise error; do not cascade misleading group errors.
                if(!importedByCode.TryGetValue(code,out var tank))continue;
                try{var texts=rows.Select(row=>Text(row.ElementAtOrDefault(1))).ToArray();if(texts.Any(t=>t=="")&&texts.Length!=1)throw new ArgumentException("空罐组行不能与其他关联行混用");var ids=texts.Where(t=>t!="").Select(t=>Reference(t,"$groups",choices)).ToArray();if(ids.Distinct().Count()!=ids.Length)throw new ArgumentException("重复关联同一罐组");groupMemberships[tank.Id]=ids.ToHashSet();}catch(ArgumentException error){issues.Add("罐组关联 / "+code+"："+error.Message);}
            }
        }
        var proposed=current with{Tanks=result.Values.ToArray(),Groups=current.Groups.Select(g=>g with{TankIds=current.Tanks.Select(t=>t.Id).Concat(result.Keys).Distinct().Where(id=>groupMemberships.TryGetValue(id,out var membership)?membership.Contains(g.Id):g.TankIds.Contains(id)).ToArray()}).ToArray()};
        issues.AddRange(ConfigurationRules.Validate(proposed).Select(i=>i.Field+"："+i.Message));
        foreach(var tank in proposed.Tanks)foreach(var (id,kind) in new[]{(tank.Metering.MainTableId,CapacityTableKind.Main),(tank.Metering.StaticPressureTableId,CapacityTableKind.StaticPressure),(tank.Metering.DecimalTableId,CapacityTableKind.Decimal)})if(id.HasValue&&!tables.Any(t=>t.Id==id&&t.Kind==kind))issues.Add(tank.Code+"：关联罐容表不存在或类型不符");
        List<TankConfigurationDifference> differences=[];
        foreach(var tank in proposed.Tanks.Where(t=>seenIds.Contains(t.Id)))
        {
            var before=current.Tanks.FirstOrDefault(t=>t.Id==tank.Id);
            foreach(var field in Sheets.SelectMany(s=>s.Fields).Where(f=>f.Path!="id").DistinctBy(f=>f.Path))
            {
                var previous=before is null?"":Convert.ToString(CellValue(before,field,current),CultureInfo.InvariantCulture)??"";
                var next=Convert.ToString(CellValue(tank,field,proposed),CultureInfo.InvariantCulture)??"";
                if(previous!=next)differences.Add(new(tank.Code,field.Label,previous==""?"未设置":previous,next==""?"未设置":next));
            }
        }
        return new(proposed,issues.Distinct().ToArray(),added,updated,changed,differences);
    }
}

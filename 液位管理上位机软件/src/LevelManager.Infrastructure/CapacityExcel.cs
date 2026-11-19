using System.Globalization;
using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ExcelDataReader;
using Naide.LevelManager.Domain;

namespace Naide.LevelManager.Infrastructure;

public sealed record CapacityImportPreview(IReadOnlyList<CapacityPoint> Points, string SheetName,
    int ImportedRows, int FirstDataRow, int HeightColumn, int VolumeColumn, IReadOnlyList<string> Notes, IReadOnlyList<DecimalCapacityRow>? DecimalRows = null,
    CapacityTableKind? Kind = null, HeightUnit? HeightUnit = null, VolumeUnit? VolumeUnit = null, decimal? ReferenceDensityKgM3 = null);

public static class CapacityExcel
{
    public static CapacityImportPreview PreviewDecimal(byte[] file,int sheet,int firstRow,CancellationToken ct=default)
    {
        // Reuse size/ZIP limits and numeric parsing before reading all five columns.
        var metadata=Preview(file,sheet,firstRow,1,2,ct);
        if(metadata.Kind is not null && metadata.Kind!=CapacityTableKind.Decimal)throw new ArgumentException("文件类型不是小数表，请先选择对应表类型");
        if(metadata.HeightUnit is not null && metadata.HeightUnit!=Domain.HeightUnit.Millimetre || metadata.VolumeUnit is not null && metadata.VolumeUnit!=Domain.VolumeUnit.Litre)throw new ArgumentException("五列小数表必须使用mm和L");
        using var reader=Open(new MemoryStream(file));
        for(var i=0;i<sheet;i++)if(!reader.NextResult())throw new ArgumentException("工作表不存在");
        List<DecimalCapacityRow> rows=[];var at=0;
        while(reader.Read())
        {
            if(++at<firstRow)continue;ct.ThrowIfCancellationRequested();
            string Text(int i)=>Convert.ToString(i<reader.FieldCount?reader.GetValue(i):null,CultureInfo.InvariantCulture)?.Trim()??"";
            if(new[]{0,1,3,4}.All(i=>Text(i)==""))continue;
            decimal Number(int i)=>decimal.TryParse(Text(i),NumberStyles.Float|NumberStyles.AllowThousands,CultureInfo.InvariantCulture,out var n)?n:throw new ArgumentException("第"+at+"行第"+(i+1)+"列须填写数值");
            var order=Number(2);if(order!=decimal.Truncate(order)||order<1||order>9)throw new ArgumentException("第"+at+"行序号须为1～9整数");
            rows.Add(new(Number(0),Number(1),(int)order,Number(3),Number(4)));
            if(rows.Count>100000)throw new ArgumentException("小数表数据过多");
        }
        var table=new CapacityTable{Number="preview",Name="预览",Kind=CapacityTableKind.Decimal,HeightUnit=HeightUnit.Millimetre,VolumeUnit=VolumeUnit.Litre,DecimalRows=rows};
        var issues=CapacityRules.ValidateDecimalRows(table);if(issues.Count>0)throw new ArgumentException(string.Join("；",issues.Select(i=>i.Message).Distinct()));
        if(rows.Count==0)throw new ArgumentException("未填写小数表数据");
        return new([],reader.Name,rows.Count,firstRow,1,4,["五列模板：起始mm、结束mm、序号1～9、厘米累计增量L、毫米累计增量L。","仅读取保存数值，预览核对区段与主表节点后启用。"],rows,CapacityTableKind.Decimal,Domain.HeightUnit.Millimetre,Domain.VolumeUnit.Litre);
    }
    static CapacityExcel() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static CapacityImportPreview Preview(byte[] file, int sheet, int firstRow, int heightColumn, int volumeColumn, CancellationToken ct = default)
    {
        if (file.Length is < 8 or > 20 * 1024 * 1024 || sheet is < 0 or > 99 || firstRow is < 1 or > 1000 || heightColumn is < 1 or > 256 || volumeColumn is < 1 or > 256 || heightColumn == volumeColumn)
            throw new ArgumentException("文件大小或工作表/行列选择无效");
        if (file[0] == 'P' && file[1] == 'K')
        {
            using var zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read);
            if (zip.Entries.Count > 10000 || zip.Entries.Sum(e => e.Length) > 150L * 1024 * 1024) throw new ArgumentException("Excel解压后过大，请只保留罐容表数据");
        }
        using var input = new MemoryStream(file);
        using var reader = Open(input);
        for (var i = 0; i < sheet; i++) if (!reader.NextResult()) throw new ArgumentException("工作表序号不存在");
        List<CapacityPoint> points = []; var row = 0;
        while (reader.Read())
        {
            if (++row < firstRow) continue;
            ct.ThrowIfCancellationRequested();
            if (reader.FieldCount < Math.Max(heightColumn, volumeColumn)) throw new ArgumentException("选择列超出工作表范围");
            var h = reader.GetValue(heightColumn - 1); var v = reader.GetValue(volumeColumn - 1);
            if (h is null && v is null || string.IsNullOrWhiteSpace(h?.ToString()) && string.IsNullOrWhiteSpace(v?.ToString())) continue;
            decimal Number(object? value)
            {
                if (value is double number && double.IsFinite(number)) return Convert.ToDecimal(number);
                if (value is decimal d) return d;
                if (decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var n)) return n;
                throw new ArgumentException("第" + row + "行不是完整数值，请检查起始行和列号");
            }
            points.Add(new(Number(h), Number(v)));
            if (points.Count > 100000) throw new ArgumentException("罐容表不能超过100000组数据");
        }
        if (points.Count < 2) throw new ArgumentException("未找到至少两组有效数据");
        var metadata=ReadMetadata(file,ct);
        return new(points, reader.Name, points.Count, firstRow, heightColumn, volumeColumn,
            ["导入文件中已保存的数值，不执行宏或重新计算Excel公式；请先在Excel重新计算并保存，预览核对后启用。", metadata.Kind is null?"文件未附本系统资料页，单位与参考密度请按原始资料明确选择，不按数值大小猜测。":"已读取文件资料页的表类型、单位及参考密度，请核对后保存。"],
            Kind:metadata.Kind,HeightUnit:metadata.Height,VolumeUnit:metadata.Volume,ReferenceDensityKgM3:metadata.Density);
    }

    // Only adopt explicit metadata emitted by this application's export. External workbooks
    // without that metadata still require the operator to select units; never infer by magnitude.
    private static (CapacityTableKind? Kind,HeightUnit? Height,VolumeUnit? Volume,decimal? Density) ReadMetadata(byte[] file,CancellationToken ct)
    {
        using var reader=Open(new MemoryStream(file));
        for(var sheet=0;sheet<100;sheet++)
        {
            ct.ThrowIfCancellationRequested();
            if(reader.Name=="资料")
            {
                Dictionary<string,string> values=new(StringComparer.Ordinal);
                for(var row=0;row<64&&reader.Read();row++)
                {
                    ct.ThrowIfCancellationRequested();
                    if(reader.FieldCount<2)continue;
                    var key=Convert.ToString(reader.GetValue(0),CultureInfo.InvariantCulture)?.Trim()??"";
                    if(!new[]{"表编号","类型","高度单位","容积单位","静压力参考密度 kg/m³"}.Contains(key))continue;
                    if(!values.TryAdd(key,Convert.ToString(reader.GetValue(1),CultureInfo.InvariantCulture)?.Trim()??""))throw new ArgumentException("资料页存在重复字段："+key);
                }
                if(!values.ContainsKey("表编号")||!values.ContainsKey("类型"))return default;
                var kind=values["类型"] switch{"主罐容表"=>CapacityTableKind.Main,"静压力容积修正表"=>CapacityTableKind.StaticPressure,"小数表"=>CapacityTableKind.Decimal,_=>throw new ArgumentException("资料页表类型无效")};
                var height=values.GetValueOrDefault("高度单位") switch{"mm"=>Domain.HeightUnit.Millimetre,"cm"=>Domain.HeightUnit.Centimetre,"m"=>Domain.HeightUnit.Metre,_=>throw new ArgumentException("资料页高度单位无效，请核对原始文件")};
                var volume=values.GetValueOrDefault("容积单位") switch{"L"=>Domain.VolumeUnit.Litre,"m³"=>Domain.VolumeUnit.CubicMetre,_=>throw new ArgumentException("资料页容积单位无效，请核对原始文件")};
                decimal? density=null;
                if(kind==CapacityTableKind.StaticPressure)
                {
                    if(!decimal.TryParse(values.GetValueOrDefault("静压力参考密度 kg/m³"),NumberStyles.Float,CultureInfo.InvariantCulture,out var d)||d<=0||d>1000000)throw new ArgumentException("资料页静压力参考密度无效");
                    density=d;
                }
                return(kind,height,volume,density);
            }
            if(!reader.NextResult())break;
        }
        return default;
    }

    private static IExcelDataReader Open(Stream stream)
    {
        try{return ExcelReaderFactory.CreateReader(stream);}
        catch(Exception error) when(error.GetType().Namespace?.StartsWith("ExcelDataReader.Exceptions",StringComparison.Ordinal)==true)
        {throw new ArgumentException("无法读取Excel文件，请使用有效的xls或xlsx文件",error);}
    }
    public static byte[] Export(CapacityTable table)
    {
        using var stream = new MemoryStream();
        using (var workbook = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var part = workbook.AddWorkbookPart(); part.Workbook = new Workbook(); var styles=part.AddNewPart<WorkbookStylesPart>();styles.Stylesheet=WorkbookBorders.CreateStyles();styles.Stylesheet.Save(); var sheets = part.Workbook.AppendChild(new Sheets());
            void Sheet(string name, IEnumerable<object?[]> rows)
            {
                var ws = part.AddNewPart<WorksheetPart>();
                using (var writer = OpenXmlWriter.Create(ws))
                {
                    writer.WriteStartElement(new Worksheet()); writer.WriteStartElement(new SheetData()); uint row = 0;
                    foreach (var cells in rows)
                    {
                        writer.WriteStartElement(new Row { RowIndex = ++row });
                        foreach (var value in cells)
                        {
                            var number = value is decimal or double or float or int or long;
                            writer.WriteStartElement(new Cell { StyleIndex=row==1?1u:0u, DataType = number ? CellValues.Number : CellValues.InlineString });
                            if (number) writer.WriteElement(new CellValue(Convert.ToString(value, CultureInfo.InvariantCulture)!));
                            else writer.WriteElement(new InlineString(new Text(value?.ToString() ?? "") { Space = SpaceProcessingModeValues.Preserve }));
                            writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement(); writer.WriteEndElement();
                }
                sheets.Append(new Sheet { Id = part.GetIdOfPart(ws), SheetId = (uint)sheets.Count() + 1, Name = name });
            }
            if(table.Kind==CapacityTableKind.Decimal&&table.DecimalRows.Count>0)
                Sheet("数据",new[]{new object?[]{"起始高度（mm）","结束高度（mm）","序号（1～9）","厘米累计容积增量（L）","毫米累计容积增量（L）"}}.Concat(table.DecimalRows.Select(r=>new object?[]{r.StartHeightMm,r.EndHeightMm,r.Number,r.CentimetreLitres,r.MillimetreLitres})));
            else Sheet("数据", new[] { new object?[] { (table.Kind==CapacityTableKind.Decimal?"高度余数（":"液位高度（") + (table.HeightUnit==HeightUnit.Metre?"m":table.HeightUnit==HeightUnit.Centimetre?"cm":"mm") + "）", (table.Kind==CapacityTableKind.StaticPressure?"容积修正量（":table.Kind==CapacityTableKind.Decimal?"容积增量（":"容积（") + (table.VolumeUnit==VolumeUnit.Litre?"L":"m³") + "）" } }
                .Concat(table.Points.Select(p => new object?[] { p.Height, p.Volume })));
            Sheet("资料", new[] { new object?[] { "表编号", table.Number }, ["表名称", table.Name], ["类型", (table.Kind==CapacityTableKind.Main?"主罐容表":table.Kind==CapacityTableKind.StaticPressure?"静压力容积修正表":"小数表")], ["版本", table.Revision], ["高度单位", (table.HeightUnit==HeightUnit.Metre?"m":table.HeightUnit==HeightUnit.Centimetre?"cm":"mm")], ["容积单位", (table.VolumeUnit==VolumeUnit.Litre?"L":"m³")], ["静压力参考密度 kg/m³", table.ReferenceDensityKgM3], ["备注", table.Notes] });
            part.Workbook.Save();
        }
        return stream.ToArray();
    }
}

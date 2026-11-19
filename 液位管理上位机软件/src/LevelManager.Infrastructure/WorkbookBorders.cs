using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
namespace Naide.LevelManager.Infrastructure;
public static class WorkbookBorders
{
    public static Border Thin()=>new(new LeftBorder(new Color{Rgb="FFD9E1E6"}){Style=BorderStyleValues.Thin},new RightBorder(new Color{Rgb="FFD9E1E6"}){Style=BorderStyleValues.Thin},new TopBorder(new Color{Rgb="FFD9E1E6"}){Style=BorderStyleValues.Thin},new BottomBorder(new Color{Rgb="FFD9E1E6"}){Style=BorderStyleValues.Thin},new DiagonalBorder());
    public static Stylesheet CreateStyles()=>new(new Fonts(new Font{FontSize=new FontSize{Val=11},FontName=new FontName{Val="微软雅黑"}},new Font{Bold=new Bold(),FontSize=new FontSize{Val=11},FontName=new FontName{Val="微软雅黑"}}),new Fills(new Fill(new PatternFill{PatternType=PatternValues.None}),new Fill(new PatternFill{PatternType=PatternValues.Gray125}),new Fill(new PatternFill(new ForegroundColor{Rgb="FFEAF4EF"}){PatternType=PatternValues.Solid})),new Borders(Thin()),new CellStyleFormats(new CellFormat()),new CellFormats(new CellFormat{BorderId=0,ApplyBorder=true},new CellFormat{FontId=1,FillId=2,ApplyFont=true,ApplyFill=true,BorderId=0,ApplyBorder=true,Alignment=new Alignment{WrapText=true}}));
    public static void Apply(Stylesheet styles)
    {
        styles.Borders??=new Borders();var index=(uint)styles.Borders.ChildElements.Count;styles.Borders.Append(Thin());styles.Borders.Count=(uint)styles.Borders.ChildElements.Count;
        styles.CellFormats??=new CellFormats(new CellFormat());foreach(var format in styles.CellFormats.Elements<CellFormat>()){format.BorderId=index;format.ApplyBorder=true;}
    }
    // Used when serving original template assets. Only styles change: formulas,
    // validation, values, sheet order and calculation examples stay intact.
    public static byte[] AddToTemplate(byte[] original)
    {
        using var stream=new MemoryStream();stream.Write(original);stream.Position=0;
        using(var doc=SpreadsheetDocument.Open(stream,true)){var part=doc.WorkbookPart!;var style=part.WorkbookStylesPart??part.AddNewPart<WorkbookStylesPart>();style.Stylesheet??=CreateStyles();Apply(style.Stylesheet);style.Stylesheet.Save();}
        return stream.ToArray();
    }
}

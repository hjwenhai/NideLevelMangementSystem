namespace Naide.LevelManager.Domain;

public static partial class CapacityRules
{
    public static IReadOnlyList<ValidationIssue> ValidateDecimalRows(CapacityTable table)
    {
        List<ValidationIssue> issues=[];
        var rows=table.DecimalRows;
        if(table.HeightUnit!=HeightUnit.Millimetre||table.VolumeUnit!=VolumeUnit.Litre||table.Points.Count>0)
            issues.Add(new("decimalRows","分区小数表固定使用mm和L，不与旧两列数据混存"));
        if(rows.Count>100000) return [new("decimalRows","小数表数据过多")];
        decimal? end=null;
        foreach(var band in rows.GroupBy(r=>(r.StartHeightMm,r.EndHeightMm)).OrderBy(g=>g.Key.StartHeightMm))
        {
            var (start,finish)=band.Key;var ordered=band.OrderBy(r=>r.Number).ToArray();
            if(start<0||finish<=start||finish>100000||end.HasValue&&start!=end.Value)
                issues.Add(new("decimalRows","高度区段须连续、递增且不重叠，结束高度须大于起始高度"));
            if(!ordered.Select(r=>r.Number).SequenceEqual(Enumerable.Range(1,9)))
                issues.Add(new("decimalRows","每区段必须完整填写序号1～9各一次"));
            decimal cm=0,mm=0;
            foreach(var row in ordered){if(row.CentimetreLitres<cm||row.MillimetreLitres<mm||row.CentimetreLitres>1000000000||row.MillimetreLitres>1000000000)issues.Add(new("decimalRows","厘米和毫米累计增量须非负且递增，请核对原检定表"));cm=row.CentimetreLitres;mm=row.MillimetreLitres;}
            end=finish;
        }
        return issues;
    }
    public static decimal WithDecimal(CapacityTable main,CapacityTable? small,decimal height,FractionalMillimetreRule fraction)
    {
        if(small is null||!small.Enabled||small.Kind!=CapacityTableKind.Decimal||small.DecimalRows.Count==0)
            throw new InvalidOperationException("请关联已启用的五列分区小数表；旧两列表不用于组合计算");
        if(ValidateDecimalRows(small).Count>0)throw new InvalidOperationException("小数表区段数据未通过校核");
        decimal H(CapacityPoint p)=>HeightToMm(p.Height,main.HeightUnit);
        var groups=small.DecimalRows.GroupBy(r=>(r.StartHeightMm,r.EndHeightMm)).ToArray();
        var heights=main.Points.Select(H).ToHashSet();
        if(groups.Any(g=>!heights.Contains(g.Key.StartHeightMm)||!heights.Contains(g.Key.EndHeightMm)))
            throw new InvalidOperationException("小数表区段边界必须与主表精确节点对应，请核对两表");
        if(height<H(main.Points[0])||height>H(main.Points[^1]))throw new ArgumentOutOfRangeException(nameof(height));
        var exact=main.Points.FirstOrDefault(p=>H(p)==height);
        if(main.Points.Any(p=>H(p)==height))return VolumeToM3(exact.Volume,main.VolumeUnit);
        if(height!=decimal.Truncate(height)&&fraction!=FractionalMillimetreRule.Linear)
            throw new InvalidOperationException(fraction==FractionalMillimetreRule.IntegerOnly?"小数表按整数毫米计算，当前液位包含不足1mm部分":"请先选择不足1mm的处理规则");
        decimal AtInteger(decimal target)
        {
            var anchor=main.Points.Last(p=>H(p)<=target);
            if(H(anchor)==target)return VolumeToM3(anchor.Volume,main.VolumeUnit);
            var band=groups.SingleOrDefault(g=>target>=g.Key.StartHeightMm&&target<g.Key.EndHeightMm)
                ??throw new InvalidOperationException("当前液位不在小数表有效区段，未外推");
            var remainder=target-H(anchor);
            if(remainder<0||remainder>=100)throw new InvalidOperationException("主表相邻节点超过100mm，不能使用本厘米毫米小数表");
            var cm=(int)(remainder/10);var mm=(int)(remainder%10);
            var litres=(cm==0?0:band.Single(r=>r.Number==cm).CentimetreLitres)+(mm==0?0:band.Single(r=>r.Number==mm).MillimetreLitres);
            return VolumeToM3(anchor.Volume,main.VolumeUnit)+litres/1000m;
        }
        var lower=decimal.Floor(height);var upper=decimal.Ceiling(height);
        return lower==upper?AtInteger(lower):AtInteger(lower)+(AtInteger(upper)-AtInteger(lower))*(height-lower);
    }
}

using Microsoft.Data.Sqlite;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    private static string QualityPredicate(HistoryQuery query,SqliteCommand command)
    {
        HistoryQualityPolicy.Validate(query.QualityQuantity,query.Quality);
        if(query.Quality==HistoryQualityFilter.All)return "";
        var (column,shift)=query.QualityQuantity switch
        {
            TrendQuantity.Level=>("QualityBits",0),TrendQuantity.InstantTemperature=>("QualityBits",6),
            TrendQuantity.AverageTemperature=>("QualityBits",9),TrendQuantity.Density=>("QualityBits",12),
            TrendQuantity.CurrentVolume=>("QualityBits",15),TrendQuantity.NetVolume=>("QualityBits",18),
            TrendQuantity.Mass=>("QualityBits",21),TrendQuantity.Pressure1=>("PressureQualityBits",0),
            TrendQuantity.Pressure2=>("PressureQualityBits",3),_=>throw new ArgumentException("状态筛选的数据项无效")
        };
        var expression=$"((h.{column} >> {shift}) & 7)";
        if(query.Quality==HistoryQualityFilter.Invalid)return " AND "+expression+"<>0";
        command.Parameters.AddWithValue("$quality",(int)Enum.Parse<ValueQuality>(query.Quality.ToString()));
        return " AND "+expression+"=$quality";
    }
}

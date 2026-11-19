using Naide.LevelManager.Domain;

namespace Naide.LevelManager.Application;

public interface IHistoryStore
{
    IAsyncEnumerable<HistorySample> ReadForExportAsync(HistoryQuery query,CancellationToken ct=default);
    Task<IReadOnlyList<TrendSeries>> QueryTrendAsync(TrendQuery query,CancellationToken ct=default);
    Task<int> AppendAsync(IReadOnlyList<HistorySample> samples, CancellationToken ct = default);
    Task<HistoryPage> QueryAsync(HistoryQuery query, CancellationToken ct = default);
    Task<HistoryStatistics> StatisticsAsync(CancellationToken ct = default);
    Task<int> DeleteExpiredBatchAsync(long beforeUtcMs, int maximumRows = 4096, CancellationToken ct = default);
}

using Microsoft.Extensions.Logging;

namespace IndexInvestment.Core;

/// <summary>
/// Turns raw MOEX data (index composition + TQBR board) into an
/// allocatable <see cref="MarketSnapshot"/> and hands it out through
/// the per-index cache.
///
/// A snapshot is exactly two request shapes: the index analytics
/// (weights + trade date) and the TQBR board (lot sizes + last legal
/// close per ticker), so a cold fetch is ~1 s instead of one request
/// per ticker.
/// </summary>
public sealed class MarketDataService
{
    /// <summary>Market data is settled daily; cache it for an hour.</summary>
    public const double SnapshotTtl = 3600;

    /// <summary>Reject quotes whose last settled session is older than this.</summary>
    public const int MaxPriceAgeDays = 30;

    private readonly IMoexClient _client;
    private readonly SnapshotCache _cache;
    private readonly ILogger<MarketDataService> _logger;

    public MarketDataService(IMoexClient client, SnapshotCache cache, ILogger<MarketDataService> logger)
    {
        _client = client;
        _cache = cache;
        _logger = logger;
    }

    public Task<MarketSnapshot> GetSnapshotAsync(
        string indexId, double maxAgeSeconds = SnapshotTtl, CancellationToken ct = default) =>
        _cache.GetOrFetchAsync(indexId, maxAgeSeconds, c => FetchAsync(indexId, c), ct);

    /// <summary>Drop the cache so the next dashboard load refetches.</summary>
    public void Invalidate(string? indexId) => _cache.Invalidate(indexId);

    private async Task<MarketSnapshot> FetchAsync(string indexId, CancellationToken ct)
    {
        var composition = await _client.GetIndexCompositionAsync(indexId, ct: ct);
        var board = await _client.GetTqbrBoardAsync(ct);
        var rows = FillPricesAndLots(composition, board);
        _logger.LogInformation(
            "Fetched {Index}: {Count} constituents, trade date {Date}",
            indexId, rows.Count, composition.TradeDate);
        return new MarketSnapshot { TradeDate = composition.TradeDate, Rows = rows };
    }

    /// <summary>
    /// Fill price (last legal close) and lot size for every index
    /// ticker from the board payload.
    ///
    /// The board's PREVLEGALCLOSEPRICE is the close of the ticker's
    /// last settled session (PREVDATE). While the market is still
    /// trading that is the previous day's close. A ticker whose last
    /// session is more than MaxPriceAgeDays older than the trade date
    /// (long suspension) makes the whole snapshot fail with a clear
    /// error — allocating on stale prices would be worse than no data.
    /// </summary>
    private static List<Constituent> FillPricesAndLots(IndexComposition composition,
        IReadOnlyDictionary<string, BoardSecurity> board)
    {
        var tradeDate = DateTime.Parse(composition.TradeDate,
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None);
        var rows = new List<Constituent>(composition.Constituents.Count);
        var problems = new List<string>();

        foreach (var c in composition.Constituents)
        {
            if (!board.TryGetValue(c.Ticker, out var sec))
            {
                problems.Add($"{c.Ticker}: not found on TQBR board");
                continue;
            }
            if (sec.PrevDate is not { Length: > 0 } prevDate)
            {
                problems.Add($"{c.Ticker}: no price data (last session n/a)");
                continue;
            }
            if (sec.PrevLegalClosePrice is not { } price || price <= 0)
            {
                problems.Add($"{c.Ticker}: no price data (last session {prevDate})");
                continue;
            }
            var prev = DateTime.Parse(prevDate,
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None);
            var ageDays = (tradeDate - prev).Days;
            if (ageDays > MaxPriceAgeDays)
            {
                problems.Add($"{c.Ticker}: last session {prevDate} is {ageDays}d before {composition.TradeDate}");
                continue;
            }
            if (sec.LotSize is not { } lot || lot <= 0)
            {
                problems.Add($"{c.Ticker}: bad lot size");
                continue;
            }
            rows.Add(new Constituent(c.Ticker, c.Name, c.Weight, price, (long)lot));
        }

        if (problems.Count > 0)
            throw new MarketDataException("Incomplete market data: " + string.Join("; ", problems));
        return rows;
    }
}

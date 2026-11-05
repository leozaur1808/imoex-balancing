using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace IndexInvestment.Core;

/// <summary>
/// Thin async client over the MOEX ISS endpoints we use. All market
/// data in this app comes from exactly two request shapes:
///
///   * index analytics — composition + weights + settled trade date
///     (one or more pages for large indexes)
///   * TQBR board      — lot sizes + last legal close for every share
///
/// (The old per-ticker history fetches are gone; one board request
/// covers all of them.)
/// </summary>
public interface IMoexClient
{
    Task<IndexComposition> GetIndexCompositionAsync(string indexId, int pageSize = 300, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, BoardSecurity>> GetTqbrBoardAsync(CancellationToken ct = default);
}

public sealed class MoexClient : IMoexClient
{
    private const string AnalyticsUrl =
        "https://iss.moex.com/iss/statistics/engines/stock/markets/index/analytics/{0}.json?limit={1}&start={2}";
    private const string BoardUrl =
        "https://iss.moex.com/iss/engines/stock/markets/shares/boards/TQBR/securities.json?iss.meta=off&iss.only=securities";

    private const int MaxPages = 10;

    private readonly HttpClient _http;
    private readonly ILogger<MoexClient> _logger;

    public MoexClient(HttpClient http, ILogger<MoexClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IndexComposition> GetIndexCompositionAsync(
        string indexId, int pageSize = 300, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);

        var all = new List<IndexConstituent>();
        string tradeDate = null!;
        for (var page = 0; ; page++)
        {
            if (page >= MaxPages)
                throw new MarketDataException(
                    $"Index {indexId}: expected more than {MaxPages * pageSize} constituents; aborting.");
            var (date, rows) = await FetchAnalyticsPageAsync(indexId, pageSize, page * pageSize, ct);
            if (page == 0) tradeDate = date;
            all.AddRange(rows);
            if (rows.Count < pageSize) break;   // last (or only) page
        }
        if (all.Count == 0)
            throw new MarketDataException(
                $"Index {indexId}: MOEX returned an empty composition (unknown index id?).");
        return new IndexComposition(tradeDate, all);
    }

    private async Task<(string TradeDate, List<IndexConstituent> Rows)> FetchAnalyticsPageAsync(
        string indexId, int limit, int start, CancellationToken ct)
    {
        var url = string.Format(AnalyticsUrl, Uri.EscapeDataString(indexId), limit, start);
        string body;
        try
        {
            body = await _http.GetStringAsync(url, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new MarketDataException($"MOEX request failed ({url}): {e.Message}", e);
        }

        List<string> columns;
        List<List<string?>> rows;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var analytics = doc.RootElement.GetProperty("analytics");
            columns = analytics.GetProperty("columns")
                .EnumerateArray().Select(e => e.GetString()!).ToList();
            rows = analytics.GetProperty("data")
                .EnumerateArray()
                .Select(row => row.EnumerateArray().Select(ReadCell).ToList())
                .ToList();
        }
        catch (Exception e) when (e is KeyNotFoundException or JsonException)
        {
            throw new MarketDataException($"MOEX analytics for {indexId} has an unexpected shape", e);
        }

        if (columns.Count == 0 || rows.Count == 0)
            return (null!, new List<IndexConstituent>());

        int tickers = columns.IndexOf("ticker");
        int names   = columns.IndexOf("shortnames");
        int weights = columns.IndexOf("weight");
        int dates   = columns.IndexOf("trade_session_date");
        if (tickers < 0 || names < 0 || weights < 0 || dates < 0)
            throw new MarketDataException(
                $"MOEX analytics for {indexId} is missing expected columns (got: {string.Join(", ", columns)})");

        var constituents = new List<IndexConstituent>(rows.Count);
        foreach (var row in rows)
        {
            var weight = row[weights] is { } w && double.TryParse(
                w, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pw)
                ? pw
                : 0.0;
            constituents.Add(new IndexConstituent(row[tickers]!, row[names] ?? "", weight));
        }
        var tradeDate = rows[0][dates] ?? "";
        return (tradeDate, constituents);
    }

    public async Task<IReadOnlyDictionary<string, BoardSecurity>> GetTqbrBoardAsync(CancellationToken ct = default)
    {
        string body;
        try
        {
            body = await _http.GetStringAsync(BoardUrl, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new MarketDataException($"MOEX TQBR board request failed: {e.Message}", e);
        }

        List<string> columns;
        List<List<string?>> rows;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var securities = doc.RootElement.GetProperty("securities");
            columns = securities.GetProperty("columns")
                .EnumerateArray().Select(e => e.GetString()!).ToList();
            rows = securities.GetProperty("data")
                .EnumerateArray()
                .Select(row => row.EnumerateArray().Select(ReadCell).ToList())
                .ToList();
        }
        catch (Exception e) when (e is KeyNotFoundException or JsonException)
        {
            throw new MarketDataException("MOEX TQBR board has an unexpected shape", e);
        }

        int secid = columns.IndexOf("SECID");
        int lot   = columns.IndexOf("LOTSIZE");
        int price = columns.IndexOf("PREVLEGALCLOSEPRICE");
        int pdate = columns.IndexOf("PREVDATE");
        if (secid < 0 || lot < 0 || price < 0 || pdate < 0)
            throw new MarketDataException(
                $"MOEX TQBR board is missing expected columns (got: {string.Join(", ", columns)})");

        var board = new Dictionary<string, BoardSecurity>(rows.Count);
        foreach (var row in rows)
        {
            if (row[secid] is not { Length: > 0 } id) continue;
            board[id] = new BoardSecurity(
                id,
                ParseDouble(row[lot]),
                ParseDouble(row[price]),
                string.IsNullOrEmpty(row[pdate]) ? null : row[pdate]);
        }
        _logger.LogDebug("TQBR board: {Count} securities", board.Count);
        return board;
    }

    private static string? ReadCell(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        _ => null,
    };

    private static double? ParseDouble(string? s) =>
        s is { Length: > 0 } && double.TryParse(
            s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}

namespace IndexInvestment.Core;

/// <summary>One index constituent as reported by MOEX analytics.</summary>
public sealed record IndexConstituent(string Ticker, string Name, double Weight);

/// <summary>
/// Index composition page: the settled trading session date plus the
/// constituents with their (percent) weights.
/// </summary>
public sealed record IndexComposition(string TradeDate, IReadOnlyList<IndexConstituent> Constituents);

/// <summary>
/// A TQBR board row: lot size and the last *legal* close (settled
/// session). While the market is still trading this is the previous
/// session's close.
/// </summary>
public sealed record BoardSecurity(
    string Secid,
    double? LotSize,
    double? PrevLegalClosePrice,
    string? PrevDate);

/// <summary>
/// A ready-to-allocate snapshot: index constituents enriched with
/// prices (last legal close) and lot sizes.
/// </summary>
public sealed record Constituent(
    string Ticker,
    string Name,
    double Weight,
    double Price,
    long LotSize);

public sealed class MarketSnapshot
{
    public required string TradeDate { get; init; }
    public required IReadOnlyList<Constituent> Rows { get; init; }
}

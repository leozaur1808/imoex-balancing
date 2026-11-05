namespace IndexInvestment.Core;

// Structured results of the pipeline. Key names mirror the old Python
// API so the dashboard contract is unchanged.

/// <summary>One buy order in a run result.</summary>
public sealed record BuyRow(
    string Ticker, string Name, double Weight,
    long Shares, double Price, double Value, double Commission);

/// <summary>Per-ticker row of a run report (see RunReport entity).</summary>
public sealed record ReportRow(
    string Ticker, string Name, double Weight, double Price, int LotSize,
    int CurrentShares, double CurrentValue, double CostBasis,
    int BuyShares, double BuyValue, double BuyComm,
    double PostValue, double PostWeight, double TargetW, double WeightDiff);

/// <summary>Full structured result of one DCA cycle.</summary>
public sealed class RunResult
{
    public required string TradeDate { get; init; }
    public double PortfolioValue { get; init; }
    public double TotalAfter { get; init; }
    public List<BuyRow> Buys { get; init; } = new();
    public double StockBought { get; init; }
    public double CommissionPaid { get; init; }
    public double TotalSpent { get; init; }
    public double Leftover { get; init; }
    public double? L1Error { get; init; }
    public bool Carried { get; init; }
    public string? CarryReason { get; init; }
    public int Rows { get; init; }
    public bool DryRun { get; init; }
    public int? RunId { get; init; }
}

/// <summary>Result of undoing an applied run.</summary>
public sealed class UndoResult
{
    public required int Id { get; init; }
    public required string Date { get; init; }
    public double? Amount { get; init; }
    public string? TradeDate { get; init; }
    public required int NReversed { get; init; }
}

/// <summary>The latest applied run the UI can offer to undo (or null).</summary>
public sealed class UndoTarget
{
    public required int Id { get; init; }
    public required string Date { get; init; }
    public double? Amount { get; init; }
    public string? TradeDate { get; init; }
}

/// <summary>Dashboard row: one index constituent with the current holding.</summary>
public sealed class PortfolioRow
{
    public required string Ticker { get; init; }
    public required string Name { get; init; }
    public double Weight { get; init; }
    public double Price { get; init; }
    public long Shares { get; init; }
    public double Value { get; init; }
    public double CostBasis { get; init; }
}

/// <summary>Summary + holdings for the dashboard.</summary>
public sealed class PortfolioView
{
    public required string TradeDate { get; init; }
    public required List<PortfolioRow> Rows { get; init; }
    public double PortfolioValue { get; init; }
    public double CostBasis { get; init; }
    public int NHeld { get; init; }
    public int NIndex { get; init; }
}

// Read-model rows returned by PortfolioService.

public sealed record PositionRow(string Ticker, long Shares, double AvgPrice);

public sealed record TransactionRow(
    int Id, string Date, string Ticker, string Action,
    long Shares, double Price, double Value, double Commission);

public sealed record RunRow(
    int Id, string Date, double? Amount, string? TradeDate, string? IndexId, string Status);

public sealed record ReportRowDto(
    string Ticker, string? Shortnames, double? Weight, double? PriceRub, int? LotSize,
    int CurrentShares, double? CurrentValue, double? CostBasis,
    int BuyShares, double? BuyValue, double? BuyComm,
    double? PostValue, double? PostWeight, double? TargetW, double? WeightDiff);

// Import input rows.

public sealed record ImportPositionRow(string Ticker, long Shares, double AvgPrice);

public sealed record ImportTransactionRow(
    string Date, string Ticker, string Action, long Shares, double Price, double Value, double Commission);

public sealed class ImportResult
{
    public required int RowsImported { get; init; }
    public int? RunId { get; init; }
}

namespace IndexInvestment.Core.Entities;

/// <summary>
/// Per-user settings: the commission rate (percent, e.g. 0.045 means
/// 0.045%) and the MOEX index the user DCAs into. One row per user.
/// </summary>
public class UserSettings
{
    public string UserId { get; set; } = default!;
    public decimal CommissionPercent { get; set; } = 0.045m;
    public string IndexId { get; set; } = "IMOEX";
}

/// <summary>Current holding of one user in one ticker.</summary>
public class Position
{
    public string UserId { get; set; } = default!;
    public string Ticker { get; set; } = default!;
    public long Shares { get; set; }
    public double AvgPrice { get; set; }
}

/// <summary>
/// One DCA cycle (or an imported history batch).
/// Status: applied | undone | imported. Only 'applied' runs can be
/// undone; 'imported' runs are the non-undoable analogue of the old
/// Python 'legacy' run.
/// </summary>
public class Run
{
    public int Id { get; set; }
    public string UserId { get; set; } = default!;
    /// <summary>ISO date (yyyy-MM-dd) the run was applied.</summary>
    public string Date { get; set; } = default!;
    /// <summary>Cash amount for applied runs; null for imported runs.</summary>
    public double? Amount { get; set; }
    /// <summary>Last settled trading session the prices refer to.</summary>
    public string? TradeDate { get; set; }
    /// <summary>Index the run was computed for (applied runs only).</summary>
    public string? IndexId { get; set; }
    public string Status { get; set; } = "applied";
}

public static class RunStatus
{
    public const string Applied = "applied";
    public const string Undone = "undone";
    public const string Imported = "imported";
}

/// <summary>ISO date of today — the format stored in the date columns.</summary>
public static class Dates
{
    public static string Today() => DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
}

/// <summary>
/// One audit-log line. BUY rows are real buys (from applied or imported
/// runs); UNDO rows are mirrors appended when an applied run is undone.
/// </summary>
public class Transaction
{
    public int Id { get; set; }
    public string UserId { get; set; } = default!;
    public int RunId { get; set; }
    public string Date { get; set; } = default!;
    public string Ticker { get; set; } = default!;
    /// <summary>BUY | UNDO</summary>
    public string Action { get; set; } = "BUY";
    public long Shares { get; set; }
    public double Price { get; set; }
    public double Value { get; set; }
    public double Commission { get; set; }
    public Run? Run { get; set; }
}

public static class TransactionAction
{
    public const string Buy = "BUY";
    public const string Undo = "UNDO";
}

/// <summary>
/// Per-ticker composition snapshot of a run: what was held, what was
/// bought, and the resulting vs target weights.
/// </summary>
public class RunReport
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public string Ticker { get; set; } = default!;
    public string? Name { get; set; }
    public double? Weight { get; set; }
    public double? Price { get; set; }
    public int? LotSize { get; set; }
    public int CurrentShares { get; set; }
    public double? CurrentValue { get; set; }
    public double? CostBasis { get; set; }
    public int BuyShares { get; set; }
    public double? BuyValue { get; set; }
    public double? BuyComm { get; set; }
    public double? PostValue { get; set; }
    public double? PostWeight { get; set; }
    public double? TargetW { get; set; }
    public double? WeightDiff { get; set; }
}

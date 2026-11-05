namespace IndexInvestment.Core;

/// <summary>
/// Parses the two import formats (both accept the exact CSVs the old
/// Python app produced):
///
///   positions:      ticker,shares,avg_price
///   transactions:   date,ticker,action,shares,price[,value][,commission]
///
/// `value` and `commission` are optional in transactions (defaults:
/// shares·price and 0). Errors carry the 1-based row number.
/// </summary>
public static class CsvImporter
{
    public static List<ImportPositionRow> ParsePositions(string csv)
    {
        var (header, lines) = Split(csv);
        var col = HeaderIndex(header, "ticker", "shares", "avg_price");
        var rows = new List<ImportPositionRow>();
        foreach (var (line, no) in lines)
        {
            var f = Fields(line, no);
            var ticker = Norm(f[col["ticker"]], "ticker", no);
            var shares = ParseInt(f[col["shares"]], "shares", no);
            var avg = ParseDouble(f[col["avg_price"]], "avg_price", no);
            if (shares <= 0) throw new ValidationError($"positions row {no}: shares must be positive");
            if (avg < 0) throw new ValidationError($"positions row {no}: avg_price must be >= 0");
            rows.Add(new ImportPositionRow(ticker, shares, avg));
        }
        if (rows.Count == 0) throw new ValidationError("positions: no data rows");
        return rows;
    }

    public static List<ImportTransactionRow> ParseTransactions(string csv)
    {
        var (header, lines) = Split(csv);
        var col = HeaderIndex(header, "date", "ticker", "action", "shares", "price");
        var valueIdx = FindColumn(header, "value");
        var commIdx = FindColumn(header, "commission");
        var rows = new List<ImportTransactionRow>();
        foreach (var (line, no) in lines)
        {
            var f = Fields(line, no);
            var date = Norm(f[col["date"]], "date", no);
            if (!date.TryParseMoexDate(out var isoDate))
                throw new ValidationError($"transactions row {no}: bad date '{date}' (expected yyyy-MM-dd)");
            var ticker = Norm(f[col["ticker"]], "ticker", no);
            var action = Norm(f[col["action"]], "action", no).ToUpperInvariant();
            if (action is not ("BUY" or "UNDO"))
                throw new ValidationError($"transactions row {no}: action must be BUY or UNDO");
            var shares = ParseInt(f[col["shares"]], "shares", no);
            var price = ParseDouble(f[col["price"]], "price", no);
            if (shares <= 0) throw new ValidationError($"transactions row {no}: shares must be positive");
            if (price <= 0) throw new ValidationError($"transactions row {no}: price must be positive");
            var value = valueIdx is { } vi
                ? ParseDouble(f[vi], "value", no)
                : price * shares;
            var commission = commIdx is { } ci
                ? (string.IsNullOrWhiteSpace(f[ci]) ? 0.0 : ParseDouble(f[ci], "commission", no))
                : 0.0;
            rows.Add(new ImportTransactionRow(isoDate, ticker, action, shares, price, value, commission));
        }
        if (rows.Count == 0) throw new ValidationError("transactions: no data rows");
        return rows;
    }

    // ============================================================

    private static (List<string> Header, List<(string Line, int No)>) Split(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) throw new ValidationError("Empty input");
        var lines = csv.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0].Split(',').Select(h => h.Trim().ToLowerInvariant()).ToList();
        // 1-based row numbers: the header is row 1
        var data = lines.Skip(1)
            .Select((l, i) => (Line: l, No: i + 2))
            .ToList();
        return (header, data);
    }

    private static Dictionary<string, int> HeaderIndex(List<string> header, params string[] required)
    {
        var idx = header.Select((h, i) => (h, i)).ToDictionary(t => t.h, t => t.i);
        foreach (var r in required)
        {
            if (!idx.ContainsKey(r))
                throw new ValidationError($"Missing required column '{r}' (header: {string.Join(", ", header)})");
        }
        return idx;
    }

    private static int? FindColumn(List<string> header, string name)
        => header.IndexOf(name) >= 0 ? header.IndexOf(name) : null;

    private static string[] Fields(string line, int rowNo)
    {
        var f = line.Split(',');
        if (f.Length == 1 && string.IsNullOrWhiteSpace(f[0]))
            throw new ValidationError($"Row {rowNo}: empty");
        return f.Select(x => x.Trim()).ToArray();
    }

    private static string Norm(string? raw, string what, int rowNo)
    {
        var v = (raw ?? "").Trim();
        if (v.Length == 0 || v.Length > 32)
            throw new ValidationError($"Row {rowNo}: bad {what}");
        if (v.Any(char.IsWhiteSpace))
            throw new ValidationError($"Row {rowNo}: {what} must not contain spaces");
        return v;
    }

    private static long ParseInt(string? raw, string what, int rowNo)
    {
        var v = (raw ?? "").Trim();
        if (!long.TryParse(v, out var n) || n <= 0)
            throw new ValidationError($"Row {rowNo}: {what} must be a positive integer (got '{v}')");
        return n;
    }

    private static double ParseDouble(string? raw, string what, int rowNo)
    {
        var v = (raw ?? "").Replace(",", ".").Trim();
        if (!double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0)
            throw new ValidationError($"Row {rowNo}: {what} must be a positive number (got '{raw}')");
        return n;
    }
}

internal static class DateParsing
{
    /// <summary>Accepts yyyy-MM-dd (also dd.MM.yyyy); returns ISO form.</summary>
    public static bool TryParseMoexDate(this string s, out string iso)
    {
        iso = "";
        var formats = new[] { "yyyy-MM-dd", "dd.MM.yyyy", "dd/MM/yyyy" };
        foreach (var fmt in formats)
        {
            if (DateTime.TryParseExact(s, fmt,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d))
            {
                iso = d.ToString("yyyy-MM-dd");
                return true;
            }
        }
        return false;
    }
}

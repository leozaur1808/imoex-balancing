using IndexInvestment.Core;
using Xunit;

namespace IndexInvestment.Tests;

/// <summary>Parser tests for the two import formats.</summary>
public class CsvImportTests
{
    [Fact]
    public void ParsesPositionsOldFormat()
    {
        var rows = CsvImporter.ParsePositions(
            "ticker,shares,avg_price\nAFKS,200,7.4037\nAFLT,100,32.9121136\n");
        Assert.Equal(new[]
        {
            new ImportPositionRow("AFKS", 200, 7.4037),
            new ImportPositionRow("AFLT", 100, 32.9121136),
        }, rows);
    }

    [Fact]
    public void ParsesTransactionsWithAllColumns()
    {
        var rows = CsvImporter.ParseTransactions(
            "date,ticker,action,shares,price,value,commission\n" +
            "2026-09-24,AFKS,BUY,200,7.4,1480.0,0.74\n");
        Assert.Equal(new ImportTransactionRow("2026-09-24", "AFKS", "BUY", 200, 7.4, 1480.0, 0.74), rows.Single());
    }

    [Fact]
    public void OptionalValueAndCommissionDefault()
    {
        var rows = CsvImporter.ParseTransactions(
            "date,ticker,action,shares,price\n2026-09-24,AFKS,BUY,200,7.4\n");
        var r = rows.Single();
        Assert.Equal(1480.0, r.Value, 9);
        Assert.Equal(0.0, r.Commission, 9);
    }

    [Fact]
    public void AlternativeDateFormatsNormalizeToIso()
    {
        var rows = CsvImporter.ParseTransactions(
            "date,ticker,action,shares,price\n24.09.2026,AFKS,BUY,10,7.4\n");
        Assert.Equal("2026-09-24", rows.Single().Date);
    }

    [Fact]
    public void MissingRequiredColumnRejected()
    {
        Assert.Throws<ValidationError>(() => CsvImporter.ParsePositions("ticker,shares\nAAA,10\n"));
        Assert.Throws<ValidationError>(() =>
            CsvImporter.ParseTransactions("date,ticker,action,shares\n1,2,BUY,3\n"));
    }

    [Theory]
    [InlineData("ticker,shares,avg_price\n,10,5\n")]
    [InlineData("ticker,shares,avg_price\nAAA,x,5\n")]
    [InlineData("ticker,shares,avg_price\nAAA,10,-5\n")]
    public void BadPositionRowsRejected(string csv)
    {
        var ex = Assert.Throws<ValidationError>(() => CsvImporter.ParsePositions(csv));
        Assert.Contains("Row 2", ex.Message);
    }

    [Theory]
    [InlineData("date,ticker,action,shares,price\nBAD,AAA,BUY,10,5\n")]
    [InlineData("date,ticker,action,shares,price\n2026-01-01,AAA,SELL,10,5\n")]
    [InlineData("date,ticker,action,shares,price\n2026-01-01,AAA,BUY,0,5\n")]
    [InlineData("date,ticker,action,shares,price\n2026-01-01,AAA,BUY,10,-5\n")]
    public void BadTransactionRowsRejected(string csv)
    {
        Assert.Throws<ValidationError>(() => CsvImporter.ParseTransactions(csv));
    }

    [Fact]
    public void EmptyInputRejected()
    {
        Assert.Throws<ValidationError>(() => CsvImporter.ParsePositions(""));
        Assert.Throws<ValidationError>(() => CsvImporter.ParseTransactions("   \n"));
    }

    [Fact]
    public void HeaderOnlyRejected()
    {
        Assert.Throws<ValidationError>(() => CsvImporter.ParsePositions("ticker,shares,avg_price\n"));
    }
}

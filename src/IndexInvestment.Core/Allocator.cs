namespace IndexInvestment.Core;

/// <summary>
/// Commission-aware greedy allocator. Given per-ticker target *stock*
/// values (RUB) and a cash budget, it buys whole lots so as to
/// minimize the L1 weight error:
///
///   1. start from the floor number of lots per ticker,
///   2. greedily add one lot at a time to whichever ticker reduces the
///      L1 error the most (at most n+1 lots — one per ticker, plus one
///      safety iteration, mirroring the original implementation),
///   3. optionally spend the remaining cash even at a slight error cost.
///
/// Ported 1:1 from the Python dca.allocate(); the numerics are the
/// same (same tolerances, same first-max tie-breaking).
/// </summary>
public static class Allocator
{
    public sealed record Result(
        long[] BuyLots,
        long[] BuyShares,
        double[] BuyValue,
        double[] BuyComm,
        double[] BuyCost,
        double LeftoverCash);

    public static Result Allocate(
        double[] prices,
        long[] lots,
        double[] targetValues,
        double investableCash,
        double commission)
    {
        var n = prices.Length;
        var lotPrice = new double[n];
        var lotCost = new double[n];
        for (var i = 0; i < n; i++)
        {
            lotPrice[i] = prices[i] * lots[i];
            lotCost[i] = lotPrice[i] * (1.0 + commission);
        }

        // --- start from floor ---
        var numLots = new long[n];
        var cash = investableCash * (1 + commission);
        for (var i = 0; i < n; i++)
        {
            numLots[i] = (long)Math.Floor(targetValues[i] / lotPrice[i]);
            cash -= numLots[i] * lotCost[i];
        }

        // --- phase 1: greedy, add 1 lot at a time to reduce L1 error ---
        for (var iter = 0; iter <= n; iter++)
        {
            var best = -1;
            var bestScore = double.NegativeInfinity;
            for (var i = 0; i < n; i++)
            {
                var cur = numLots[i] * lotPrice[i];
                var gain = Math.Abs(cur - targetValues[i])
                          - Math.Abs(cur + lotPrice[i] - targetValues[i]);
                var score = lotCost[i] <= cash + 1e-6 ? gain : double.NegativeInfinity;
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best < 0 || bestScore <= 1e-9) break;
            numLots[best]++;
            cash -= lotCost[best];
        }

        // --- phase 2: spend remaining cash even at slight error cost ---
        var minLotCost = lotCost.Min();
        while (cash + 1e-6 >= minLotCost)
        {
            var best = -1;
            var bestScore = double.NegativeInfinity;
            for (var i = 0; i < n; i++)
            {
                var cur = numLots[i] * lotPrice[i];
                var penalty = Math.Abs(cur + lotPrice[i] - targetValues[i])
                            - Math.Abs(cur - targetValues[i]);
                var score = lotCost[i] <= cash + 1e-6 ? -penalty : double.NegativeInfinity;
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best < 0) break;   // nothing affordable
            numLots[best]++;
            cash -= lotCost[best];
        }

        var buyShares = new long[n];
        var buyValue = new double[n];
        var buyComm = new double[n];
        var buyCost = new double[n];
        for (var i = 0; i < n; i++)
        {
            buyShares[i] = numLots[i] * lots[i];
            buyValue[i] = buyShares[i] * prices[i];
            buyComm[i] = buyValue[i] * commission;
            buyCost[i] = buyValue[i] + buyComm[i];
        }
        return new Result(numLots, buyShares, buyValue, buyComm, buyCost, cash);
    }
}

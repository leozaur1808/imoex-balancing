using IndexInvestment.Core;
using Xunit;

namespace IndexInvestment.Tests;

/// <summary>Unit tests for the greedy allocator (port of the Python suite).</summary>
public class AllocatorTests
{
    private static (long[] Shares, double Leftover) Allocate(double[] prices, long[] lots,
        double[] targets, double cash, double commission)
    {
        var res = Allocator.Allocate(prices, lots, targets, cash, commission);
        return (res.BuyShares, res.LeftoverCash);
    }

    [Fact]
    public void FloorIsTheStartingPoint()
    {
        var (shares, leftover) = Allocate(new[] { 10.0, 20.0 }, new[] { 1L, 1L },
            new[] { 500.0, 500.0 }, 1000.0, 0.0);
        Assert.Equal(new long[] { 50, 25 }, shares);
        Assert.Equal(0.0, leftover, 9);
    }

    [Fact]
    public void GreedyReducesL1Error()
    {
        var (shares, _) = Allocate(new[] { 10.0, 20.0 }, new[] { 1L, 1L },
            new[] { 605.0, 395.0 }, 1000.0, 0.0);
        // floor is [60, 19] (980 RUB, L1 error 20); the greedy phase buys one
        // BBB lot (20 RUB) which cuts the error to 10
        Assert.Equal(new long[] { 60, 20 }, shares);
        var actual = new[] { 60.0 * 10.0, 20.0 * 20.0 };
        var target = new[] { 605.0, 395.0 };
        var l1 = 0.0;
        for (var i = 0; i < 2; i++) l1 += Math.Abs(actual[i] - target[i]);
        Assert.Equal(10.0, l1, 9);
    }

    [Fact]
    public void Phase2SpendsLeftoverEvenAtErrorCost()
    {
        var (shares, leftover) = Allocate(new[] { 10.0, 20.0 }, new[] { 1L, 1L },
            new[] { 550.0, 450.0 }, 1000.0, 0.0);
        // phase 1 stops: no affordable lot reduces error. phase 2 spends the
        // remaining 10 RUB on the only affordable lot despite the +L1 cost
        Assert.Equal(new long[] { 56, 22 }, shares);
        var res = Allocator.Allocate(new[] { 10.0, 20.0 }, new[] { 1L, 1L },
            new[] { 550.0, 450.0 }, 1000.0, 0.0);
        Assert.Equal(1000.0, res.BuyValue.Sum(), 9);
        Assert.Equal(0.0, leftover, 9);
    }

    [Fact]
    public void BudgetNeverExceeded()
    {
        var (shares, leftover) = Allocate(new[] { 7.0, 13.0 }, new[] { 1L, 1L },
            new[] { 90.0, 10.0 }, 100.0, 0.0);
        var res = Allocator.Allocate(new[] { 7.0, 13.0 }, new[] { 1L, 1L },
            new[] { 90.0, 10.0 }, 100.0, 0.0);
        Assert.True(res.BuyValue.Sum() <= 100.0 + 1e-9);
        Assert.True(leftover >= -1e-6);
        // hand-checked: floor [12, 0], greedy buys one BBB lot (improves error
        // from 16 to 9), 3 RUB left over and unaffordable in phase 2
        Assert.Equal(new long[] { 12, 1 }, shares);
        Assert.Equal(3.0, leftover, 9);
    }

    [Fact]
    public void CommissionBudgetNeverExceeded()
    {
        var res = Allocator.Allocate(new[] { 10.0, 20.0 }, new[] { 1L, 1L },
            new[] { 600.0, 400.0 }, 1000.0, 0.00045);
        // the caller passes cash net of commission, so total cost may reach
        // investable * (1 + commission)
        Assert.True(res.BuyCost.Sum() <= 1000.0 * 1.00045 + 1e-6);
        Assert.True(res.LeftoverCash >= -1e-6);
    }

    [Fact]
    public void NoAffordableLots()
    {
        var (shares, leftover) = Allocate(new[] { 100.0, 200.0 }, new[] { 1L, 1L },
            new[] { 30.0, 20.0 }, 50.0, 0.0);
        Assert.Equal(new long[] { 0, 0 }, shares);
        Assert.Equal(50.0, leftover, 9);
    }
}

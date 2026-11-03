"""Unit tests for the greedy allocator (dca.allocate)."""

import numpy as np
import pandas as pd
import pytest

from dca import allocate


def make_df(prices, lots):
    return pd.DataFrame({'price_rub': prices, 'lot_size': lots})


def test_floor_is_the_starting_point():
    df = make_df([10.0, 20.0], [1, 1])
    out, leftover = allocate(df, 1000.0, np.array([500.0, 500.0]), commission=0.0)
    assert out['buy_shares'].tolist() == [50, 25]
    assert leftover == pytest.approx(0.0)


def test_greedy_reduces_l1_error():
    df = make_df([10.0, 20.0], [1, 1])
    out, _ = allocate(df, 1000.0, np.array([605.0, 395.0]), commission=0.0)
    # floor is [60, 19] (980 RUB, L1 error 20); the greedy phase buys one
    # BBB lot (20 RUB) which cuts the error to 10
    assert out['buy_shares'].tolist() == [60, 20]
    post = np.array([60, 20]) * np.array([10.0, 20.0])
    assert float(np.abs(post - np.array([605.0, 395.0])).sum()) == pytest.approx(10.0)


def test_phase2_spends_leftover_even_at_error_cost():
    df = make_df([10.0, 20.0], [1, 1])
    out, leftover = allocate(df, 1000.0, np.array([550.0, 450.0]), commission=0.0)
    # phase 1 stops: no affordable lot reduces error. phase 2 spends the
    # remaining 10 RUB on the only affordable lot despite the +L1 cost
    assert out['buy_shares'].tolist() == [56, 22]
    assert float(out['buy_value'].sum()) == pytest.approx(1000.0)
    assert leftover == pytest.approx(0.0)


def test_budget_never_exceeded():
    df = make_df([7.0, 13.0], [1, 1])
    out, leftover = allocate(df, 100.0, np.array([90.0, 10.0]), commission=0.0)
    assert float(out['buy_value'].sum()) <= 100.0 + 1e-9
    assert leftover >= -1e-6
    # hand-checked: floor [12, 0], greedy buys one BBB lot (improves error
    # from 16 to 9), 3 RUB left over and unaffordable in phase 2
    assert out['buy_shares'].tolist() == [12, 1]
    assert leftover == pytest.approx(3.0)


def test_commission_budget_never_exceeded():
    df = make_df([10.0, 20.0], [1, 1])
    out, leftover = allocate(df, 1000.0, np.array([600.0, 400.0]), commission=0.00045)
    # the caller passes cash net of commission, so total cost may reach
    # investable * (1 + commission)
    assert float(out['buy_cost'].sum()) <= 1000.0 * 1.00045 + 1e-6
    assert leftover >= -1e-6


def test_no_affordable_lots():
    df = make_df([100.0, 200.0], [1, 1])
    out, leftover = allocate(df, 50.0, np.array([30.0, 20.0]), commission=0.0)
    assert out['buy_shares'].tolist() == [0, 0]
    assert leftover == pytest.approx(50.0)

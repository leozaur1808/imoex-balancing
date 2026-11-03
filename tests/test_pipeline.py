"""
End-to-end tests for dca.run / dca.undo with a mocked market and a temp
DB (see conftest). Market: AAA 60% @ 10.0 (lot 10), BBB 40% @ 20.0
(lot 10); lot costs 100 / 200 RUB incl. commission headroom.
"""

import pytest

import dca
import db


def test_apply_run_math(market):
    res = dca.run(1000.0, dry_run=False)
    assert res['dry_run'] is False
    assert not res['carried']

    pos = {p['ticker']: p for p in dca.get_positions()}
    # floor [5, 1] lots + one greedy BBB lot: AAA 50 shares, BBB 20 shares
    assert pos['AAA']['shares'] == 50
    assert pos['BBB']['shares'] == 20
    # avg_price includes commission: price * (1 + 0.00045)
    assert pos['AAA']['avg_price'] == pytest.approx(10.0 * 1.00045)
    assert pos['BBB']['avg_price'] == pytest.approx(20.0 * 1.00045)
    assert res['stock_bought'] == pytest.approx(900.0)
    assert res['commission_paid'] == pytest.approx(900.0 * 0.00045)
    assert res['leftover'] == pytest.approx(1000.0 - 900.0 - 900.0 * 0.00045)

    # audit log: one BUY row per ticker
    txs = dca.get_transactions()
    assert [(t['ticker'], t['action']) for t in txs] == [('BBB', 'BUY'), ('AAA', 'BUY')]

    # report stored and readable
    report = dca.get_report()
    assert len(report) == 2
    aaa = next(r for r in report if r['ticker'] == 'AAA')
    assert aaa['buy_shares'] == 50
    assert aaa['shortnames'] == 'Aa Corp'
    assert aaa['price_rub'] == pytest.approx(10.0)
    assert aaa['lot_size'] == 10


def test_apply_then_undo_restores_exact_state(market):
    before = dca.get_positions()
    res = dca.run(1000.0, dry_run=False)
    assert len(res['buys']) == 2
    assert {p['ticker'] for p in dca.get_positions()} == {'AAA', 'BBB'}

    undo_res = dca.undo()
    assert undo_res['n_reversed'] == 2

    # positions byte-identical to the pre-run state
    assert dca.get_positions() == before

    # audit log kept both the BUYs and the mirror UNDOs
    actions = [t['action'] for t in dca.get_transactions()]
    assert actions.count('BUY') == 2
    assert actions.count('UNDO') == 2

    # the run is marked undone and is no longer the undo target
    assert dca.latest_undoable_run() is None
    with pytest.raises(RuntimeError, match='Nothing to undo'):
        dca.undo()


def test_undo_of_run_that_extended_existing_position(market):
    # pre-existing AAA position that the run adds to
    conn = db.connect()
    conn.execute(
        "INSERT INTO positions (ticker, shares, avg_price) "
        "VALUES ('AAA', 100, 8.0)")
    conn.close()

    res = dca.run(1000.0, dry_run=False)
    assert res['buys']  # something bought
    pos = {p['ticker']: p for p in dca.get_positions()}
    assert pos['AAA']['shares'] > 100

    dca.undo()
    after = {p['ticker']: p for p in dca.get_positions()}
    assert after['AAA']['shares'] == 100
    # average cost restored exactly (undo subtracts value + commission)
    assert after['AAA']['avg_price'] == pytest.approx(8.0)


def test_dry_run_writes_nothing(market):
    res = dca.run(1000.0, dry_run=True)
    assert res['dry_run'] is True
    assert len(res['buys']) == 2
    assert dca.get_positions() == []
    assert dca.get_transactions() == []
    assert dca.latest_undoable_run() is None


def test_carry_under_threshold(market):
    # 50 RUB buys less than one lot of the cheapest ticker (100 RUB)
    res = dca.run(50.0, dry_run=True)
    assert res['carried'] is True
    assert res['buys'] == []
    assert res['leftover'] == pytest.approx(50.0)


def test_stale_price_rejected(monkeypatch):
    import dca as d

    def fake_composition(indexid='IMOEX', limit=100):
        import pandas as pd
        return pd.DataFrame({
            'ticker': ['AAA'], 'shortnames': ['Aa'], 'weight': [100.0],
            'trade_session_date': ['2026-11-02'],
        })

    def stale_board():
        return {'AAA': {'SECID': 'AAA', 'LOTSIZE': 10,
                        'PREVLEGALCLOSEPRICE': 10.0, 'PREVDATE': '2026-09-01'}}

    monkeypatch.setattr(d, 'get_index_composition', fake_composition)
    monkeypatch.setattr(d, 'get_board_securities', stale_board)
    d.invalidate_snapshot()
    with pytest.raises(RuntimeError, match='last session'):
        d.get_snapshot(max_age=0)


def test_missing_board_ticker_rejected(monkeypatch):
    import dca as d

    def fake_composition(indexid='IMOEX', limit=100):
        import pandas as pd
        return pd.DataFrame({
            'ticker': ['AAA'], 'shortnames': ['Aa'], 'weight': [100.0],
            'trade_session_date': ['2026-11-02'],
        })

    monkeypatch.setattr(d, 'get_index_composition', fake_composition)
    monkeypatch.setattr(d, 'get_board_securities', lambda: {})
    d.invalidate_snapshot()
    with pytest.raises(RuntimeError, match='not found on TQBR board'):
        d.get_snapshot(max_age=0)


def test_overlapping_run_rejected(monkeypatch):
    # hold the run lock and verify run() refuses instead of waiting
    import threading
    import dca as d
    lock = threading.Lock()
    lock.acquire()
    monkeypatch.setattr(d, '_RUN_LOCK', lock)
    with pytest.raises(RuntimeError, match='in progress'):
        d.run(1000.0, dry_run=True)

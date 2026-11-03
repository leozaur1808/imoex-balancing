"""Tests for the SQLite layer: legacy import, undo edge cases, exports."""

import pytest

import db


def test_migration_imports_csvs_once(tmp_path, monkeypatch):
    (tmp_path / 'positions.csv').write_text(
        "ticker,shares,avg_price\nAAA,100,5.0\nBBB,50,10.0\n")
    (tmp_path / 'transactions.csv').write_text(
        "date,ticker,action,shares,price,value,commission\n"
        "2026-01-01,AAA,BUY,100,5.0,500.0,0.225\n")
    monkeypatch.setattr(db, 'LEGACY_POSITIONS_CSV', tmp_path / 'positions.csv')
    monkeypatch.setattr(db, 'LEGACY_TRANSACTIONS_CSV', tmp_path / 'transactions.csv')
    db.init()

    assert db.get_positions() == [
        {'ticker': 'AAA', 'shares': 100, 'avg_price': 5.0},
        {'ticker': 'BBB', 'shares': 50, 'avg_price': 10.0},
    ]
    assert len(db.get_transactions()) == 1
    # re-init is a no-op (no double import)
    db.init()
    assert len(db.get_transactions()) == 1


def test_migration_imports_legacy_report(tmp_path, monkeypatch):
    (tmp_path / 'composition.csv').write_text(
        "ticker,shortnames,weight,price_rub,lot_size,current_shares,current_value,"
        "buy_shares,buy_value,buy_comm,post_value,post_weight,target_w,weight_diff\n"
        "AAA,Aa Corp,60.0,10.0,10,100,1000.0,10,100.0,0.045,1100.0,55.0,60.0,-5.0\n")
    monkeypatch.setattr(db, 'LEGACY_REPORT_CSV', tmp_path / 'composition.csv')
    db.init()

    report = db.get_report()
    assert len(report) == 1
    assert report[0]['ticker'] == 'AAA'
    assert report[0]['shortnames'] == 'Aa Corp'
    assert report[0]['lot_size'] == 10


def test_legacy_run_cannot_be_undone():
    conn = db.connect()
    conn.execute(
        "INSERT INTO runs (date, amount, trade_date, status) "
        "VALUES ('2026-09-24', NULL, NULL, 'legacy')")
    conn.close()
    assert db.latest_undoable_run() is None


def test_undo_requires_matching_position():
    conn = db.connect()
    conn.execute(
        "INSERT INTO runs (date, amount, trade_date, status) "
        "VALUES ('2026-11-02', 1000, '2026-11-02', 'applied')")
    run_id = conn.execute("SELECT last_insert_rowid()").fetchone()[0]
    conn.execute(
        "INSERT INTO transactions (run_id, date, ticker, action, shares, "
        "price, value, commission) VALUES (?,?,?, 'BUY', ?, ?, ?, ?)",
        (run_id, '2026-11-02', 'AAA', 10, 10.0, 100.0, 0.045))
    # no position row for AAA
    conn.close()
    with pytest.raises(RuntimeError, match='missing'):
        db.undo_run(run_id, '2026-11-03')


def test_undo_oversold_position_fails():
    conn = db.connect()
    conn.execute(
        "INSERT INTO runs (date, amount, trade_date, status) "
        "VALUES ('2026-11-02', 1000, '2026-11-02', 'applied')")
    run_id = conn.execute("SELECT last_insert_rowid()").fetchone()[0]
    conn.execute(
        "INSERT INTO transactions (run_id, date, ticker, action, shares, "
        "price, value, commission) VALUES (?,?,?, 'BUY', ?, ?, ?, ?)",
        (run_id, '2026-11-02', 'AAA', 10, 10.0, 100.0, 0.045))
    conn.execute(
        "INSERT INTO positions (ticker, shares, avg_price) VALUES ('AAA', 5, 10.0)")
    conn.close()
    with pytest.raises(RuntimeError, match='has 5 shares'):
        db.undo_run(run_id, '2026-11-03')


def test_positions_csv_roundtrip():
    conn = db.connect()
    conn.execute(
        "INSERT INTO positions (ticker, shares, avg_price) VALUES ('AAA', 100, 5.125)")
    conn.close()
    csv = db.positions_csv()
    assert csv.splitlines() == ['ticker,shares,avg_price', 'AAA,100,5.125']


def test_empty_exports_still_have_headers():
    assert db.positions_csv().startswith('ticker,shares,avg_price')
    assert db.transactions_csv().startswith('date,ticker,action')
    assert db.composition_csv().startswith('ticker,shortnames')

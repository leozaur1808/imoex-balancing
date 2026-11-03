import pytest

import dca
import db


@pytest.fixture(autouse=True)
def fresh_db(tmp_path, monkeypatch):
    """
    Point the DB (and the legacy-CSV sources) at a temp dir so tests
    never touch the real data/. Also drop the market snapshot cache.
    """
    monkeypatch.setattr(db, 'DB_PATH', tmp_path / 'test.db')
    monkeypatch.setattr(db, 'LEGACY_POSITIONS_CSV', tmp_path / 'positions.csv')
    monkeypatch.setattr(db, 'LEGACY_TRANSACTIONS_CSV', tmp_path / 'transactions.csv')
    monkeypatch.setattr(db, 'LEGACY_REPORT_CSV', tmp_path / 'composition.csv')
    db.init()
    dca.invalidate_snapshot()
    yield


@pytest.fixture
def market(monkeypatch):
    """
    Two-ticker index: AAA (60%, price 10, lot 10) and BBB (40%, price 20,
    lot 10). No real HTTP in tests.
    """
    import pandas as pd

    def fake_composition(indexid='IMOEX', limit=100):
        return pd.DataFrame({
            'ticker':               ['AAA', 'BBB'],
            'shortnames':           ['Aa Corp', 'Bb Corp'],
            'weight':               [60.0, 40.0],
            'trade_session_date':   ['2026-11-02', '2026-11-02'],
        })

    def fake_board():
        return {
            'AAA': {'SECID': 'AAA', 'LOTSIZE': 10,
                    'PREVLEGALCLOSEPRICE': 10.0, 'PREVDATE': '2026-11-02'},
            'BBB': {'SECID': 'BBB', 'LOTSIZE': 10,
                    'PREVLEGALCLOSEPRICE': 20.0, 'PREVDATE': '2026-11-02'},
        }

    monkeypatch.setattr(dca, 'get_index_composition', fake_composition)
    monkeypatch.setattr(dca, 'get_board_securities', fake_board)
    return {}

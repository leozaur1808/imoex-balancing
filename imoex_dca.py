"""
imoex_dca.py — CLI wrapper around dca.run() (bi-weekly DCA into IMOEX).

Usage:
    python imoex_dca.py                  # interactive: prompts for amount
    python imoex_dca.py --dry-run [N]    # preview without writing files
    python imoex_dca.py --undo           # reverse the last applied run

All logic lives in dca.py; this module only handles I/O for the terminal.
"""

from __future__ import annotations
import logging
import sys
from datetime import datetime
import pandas as pd
import dca
import db

def _fmt_table(rows: list[dict]) -> str:
    if not rows:
        return "(none)"
    df = pd.DataFrame(rows).rename(columns={
        'ticker': 'ticker', 'name': 'name', 'weight': 'idx_w%',
        'shares': 'shares', 'price': 'price',
        'value': 'value_rub', 'commission': 'comm_rub',
    })
    return df.to_string(index=False)

def _undo() -> None:
    try:
        res = dca.undo()
    except RuntimeError as e:
        print(f"✗ {e}")
        sys.exit(1)
    print(f"✓ Run of {res['date']} ({res['amount']:,.2f} RUB) undone —")
    print(f"  positions restored to their pre-run state "
          f"({res['n_reversed']} buys reversed).")

def main() -> None:
    logging.basicConfig(level=logging.INFO,
                        format="%(levelname)s %(name)s: %(message)s")
    db.init()

    args = sys.argv[1:]
    if args and args[0] == "--undo":
        _undo()
        return
    if args and args[0] == "--dry-run":
        amount = float(args[1]) if len(args) > 1 else 10_000
        dry_run = True
    else:
        amount = float(input("Amount to invest this period (RUB): "))
        dry_run = False

    print(f"\n{'='*60}")
    print(f"  IMOEX DCA run — {datetime.now():%Y-%m-%d %H:%M:%S}")
    print(f"  New cash: {amount:,.2f} RUB")
    print(f"{'='*60}\n")

    try:
        res = dca.run(amount, dry_run=dry_run)
    except Exception as e:
        print(f"✗ Run failed: {e}")
        sys.exit(1)

    print(f"Current portfolio value : {res['portfolio_value']:,.2f} RUB")
    print(f"After contribution      : {res['total_after']:,.2f} RUB\n")

    if res['carried'] and res['carry_reason']:
        print(f"⚠ {res['carry_reason']}\n")

    print("─── BUY ORDERS ───────────────────────────────────────")
    print(_fmt_table(res['buys']))

    print(f"\nStock value bought : {res['stock_bought']:>15,.2f} RUB")
    print(f"Commission paid    : {res['commission_paid']:>15,.2f} RUB")
    print(f"Total spent        : {res['total_spent']:>15,.2f} RUB")
    print(f"Leftover carried   : {res['leftover']:>15,.2f} RUB "
          f"({res['leftover']/amount*100:.3f}%)")

    if res['l1_error'] is not None:
        print(f"Post-trade L1 weight error : {res['l1_error']:.4f}% "
              f"(half-sum, 0 = perfect)")

    if dry_run:
        print("\n[dry-run] no files written.")
        return

    print(f"\n✓ Run #{res['run_id']} applied    (undo anytime: python imoex_dca.py --undo)")
    print(f"✓ Data stored        → {db.DB_PATH}")
    print(f"✓ Rows processed     : {res['rows']}\n")

if __name__ == "__main__":
    main()

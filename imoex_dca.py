"""
imoex_dca.py — CLI wrapper around dca.run() (bi-weekly DCA into IMOEX).

Usage:
    python imoex_dca.py                  # interactive: prompts for amount
    python imoex_dca.py --dry-run [N]    # preview without writing files

All logic lives in dca.py; this module only handles I/O for the terminal.
"""

from __future__ import annotations
import sys
from datetime import datetime
import pandas as pd
import dca

def _fmt_table(rows: list[dict]) -> str:
    if not rows:
        return "(none)"
    df = pd.DataFrame(rows).rename(columns={
        'ticker': 'ticker', 'name': 'name', 'weight': 'idx_w%',
        'shares': 'shares', 'price': 'price',
        'value': 'value_rub', 'commission': 'comm_rub',
    })
    return df.to_string(index=False)

def main() -> None:
    if len(sys.argv) > 1 and sys.argv[1] == "--dry-run":
        amount = float(sys.argv[2]) if len(sys.argv) > 2 else 10_000
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

    print(f"\n✓ Positions saved    → {dca.POSITIONS_FILE}")
    print(f"✓ Transactions log   → {dca.TRANSACTIONS_FILE}")
    print(f"✓ Report             → {dca.REPORT_FILE}")
    print(f"✓ Rows processed     : {res['rows']}\n")

if __name__ == "__main__":
    main()

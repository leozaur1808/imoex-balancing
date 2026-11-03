"""
main.py — IMOEX DCA webapp (FastAPI).

Run:
    .venv/bin/uvicorn main:app --port 8000

Binds to 127.0.0.1 by default — keep it that way, the app contains
personal financial data.
"""

from __future__ import annotations
import logging
import secrets

from fastapi import Depends, FastAPI, HTTPException, Request
from fastapi.responses import RedirectResponse, Response
from fastapi.staticfiles import StaticFiles
from fastapi.templating import Jinja2Templates
from pydantic import BaseModel, Field
from starlette.middleware.sessions import SessionMiddleware

import auth
import dca
import db
from pathlib import Path

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(name)s %(levelname)s: %(message)s",
)
logger = logging.getLogger("imoex.web")

BASE_DIR = Path(__file__).parent

app = FastAPI(title="IMOEX DCA", docs_url=None, redoc_url=None)

# Session secret: persisted once a password exists (survives restarts),
# random per launch until then (no sessions are meaningful pre-setup).
app.add_middleware(
    SessionMiddleware,
    secret_key=auth.get_secret() or secrets.token_hex(32),
    max_age=7 * 24 * 3600,  # sessions expire after a week
)

db.init()  # create the SQLite schema + one-time legacy CSV import

templates = Jinja2Templates(directory=str(BASE_DIR / "templates"))
app.mount("/static", StaticFiles(directory=str(BASE_DIR / "static")), name="static")

# ============================================================
# Health (unauthenticated — used by local monitoring/polling)
# ============================================================
@app.get("/health")
def health():
    return {"ok": True}

# ============================================================
# Auth routes
# ============================================================
@app.get("/")
def index(request: Request):
    if not auth.is_authed(request):
        return RedirectResponse(url="/login", status_code=302)
    return templates.TemplateResponse(request, "index.html")

@app.get("/login", response_class=templates.TemplateResponse)
def login_page(request: Request):
    if request.session.get("authed"):
        return templates.TemplateResponse(request, "index.html")
    return templates.TemplateResponse(
        request, "login.html", {"mode": "setup" if not auth.has_password() else "login"}
    )

class PasswordIn(BaseModel):
    password: str = Field(min_length=4)

@app.post("/api/setup")
def setup(payload: PasswordIn, request: Request):
    """First run: create the password and start a session."""
    try:
        auth.set_password(payload.password)
    except ValueError:
        raise HTTPException(status_code=409, detail="Password is already set")
    request.session["authed"] = True
    dca.invalidate_snapshot()  # first load after login fetches fresh data
    return {"ok": True}

@app.post("/api/login")
def login(payload: PasswordIn, request: Request):
    auth.check_lockout()
    if not auth.verify_password(payload.password):
        auth.record_failure()
        raise HTTPException(status_code=401, detail="Wrong password")
    auth.reset_failures()
    request.session["authed"] = True
    dca.invalidate_snapshot()  # refresh market data once per login
    return {"ok": True}

@app.post("/api/logout")
def logout(request: Request):
    request.session.clear()
    return {"ok": True}

# ============================================================
# Data endpoints (all authenticated)
# ============================================================
@app.get("/api/portfolio")
def api_portfolio(_: None = Depends(auth.require_auth)):
    return dca.get_portfolio()

@app.get("/api/positions")
def api_positions(_: None = Depends(auth.require_auth)):
    return {"rows": dca.get_positions()}

@app.get("/api/transactions")
def api_transactions(_: None = Depends(auth.require_auth)):
    return {"rows": dca.get_transactions()}

@app.get("/api/report")
def api_report(_: None = Depends(auth.require_auth)):
    return {"rows": dca.get_report()}

@app.post("/api/refresh")
def api_refresh(_: None = Depends(auth.require_auth)):
    """Drop the market-data cache; the next dashboard load refetches."""
    dca.invalidate_snapshot()
    return {"ok": True}

class RunIn(BaseModel):
    amount: float = Field(gt=0)
    dry_run: bool = False

@app.post("/api/run")
def api_run(payload: RunIn, _: None = Depends(auth.require_auth)):
    """
    Synchronous endpoint on purpose: FastAPI runs it in the threadpool,
    so the MOEX fetches don't block the event loop.

    Previews reuse the cached market snapshot (instant); an applied
    run always computes on a fresh fetch.
    """
    try:
        max_age = dca.SNAPSHOT_TTL if payload.dry_run else 0.0
        return dca.run(payload.amount, dry_run=payload.dry_run, max_age=max_age)
    except RuntimeError as e:
        raise HTTPException(status_code=409, detail=str(e))
    except Exception as e:
        logger.exception("Run failed")
        raise HTTPException(status_code=502, detail=f"Run failed: {e}")

@app.get("/api/undo-target")
def api_undo_target(_: None = Depends(auth.require_auth)):
    """The latest applied run the UI can offer to undo (or null)."""
    return {"run": dca.latest_undoable_run()}

@app.post("/api/undo")
def api_undo(_: None = Depends(auth.require_auth)):
    try:
        return dca.undo()
    except RuntimeError as e:
        raise HTTPException(status_code=409, detail=str(e))
    except Exception as e:
        logger.exception("Undo failed")
        raise HTTPException(status_code=500, detail=f"Undo failed: {e}")

# ============================================================
# CSV downloads — synthesized from the DB on demand
# ============================================================
_DOWNLOADS = {
    "positions":    (db.positions_csv, "positions.csv"),
    "transactions": (db.transactions_csv, "transactions.csv"),
    "composition":  (db.composition_csv, "composition.csv"),
}

@app.get("/download/{name}")
def download(name: str, _: None = Depends(auth.require_auth)):
    entry = _DOWNLOADS.get(name)
    if entry is None:
        raise HTTPException(status_code=404, detail="File not found")
    make_csv, filename = entry
    return Response(
        make_csv(),
        media_type="text/csv",
        headers={"Content-Disposition": f'attachment; filename="{filename}"'},
    )

"""
auth.py — Single-user password auth for the webapp.

- Password stored as a PBKDF2-SHA256 hash (stdlib, no passlib) in
  data/auth.json (gitignored), file mode 0600.
- Session cookie signed with a secret persisted in the same file, so
  a server restart does not force a re-login.
- Simple in-memory lockout after repeated failed logins.
"""

from __future__ import annotations
import hashlib
import hmac
import json
import os
import secrets
import time
from pathlib import Path

from fastapi import HTTPException, Request

AUTH_FILE = Path("data") / "auth.json"

PBKDF2_ITERATIONS = 600_000
SALT_BYTES        = 16
MAX_ATTEMPTS      = 5
LOCKOUT_SECONDS   = 30

# in-memory lockout state (single-user local app)
_failed_attempts = 0
_lockout_until   = 0.0

# ============================================================
# Credential storage
# ============================================================
def _load() -> dict | None:
    if not AUTH_FILE.exists():
        return None
    try:
        with open(AUTH_FILE) as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return None

def _save(data: dict) -> None:
    AUTH_FILE.parent.mkdir(parents=True, exist_ok=True)
    tmp = AUTH_FILE.with_suffix(".tmp")
    with open(tmp, "w") as f:
        json.dump(data, f)
    os.replace(tmp, AUTH_FILE)
    os.chmod(AUTH_FILE, 0o600)

def has_password() -> bool:
    data = _load()
    return bool(data and data.get("hash"))

def get_secret() -> str | None:
    """Session-signing secret; None until a password has been set."""
    data = _load()
    if data and data.get("secret"):
        return data["secret"]
    return None

def set_password(password: str) -> None:
    """Create the password on first run (refuses to overwrite)."""
    if has_password():
        raise ValueError("Password is already set")
    salt = secrets.token_bytes(SALT_BYTES)
    digest = hashlib.pbkdf2_hmac("sha256", password.encode(), salt, PBKDF2_ITERATIONS)
    _save({
        "salt":       salt.hex(),
        "hash":       digest.hex(),
        "iterations": PBKDF2_ITERATIONS,
        "secret":     secrets.token_hex(32),
    })

def verify_password(password: str) -> bool:
    data = _load()
    if not data or not data.get("hash"):
        return False
    salt = bytes.fromhex(data["salt"])
    digest = hashlib.pbkdf2_hmac(
        "sha256", password.encode(), salt, int(data.get("iterations", PBKDF2_ITERATIONS))
    )
    return hmac.compare_digest(digest.hex(), data["hash"])

# ============================================================
# Lockout
# ============================================================
def check_lockout() -> None:
    global _failed_attempts
    if _failed_attempts >= MAX_ATTEMPTS and time.time() < _lockout_until:
        wait = int(_lockout_until - time.time()) + 1
        raise HTTPException(
            status_code=429,
            detail=f"Too many failed attempts. Try again in {wait}s.",
        )
    if time.time() >= _lockout_until:
        _failed_attempts = 0

def record_failure() -> None:
    global _failed_attempts, _lockout_until
    _failed_attempts += 1
    if _failed_attempts >= MAX_ATTEMPTS:
        _lockout_until = time.time() + LOCKOUT_SECONDS

def reset_failures() -> None:
    global _failed_attempts, _lockout_until
    _failed_attempts = 0
    _lockout_until   = 0.0

# ============================================================
# FastAPI dependency
# ============================================================
def require_auth(request: Request) -> None:
    """Guard for API/download routes: 401 when there is no valid session."""
    if request.session.get("authed"):
        return
    raise HTTPException(status_code=401, detail="Not authenticated")

def is_authed(request: Request) -> bool:
    return bool(request.session.get("authed"))

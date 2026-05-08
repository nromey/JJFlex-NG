"""JJ Flex crash + feedback receiver.

Accepts user-consented uploads of crash bundles and feedback bundles. Persists
each bundle byte-for-byte alongside a JSON sidecar; indexes in SQLite for
triage queries. The bundle is opaque -- the receiver does NOT extract or
inspect it.

Storage truth lives on disk (zip + sidecar JSON). SQLite is a query index
that can be rebuilt from sidecar JSONs (rebuild utility is future scope;
the sidecar contains everything needed). See
docs/planning/active/rarbox-claude-F3-G-briefing.md.
"""
from __future__ import annotations

import hashlib
import json
import logging
import sqlite3
import sys
import uuid
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional

from fastapi import Depends, FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.responses import JSONResponse

STORAGE_ROOT = Path("/var/lib/jjflex-receiver")
INDEX_DB = STORAGE_ROOT / "index.db"
MAX_BODY_BYTES = 50 * 1024 * 1024
ZIP_MAGIC = b"PK\x03\x04"
SCHEMA_VERSION = 1


class _JsonFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        payload = {
            "ts": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "level": record.levelname.lower(),
            "event": record.getMessage(),
        }
        fields = getattr(record, "fields", None)
        if isinstance(fields, dict):
            payload.update(fields)
        return json.dumps(payload, separators=(",", ":"))


def _init_logger() -> logging.Logger:
    lg = logging.getLogger("jjflex_receiver")
    lg.setLevel(logging.INFO)
    if not lg.handlers:
        handler = logging.StreamHandler(sys.stdout)
        handler.setFormatter(_JsonFormatter())
        lg.addHandler(handler)
        lg.propagate = False
    return lg


_log = _init_logger()


def _emit(level: str, event: str, **fields) -> None:
    record = _log.makeRecord(
        _log.name, getattr(logging, level.upper()), "", 0, event, None, None
    )
    record.fields = fields
    _log.handle(record)


def _init_db() -> None:
    STORAGE_ROOT.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(INDEX_DB)
    try:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS bundles (
                uuid TEXT PRIMARY KEY,
                received_at TEXT NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('crashes', 'feedback')),
                bundle_path TEXT NOT NULL,
                metadata_path TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                dedup_hash TEXT NOT NULL,
                app_version TEXT,
                user_agent TEXT,
                triage_status TEXT NOT NULL DEFAULT 'untriaged'
                    CHECK (triage_status IN ('untriaged', 'in_progress', 'triaged', 'duplicate', 'invalid'))
            );
            CREATE INDEX IF NOT EXISTS idx_bundles_received_at ON bundles(received_at);
            CREATE INDEX IF NOT EXISTS idx_bundles_dedup_hash ON bundles(dedup_hash);
            CREATE INDEX IF NOT EXISTS idx_bundles_triage_status ON bundles(triage_status);
            CREATE INDEX IF NOT EXISTS idx_bundles_kind ON bundles(kind);
            """
        )
        conn.execute(f"PRAGMA user_version = {SCHEMA_VERSION}")
        conn.commit()
    finally:
        conn.close()


def _client_ip(request: Request) -> str:
    xff = request.headers.get("x-forwarded-for")
    if xff:
        # nginx in F5 sets X-Forwarded-For to a single $remote_addr; trust it
        # because nginx is the only thing in front of us.
        return xff.split(",")[0].strip()
    return request.client.host if request.client else "unknown"


def _ip_hash(ip: str) -> str:
    return hashlib.sha256(ip.encode()).hexdigest()[:16]


def _iso_utc(dt: Optional[datetime] = None) -> str:
    return (dt or datetime.now(timezone.utc)).strftime("%Y-%m-%dT%H:%M:%SZ")


async def _require_multipart(request: Request) -> None:
    ct = request.headers.get("content-type", "")
    if not ct.lower().startswith("multipart/form-data"):
        raise HTTPException(status_code=415, detail="content-type must be multipart/form-data")


async def _ingest(
    kind: str,
    request: Request,
    file: UploadFile,
    user_note: Optional[str],
    app_version: Optional[str],
    env_fingerprint: Optional[str],
) -> dict:
    body = await file.read()
    size_bytes = len(body)

    if size_bytes > MAX_BODY_BYTES:
        raise HTTPException(status_code=413, detail="payload exceeds 50 MB limit")
    if not body.startswith(ZIP_MAGIC):
        raise HTTPException(status_code=400, detail="file is not a zip (bad magic bytes)")

    bundle_uuid = uuid.uuid4().hex
    now = datetime.now(timezone.utc)
    date_str = now.strftime("%Y-%m-%d")
    received_at = _iso_utc(now)

    day_dir = STORAGE_ROOT / date_str
    day_dir.mkdir(parents=True, exist_ok=True)

    bundle_path = day_dir / f"{bundle_uuid}.zip"
    metadata_path = day_dir / f"{bundle_uuid}.json"

    dedup_hash = hashlib.sha256(body).hexdigest()
    client_ip_hash = _ip_hash(_client_ip(request))
    user_agent = request.headers.get("user-agent")

    # Order: zip on disk first, sidecar JSON second, index row last. If we
    # crash mid-write, the disk artifacts are recoverable; the index can be
    # rebuilt from sidecar JSONs.
    with open(bundle_path, "wb") as f:
        f.write(body)

    sidecar = {
        "uuid": bundle_uuid,
        "received_at": received_at,
        "kind": kind,
        "client_ip_hash": client_ip_hash,
        "content_type": file.content_type,
        "size_bytes": size_bytes,
        "user_agent": user_agent,
        "user_note": user_note,
        "app_version": app_version,
        "env_fingerprint": env_fingerprint,
        "dedup_hash": dedup_hash,
        "triage": {
            "status": "untriaged",
            "classifications": [],
            "responses": [],
        },
    }
    with open(metadata_path, "w", encoding="utf-8") as f:
        json.dump(sidecar, f, indent=2)

    conn = sqlite3.connect(INDEX_DB)
    try:
        conn.execute(
            """
            INSERT INTO bundles (
                uuid, received_at, kind, bundle_path, metadata_path,
                size_bytes, dedup_hash, app_version, user_agent, triage_status
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 'untriaged')
            """,
            (
                bundle_uuid,
                received_at,
                kind,
                str(bundle_path.relative_to(STORAGE_ROOT)),
                str(metadata_path.relative_to(STORAGE_ROOT)),
                size_bytes,
                dedup_hash,
                app_version,
                user_agent,
            ),
        )
        conn.commit()
    finally:
        conn.close()

    _emit(
        "info",
        "bundle_received",
        kind=kind,
        uuid=bundle_uuid,
        size_bytes=size_bytes,
        dedup_hash=dedup_hash,
        client_ip_hash=client_ip_hash,
    )

    return {"uuid": bundle_uuid, "received_at": received_at, "kind": kind}


@asynccontextmanager
async def _lifespan(_app: FastAPI):
    _init_db()
    _emit("info", "receiver_started", storage_root=str(STORAGE_ROOT))
    yield


app = FastAPI(title="jjflex-receiver", version="1", lifespan=_lifespan)


@app.exception_handler(HTTPException)
async def _http_exception_handler(request: Request, exc: HTTPException):
    _emit(
        "error",
        "validation_failed",
        path=request.url.path,
        status=exc.status_code,
        error=str(exc.detail),
    )
    return JSONResponse(
        status_code=exc.status_code,
        content={"error": exc.detail, "status": exc.status_code},
    )


@app.get("/healthz")
async def healthz() -> dict:
    return {"status": "ok"}


@app.post("/crashes", dependencies=[Depends(_require_multipart)])
async def post_crashes(
    request: Request,
    file: UploadFile = File(...),
    user_note: Optional[str] = Form(None),
    app_version: Optional[str] = Form(None),
    env_fingerprint: Optional[str] = Form(None),
) -> dict:
    return await _ingest("crashes", request, file, user_note, app_version, env_fingerprint)


@app.post("/feedback", dependencies=[Depends(_require_multipart)])
async def post_feedback(
    request: Request,
    file: UploadFile = File(...),
    user_note: Optional[str] = Form(None),
    app_version: Optional[str] = Form(None),
    env_fingerprint: Optional[str] = Form(None),
) -> dict:
    return await _ingest("feedback", request, file, user_note, app_version, env_fingerprint)

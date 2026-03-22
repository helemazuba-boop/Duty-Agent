from __future__ import annotations

import base64
import binascii
from contextvars import ContextVar, Token
import hashlib
import hmac
import secrets
from collections.abc import Mapping

from fastapi import Request, WebSocket
from fastapi.responses import JSONResponse

ACCESS_TOKEN_MODE_DYNAMIC = "dynamic"
ACCESS_TOKEN_MODE_STATIC = "static"
PBKDF2_SHA256_PREFIX = "pbkdf2_sha256"
PBKDF2_SHA256_ITERATIONS = 120_000
PBKDF2_SHA256_SALT_BYTES = 16
PBKDF2_SHA256_HASH_BYTES = 32
UNAUTHORIZED_DETAIL = "Missing or invalid bearer token."
WEBSOCKET_UNAUTHORIZED_CODE = 4401
WEBSOCKET_BUSY_CODE = 4409

_CURRENT_BEARER_TOKEN: ContextVar[str | None] = ContextVar("duty_current_bearer_token", default=None)

_PUBLIC_HTTP_EXACT_PATHS = frozenset({
    "/",
    "/app",
    "/health",
    "/engine/info",
})
_PUBLIC_HTTP_PREFIXES = ("/app/",)
_PROTECTED_HTTP_EXACT_PATHS = frozenset({
    "/shutdown",
})
_PROTECTED_HTTP_PREFIXES = (
    "/api/v1/",
    "/mcp",
)


def extract_bearer_token(headers: Mapping[str, str]) -> str | None:
    authorization = str(headers.get("authorization") or headers.get("Authorization") or "").strip()
    if not authorization:
        return None

    scheme, _, token = authorization.partition(" ")
    if scheme.lower() != "bearer":
        return None

    normalized_token = token.strip()
    return normalized_token or None


def get_current_request_bearer_token() -> str | None:
    return _CURRENT_BEARER_TOKEN.get()


def push_current_request_bearer_token(token: str | None) -> Token[str | None]:
    return _CURRENT_BEARER_TOKEN.set(token)


def pop_current_request_bearer_token(token: Token[str | None]) -> None:
    _CURRENT_BEARER_TOKEN.reset(token)


def normalize_access_token_mode(value: object) -> str:
    return ACCESS_TOKEN_MODE_STATIC if str(value or "").strip().lower() == ACCESS_TOKEN_MODE_STATIC else ACCESS_TOKEN_MODE_DYNAMIC


def create_pbkdf2_sha256_verifier(token: str) -> str:
    normalized_token = str(token or "")
    if not normalized_token:
        raise ValueError("token is required")

    salt = secrets.token_bytes(PBKDF2_SHA256_SALT_BYTES)
    derived = hashlib.pbkdf2_hmac(
        "sha256",
        normalized_token.encode("utf-8"),
        salt,
        PBKDF2_SHA256_ITERATIONS,
        dklen=PBKDF2_SHA256_HASH_BYTES,
    )
    return (
        f"{PBKDF2_SHA256_PREFIX}"
        f"${PBKDF2_SHA256_ITERATIONS}"
        f"${base64.b64encode(salt).decode('ascii')}"
        f"${base64.b64encode(derived).decode('ascii')}"
    )


def verify_pbkdf2_sha256_token(token: str | None, verifier: str | None) -> bool:
    if not token or not verifier:
        return False

    parts = str(verifier).split("$")
    if len(parts) != 4 or parts[0] != PBKDF2_SHA256_PREFIX:
        return False

    try:
        iterations = int(parts[1])
    except (TypeError, ValueError):
        return False

    if iterations != PBKDF2_SHA256_ITERATIONS:
        return False

    try:
        salt = base64.b64decode(parts[2], validate=True)
        expected = base64.b64decode(parts[3], validate=True)
    except (binascii.Error, ValueError):
        return False

    if len(salt) != PBKDF2_SHA256_SALT_BYTES or len(expected) != PBKDF2_SHA256_HASH_BYTES:
        return False

    actual = hashlib.pbkdf2_hmac(
        "sha256",
        token.encode("utf-8"),
        salt,
        iterations,
        dklen=PBKDF2_SHA256_HASH_BYTES,
    )
    return hmac.compare_digest(actual, expected)


def is_mcp_path(path: str) -> bool:
    return path == "/mcp" or path.startswith("/mcp/")


def is_public_http_path(path: str) -> bool:
    return path in _PUBLIC_HTTP_EXACT_PATHS or any(path.startswith(prefix) for prefix in _PUBLIC_HTTP_PREFIXES)


def is_protected_http_path(path: str) -> bool:
    return path in _PROTECTED_HTTP_EXACT_PATHS or any(path.startswith(prefix) for prefix in _PROTECTED_HTTP_PREFIXES)


def build_http_unauthorized_response() -> JSONResponse:
    return JSONResponse(
        status_code=401,
        headers={"WWW-Authenticate": "Bearer"},
        content={"detail": UNAUTHORIZED_DETAIL},
    )


def is_request_authorized(request: Request, runtime) -> bool:
    return runtime is not None and runtime.is_authorized(extract_bearer_token(request.headers))


def is_websocket_authorized(websocket: WebSocket, runtime) -> bool:
    return runtime is not None and runtime.is_authorized(extract_bearer_token(websocket.headers))

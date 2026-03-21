from __future__ import annotations

from collections.abc import Mapping

from fastapi import Request, WebSocket
from fastapi.responses import JSONResponse

UNAUTHORIZED_DETAIL = "Missing or invalid bearer token."
WEBSOCKET_UNAUTHORIZED_CODE = 4401

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

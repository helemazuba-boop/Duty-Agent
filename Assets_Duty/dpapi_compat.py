# -*- coding: utf-8 -*-
"""DPAPI 兼容层（C2 契约：B 写密文 → A 读明文）。

api_key 由 C# 客户端以 Windows DPAPI（``CryptProtectData``，CurrentUser 作用域）
加密后写入 config.json，形如 ``dpapi:v1:<base64>``；Python 侧只负责读取解密，
无前缀的旧明文键原样透传（兼容加密上线前的存量数据）。

契约要点（REPAIR_PLAN §0 D4 / §2 C2）：

* 密文格式 ``dpapi:v1:`` + base64(CryptProtectData(utf8(plain)))；
* 与 C# 侧约定不使用 pOptionalEntropy（否则两边互相解不开）；
* :func:`unprotect` 失败 → 置空 key + 告警日志，**绝不抛出**——密钥损坏不能
  拖垮整个排班流程（D4 明文键兼容读取同理）。
"""
from __future__ import annotations

import base64
import ctypes
import logging
import os
from ctypes import wintypes

DPAPI_PREFIX = "dpapi:v1:"
# UI_FORBIDDEN：解密不得弹出交互窗口（后端跑在无人值守的客户端子进程里）。
_CRYPTPROTECT_UI_FORBIDDEN = 0x01

_STDLIB_LOGGER = logging.getLogger("duty.dpapi")


class _DATA_BLOB(ctypes.Structure):
    _fields_ = [
        ("cbData", wintypes.DWORD),
        ("pbData", ctypes.POINTER(ctypes.c_byte)),
    ]


if os.name == "nt":
    _CRYPT32 = ctypes.WinDLL("crypt32", use_last_error=True)
    _CRYPT32.CryptProtectData.argtypes = [
        ctypes.POINTER(_DATA_BLOB),
        wintypes.LPCWSTR,
        ctypes.POINTER(_DATA_BLOB),
        ctypes.c_void_p,
        ctypes.c_void_p,
        wintypes.DWORD,
        ctypes.POINTER(_DATA_BLOB),
    ]
    _CRYPT32.CryptProtectData.restype = wintypes.BOOL
    _CRYPT32.CryptUnprotectData.argtypes = [
        ctypes.POINTER(_DATA_BLOB),
        ctypes.POINTER(wintypes.LPWSTR),
        ctypes.POINTER(_DATA_BLOB),
        ctypes.c_void_p,
        ctypes.c_void_p,
        wintypes.DWORD,
        ctypes.POINTER(_DATA_BLOB),
    ]
    _CRYPT32.CryptUnprotectData.restype = wintypes.BOOL
    _KERNEL32 = ctypes.WinDLL("kernel32", use_last_error=True)
    _KERNEL32.LocalFree.argtypes = [ctypes.c_void_p]
    _KERNEL32.LocalFree.restype = ctypes.c_void_p


def _make_blob(data: bytes) -> tuple[_DATA_BLOB, ctypes.Array]:
    """构建 CRYPT_DATA_BLOB；返回 (blob, 底层缓冲)——缓冲必须在 C 调用期间存活。"""
    buffer = ctypes.create_string_buffer(data, len(data))  # 显式传长度，避免按 NUL 截断
    blob = _DATA_BLOB(len(data), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_byte)))
    return blob, buffer


def _dpapi_invoke(ciphertext: bytes, *, protect: bool) -> bytes:
    in_blob, in_buffer = _make_blob(ciphertext)
    out_blob = _DATA_BLOB()
    func = _CRYPT32.CryptProtectData if protect else _CRYPT32.CryptUnprotectData
    # 第二参数（数据描述）在解密方向需要 LPWSTR 接收缓冲；我们不需要描述文本，
    # 传 None 让系统自行分配，LocalFree 时一并释放即可。
    description = wintypes.LPWSTR() if not protect else None
    ok = func(
        ctypes.byref(in_blob),
        description,
        None,  # pOptionalEntropy：与 C# 侧约定为空
        None,  # pvReserved
        None,  # pPromptStruct（UI_FORBIDDEN 下不适用）
        _CRYPTPROTECT_UI_FORBIDDEN,
        ctypes.byref(out_blob),
    )
    if not ok:
        raise OSError(f"Crypt{'Protect' if protect else 'Unprotect'}Data failed (winerror={ctypes.get_last_error()})")
    try:
        return ctypes.string_at(out_blob.pbData, out_blob.cbData)
    finally:
        _KERNEL32.LocalFree(ctypes.cast(out_blob.pbData, ctypes.c_void_p))


def protect(value: str) -> str:
    """明文 → ``dpapi:v1:`` 密文（测试/排障用途；生产写入在 C# 侧）。非 Windows 抛 RuntimeError。"""
    if os.name != "nt":
        raise RuntimeError("DPAPI is only available on Windows.")
    ciphertext = _dpapi_invoke(value.encode("utf-8"), protect=True)
    return DPAPI_PREFIX + base64.b64encode(ciphertext).decode("ascii")


def unprotect(value: object, logger=None) -> str:
    """``dpapi:v1:`` 密文 → 明文。

    * 无前缀 → 旧明文键，原样返回（C# 侧下次保存时会升级为密文）；
    * 解密失败（损坏 / 跨用户 / 非密文内容）→ 空串 + 告警，不抛（C2）。
    ``logger`` 可选注入宿主的 DutyDiagnosticsLogger（写 duty-backend-*.log）；
    缺省退回 stdlib logging（WARNING 级别默认可见）。
    """
    raw = str(value or "")
    if not raw:
        return ""
    if not raw.startswith(DPAPI_PREFIX):
        return raw
    if os.name != "nt":
        _warn(logger, "DPAPI payload found but DPAPI is unavailable on this platform; api_key cleared.")
        return ""
    try:
        ciphertext = base64.b64decode(raw[len(DPAPI_PREFIX):], validate=True)
        return _dpapi_invoke(ciphertext, protect=False).decode("utf-8")
    except Exception as ex:  # noqa: BLE001 — C2 契约：解密失败绝不能向上抛
        _warn(logger, f"DPAPI unprotect failed; api_key cleared ({type(ex).__name__}: {str(ex)[:160]}).")
        return ""


def _warn(logger, message: str) -> None:
    if logger is not None and hasattr(logger, "warn"):
        try:
            logger.warn("DpapiCompat", message)
            return
        except Exception:  # noqa: BLE001 — 日志故障不允许打断归一化流程
            pass
    _STDLIB_LOGGER.warning(message)

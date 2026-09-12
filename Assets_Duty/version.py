# -*- coding: utf-8 -*-
"""C3 契约：版本单一事实源。

后端代码（core.py / runtime.py / ...）一律 ``from version import APP_VERSION``，
禁止再内联版本字符串。W2 的 stamp 脚本（Agent D）负责改写本文件并同步两个
csproj 与 manifest，四处的版本号以本文件为准。
"""

APP_VERSION = "0.50.0"

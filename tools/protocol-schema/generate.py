#!/usr/bin/env python3
"""
DisplayBridge protocol codegen.

Reads schema.yaml (single source of truth for the wire format) and emits:
  - pc-host/src/DisplayBridge.Core/Protocol/Generated/*.cs
    (Messages.cs, ProtocolCommon.cs, ProtocolIO.cs, MessageFraming.cs)
  - android-client/app/src/main/java/.../protocol/generated/*.kt
    (Messages.kt, ProtocolCommon.kt, WireIO.kt, MessageFraming.kt)

Dependency: PyYAML (`pip install pyyaml`). Everything else is stdlib.

Usage:
    python generate.py

Run from anywhere; paths are resolved relative to this script's location
so `tools/protocol-schema/schema.yaml` and the repo root are always found
correctly regardless of the caller's cwd.
"""
from __future__ import annotations

import os
import sys

try:
    import yaml
except ImportError:
    print("ERROR: PyYAML is required. Install with: pip install pyyaml", file=sys.stderr)
    sys.exit(1)

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
SCHEMA_PATH = os.path.join(SCRIPT_DIR, "schema.yaml")

CS_OUT_DIR = os.path.join(
    REPO_ROOT, "pc-host", "src", "DisplayBridge.Core", "Protocol", "Generated"
)
KT_OUT_DIR = os.path.join(
    REPO_ROOT,
    "android-client", "app", "src", "main", "java",
    "com", "displaybridge", "protocol", "generated",
)


def load_schema() -> dict:
    with open(SCHEMA_PATH, "r", encoding="utf-8") as f:
        return yaml.safe_load(f)


def write_files(out_dir: str, files: dict) -> None:
    os.makedirs(out_dir, exist_ok=True)
    for filename, contents in files.items():
        path = os.path.join(out_dir, filename)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(contents)
        print(f"generated {path}")


def main() -> None:
    # sys.path already includes this script's directory (Python adds it automatically).
    from codegen_csharp import generate_csharp_files
    from codegen_kotlin import generate_kotlin_files

    schema = load_schema()

    write_files(CS_OUT_DIR, generate_csharp_files(schema))
    write_files(KT_OUT_DIR, generate_kotlin_files(schema))


if __name__ == "__main__":
    main()

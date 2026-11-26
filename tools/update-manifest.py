#!/usr/bin/env python3
"""Inserta o actualiza una versión en manifest.json (la más reciente primero)."""

import json
import sys


def main() -> int:
    if len(sys.argv) != 8:
        print(
            "uso: update-manifest.py <manifest> <version> <changelog> <checksum> "
            "<timestamp> <url> <targetAbi>",
            file=sys.stderr,
        )
        return 2

    path, version, changelog, checksum, timestamp, url, target_abi = sys.argv[1:8]

    with open(path, encoding="utf-8") as handle:
        data = json.load(handle)

    if not isinstance(data, list) or not data:
        print("manifest.json debe ser un array con al menos un plugin", file=sys.stderr)
        return 1

    plugin = data[0]
    entry = {
        "version": version,
        "changelog": changelog,
        "targetAbi": target_abi,
        "sourceUrl": url,
        "checksum": checksum,
        "timestamp": timestamp,
    }

    previous = plugin.get("versions", [])
    plugin["versions"] = [entry] + [
        item for item in previous if item.get("version") != version
    ]

    with open(path, "w", encoding="utf-8") as handle:
        json.dump(data, handle, ensure_ascii=False, indent=4)
        handle.write("\n")

    print(f"manifest.json actualizado con la versión {version}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

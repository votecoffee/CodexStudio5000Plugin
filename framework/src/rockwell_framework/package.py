"""Verify reviewed framework wheel bytes and the installed package closure."""

import argparse
import base64
import csv
import hashlib
import importlib.metadata
import io
import json
import zipfile
from pathlib import Path

from .publication import baseline, check_versions


def verify_wheel(path):
    path = Path(path)
    expected = baseline()
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if path.name != expected["wheel"] or digest != expected["wheel_sha256"]:
        raise ValueError("Framework wheel differs from the reviewed baseline")
    check_versions()
    distribution = importlib.metadata.distribution(expected["distribution"])
    checked = 0
    with zipfile.ZipFile(path) as wheel:
        record = next(n for n in wheel.namelist() if n.endswith(".dist-info/RECORD"))
        for name, checksum, size in csv.reader(io.StringIO(wheel.read(record).decode())):
            if not checksum:
                continue
            data = wheel.read(name)
            wanted = "sha256=" + base64.urlsafe_b64encode(
                hashlib.sha256(data).digest()
            ).decode().rstrip("=")
            if checksum != wanted or int(size) != len(data):
                raise ValueError("Wheel RECORD checksum mismatch")
            if distribution.locate_file(name).read_bytes() != data:
                raise ValueError("Installed framework bytes differ from reviewed wheel")
            checked += 1
        if "universal_tools/management.py" not in wheel.namelist():
            raise ValueError("Reviewed management implementation is missing")
    return {
        "framework": distribution.version,
        "mcp": importlib.metadata.version("mcp"),
        "wheel_sha256": digest,
        "verified_files": checked,
        "record_verified": True,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--wheel", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(verify_wheel(args.wheel)))


if __name__ == "__main__":
    main()

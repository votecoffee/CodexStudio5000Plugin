"""App-owned framework integration; the .NET service retains all domain authority."""

import json
from importlib.resources import files

_release = json.loads(files(__package__).joinpath("release-version.json").read_text("utf-8"))
__version__ = _release["packageVersion"]
__display_version__ = _release["displayVersion"]

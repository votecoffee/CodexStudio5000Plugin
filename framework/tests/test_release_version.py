import importlib.resources
import json

from rockwell_framework import __display_version__, __version__
from rockwell_framework.management import component, version_key


def test_release_package_data_uses_fixed_display_and_canonical_identity():
    release = json.loads(importlib.resources.files("rockwell_framework").joinpath("release-version.json").read_text())
    assert release["displayVersion"] == __display_version__
    assert release["packageVersion"] == __version__
    assert version_key(__display_version__) == version_key(__version__)


def test_padded_timestamps_do_not_report_false_component_updates():
    assert not component("26.101.1", "26.0101.000001", "26.0101.000001").update_pending
    assert component("26.101.1", "26.0101.000002").update_pending

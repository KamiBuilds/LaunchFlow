#!/usr/bin/env python3
"""Keep v0.7.46 in-app steps, real labels/targets and written guide aligned.

Run: python3 tests/ui/verify_guide.py [--write]
"""
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "LaunchFlow" / "TutorialWindow.xaml.cs"
DOC = ROOT / "docs" / "GETTING-STARTED.md"
NS = "{http://schemas.microsoft.com/winfx/2006/xaml}"
WINDOWS = {
    "MainWindow": "MainWindow.xaml",
    "NewWorkflowWindow": "NewWorkflowWindow.xaml",
    "VisualRecorderWindow": "VisualRecorderWindow.xaml",
    "BrowserSetupWindow": "BrowserSetupWindow.xaml",
}
ROUTES = {
    "BASIC": ("First website — no saved login", 13, "basic"),
    "PASS": ("Saved website login — optional", 8, "passwords"),
    "RECORD": ("Record clicks — optional", 6, "recording"),
    "STARTUP": ("Startup and background — optional", 5, "startup"),
    "HELP": ("Troubleshooting", 5, "recovery"),
}
TOKEN = r'"(?:\\.|[^"\\])*"'
STEP_RE = re.compile(r'\bnew\(\s*((?:' + TOKEN + r'\s*,?\s*){8,10})\)', re.S)
TOKEN_RE = re.compile(TOKEN)


def read_steps():
    source = SOURCE.read_text(encoding="utf-8")
    groups = {key: [] for key in ROUTES}
    for match in STEP_RE.finditer(source):
        args = [json.loads(m.group()) for m in TOKEN_RE.finditer(match.group(1))]
        if len(args) not in (8, 10):
            raise ValueError("Invalid GuideStep arguments")
        prefix, _, number = args[0].partition("-")
        if prefix in groups and number.isdigit():
            groups[prefix].append(args)
    return groups


def markdown(groups):
    out = [
        "# LaunchFlow — Getting started", "",
        "Generated from the same one-action instructions as the v0.7.46 in-app guide. "
        "Control labels are verified against the English source, not native screenshots. "
        "Dutch navigation can display Instellingen instead of Settings; other screens "
        "may remain English.", "",
        "## First launch (before the in-app guide)", "",
        "Install the packaged app, not a developer SDK. A new installation displays "
        "Set up LaunchFlow with Master password, Confirm master password and "
        "Create vault & continue. An existing vault instead displays Unlock LaunchFlow, "
        "with Unlock and optional Windows Hello. Your vault master password is not "
        "your website password or Windows password; it cannot be recovered if forgotten. "
        "Never delete an existing vault to follow a tutorial.", "",
        "After unlocking, click Getting started. This modeless guide does not click, "
        "save credentials or run automation. Next means a page was viewed, not "
        "that its action was completed.", "",
    ]
    for prefix, (title, count, route) in ROUTES.items():
        out.extend(["## " + title, ""])
        for number, args in enumerate(groups[prefix], 1):
            step_id, heading, instruction, location, section, control, expected, recovery = args[:8]
            out.extend([
                f"{number}. **{heading}** ({step_id}): {instruction}",
                f"   - **Where:** {location} → {section}. Look for **{control}**.",
                f"   - **You should see:** {expected}",
                f"   - **Can't find it?** {recovery}",
            ])
        out.append("")
    out.extend([
        "## Confirm a website survives close and reopen", "",
        "Once LaunchFlow has confirmed a durable save, use its normal Exit path "
        "(not simply minimize/Background). Then reopen LaunchFlow, unlock the same "
        "vault, and confirm the website name, URL, browser and screen remain. "
        "Do this manually; the guide does not exit/restart the app. "
        "If Exit reports unsaved edits, a locked vault or failed cleanup, "
        "follow that recovery path instead of forcing shutdown.", "",
        "## Visual provenance and limitations", "",
        "The in-app locator is a source-based schematic, not a native screenshot. "
        "The optional Show target in app action outlines an existing visible "
        "control without clicking or changing it. A confirmation checkbox records "
        "only the user's own in-memory observation, not evidence of completed "
        "storage or login. Native Windows captures, layout, keyboard, language, "
        "DPI and multi-monitor behavior require separate acceptance tests.", "",
    ])
    return "\n".join(out)


def verify(groups):
    source = SOURCE.read_text(encoding="utf-8")
    guide_xml = (ROOT / "LaunchFlow" / "TutorialWindow.xaml").read_text(encoding="utf-8")
    guide_root = ET.fromstring(guide_xml)
    guide_named = {n.get(NS + "Name") for n in guide_root.iter() if n.get(NS + "Name")}
    assert {
        "RouteCombo", "ProgressLabel", "StepHeading", "StepDetails",
        "ExpectedResult", "ConfirmStepBox", "NextButton", "BackButton",
        "CloseGuideButton", "ShowTargetButton", "RecoveryPanel",
        "ExampleAction", "ExampleTitle", "ExampleWebsite"
    }.issubset(guide_named)
    assert "ILLUSTRATION" in guide_xml and "not a Windows screenshot" in guide_xml
    assert "Closed=\"Guide_Closed\"" in guide_xml
    assert "IsHitTestVisible = false" in source and "Next does not mark a task complete" in source
    assert "ClearHighlight();" in source and "_confirmed.Clear();" in source
    assert "SaveAll()" not in source and "RunEnabledAsync" not in source and "ReadCredential" not in source

    named_by_window = {}
    text_by_window = {}
    for kind, file in WINDOWS.items():
        root = ET.parse(ROOT / "LaunchFlow" / file).getroot()
        named_by_window[kind] = {
            n.get(NS + "Name"): n for n in root.iter() if n.get(NS + "Name")
        }
        text_by_window[kind] = "\n".join(
            value for node in root.iter() for attr, value in node.attrib.items()
            if attr.split("}")[-1] in ("Text", "Content", "Header", "Title", "Name", "ToolTip", "AutomationProperties.Name")
        )
    credential_code = (ROOT / "LaunchFlow" / "MainWindow.Credentials.cs").read_text(encoding="utf-8")
    seen = set()
    total = 0
    for prefix, (title, expected_count, route) in ROUTES.items():
        collection = groups[prefix]
        assert len(collection) == expected_count, (prefix, len(collection))
        assert f'["{route}"] =' in source
        for index, args in enumerate(collection, 1):
            total += 1
            step_id, heading, instruction, location, section, label, expected, recovery = args[:8]
            assert step_id == f"{prefix}-{index:02d}" and step_id not in seen, step_id
            seen.add(step_id)
            assert all((heading, instruction, location, section, label, expected, recovery)), step_id
            if len(args) == 8:
                continue
            kind, target = args[8:10]
            assert kind in WINDOWS and target in named_by_window[kind], (step_id, kind, target)
            if label == "CaptureStatus / TargetStatus":
                assert "TargetStatus" in named_by_window[kind]
            elif label == "Encrypted credentials saved":
                assert label in credential_code
            else:
                assert label in text_by_window[kind], (step_id, "label drift", label)
    assert total == 37
    assert "Getting started" in text_by_window["MainWindow"]
    assert "Save for selected website" in text_by_window["MainWindow"]
    print(f"PASS guide parity: {total} steps, {len(WINDOWS)} source windows, control labels and no implicit actions")


def main():
    groups = read_steps()
    verify(groups)
    expected = markdown(groups)
    if sys.argv[1:] == ["--write"]:
        DOC.parent.mkdir(exist_ok=True)
        DOC.write_text(expected, encoding="utf-8")
        print("Wrote docs/GETTING-STARTED.md")
    else:
        assert DOC.is_file() and DOC.read_text(encoding="utf-8") == expected, (
            "Written guide drifted: run python3 tests/ui/verify_guide.py --write"
        )
        print("PASS written guide is generated from the same in-app steps")


if __name__ == "__main__":
    main()

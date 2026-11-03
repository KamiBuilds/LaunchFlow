#!/usr/bin/env python3
"""Keep v0.7.46 in-app steps, real labels/targets and written guide aligned.

Run: python3 tests/ui/verify_guide.py [--write]
"""
import copy
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
    "PASS": ("Saved website login — optional", 9, "passwords"),
    "RECORD": ("Record clicks — optional", 6, "recording"),
    "STARTUP": ("Startup and background — optional", 5, "startup"),
    "HELP": ("Troubleshooting", 5, "recovery"),
}
TOKEN = r'"(?:\\.|[^"\\])*"'
STEP_RE = re.compile(r'\bnew\(\s*((?:' + TOKEN + r'\s*,?\s*){8,10})\)', re.S)
TOKEN_RE = re.compile(TOKEN)

# Controls without a self-contained label use explicit, source-verified
# field associations. This must never accept arbitrary labels elsewhere in
# the same window as proof that a target is correct.
FIELD_LABELS = {
    ("NewWorkflowWindow", "NameBox"): "Website name",
    ("NewWorkflowWindow", "UrlBox"): "Website address (URL)",
    ("NewWorkflowWindow", "BrowserCombo"): "Browser",
    ("NewWorkflowWindow", "MonitorCombo"): "Screen (monitor)",
    ("MainWindow", "LoginModeCombo"): "How should LaunchFlow sign in?",
    ("MainWindow", "UsernameBox"): "New username or email (leave blank to keep current)",
    ("MainWindow", "PasswordInput"): "New password (leave blank to keep current)",
    ("VisualRecorderWindow", "StepList"): "Recorded actions",
}


def read_steps(source=None):
    if source is None:
        source = SOURCE.read_text(encoding="utf-8")
    # Refuse unknown/malformed records rather than silently skipping them.
    start = source.index("private static readonly IReadOnlyDictionary")
    end = source.index("private readonly HashSet", start)
    body = source[start:end]
    parsed = list(STEP_RE.finditer(body))
    if len(parsed) != len(re.findall(r"\bnew\s*\(", body)):
        raise ValueError("Malformed or unrecognized GuideStep")
    markers = list(re.finditer(r'\["([^"]+)"\]\s*=\s*\[', body))
    expected_route = {details[2]: prefix for prefix, details in ROUTES.items()}
    if {marker.group(1) for marker in markers} != set(expected_route):
        raise ValueError("Unknown or missing guide route")
    groups = {key: [] for key in ROUTES}
    for match in parsed:
        args = [json.loads(m.group()) for m in TOKEN_RE.finditer(match.group(1))]
        if len(args) not in (8, 10):
            raise ValueError("Invalid GuideStep arguments")
        prefix, _, number = args[0].partition("-")
        if prefix not in groups or not number.isdigit():
            raise ValueError("Unknown GuideStep ID: " + args[0])
        route = next((m.group(1) for m in reversed(markers) if m.start() < match.start()), None)
        if expected_route.get(route) != prefix:
            raise ValueError("Step in incorrect route: " + args[0])
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
        if prefix == "PASS":
            out.extend([
                "After PASS-04, Saved password continues with PASS-05–08. "
                "No login required and SSO skip password entry and use PASS-09 "
                "to save only the sign-in preference. The app's mode selection "
                "is read without accessing credentials.", ""
            ])
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
    autosave_code = (ROOT / "LaunchFlow" / "MainWindow.AutoSave.cs").read_text(encoding="utf-8")
    assert 'SaveChangesButton.Content = "Retry save"' in autosave_code
    assert "CurrentSaveButtonLabel()" in source and "ReadSelectedPasswordMode();" in source
    assert 'step.Id == "PASS-09"' in source
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
                assert target == "CredentialStatus" and 'CredentialStatus.Text = "Encrypted credentials saved"' in credential_code
            elif (kind, target) == ("MainWindow", "SaveChangesButton"):
                assert step_id in ("BASIC-13", "PASS-09", "HELP-04")
                assert label == "Save changes / Retry save"
                assert named_by_window[kind][target].get("Content") == "Save changes"
            elif (kind, target) in FIELD_LABELS:
                assert label == FIELD_LABELS[(kind, target)], (step_id, "wrong field label", label)
                assert label in text_by_window[kind], (step_id, "missing source field label", label)
            else:
                element = named_by_window[kind][target]
                own_labels = {
                    element.get("Content"), element.get("Header"), element.get("Text"),
                    element.get("AutomationProperties.Name"),
                    element.get("{http://schemas.microsoft.com/winfx/2006/xaml/presentation}AutomationProperties.Name")
                }
                assert label in own_labels, (step_id, "wrong control/label association", target, label)
    assert total == 38
    assert "Getting started" in text_by_window["MainWindow"]
    assert "Save for selected website" in text_by_window["MainWindow"]
    print(f"PASS guide parity: {total} steps, {len(WINDOWS)} source windows, bound control labels and no implicit actions")


def negative_controls(groups):
    def must_fail(name, transform):
        mutated = copy.deepcopy(groups)
        transform(mutated)
        try:
            verify(mutated)
        except (AssertionError, ValueError):
            print("PASS guide negative control: " + name)
            return
        raise AssertionError("Accepted invalid guide mapping: " + name)

    must_fail("Save step targets Remove button", lambda g:
              g["PASS"][6].__setitem__(9, "ClearCredentialButton"))
    must_fail("unrelated label elsewhere in window", lambda g:
              g["PASS"][6].__setitem__(5, "Remove from selected website"))
    must_fail("unknown target", lambda g:
              g["PASS"][6].__setitem__(9, "NotARealControl"))
    bad_source = SOURCE.read_text(encoding="utf-8").replace(
        'new("PASS-07"', 'new("UNRECOGNIZED-07"', 1)
    try:
        read_steps(bad_source)
    except ValueError:
        print("PASS guide negative control: unknown step ID")
    else:
        raise AssertionError("Unknown GuideStep record was silently skipped")


def main():
    groups = read_steps()
    verify(groups)
    negative_controls(groups)
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

#!/usr/bin/env python3
"""Compile unchanged production SaveAll, WPF timer handlers and dirty event subscriptions."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[2]
autosave = (root / "LaunchFlow/UI/Shell/MainWindow.AutoSave.cs").read_text(encoding="utf-8")
persistence = (root / "LaunchFlow/UI/Shell/MainWindow.Persistence.cs").read_text(encoding="utf-8")
mainwindow = (root / "LaunchFlow/UI/Shell/MainWindow.xaml.cs").read_text(encoding="utf-8")

def extract_method(source, name):
    pattern = r"^    private [^\n]+\\b" + re.escape(name) + r"\\([^\n]*\\)\\s*\n    \\{"
    # Regex written separately to keep source anchor strict and unambiguous.
    pattern = r"^    private [^\n]+\b" + re.escape(name) + r"\([^\n]*\)\s*\n    \{"
    hits = list(re.finditer(pattern, source, re.M))
    if len(hits) != 1:
        raise RuntimeError(f"Expected exactly one original method {name}; got {len(hits)}")
    start = hits[0].start()
    end = source.index("\n    }", hits[0].end()) + len("\n    }")
    return source[start:end]

auto_fields_start = autosave.index("    private readonly AutoSaveSchedule _autoSaveSchedule")
auto_fields_end = autosave.index("    private void StartAutoSave()", auto_fields_start)
save_fields_start = persistence.index("    private bool _saveInProgress;")
save_fields_end = persistence.index("    private ConfigurationEditDraft CaptureConfigurationDraft(", save_fields_start)
events_start = mainwindow.index("        NameBox.TextChanged += (_, _) => MarkUnsavedChanges();")
events_end_line = "        LanguageCombo.SelectionChanged += (_, _) => MarkUnsavedChanges();"
events_end = mainwindow.index(events_end_line, events_start) + len(events_end_line)
events = mainwindow[events_start:events_end]

methods = [
    extract_method(autosave, name) for name in
    ("StartAutoSave", "AutoSaveTimer_Tick", "ShowAutoSaveFailed")
] + [
    extract_method(persistence, name) for name in
    ("CaptureConfigurationDraft", "SaveEnabledToggleImmediately", "FlushDeferredEnabledToggle", "MarkUnsavedChanges", "MarkSaved", "SaveAll")
]
generated = """// Source-linked verbatim production methods and constructor subscriptions.
// Do not edit; rerun tests/autosave/extract.py after production changes.
#nullable enable
using System;
using LaunchFlow.Models;
using LaunchFlow.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

partial class AutoSaveFixture
{
"""+autosave[auto_fields_start:auto_fields_end]+persistence[save_fields_start:save_fields_end]+"""
    public void AttachProductionDirtyEvents()
    {
"""+events+"""
    }

"""+ "\n\n".join(methods)+"""
}
"""
target = root / "tests/autosave/obj/ProductionAutosave.g.cs"
target.parent.mkdir(exist_ok=True)
target.write_text(generated, encoding="utf-8")
print("Extracted production SaveAll, timer, draft, dirty tracking and " +
      "original constructor change subscriptions; bytes=" + str(len(generated)))

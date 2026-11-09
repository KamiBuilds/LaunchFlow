#!/usr/bin/env python3
"""Link the actual production import-handler body into the disposable SQLite fixture."""
from pathlib import Path
import re
root = Path(__file__).resolve().parents[2]
source = (root / "LaunchFlow/UI/Shell/MainWindow.Transfer.cs").read_text(encoding="utf-8")
match = re.search(r"^    private void ImportLaunchFlowFile\(string path\)\s*\n    \{", source, re.M)
if match is None:
    raise SystemExit("Production import entrypoint not found")
end = source.find("\n    }", match.end())
if end < 0:
    raise SystemExit("Production import method terminator not found")
method = source[match.start():end + len("\n    }")]
target = Path(__file__).resolve().parent / "GeneratedImportHandler.g.cs"
target.write_text(
    "using LaunchFlow.Models;\nusing LaunchFlow.Services;\nusing System.Windows;\n"
    "namespace LaunchFlow;\npublic sealed partial class MainWindow\n{\n" + method + "\n}\n",
    encoding="utf-8")
print("Linked production import handler:", target.name)

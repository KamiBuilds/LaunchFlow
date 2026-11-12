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
# Link the production cache ownership helper; imports invalidate the
# current KeePass cache epoch after replacing the encrypted configuration.
cache_source = (root / "LaunchFlow/UI/Shell/MainWindow.KeePassSettings.cs").read_text(encoding="utf-8")
cache_match = re.search(
    r"^    private void PublishKeePassCache\(string\? secret\)\s*\n    \{",
    cache_source, re.M)
if cache_match is None:
    raise SystemExit("Production KeePass cache helper not found")
cache_end = cache_source.find("\n    }", cache_match.end())
if cache_end < 0:
    raise SystemExit("Production KeePass cache method terminator not found")
cache_method = cache_source[cache_match.start():cache_end + len("\n    }")]
target = Path(__file__).resolve().parent / "GeneratedImportHandler.g.cs"
target.write_text(
    "using LaunchFlow.Models;\nusing LaunchFlow.Services;\nusing System.Windows;\n"
    "namespace LaunchFlow;\npublic sealed partial class MainWindow\n{\n" + method + "\n" + cache_method + "\n}\n",
    encoding="utf-8")
print("Linked production import handler:", target.name)

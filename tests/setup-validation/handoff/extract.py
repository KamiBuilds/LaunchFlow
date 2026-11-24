"""Compile the exact production startup handler with explicit portable adapters."""
from pathlib import Path
import sys
root = Path(__file__).resolve().parents[3]
source = (root / 'LaunchFlow/App.xaml.cs').read_text(encoding='utf-8')
start = source.index('    private static bool InitializeVault()')
end = source.index('    internal void KeepAliveForVaultResetSetup()', start)
output = Path(sys.argv[1])
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text('#nullable enable\nusing LaunchFlow.Models;\nusing LaunchFlow.Services;\nusing System.Windows;\nusing MessageBox = System.Windows.MessageBox;\nnamespace LaunchFlow;\npublic partial class App {\n' + source[start:end] + '\n}\n', encoding='utf-8')

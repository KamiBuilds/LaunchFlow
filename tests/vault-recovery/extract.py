"""Regenerate the exact production recovery handler; native dependencies are explicit adapters."""
from pathlib import Path
import sys
root = Path(__file__).resolve().parents[2]
source = Path(sys.argv[2]).read_text(encoding='utf-8') if len(sys.argv) > 2 else (root / 'LaunchFlow/App.xaml.cs').read_text(encoding='utf-8')
start = source.index('    private static bool TryRecoverIncompleteVault()')
end = source.index('    private static bool InitializeVault()', start)
output = Path(sys.argv[1])
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text('#nullable enable\nusing LaunchFlow.Models;\nusing LaunchFlow.Services;\nusing System.Windows;\nusing MessageBox = System.Windows.MessageBox;\nnamespace LaunchFlow;\npublic partial class App {\n' + source[start:end] + '\ninternal static bool RecoverForTest() => TryRecoverIncompleteVault();\n}\n', encoding='utf-8')
master = (root / 'LaunchFlow/UI/Shell/MainWindow.PasswordManager.cs').read_text(encoding='utf-8')
start = master.index('    private async void ChangeMasterPassword_Click(')
end = master.rindex('\n}')
output.with_name('MainWindow.Master.g.cs').write_text('#nullable enable\nusing LaunchFlow.Services;\nusing System.Windows;\nusing MessageBox = System.Windows.MessageBox;\nnamespace LaunchFlow;\npublic partial class MainWindow {\n' + master[start:end] + '\ninternal void ChangeForTest() => ChangeMasterPassword_Click(this, new RoutedEventArgs());\n}\n', encoding='utf-8')

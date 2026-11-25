using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace NativeUiDriver {
    public sealed class WindowRef {
        public long Handle;
        public string Kind;
        public bool Owned;
    }
    public static partial class Native {
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public UIntPtr dwExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public UIntPtr dwExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] public struct HARDWAREINPUT {
            public uint uMsg;
            public ushort wParamL, wParamH;
        }
        [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT {
            public uint type;
            public INPUTUNION data;
        }
        public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] input, int size);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll")] static extern bool SwitchDesktop(IntPtr desktop);
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        public static int InputSize { get { return Marshal.SizeOf(typeof(INPUT)); } }
        public static void SetDpiAwareness() {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch (EntryPointNotFoundException) { }
            SetProcessDPIAware();
        }
        public static bool InputDesktopAvailable() {
            IntPtr desktop = OpenInputDesktop(0, false, 0x0101);
            if (desktop == IntPtr.Zero) return false;
            try { return SwitchDesktop(desktop) && GetSystemMetrics(78) > 0 && GetSystemMetrics(79) > 0; }
            finally { CloseDesktop(desktop); }
        }
        static string Classify(string title) {
            switch (title) {
                case "Set up LaunchFlow": return "Setup";
                case "Unlock LaunchFlow": return "Unlock";
                case "LaunchFlow \u2014 Website workspace automation": return "Main";
                case "LaunchFlow startup error": return "StartupError";
                case "LaunchFlow cannot start on this PC": return "DependencyError";
                case "Windows startup needs attention": return "StartupRegistrationWarning";
                case "Choose a stronger master password": return "WeakInput";
                case "Setup": return "SetupInputRejected";
                case "Vault locked": return "AuthenticationRejected";
                case "Vault recovery required": return "Recovery";
                case "Vault unchanged": return "VaultUnchanged";
                default: return "Other";
            }
        }
        public static WindowRef[] Windows(int process) {
            List<WindowRef> windows = new List<WindowRef>();
            EnumWindows(delegate(IntPtr window, IntPtr parameter) {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner == (uint)process && IsWindowVisible(window)) {
                    // EnumWindows also reports WPF tooltip, popup and menu HWNDs.
                    // They can be owned and visible for the entire hover interval,
                    // but are not modal dialogs. Never let them masquerade as an
                    // unexpected password/setup window.
                    try {
                        AutomationElement element = AutomationElement.FromHandle(window);
                        if (element != null && element.Current.ControlType != ControlType.Window)
                            return true;
                    } catch (ElementNotAvailableException) {
                        return true; // Transient WPF HWND destroyed during enumeration.
                    }
                    StringBuilder caption = new StringBuilder(256);
                    GetWindowText(window, caption, caption.Capacity);
                    windows.Add(new WindowRef {
                        Handle = window.ToInt64(), Kind = Classify(caption.ToString()),
                        Owned = GetWindow(window, 4) != IntPtr.Zero
                    });
                }
                return true;
            }, IntPtr.Zero);
            return windows.ToArray();
        }
        static AutomationElement Root(long handle, int process) {
            AutomationElement root = AutomationElement.FromHandle(new IntPtr(handle));
            if (root == null || root.Current.ProcessId != process) throw new InvalidOperationException("WindowIdentityLost");
            return root;
        }
        static AutomationElement ById(AutomationElement root, string id) {
            return root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        }
        static AutomationElement Button(AutomationElement root, string name) {
            AutomationElementCollection found = root.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, name)));
            if (found.Count != 1) throw new InvalidOperationException("ButtonNotUnique");
            return found[0];
        }
        static void Send(INPUT[] inputs) {
            if (SendInput((uint)inputs.Length, inputs, InputSize) != (uint)inputs.Length) {
                // NoModifiers was asserted before each owned input sequence.
                INPUT[] release = new INPUT[] {Key(0x10,2),Key(0x11,2),Key(0x12,2)};
                SendInput((uint)release.Length,release,InputSize);
                throw new InvalidOperationException("SendInputNotAccepted");
            }
        }
        static INPUT Key(ushort key, uint flags) {
            INPUT input = new INPUT(); input.type = 1;
            input.data.ki = new KEYBDINPUT { wVk = key, dwFlags = flags };
            return input;
        }
        static INPUT Mouse(int x, int y, uint flags) {
            INPUT input = new INPUT(); input.type = 0;
            input.data.mi = new MOUSEINPUT { dx = x, dy = y, dwFlags = flags };
            return input;
        }
        static void NoModifiers() {
            foreach (int key in new int[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
                if ((GetAsyncKeyState(key) & 0x8000) != 0) throw new InvalidOperationException("ModifierKeyHeld");
        }
        static void Click(AutomationElement element, long handle, int process) {
            IntPtr window = new IntPtr(handle);
            if (element.Current.ProcessId != process || !element.Current.IsEnabled || element.Current.IsOffscreen)
                throw new InvalidOperationException("ControlNotInteractable");
            ShowWindowAsync(window, 9);
            SetForegroundWindow(window);
            Thread.Sleep(100);
            Point point;
            if (!element.TryGetClickablePoint(out point) || Double.IsNaN(point.X) || Double.IsNaN(point.Y))
                throw new InvalidOperationException("ClickablePointUnavailable");
            int x = (int)Math.Round(point.X), y = (int)Math.Round(point.Y);
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
            int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            if (width < 2 || height < 2 || x < left || x >= left + width || y < top || y >= top + height)
                throw new InvalidOperationException("ClickablePointOutsideDesktop");
            POINT hitPoint = new POINT { X = x, Y = y };
            IntPtr hit = GetAncestor(WindowFromPoint(hitPoint), 2);
            uint hitProcess;
            GetWindowThreadProcessId(hit, out hitProcess);
            if (hit != window || hitProcess != (uint)process) throw new InvalidOperationException("ControlObscured");
            NoModifiers();
            int absoluteX = (int)Math.Round((x - left) * 65535.0 / (width - 1));
            int absoluteY = (int)Math.Round((y - top) * 65535.0 / (height - 1));
            Send(new INPUT[] { Mouse(absoluteX, absoluteY, 0xC001), Mouse(0, 0, 0x0002), Mouse(0, 0, 0x0004) });
        }
        static void AssertFocused(AutomationElement expected, long handle, int process) {
            AutomationElement focused = AutomationElement.FocusedElement;
            if (GetAncestor(GetForegroundWindow(), 2) != new IntPtr(handle) || focused == null ||
                focused.Current.ProcessId != process || !focused.Current.IsPassword ||
                !Automation.Compare(expected, focused)) throw new InvalidOperationException("PasswordFocusNotConfirmed");
        }
        public static void Fill(long handle, int process, string id, string value, string mode) {
            AutomationElement field = ById(Root(handle, process), id);
            if (field == null || !field.Current.IsPassword || field.Current.ControlType != ControlType.Edit)
                throw new InvalidOperationException("PasswordControlNotFound");
            Click(field, handle, process);
            Thread.Sleep(120);
            AssertFocused(field, handle, process);
            Send(new INPUT[] { Key(0x11, 0), Key(0x41, 0), Key(0x41, 2), Key(0x11, 2) });
            Thread.Sleep(80);
            AssertFocused(field, handle, process);
            NoModifiers();
            if (mode == "NativeSendInputKeyboard") { FillKeyboard(field, handle, process, value); return; }
            if (mode == "NativeClipboardPaste") { FillClipboard(field, handle, process, value); return; }
            if (mode != "NativeSendInputUnicode") throw new InvalidOperationException("EntryModeInvalid");
            // Dispatch Unicode UTF-16 input as bounded key pairs rather than
            // one burst for the entire password. WPF PasswordBox text
            // composition must observe each character before the next one,
            // including combining marks and non-Latin BMP characters. Never
            // substitute clipboard/ValuePattern or read a password back.
            for (int i = 0; i < value.Length; i++) {
                AssertFocused(field, handle, process);
                NoModifiers();
                INPUT[] input = new INPUT[2];
                try {
                    input[0] = Key(0, 4);
                    input[0].data.ki.wScan = value[i];
                    input[1] = Key(0, 6);
                    input[1].data.ki.wScan = value[i];
                    Send(input);
                } finally { Array.Clear(input, 0, input.Length); }
                Thread.Sleep(30);
            }
            Thread.Sleep(300);
            AssertFocused(field, handle, process);
        }
        public static void ClickButton(long handle, int process, string name) {
            Click(Button(Root(handle, process), name), handle, process);
        }
        public static bool MainReady(long handle, int process) {
            AutomationElement tabs = ById(Root(handle, process), "MainTabs");
            return tabs != null && tabs.Current.IsEnabled && !tabs.Current.IsOffscreen;
        }
    }
}

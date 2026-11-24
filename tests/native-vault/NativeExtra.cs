using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
namespace NativeUiDriver {
    public static partial class Native {
        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern short VkKeyScanEx(char value, IntPtr layout);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr layout);
        [DllImport("user32.dll")] static extern short GetKeyState(int key);
        [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr window);
        [DllImport("user32.dll")] static extern bool CloseClipboard();
        [DllImport("user32.dll")] static extern bool EmptyClipboard();
        [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr memory);
        [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr memory);
        static bool clipboardTouched;
        public static bool ClipboardSequenceChanged { get; private set; }
        static bool OpenClipboardBounded(IntPtr owner) {
            for (int i=0;i<30;i++) { if (OpenClipboard(owner)) return true; Thread.Sleep(30); }
            return false;
        }
        public static void ClearClipboardFinally() {
            if (!clipboardTouched) return;
            if (!OpenClipboardBounded(IntPtr.Zero)) throw new InvalidOperationException("ClipboardClearFailed");
            try { if (!EmptyClipboard()) throw new InvalidOperationException("ClipboardClearFailed"); clipboardTouched=false; }
            finally { CloseClipboard(); }
        }
        public static void ClearClipboardAfterWatchdog() { clipboardTouched=true; ClearClipboardFinally(); }
        static void AssertClipboardPayload(string expected, uint sequence) {
            if (!OpenClipboardBounded(IntPtr.Zero)) throw new InvalidOperationException("ClipboardUnavailable");
            try {
                IntPtr memory=GetClipboardData(13);
                if (memory==IntPtr.Zero || GlobalSize(memory).ToUInt64() < (ulong)((expected.Length+1)*2))
                    throw new InvalidOperationException("ClipboardPayloadMismatch");
                IntPtr address=GlobalLock(memory);
                if (address==IntPtr.Zero) throw new InvalidOperationException("ClipboardUnavailable");
                try {
                    string actual=Marshal.PtrToStringUni(address,expected.Length);
                    if (Marshal.ReadInt16(address,expected.Length*2)!=0 || !ClipboardContract.CanDispatch(expected,actual))
                        throw new InvalidOperationException("ClipboardPayloadMismatch");
                    actual=null;
                } finally { GlobalUnlock(memory); }
                // Closing/transcoding can change a sequence without changing the text.
                ClipboardSequenceChanged |= GetClipboardSequenceNumber()!=sequence;
            } finally { CloseClipboard(); }
        }
        static void FillClipboard(AutomationElement field, long handle, int process, string value) {
            IntPtr owner=CreateWindowEx(0,"STATIC","",0,0,0,0,0,new IntPtr(-3),IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            if (owner==IntPtr.Zero) throw new InvalidOperationException("ClipboardUnavailable");
            byte[] bytes=Encoding.Unicode.GetBytes(value+"\0");
            IntPtr memory=IntPtr.Zero;
            try {
                AssertFocused(field,handle,process);
                if (!OpenClipboardBounded(owner)) throw new InvalidOperationException("ClipboardUnavailable");
                uint sequence;
                try {
                    clipboardTouched=true;
                    if (!EmptyClipboard()) throw new InvalidOperationException("ClipboardUnavailable");
                    memory=GlobalAlloc(0x42,new UIntPtr((uint)bytes.Length));
                    if (memory==IntPtr.Zero) throw new InvalidOperationException("ClipboardUnavailable");
                    IntPtr address=GlobalLock(memory);
                    if (address==IntPtr.Zero) throw new InvalidOperationException("ClipboardUnavailable");
                    try { Marshal.Copy(bytes,0,address,bytes.Length); } finally { GlobalUnlock(memory); }
                    if (SetClipboardData(13,memory)==IntPtr.Zero) throw new InvalidOperationException("ClipboardUnavailable");
                    memory=IntPtr.Zero;
                    sequence=GetClipboardSequenceNumber();
                } finally { CloseClipboard(); }
                AssertFocused(field,handle,process); NoModifiers();
                // Exact synthetic payload in memory immediately before dispatch; no sequence-only refusal.
                AssertClipboardPayload(value,sequence);
                Send(new INPUT[] {Key(0x11,0),Key(0x56,0),Key(0x56,2),Key(0x11,2)});
                Thread.Sleep(300);
                AssertFocused(field,handle,process);
                AssertClipboardPayload(value,sequence);
            } finally {
                Array.Clear(bytes,0,bytes.Length);
                if (memory!=IntPtr.Zero) GlobalFree(memory);
                try { ClearClipboardFinally(); } finally { DestroyWindow(owner); }
            }
        }
        static void FillKeyboard(AutomationElement field, long handle, int process, string value) {
            uint owner;
            uint thread=GetWindowThreadProcessId(new IntPtr(handle),out owner);
            IntPtr layout=GetKeyboardLayout(thread);
            if ((GetKeyState(0x14)&1)!=0) throw new InvalidOperationException("CapsLockActive");
            for (int i=0;i<value.Length;i++) {
                short map=VkKeyScanEx(value[i],layout);
                KeyboardPlanner.Plan(map,MapVirtualKeyEx((uint)(map&0xff),0,layout));
            }
            for (int i=0;i<value.Length;i++) {
                AssertFocused(field,handle,process); NoModifiers();
                if (GetKeyboardLayout(thread)!=layout || (GetKeyState(0x14)&1)!=0) throw new InvalidOperationException("KeyboardLayoutChanged");
                short map=VkKeyScanEx(value[i],layout);
                Stroke[] strokes=KeyboardPlanner.Plan(map,MapVirtualKeyEx((uint)(map&0xff),0,layout));
                INPUT[] input=new INPUT[strokes.Length];
                try {
                    for (int j=0;j<strokes.Length;j++) { input[j]=Key(strokes[j].Vk,strokes[j].Flags); input[j].data.ki.wScan=strokes[j].Scan; }
                    Send(input);
                } finally { Array.Clear(input,0,input.Length); Array.Clear(strokes,0,strokes.Length); }
                Thread.Sleep(25);
            }
            Thread.Sleep(180); AssertFocused(field,handle,process);
        }
        public static bool TooShortLabelObserved(long handle,int process) {
            AutomationElement label=ById(Root(handle,process),"StrengthLabel");
            return label!=null && label.Current.Name=="Strength: Too short";
        }
    }
}

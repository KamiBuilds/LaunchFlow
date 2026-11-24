using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
namespace NativeUiDriver {
    public static partial class Native {
        // Real native WPF/Win32 controls. All comparison results stay in memory.
        public static void ClipboardPreflight() {
            Window window=null; PasswordBox field=null; Exception uiFailure=null;
            long handle=0; var ready=new ManualResetEvent(false);
            Thread ui=new Thread(delegate() {
                try {
                    window=new Window {Title="Synthetic input preflight",Width=440,Height=180,WindowStartupLocation=WindowStartupLocation.CenterScreen};
                    field=new PasswordBox {Name="SyntheticPassword",Margin=new Thickness(30),Height=40};
                    AutomationProperties.SetAutomationId(field,"SyntheticPassword"); window.Content=field;
                    window.Loaded+=delegate {handle=new WindowInteropHelper(window).Handle.ToInt64();ready.Set();};
                    window.Show(); Dispatcher.Run();
                } catch(Exception e){uiFailure=e;ready.Set();}
            });
            ui.IsBackground=true;ui.SetApartmentState(ApartmentState.STA);ui.Start();
            try {
                if(!ready.WaitOne(10000) || uiFailure!=null || handle==0)throw new InvalidOperationException("DesktopUnavailable");
                string value="Synthetic-"+Guid.NewGuid().ToString("N")+"-\u03a9\u4e2d";
                int process=Process.GetCurrentProcess().Id;
                foreach(string mode in new string[] {"NativeSendInputKeyboard","NativeSendInputUnicode"}) {
                    string synthetic=mode=="NativeSendInputKeyboard"?"Ab8!"+Guid.NewGuid().ToString("N"):value;
                    Fill(handle,process,"SyntheticPassword",synthetic,mode);
                    bool matched=(bool)window.Dispatcher.Invoke(new Func<bool>(delegate {return String.Equals(field.Password,synthetic,StringComparison.Ordinal);}));
                    if(!matched)throw new InvalidOperationException("SendInputNotAccepted");
                    synthetic=null;
                }
                Fill(handle,process,"SyntheticPassword",value,"NativeClipboardPaste");
                bool exact=(bool)window.Dispatcher.Invoke(new Func<bool>(delegate {return String.Equals(field.Password,value,StringComparison.Ordinal);}));
                if(!exact)throw new InvalidOperationException("ClipboardPayloadMismatch");
                // Current clipboard was cleared by Fill. Missing and unrelated payloads must refuse.
                bool refused=false;
                try {AssertClipboardPayload(value,GetClipboardSequenceNumber());}catch(InvalidOperationException e){refused=e.Message=="ClipboardPayloadMismatch";}
                if(!refused)throw new InvalidOperationException("ClipboardPayloadMismatch");
                window.Dispatcher.Invoke(new Action(delegate {System.Windows.Clipboard.SetText("Unrelated synthetic control");}));
                refused=false;
                try {AssertClipboardPayload(value,GetClipboardSequenceNumber());}catch(InvalidOperationException e){refused=e.Message=="ClipboardPayloadMismatch";}
                if(!refused)throw new InvalidOperationException("ClipboardPayloadMismatch");
                // Force an independent clipboard publication with the same exact text and stale sequence.
                uint stale=GetClipboardSequenceNumber();
                window.Dispatcher.Invoke(new Action(delegate {System.Windows.Clipboard.SetText(value);}));
                clipboardTouched=true;AssertClipboardPayload(value,stale);
                if(GetClipboardSequenceNumber()==stale || !ClipboardSequenceChanged)throw new InvalidOperationException("ClipboardPayloadMismatch");
                ClearClipboardFinally();value=null;
            } finally {
                try {ClearClipboardAfterWatchdog();} finally {
                    if(window!=null && !window.Dispatcher.HasShutdownStarted) window.Dispatcher.BeginInvoke(new Action(delegate {field.Clear();window.Close();Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);}));
                    if(!ui.Join(5000))throw new InvalidOperationException("CleanupIncomplete");
                    ready.Dispose();
                }
            }
        }
    }
}

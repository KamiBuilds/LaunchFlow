using System;
using NativeUiDriver;
class MechanismTests {
    static void Require(bool v) { if (!v) throw new Exception("MechanismAssertionFailed"); }
    static void Reject(Action a) { bool refused=false; try { a(); } catch(InvalidOperationException) { refused=true; } Require(refused); }
    static int Main() {
        Require(ClipboardContract.CanDispatch("synthetic-A", "synthetic-A"));
        Require(!ClipboardContract.CanDispatch("synthetic-A", "synthetic-B"));
        Require(!ClipboardContract.CanDispatch("synthetic-A", null));
        Require(!ClipboardContract.CanDispatch(null, null));
        Require(!ClipboardContract.CanDispatch("synthetic-A", "synthetic-a"));
        Require(!ClipboardContract.CanDispatch("synthetic-A", "synthetic-A-extra"));
        Require(!ClipboardContract.CanDispatch("\u00e9", "e\u0301"));
        Require(ClipboardContract.CanDispatch("\u03a9\u4e2d", "\u03a9\u4e2d"));
        var plain=KeyboardPlanner.Plan(0x41, 0x1e);
        Require(plain.Length==2 && plain[0].Flags==8 && plain[1].Flags==10 && plain[0].Scan==0x1e);
        var shifted=KeyboardPlanner.Plan(0x141, 0x1e);
        Require(shifted.Length==4 && shifted[0].Vk==0x10 && shifted[3].Flags==2);
        Reject(()=>KeyboardPlanner.Plan(-1, 1));
        Reject(()=>KeyboardPlanner.Plan(0x241, 1));
        Reject(()=>KeyboardPlanner.Plan(0x41, 0));
        Console.WriteLine("PASS clipboard ordinal payload gate (sequence-independent), keyboard scan/shift/refusal plan");
        return 0;
    }
}

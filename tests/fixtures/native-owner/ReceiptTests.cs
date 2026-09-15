using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
public static class ReceiptTests {
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateHardLink(string link,string target,IntPtr security);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return: MarshalAs(UnmanagedType.I1)] static extern bool CreateSymbolicLink(string link,string target,uint flags);
    static readonly string A=new string('a',32),B=new string('b',32);
    static int passed;
    static Dictionary<string,object> Payload(string value) { return new Dictionary<string,object>{{"challenge",value},{"message","pending notification"},{"seq","1"},{"generation","recovery"},{"note","note-id"}}; }
    static void Expect(bool value,string message) { if(!value) throw new Exception(message); }
    static void Refuses(Action action,string message) { bool refused=false;try{action();}catch{refused=true;}Expect(refused,message); }
    static void Case(string name,Action<NativeHomeLease> test) {
        string home=Path.Combine(Path.GetTempPath(),"fm-receipts-"+Guid.NewGuid().ToString("N"));
        using(var lease=new NativeHomeLease(home)) test(lease);
        passed++;Console.WriteLine("PASS: "+name);
    }
    public static int Run() {
        Case("one writer and unobserved acknowledgement refusal",lease=>{
            using(var journal=new NativeReceiptJournal(lease,A)) {
                Refuses(()=>{using(var other=new NativeReceiptJournal(lease,A)){}},"Concurrent writer accepted");
                var note=journal.Present(Payload("first"));string receipt=(string)note["receipt"];
                Refuses(()=>journal.BeginAcknowledgement(receipt,"wrong"),"Unobserved message accepted");
                journal.BeginAcknowledgement(receipt,"first");journal.CompleteAcknowledgement(receipt);
                Refuses(()=>journal.BeginAcknowledgement(receipt,"first"),"Consumed receipt accepted");
            }
        });
        Case("pending delivery survives reopen and is not replaced",lease=>{
            string receipt;
            using(var journal=new NativeReceiptJournal(lease,A)) receipt=(string)journal.Present(Payload("first"))["receipt"];
            using(var journal=new NativeReceiptJournal(lease,A)) {
                var replay=journal.Present(Payload("second"));Expect((string)replay["receipt"]==receipt && (string)replay["challenge"]=="first","Pending work replaced");
            }
        });
        Case("new generation cannot consume predecessor receipt",lease=>{
            string old;
            using(var journal=new NativeReceiptJournal(lease,A)) old=(string)journal.Present(Payload("first"))["receipt"];
            using(var journal=new NativeReceiptJournal(lease,B)) {
                Refuses(()=>journal.BeginAcknowledgement(old,"first"),"Previous generation authorized");
                Expect((string)journal.Present(Payload("first"))["receipt"]!=old,"Receipt reused across generation");
            }
        });
        Case("interrupted acknowledgement blocks fresh mutation",lease=>{
            string receipt;
            using(var journal=new NativeReceiptJournal(lease,A)) {receipt=(string)journal.Present(Payload("first"))["receipt"];journal.BeginAcknowledgement(receipt,"first");}
            using(var journal=new NativeReceiptJournal(lease,B)) {
                Expect(journal.NeedsReconciliation,"Interrupted attempt lost");
                Refuses(()=>journal.Present(Payload("second")),"New work replaced ambiguous attempt");
                Refuses(()=>journal.CompleteAcknowledgement(receipt),"New generation invented completion");
            }
        });
        Case("completed receipt remains consumed after restart",lease=>{
            string receipt;
            using(var journal=new NativeReceiptJournal(lease,A)) {receipt=(string)journal.Present(Payload("first"))["receipt"];journal.BeginAcknowledgement(receipt,"first");journal.CompleteAcknowledgement(receipt);}
            using(var journal=new NativeReceiptJournal(lease,B)) {
                Expect(!journal.NeedsReconciliation,"Completed attempt became ambiguous");
                Refuses(()=>journal.BeginAcknowledgement(receipt,"first"),"Completed predecessor replay accepted");
                journal.Present(Payload("second"));
            }
        });
        Case("multiple cycles retain independent consumed receipts",lease=>{
            using(var journal=new NativeReceiptJournal(lease,A)) {
                string first=(string)journal.Present(Payload("first"))["receipt"];
                journal.BeginAcknowledgement(first,"first");journal.CompleteAcknowledgement(first);
                string second=(string)journal.Present(Payload("second"))["receipt"];
                Refuses(()=>journal.BeginAcknowledgement(first,"first"),"Earlier cycle replay accepted");
                journal.BeginAcknowledgement(second,"second");journal.CompleteAcknowledgement(second);
            }
        });
        Case("returned objects cannot change persisted target",lease=>{
            using(var journal=new NativeReceiptJournal(lease,A)) {
                var source=Payload("first");var result=journal.Present(source);source["challenge"]="changed";result["challenge"]="changed";
                var actual=journal.BeginAcknowledgement((string)result["receipt"],"first");Expect((string)actual["challenge"]=="first","Caller changed target");
            }
        });
        Case("torn tail is preserved rather than skipped",lease=>{
            using(var journal=new NativeReceiptJournal(lease,A)) journal.Present(Payload("first"));
            string file=Path.Combine(lease.Home,"owner-receipts.jsonl");File.AppendAllText(file,"{\"version\":1");string before=File.ReadAllText(file);
            Refuses(()=>{using(var journal=new NativeReceiptJournal(lease,B)){}},"Torn record accepted");Expect(before==File.ReadAllText(file),"Torn record changed");
        });
        Case("empty existing file is not adopted",lease=>{
            using(var journal=new NativeReceiptJournal(lease,A)) {}
            string file=Path.Combine(lease.Home,"owner-receipts.jsonl");File.WriteAllText(file,"");
            Refuses(()=>{using(var journal=new NativeReceiptJournal(lease,A)){}},"Empty journal adopted");Expect(new FileInfo(file).Length==0,"Ambiguous file overwritten");
        });
        Case("hard-linked receipt file is refused",lease=>{
            using(var journal=new NativeReceiptJournal(lease,A)) journal.Present(Payload("first"));
            string file=Path.Combine(lease.Home,"owner-receipts.jsonl");
            Expect(CreateHardLink(Path.Combine(lease.Home,"alias"),file,IntPtr.Zero),"Hard-link fixture failed");
            Refuses(()=>{using(var journal=new NativeReceiptJournal(lease,A)){}},"Hard-linked receipt accepted");
        });
        Case("symbolic receipt file is refused",lease=>{
            using(var journal=new NativeReceiptJournal(lease,A)) journal.Present(Payload("first"));
            string file=Path.Combine(lease.Home,"owner-receipts.jsonl"),target=Path.Combine(lease.Home,"target");File.Move(file,target);
            Expect(CreateSymbolicLink(file,target,2),"Symlink fixture requires Windows Developer Mode");
            Refuses(()=>{using(var journal=new NativeReceiptJournal(lease,A)){}},"Symbolic receipt accepted");
        });
        Case("released home lease revokes journal operations",lease=>{
            using(var journal=new NativeReceiptJournal(lease,A)) {
                var note=journal.Present(Payload("first"));lease.Dispose();
                Refuses(()=>journal.BeginAcknowledgement((string)note["receipt"],"first"),"Released lease authorized mutation");
            }
        });
        Console.WriteLine("RECEIPT_TESTS_PASS "+passed);return 0;
    }
}

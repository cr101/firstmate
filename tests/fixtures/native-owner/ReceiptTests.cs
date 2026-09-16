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
    static string Queue(NativeHomeLease lease) { return Path.Combine(lease.Home,"state",".wake-queue"); }
    static string Pending(NativeHomeLease lease) { return Path.Combine(lease.Home,"state","inbox","note-id.note"); }
    static string Handled(NativeHomeLease lease) { return Path.Combine(lease.Home,"state","inbox","handled","note-id.note"); }
    static void Targets(NativeHomeLease lease) {
        Directory.CreateDirectory(Path.GetDirectoryName(Handled(lease)));
        File.WriteAllText(Pending(lease),"original captured inbox record\n");
        File.WriteAllText(Queue(lease),"1\t1\tcheck\tinbox:note-id\tcaptain inbox note\n");
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
        foreach(string scenario in new [] {"complete","newer","pending","note-only","wake-only","changed-note","missing-queue","malformed-queue","old-row-remains","no-evidence","duplicate-pending","reparse-handled"}) {
            Case("interrupted recovery: "+scenario,lease=>{
                Targets(lease);string receipt;
                using(var journal=new NativeReceiptJournal(lease,A)) {
                    var delivery=journal.Present(Payload("first"));receipt=(string)delivery["receipt"];
                    var evidence=NativeAcknowledgementEvidence.Capture(lease,delivery);
                    journal.BeginAcknowledgement(receipt,"first",scenario=="no-evidence" ? null : evidence);
                }
                if(scenario!="pending" && scenario!="wake-only") File.Move(Pending(lease),Handled(lease));
                if(scenario!="pending" && scenario!="note-only") File.WriteAllText(Queue(lease),"");
                if(scenario=="newer") File.WriteAllText(Queue(lease),"2\t2\tcheck\tnew-work\tuntouched\n");
                if(scenario=="changed-note") File.AppendAllText(Handled(lease),"changed");
                if(scenario=="missing-queue") File.Delete(Queue(lease));
                if(scenario=="malformed-queue") File.WriteAllText(Queue(lease),"torn");
                if(scenario=="old-row-remains") File.WriteAllText(Queue(lease),"1\t1\tcheck\tdifferent-key\tunknown\n");
                if(scenario=="duplicate-pending") File.Copy(Handled(lease),Pending(lease));
                if(scenario=="reparse-handled") {
                    string original=Path.GetDirectoryName(Handled(lease)),other=Path.Combine(lease.Home,"other-handled");Directory.Move(original,other);
                    Expect(CreateSymbolicLink(original,other,3),"Directory symlink fixture failed");
                }
                string before=File.Exists(Queue(lease)) ? File.ReadAllText(Queue(lease)) : null;
                bool complete=scenario=="complete" || scenario=="newer";
                using(var journal=new NativeReceiptJournal(lease,B)) {
                    Expect(journal.ReconcileCompletedAcknowledgements()==(complete ? 1 : 0),"Incorrect recovery classification: "+scenario);
                    Expect(journal.NeedsReconciliation!=complete,"Incorrect reconciliation obligation");
                    Refuses(()=>journal.BeginAcknowledgement(receipt,"first"),"Old receipt became reusable");
                    Expect(journal.ReconcileCompletedAcknowledgements()==0,"Recovery replay changed history");
                }
                using(var journal=new NativeReceiptJournal(lease,B)) Expect(journal.NeedsReconciliation!=complete,"Recovery result did not survive reopening");
                Expect(before==(File.Exists(Queue(lease)) ? File.ReadAllText(Queue(lease)) : null),"Recovery mutated wake data");
            });
        }
        Case("acknowledgement evidence cannot cross homes",lease=>{
            Targets(lease);
            string otherHome=Path.Combine(Path.GetTempPath(),"fm-receipts-other-"+Guid.NewGuid().ToString("N"));
            using(var other=new NativeHomeLease(otherHome)) using(var journal=new NativeReceiptJournal(lease,A)) {
                Targets(other);var delivery=journal.Present(Payload("first"));
                var evidence=NativeAcknowledgementEvidence.Capture(other,delivery);
                Refuses(()=>journal.BeginAcknowledgement((string)delivery["receipt"],"first",evidence),"Foreign home evidence accepted");
                Expect(!journal.NeedsReconciliation,"Rejected evidence created an attempt");
            }
        });
        foreach(string scenario in new [] {"no-inbox-targets","multiple-complete","multiple-partial"}) {
            Case("general wake recovery: "+scenario,lease=>{
                Targets(lease);var payload=Payload("general");payload.Remove("note");
                if(scenario=="no-inbox-targets") {
                    payload["notes"]=new string[0];File.WriteAllText(Queue(lease),"1\t1\tcheck\tdiagnostic\treport\n");
                } else {
                    payload["notes"]=new [] {"note-id","second"};payload["seq"]="2";
                    File.WriteAllText(Path.Combine(lease.Home,"state","inbox","second.note"),"second notification");
                    File.AppendAllText(Queue(lease),"2\t2\tcheck\tinbox:second\tsecond\n");
                }
                using(var journal=new NativeReceiptJournal(lease,A)) {
                    var delivery=journal.Present(payload);
                    journal.BeginAcknowledgement((string)delivery["receipt"],"general",NativeAcknowledgementEvidence.Capture(lease,delivery));
                }
                if(scenario!="no-inbox-targets") {
                    File.Move(Pending(lease),Handled(lease));
                    if(scenario=="multiple-complete")File.Move(Path.Combine(lease.Home,"state","inbox","second.note"),Path.Combine(lease.Home,"state","inbox","handled","second.note"));
                }
                File.WriteAllText(Queue(lease),"");
                using(var journal=new NativeReceiptJournal(lease,B)) Expect(journal.ReconcileCompletedAcknowledgements()==(scenario=="multiple-partial"?0:1),"Incorrect general-wake recovery");
                if(scenario=="no-inbox-targets")Expect(File.Exists(Pending(lease)),"Unrelated inbox note was consumed");
            });
        }
        Console.WriteLine("RECEIPT_TESTS_PASS "+passed);return 0;
    }
}

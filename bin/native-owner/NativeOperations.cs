// Shared registered operation dispatch; external callers never choose commands.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
public static partial class NativeOwner {
    static NativeReceiptJournal operationJournal;
    static bool shutdownRequested;
    static IntPtr FileHandle(string path, uint access, uint creation) {
        SA sa = new SA { length=Marshal.SizeOf(typeof(SA)), inherit=1 };
        IntPtr h = CreateFile(path, access, 3, ref sa, creation, 0x80, IntPtr.Zero);
        if (h == new IntPtr(-1)) throw Error("CreateFile");
        return h;
    }
    static SortedDictionary<string,string> EnvironmentFor(string pipe, string session, string home, string nonce) {
        var result = new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) {
            string k = (string)e.Key;
            if (k=="NODE_OPTIONS" || k=="NODE_PATH" || k=="BASH_ENV" || k=="ENV") continue;
            if (k.StartsWith("FM_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("PI_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("NO_MISTAKES", StringComparison.OrdinalIgnoreCase) || k == "CLAUDE_PID" || k == "CLAUDECODE") continue;
            result[k] = (string)e.Value;
        }
        result["FM_PROBE_PIPE"] = pipe; result["FM_PROBE_SESSION"] = session;
        result["FM_PROBE_HOME"] = home; result["FM_PROBE_NONCE"] = nonce;
        result["FM_PROBE_EXE"] = OwnExe;
        return result;
    }
    static ChildScope StartScope(string role, IntPtr parentJob, IntPtr environment, string home, ref SI startup, string purpose="startup") {
        var scope=new ChildScope { job=CreateJobObject(IntPtr.Zero,null), role=role, purpose=purpose };
        IntPtr operationEnvironment=IntPtr.Zero;
        if(scope.job==IntPtr.Zero) throw Error("CreateJobObject child scope");
        try {
            if(role=="owner-operation") NativeOperationLifetime.Configure(scope.job);
            string operation=role=="owner-operation" ? (purpose=="startup" ? "owner-operation" : "notification-operation "+purpose) : "scoped-client "+role;
            if(role=="owner-operation") {
                // Only fixed operations receive the grant; caller claims never select it.
                var values=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach(string key in new [] {"SystemRoot","WINDIR","TEMP","TMP","USERPROFILE","APPDATA","LOCALAPPDATA"}) {
                    string value=Environment.GetEnvironmentVariable(key); if(value!=null) values[key]=value;
                }
                values["PATH"]=@"C:\Program Files\Git\usr\bin;C:\Windows\System32;C:\Windows;C:\Program Files\nodejs;C:\Program Files\GitHub CLI;"+Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"npm");
                // Copy only the controller's registration fields from the prepared block.
                int offset=0;
                while(Marshal.ReadInt16(environment,offset)!=0) {
                    string entry=Marshal.PtrToStringUni(IntPtr.Add(environment,offset)); offset+=(entry.Length+1)*2;
                    int split=entry.IndexOf('='); if(split<=0) continue;
                    string key=entry.Substring(0,split);
                    if(key.StartsWith("FM_PROBE_",StringComparison.Ordinal) || key=="FM_HOME" || key=="MSYS") values[key]=entry.Substring(split+1);
                }
                var block=new StringBuilder(); foreach(var value in values) block.Append(value.Key).Append('=').Append(value.Value).Append('\0'); block.Append('\0');
                operationEnvironment=Marshal.StringToHGlobalUni(block.ToString());
            }
            if(!CreateProcess(OwnExe,new StringBuilder(Quote(OwnExe)+" "+operation),IntPtr.Zero,IntPtr.Zero,true,0x4|0x400|0x200,operationEnvironment==IntPtr.Zero ? environment : operationEnvironment,home,ref startup,out scope.process)) throw Error("CreateProcess child scope");
            if(!AssignProcessToJobObject(parentJob,scope.process.process) || !AssignProcessToJobObject(scope.job,scope.process.process)) throw Error("Assign child scope");
            // The caller records this scope before resuming the process.
            return scope;
        } catch {
            if(scope.process.process!=IntPtr.Zero) { TerminateProcess(scope.process.process,125); CloseHandle(scope.process.thread); CloseHandle(scope.process.process); }
            CloseHandle(scope.job); throw;
        } finally { if(operationEnvironment!=IntPtr.Zero) Marshal.FreeHGlobal(operationEnvironment); }
    }
    static void NotificationRequest(Dictionary<string,object> request,Dictionary<string,object> verdict,NativeHomeLease lease,IntPtr job,IntPtr environment,string home,ref SI startup,List<ChildScope> scopes) {
        bool allowed=lease!=null && (string)verdict["association"]=="associated" && (string)verdict["hostClassification"]=="registered-primary";
        verdict["notificationAuthorized"]=allowed;
        if(!allowed) return;
        string action=request.ContainsKey("action") ? (string)request["action"] : "";
        if(action=="shutdown") {
            shutdownRequested=true;
            foreach(var scope in scopes) if(scope.role=="owner-operation") NativeOperationLifetime.Stop(scope.job,1500);
            verdict["operationState"]="stopped";
            return;
        }
        if(shutdownRequested) { verdict["operationState"]="stopped";return; }
        bool ready=true,startupFailed=false;
        foreach(var scope in scopes) if(scope.purpose=="startup") {
            if(WaitForSingleObject(scope.process.process,0)==WAIT_TIMEOUT) ready=false;
            else {uint code;if(!GetExitCodeProcess(scope.process.process,out code)||code!=0)startupFailed=true;}
        }
        verdict["startupFailed"]=startupFailed;
        if(action=="status") {
            verdict["startupExpired"]=ready;
            verdict["operationState"]=operationJournal.NeedsReconciliation ? "reconciliation-required" : startupFailed ? "startup-failed" : ready ? "ready" : "starting";
            return;
        }
        verdict["startupExpired"]=ready;
        if(pendingOperation!=null && WaitForSingleObject(pendingOperation.process.process,0)!=WAIT_TIMEOUT) {
            uint code; if(!GetExitCodeProcess(pendingOperation.process.process,out code)) throw Error("Operation exit");
            verdict["operationExit"]=code;
            if(code!=0) { verdict["operationState"]="failed"; return; }
            string file=Path.Combine(home,"notification-"+pendingOperation.purpose+".json");
            var output=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
            if(pendingOperation.purpose=="check") {
                if(output.ContainsKey("quiet") && (bool)output["quiet"]) { delivered=null;receipt=null; }
                else {delivered=operationJournal.Present(output);receipt=(string)delivered["receipt"];}
                consumed=false;
            } else { operationJournal.CompleteAcknowledgement(receipt);consumed=true; }
            NativeOperationLifetime.Stop(pendingOperation.job,1500);
            scopes.Remove(pendingOperation);
            CloseHandle(pendingOperation.process.thread);CloseHandle(pendingOperation.process.process);CloseHandle(pendingOperation.job);
            pendingOperation=null;
        }
        if(action=="result") {
            verdict["operationState"]=pendingOperation!=null ? "pending" : operationJournal.NeedsReconciliation ? "reconciliation-required" : consumed ? "acknowledged" : delivered!=null ? "delivered" : "quiet";
            if(delivered!=null) verdict["notification"]=delivered;
            return;
        }
        if(startupFailed) {verdict["operationState"]="startup-failed";return;}
        if(!ready || pendingOperation!=null) { verdict["operationState"]="busy"; return; }
        if(operationJournal.NeedsReconciliation) { verdict["operationState"]="reconciliation-required";return; }
        if(action=="check" && (delivered==null || consumed)) checkStarts++;
        else if(action=="ack" && delivered!=null && !consumed && request.ContainsKey("receipt") && (string)request["receipt"]==receipt && request.ContainsKey("observed") && (string)request["observed"]==(string)delivered["challenge"]) {
            var targetEvidence=NativeAcknowledgementEvidence.Capture(lease,delivered);
            var acknowledged=operationJournal.BeginAcknowledgement(receipt,(string)request["observed"],targetEvidence);
            ackStarts++;
            File.WriteAllText(Path.Combine(home,"notification-ack-request.json"),Json.Serialize(acknowledged));
        } else { verdict["operationState"]="denied"; return; }
        pendingOperation=StartScope("owner-operation",job,environment,home,ref startup,action);
        scopes.Add(pendingOperation);
        if(ResumeThread(pendingOperation.process.thread)==0xffffffff) throw Error("Resume notification operation");
        verdict["operationState"]="pending";
    }
}

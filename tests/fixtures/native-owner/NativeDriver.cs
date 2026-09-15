// Test-only launchers, command dispatch, ACL experiments, and lifecycle controls.
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

public static partial class NativeOwner {
    static NativeReceiptJournal operationJournal;
    static bool shutdownRequested;
    static string LogonSid() {
        using(var identity=WindowsIdentity.GetCurrent()) {
            foreach(var row in TokenGroups(identity.Token,2))
                if (((uint)row["attributes"] & 0xc0000000U)==0xc0000000U) return (string)row["sid"];
        }
        throw new InvalidOperationException("No kernel-marked logon SID available");
    }
    static Dictionary<string,object> TokenFacts() {
        using(var identity=WindowsIdentity.GetCurrent()) return new Dictionary<string,object> {
            {"kind","token-facts"},{"pid",Process.GetCurrentProcess().Id},{"user",identity.User.Value},
            {"groups",TokenGroups(identity.Token,2)},{"restricting",TokenGroups(identity.Token,11)}
        };
    }
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
                // Only this fixed, non-extensible test operation receives the grant.
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
            if(!CreateProcess(OwnExe,new StringBuilder(Quote(OwnExe)+" "+operation),IntPtr.Zero,IntPtr.Zero,true,0x4|0x400,operationEnvironment==IntPtr.Zero ? environment : operationEnvironment,home,ref startup,out scope.process)) throw Error("CreateProcess child scope");
            if(!AssignProcessToJobObject(parentJob,scope.process.process) || !AssignProcessToJobObject(scope.job,scope.process.process)) throw Error("Assign child scope");
            // The caller records this scope before resuming the process.
            return scope;
        } catch {
            if(scope.process.process!=IntPtr.Zero) { TerminateProcess(scope.process.process,125); CloseHandle(scope.process.thread); CloseHandle(scope.process.process); }
            CloseHandle(scope.job); throw;
        } finally { if(operationEnvironment!=IntPtr.Zero) Marshal.FreeHGlobal(operationEnvironment); }
    }
    static void ReleaseFixtureWhenScopesEnd(List<ChildScope> scopes,string home) {
        if(scopes.Count==0) return;
        foreach(var scope in scopes) if(WaitForSingleObject(scope.process.process,0)==WAIT_TIMEOUT) return;
        string done=Path.Combine(home,"boundaries-done");
        if(!File.Exists(done)) File.WriteAllText(done,"complete");
    }
    static int Client(string which) {
        Console.WriteLine(Json.Serialize(TokenFacts()));
        var request = new Dictionary<string,object> {
            {"session",Environment.GetEnvironmentVariable("FM_PROBE_SESSION")},
            {"home",Environment.GetEnvironmentVariable("FM_PROBE_HOME")},
            {"nonce",Environment.GetEnvironmentVariable("FM_PROBE_NONCE")}, {"case",which}, {"claimedRole","primary"}
        };
        if (which == "wrong-session") request["session"] = Guid.NewGuid().ToString("N");
        if (which == "wrong-home") request["home"] = "C:\\not-the-test-home";
        if (which == "wrong-capability") request["nonce"] = "copied-invalid-value";
        using (var pipe = new NamedPipeClientStream(".", Environment.GetEnvironmentVariable("FM_PROBE_PIPE"), PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.Synchronize, PipeOptions.None, TokenImpersonationLevel.Identification, HandleInheritability.None)) {
            pipe.Connect(8000);
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true))
            using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true)) {
                writer.AutoFlush = true; writer.WriteLine(Json.Serialize(request));
                string line = reader.ReadLine();
                if (line == null) throw new IOException("No association response");
                Console.WriteLine(line);
            }
        }
        return 0;
    }
    static void RunFirstmate(string role,bool shouldSucceed) {
        string root=Path.Combine(Path.GetDirectoryName(OwnExe),"firstmate");
        string script=Path.Combine(root,shouldSucceed ? "exercise.sh" : "bin/fm-lock.sh").Replace('\\','/');
        using(var p=Process.Start(new ProcessStartInfo(@"C:\Program Files\Git\bin\bash.exe","--noprofile --norc "+Quote(script)) { UseShellExecute=false,RedirectStandardOutput=!shouldSucceed,RedirectStandardError=!shouldSucceed })) {
            var stdout=shouldSucceed ? System.Threading.Tasks.Task.FromResult("") : p.StandardOutput.ReadToEndAsync(); var stderr=shouldSucceed ? System.Threading.Tasks.Task.FromResult("") : p.StandardError.ReadToEndAsync();
            if(!p.WaitForExit(240000)) { p.Kill(); throw new IOException("Bounded Firstmate test timed out"); }
            File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("FM_PROBE_HOME"),"firstmate-"+role+".json"),Json.Serialize(new Dictionary<string,object>{{"exit",p.ExitCode},{"stdout",stdout.Result},{"stderr",stderr.Result}}));
            if((p.ExitCode==0)!=shouldSucceed) throw new IOException("Unexpected Firstmate operation result for "+role);
        }
    }
    static int Fixture() {
        foreach (string c in new [] {"root", "wrong-session", "wrong-home", "wrong-capability"}) Client(c);
        var child = Process.Start(new ProcessStartInfo(OwnExe, "client inherited-child") { UseShellExecute=false });
        child.WaitForExit(); if (child.ExitCode != 0) return child.ExitCode;
        // Exercise MSYS exec without trying to infer its disappearing ancestors.
        var bash = Process.Start(new ProcessStartInfo(@"C:\Program Files\Git\bin\bash.exe", "-c " + Quote("exec \"$FM_PROBE_EXE\" client msys-exec")) { UseShellExecute=false });
        bash.WaitForExit();
        if(Environment.GetEnvironmentVariable("FM_PROBE_BOUNDARIES")=="1") {
            DateTime deadline=DateTime.UtcNow.AddSeconds(15);
            string done=Path.Combine(Environment.GetEnvironmentVariable("FM_PROBE_HOME"),"boundaries-done");
            while(!File.Exists(done) && DateTime.UtcNow<deadline) Thread.Sleep(25);
            if(!File.Exists(done)) throw new IOException("Boundary fixture did not complete");
        }
        if(Environment.GetEnvironmentVariable("FM_PROBE_EXERCISE")=="1") RunFirstmate("primary",true);
        return bash.ExitCode;
    }
    static int Run(string configPath) {
        var config = Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(configPath));
        string home = Path.GetFullPath((string)config["home"]);
        string executable = (string)config["executable"], arguments = (string)config["arguments"];
        int seconds = Convert.ToInt32(config["timeoutSeconds"]);
        if(config.ContainsKey("registeredHarness")) {
            string expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"npm\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe");
            string host=Path.Combine(Path.GetDirectoryName(OwnExe),"AppHost.mjs");
            string node=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),@"nodejs\node.exe");
            bool direct=(string)config["registeredHarness"]=="codex" && string.Equals(Path.GetFullPath(executable),Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase);
            bool adapter=(string)config["registeredHarness"]=="codex-app-server" && string.Equals(Path.GetFullPath(executable),Path.GetFullPath(node),StringComparison.OrdinalIgnoreCase) && arguments==Quote(host);
            if(!direct && !adapter) throw new InvalidOperationException("Requested runtime does not match the fixed native Codex launch target");
            RegisteredHarness="codex";
        }
        Directory.CreateDirectory(home);
        string session = Guid.NewGuid().ToString("N"), pipeName = "fm-private-probe-" + session, nonce = Guid.NewGuid().ToString("N");
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw Error("CreateJobObject");
        IntPtr env = IntPtr.Zero, stdout=IntPtr.Zero, stderr=IntPtr.Zero, stdin=IntPtr.Zero;
        PI child = new PI(); bool assigned=false, timedOut=false;
        var observations = new List<Dictionary<string,object>>();
        Process outsider = null;
        var scopes=new List<ChildScope>();
        NativeHomeLease lease=null;
        int recoveredAcknowledgements=0;
        try {
            if(config.ContainsKey("leaseHome")) {
                lease=new NativeHomeLease((string)config["leaseHome"]);
                operationJournal=new NativeReceiptJournal(lease,session);
                recoveredAcknowledgements=operationJournal.ReconcileCompletedAcknowledgements();
            }
            var values = EnvironmentFor(pipeName, session, home, nonce);
            values["MSYS"]="winsymlinks:nativestrict";
            if(config.ContainsKey("apiDry") && (bool)config["apiDry"]) values["FM_PROBE_API_DRY"]="1";
            if(config.ContainsKey("ackFault")) {
                string fault=(string)config["ackFault"];
                if(!values.ContainsKey("FM_PROBE_API_DRY") || (fault!="partial" && fault!="complete")) throw new ArgumentException("Fault injection is limited to the model-free fixture");
                values["FM_PROBE_ACK_FAULT"]=fault;
            }
            if(lease!=null) { values["FM_PROBE_LEASE_HOME"]=lease.Home; values["FM_PROBE_LEASE_GENERATION"]=session; values["FM_HOME"]=lease.Home.Replace('\\','/'); }
            if(config.ContainsKey("ownerExercise") && (bool)config["ownerExercise"]) values["FM_PROBE_EXERCISE"]="1";
            if(config.ContainsKey("boundaries") && (bool)config["boundaries"]) values["FM_PROBE_BOUNDARIES"]="1";
            var block = new StringBuilder(); foreach (var e in values) block.Append(e.Key).Append('=').Append(e.Value).Append('\0'); block.Append('\0');
            env = Marshal.StringToHGlobalUni(block.ToString());
            stdout = FileHandle(Path.Combine(home,"stdout.log"),0x40000000,2);
            stderr = FileHandle(Path.Combine(home,"stderr.log"),0x40000000,2);
            stdin = FileHandle("NUL",0x80000000,3);
            SI startup = new SI { cb=Marshal.SizeOf(typeof(SI)), flags=0x100, input=stdin, output=stdout, error=stderr };
            if (!CreateProcess(executable, new StringBuilder(Quote(executable)+" "+arguments), IntPtr.Zero, IntPtr.Zero, true, 0x4 | 0x400, env, home, ref startup, out child)) throw Error("CreateProcess suspended");
            if (!config.ContainsKey("assignJob") || (bool)config["assignJob"]) {
                if (!AssignProcessToJobObject(job,child.process)) throw Error("AssignProcessToJobObject");
                assigned=true;
            }
            FT born, exited, kernel, user;
            if (!GetProcessTimes(child.process,out born,out exited,out kernel,out user)) throw Error("GetProcessTimes");
            if(lease!=null) lease.Publish(child.pid,((ulong)born.high<<32)|born.low,session,pipeName);
            if(config.ContainsKey("ownerOperation") && (bool)config["ownerOperation"]) {
                var operation=StartScope("owner-operation",job,env,home,ref startup);
                scopes.Add(operation);
                if(ResumeThread(operation.process.thread)==0xffffffff) throw Error("Resume owner operation");
            }
            if(values.ContainsKey("FM_PROBE_BOUNDARIES")) {
                foreach(string role in new [] {"worker","nested-primary"}) scopes.Add(StartScope(role,job,env,home,ref startup));
                foreach(var scope in scopes) if(ResumeThread(scope.process.thread)==0xffffffff) throw Error("Resume child scope");
            }
            if (ResumeThread(child.thread) == 0xffffffff) throw Error("ResumeThread");
            if(config.ContainsKey("abandonAfterLaunch") && (bool)config["abandonAfterLaunch"]) {
                if(executable!=OwnExe || !arguments.StartsWith("sleep ",StringComparison.Ordinal)) throw new InvalidOperationException("Abandon test only permits bounded disposable sleepers");
                Console.WriteLine("DISPOSABLE_CONTROLLER_EXIT");
                Environment.Exit(86);
            }
            DateTime deadline=DateTime.UtcNow.AddSeconds(seconds);
            var security = new PipeSecurity(); security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            string pipeAcl=config.ContainsKey("pipeAcl") ? (string)config["pipeAcl"] : "UserOnly";
            if(pipeAcl=="LogonData") {
                // Only this ephemeral endpoint gains data exchange rights for
                // this kernel-authenticated Windows logon, never Everyone or
                // server-instance creation, ACL changes, or filesystem rights.
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(LogonSid()), PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.Synchronize, AccessControlType.Allow));
            } else if(pipeAcl!="UserOnly") throw new InvalidOperationException("Unknown pipe ACL test mode");
            bool outsiderStarted=false;
            while (WaitForSingleObject(child.process,0) == WAIT_TIMEOUT && DateTime.UtcNow < deadline) {
                using (var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security)) {
                    var pending = server.BeginWaitForConnection(null,null);
                    if (!outsiderStarted && config.ContainsKey("outsider") && (bool)config["outsider"]) {
                        var info = new ProcessStartInfo(OwnExe,"client copied-outsider") { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true };
                        foreach (var e in values) info.EnvironmentVariables[e.Key]=e.Value;
                        outsider=Process.Start(info); outsiderStarted=true;
                    }
                    while (!pending.AsyncWaitHandle.WaitOne(50) && WaitForSingleObject(child.process,0) == WAIT_TIMEOUT && DateTime.UtcNow < deadline) { ReleaseFixtureWhenScopesEnd(scopes,home); }
                    if (!pending.IsCompleted) break;
                    server.EndWaitForConnection(pending);
                    uint clientPid;
                    if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out clientPid)) throw Error("GetNamedPipeClientProcessId");
                    using (var reader = new StreamReader(server,Encoding.UTF8,false,1024,true))
                    using (var writer = new StreamWriter(server,new UTF8Encoding(false),1024,true)) {
                        var read = reader.ReadLineAsync();
                        if (!read.Wait(8000)) throw new IOException("Client request timeout");
                        if (read.Result == null || read.Result.Length > 4096) throw new IOException("Invalid request");
                        var request=Json.Deserialize<Dictionary<string,object>>(read.Result);
                        var verdict=Verdict(request,clientPid,child.process,job,child.pid,scopes,session,home,nonce);
                        if(request.ContainsKey("kind") && (string)request["kind"]=="owner") AuthorizeOwner(request,verdict,server,lease,session);
                        if(request.ContainsKey("kind") && (string)request["kind"]=="notification") NotificationRequest(request,verdict,lease,job,env,home,ref startup,scopes);
                        observations.Add(verdict); writer.AutoFlush=true; writer.WriteLine(Json.Serialize(verdict));
                    }
                }
            }
            timedOut=WaitForSingleObject(child.process,0)==WAIT_TIMEOUT;
            if (timedOut) {
                if (assigned) TerminateJobObject(job,124); else TerminateProcess(child.process,124);
                WaitForSingleObject(child.process,5000);
            }
            uint code; if (!GetExitCodeProcess(child.process,out code)) throw Error("GetExitCodeProcess");
            var stale=new Dictionary<string,object>{{"session",session},{"home",home},{"nonce",nonce},{"case","after-root-exit"}};
            observations.Add(Verdict(stale,(uint)Process.GetCurrentProcess().Id,child.process,job,child.pid,scopes,session,home,nonce));
            var result = new Dictionary<string,object> {
                {"rootPid",child.pid},{"registeredHarness",RegisteredHarness},{"rootCreationFileTime",((ulong)born.high<<32)|born.low},
                {"rootExit",code},{"timedOut",timedOut},{"jobAssigned",assigned},{"pipeAcl",pipeAcl},
                {"probeGeneration",session},{"probeLeaseHeld",lease!=null},
                {"pipeDacl",security.GetSecurityDescriptorSddlForm(AccessControlSections.Access)},
                {"recoveredAcknowledgements",recoveredAcknowledgements},{"receiptNeedsReconciliation",operationJournal!=null && operationJournal.NeedsReconciliation},
                {"authorityImplemented",false},{"notificationCheckStarts",checkStarts},{"notificationAckStarts",ackStarts},{"notificationConsumed",consumed},{"observations",observations}
            };
            if (outsider != null) {
                if (!outsider.WaitForExit(10000)) throw new IOException("Outsider probe did not stop");
                result["outsiderExit"]=outsider.ExitCode; result["outsiderOutput"]=outsider.StandardOutput.ReadToEnd(); result["outsiderError"]=outsider.StandardError.ReadToEnd();
            }
            File.WriteAllText(Path.Combine(home,"result.json"),Json.Serialize(result));
            Console.WriteLine(Json.Serialize(result));
            return timedOut ? 124 : (int)code;
        } finally {
            // Only this disposable probe's retained native handles are eligible.
            // Never use a PID lookup to stop an unrelated or reused process.
            if (child.process != IntPtr.Zero && WaitForSingleObject(child.process,0)==WAIT_TIMEOUT) {
                if (assigned) TerminateJobObject(job,125); else TerminateProcess(child.process,125);
            }
            if(config.ContainsKey("ownerExercise") && (bool)config["ownerExercise"]) TerminateJobObject(job,125);
            foreach(var scope in scopes) {
                if(WaitForSingleObject(scope.process.process,0)==WAIT_TIMEOUT) TerminateJobObject(scope.job,125);
                CloseHandle(scope.process.thread); CloseHandle(scope.process.process); CloseHandle(scope.job);
            }
            foreach (IntPtr h in new [] {child.thread,child.process,stdout,stderr,stdin,job}) if (h!=IntPtr.Zero) CloseHandle(h);
            if(env!=IntPtr.Zero) Marshal.FreeHGlobal(env);
            if(operationJournal!=null) { operationJournal.Dispose();operationJournal=null; }
            if(lease!=null) lease.Dispose();
        }
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
        bool ready=true;
        foreach(var scope in scopes) if(scope.purpose=="startup" && WaitForSingleObject(scope.process.process,0)==WAIT_TIMEOUT) ready=false;
        verdict["startupExpired"]=ready;
        if(pendingOperation!=null && WaitForSingleObject(pendingOperation.process.process,0)!=WAIT_TIMEOUT) {
            uint code; if(!GetExitCodeProcess(pendingOperation.process.process,out code)) throw Error("Operation exit");
            verdict["operationExit"]=code;
            if(code!=0) { verdict["operationState"]="failed"; return; }
            string file=Path.Combine(home,"notification-"+pendingOperation.purpose+".json");
            var output=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
            if(pendingOperation.purpose=="check") {
                delivered=operationJournal.Present(output); receipt=(string)delivered["receipt"];
                consumed=false;
            } else { operationJournal.CompleteAcknowledgement(receipt);consumed=true; }
            pendingOperation=null;
        }
        if(action=="result") {
            verdict["operationState"]=pendingOperation!=null ? "pending" : operationJournal.NeedsReconciliation ? "reconciliation-required" : consumed ? "acknowledged" : delivered!=null ? "delivered" : "idle";
            if(delivered!=null) verdict["notification"]=delivered;
            return;
        }
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
    public static int Main(string[] args) {
        try {
            if(args.Length==1 && args[0]=="receipt-tests") { ReceiptTests.Run();return TestOperationLifetime(); }
            if(args.Length>=3 && args[0]=="owner") return OwnerClient(args[1],args[2],args.Length>3 ? args[3] : "");
            if(args.Length>0 && args[0]=="client") return Client(args.Length>1 ? args[1] : "agent-tool");
            if(args.Length==2 && args[0]=="sleep") { int ms=int.Parse(args[1]); if(ms<0 || ms>15000) throw new ArgumentException("Sleep must be bounded"); Thread.Sleep(ms); return 0; }
            if(args.Length==3 && args[0]=="lease-check") { Console.WriteLine(NativeHomeLease.Check(args[1],args[2])); return 0; }
            if(args.Length==2 && args[0]=="notification-operation") {
                if(args[1]!="check" && args[1]!="ack") throw new ArgumentException("Unsupported notification operation");
                string script=Path.Combine(Path.GetDirectoryName(OwnExe),"firstmate","notification-"+args[1]+".sh");
                using(var operation=Process.Start(new ProcessStartInfo(@"C:\Program Files\Git\bin\bash.exe","--noprofile --norc "+Quote(script)) {UseShellExecute=false})) {
                    if(!operation.WaitForExit(60000)) throw new IOException("Notification operation exceeded its bound");
                    return operation.ExitCode;
                }
            }
            if(args.Length>0 && args[0]=="owner-operation") {
                RunFirstmate("owner-operation",true);
                string state=Path.Combine(Environment.GetEnvironmentVariable("FM_HOME"),"state");
                int live=OwnerClient("alive",state,"native:"+Environment.GetEnvironmentVariable("FM_PROBE_SESSION"));
                int unknown=OwnerClient("alive",state,"native:00000000000000000000000000000000");
                if(live!=0 || unknown!=2) throw new IOException("Liveness classification failed");
                int verb=OwnerClient("unregistered-verb",state,"");
                if(verb!=2) throw new IOException("Unknown verb granted");
                File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("FM_PROBE_HOME"),"owner-operation.complete"),Json.Serialize(new {live=live,unknown=unknown,forbiddenVerb=verb}));
                return 0;
            }
            if(args.Length>0 && args[0]=="bounded-fixture") {
                string home=Environment.GetEnvironmentVariable("FM_PROBE_HOME");
                DateTime limit=DateTime.UtcNow.AddSeconds(260);
                while(!File.Exists(Path.Combine(home,"owner-operation.complete")) && DateTime.UtcNow<limit) {
                    string outcome=Path.Combine(home,"firstmate-owner-operation.json");
                    if(File.Exists(outcome)) {
                        var recorded=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(outcome));
                        if(Convert.ToInt32(recorded["exit"])!=0) throw new IOException("Real startup operation failed; see startup.log");
                    }
                    Thread.Sleep(25);
                }
                if(!File.Exists(Path.Combine(home,"owner-operation.complete"))) throw new IOException("Owner operation did not complete");
                RunFirstmate("unregistered",false);
                while(!File.Exists(Path.Combine(home,"boundaries-done")) && DateTime.UtcNow<limit) Thread.Sleep(25);
                if(!File.Exists(Path.Combine(home,"boundaries-done"))) throw new IOException("Boundary operations did not complete");
                return 0;
            }
            if(args.Length>0 && args[0]=="fixture") return Fixture();
            if(args.Length==2 && args[0]=="owner-fixture") { Client("scope-"+args[1]); RunFirstmate(args[1],false); return 0; }
            if(args.Length==2 && args[0]=="scoped-client") {
                Client("scope-"+args[1]);
                bool exercise=Environment.GetEnvironmentVariable("FM_PROBE_EXERCISE")=="1";
                if(exercise) RunFirstmate(args[1],false);
                var descendant=Process.Start(new ProcessStartInfo(OwnExe,(exercise ? "owner-fixture " : "client scope-")+args[1]+"-descendant") { UseShellExecute=false });
                descendant.WaitForExit(); return descendant.ExitCode;
            }
            if(args.Length==2 && args[0]=="run") return Run(args[1]);
            Console.Error.WriteLine("usage: NativeOwner run <config.json> | client [case] | fixture"); return 2;
        } catch(Exception e) { Console.Error.WriteLine(e.ToString()); return 2; }
    }
}

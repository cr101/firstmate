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
    static int EnvironmentTests() {
        string directory=Path.Combine(Path.GetTempPath(),"fm-native-environment-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string payload=Path.Combine(directory,"payload.js"),main=Path.Combine(directory,"main.js"),injected=Path.Combine(directory,"injected"),result=Path.Combine(directory,"result.json");
        File.WriteAllText(payload,"require('fs').writeFileSync("+Json.Serialize(injected.Replace('\\','/'))+",'injected')");
        File.WriteAllText(main,"const fs=require('fs');const denied=['NODE_OPTIONS','NODE_PATH','BASH_ENV','ENV','CLAUDE_PID','CLAUDECODE'];const leaked=Object.keys(process.env).filter(k=>denied.includes(k.toUpperCase())||k.toUpperCase().startsWith('FM_')||k.toUpperCase().startsWith('PI_')||k.toUpperCase().startsWith('NO_MISTAKES'));fs.writeFileSync("+Json.Serialize(result.Replace('\\','/'))+",JSON.stringify(leaked));");
        var poison=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"node_options","--require=\""+payload.Replace('\\','/')+"\""},{"node_path",directory},{"bash_env",payload},{"env",payload},{"claude_pid","123"},{"claudecode","1"},{"fm_poison","1"},{"pi_poison","1"},{"no_mistakes_poison","1"}};
        var original=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        try {
            foreach(var entry in poison){original[entry.Key]=Environment.GetEnvironmentVariable(entry.Key);Environment.SetEnvironmentVariable(entry.Key,entry.Value);}
            var values=EnvironmentFor("pipe","session",directory,"nonce");
            var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),@"nodejs\node.exe"),Quote(main)){UseShellExecute=false};
            info.EnvironmentVariables.Clear();foreach(var entry in values)info.EnvironmentVariables[entry.Key]=entry.Value;
            using(var process=Process.Start(info)){
                if(!process.WaitForExit(10000))throw new TimeoutException("Environment test child exceeded its bound");
                if(process.ExitCode!=0)throw new InvalidOperationException("Environment test child failed");
            }
            var leaked=Json.Deserialize<string[]>(File.ReadAllText(result));
            if(File.Exists(injected)||leaked.Length!=5)throw new InvalidOperationException("Denied inherited environment reached the native host");
            foreach(string key in leaked)if(!key.StartsWith("FM_PROBE_",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected inherited environment reached the native host");
            Console.WriteLine("PASS: inherited Windows environment denylist is case-insensitive");
            return 0;
        } finally {
            foreach(var entry in original)Environment.SetEnvironmentVariable(entry.Key,entry.Value);
        }
    }
    static ChildScope StartFixtureScope(string role,IntPtr parentJob,IntPtr environment,string home,ref SI startup) {
        if(role!="worker" && role!="nested-primary") throw new ArgumentException("Unsupported fixture scope");
        var scope=new ChildScope {job=CreateJobObject(IntPtr.Zero,null),role=role,purpose="fixture"};
        if(scope.job==IntPtr.Zero) throw Error("Create fixture scope job");
        try {
            if(!CreateProcess(OwnExe,new StringBuilder(Quote(OwnExe)+" scoped-client "+role),IntPtr.Zero,IntPtr.Zero,true,0x4|0x400|0x200,environment,home,ref startup,out scope.process)) throw Error("Create fixture scope");
            if(!AssignProcessToJobObject(parentJob,scope.process.process) || !AssignProcessToJobObject(scope.job,scope.process.process)) throw Error("Assign fixture scope");
            return scope;
        } catch {
            if(scope.process.process!=IntPtr.Zero) {TerminateProcess(scope.process.process,125);CloseHandle(scope.process.thread);CloseHandle(scope.process.process);}
            CloseHandle(scope.job);throw;
        }
    }
    static int LeaseCheck(string home,string generation) {
        bool current=false;
        try {
            var binding=NativeHomeLease.Binding(Path.Combine(home,"state"));
            current=binding.ContainsKey("generation") && (string)binding["generation"]==generation;
        } catch(InvalidOperationException error) {
            if(error.Message!="Primary no longer live") throw;
        }
        Console.WriteLine(Json.Serialize(new Dictionary<string,object>{{"probeOwnerCurrent",current}}));
        return 0;
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
            if(config.ContainsKey("ownerExercise") && (bool)config["ownerExercise"]) {
                if(!config.ContainsKey("jqImage") || string.IsNullOrWhiteSpace((string)config["jqImage"])) throw new ArgumentException("Owner exercise requires an explicit local jq image");
                values["FM_PROBE_JQ_IMAGE"]=(string)config["jqImage"];
            }
            values["MSYS"]="winsymlinks:nativestrict";
            if(config.ContainsKey("apiDry") && (bool)config["apiDry"]) values["FM_PROBE_API_DRY"]="1";
            if(config.ContainsKey("startupQueued") && (bool)config["startupQueued"]) {
                string note=config.ContainsKey("startupNote") ? (string)config["startupNote"] : null;
                if(!values.ContainsKey("FM_PROBE_API_DRY") || string.IsNullOrEmpty(note)) throw new ArgumentException("Queued-startup fixture requires model-free mode and a note");
                foreach(char c in note) if(!char.IsLetterOrDigit(c) && c!='_' && c!='-') throw new ArgumentException("Invalid queued-startup note");
                values["FM_PROBE_STARTUP_QUEUED"]="1";values["FM_PROBE_STARTUP_NOTE"]=note;
            }
            if(config.ContainsKey("ackFault")) {
                string fault=(string)config["ackFault"];
                if(!values.ContainsKey("FM_PROBE_API_DRY") || (fault!="partial" && fault!="complete")) throw new ArgumentException("Fault injection is limited to the model-free fixture");
                values["FM_PROBE_ACK_FAULT"]=fault;
            }
            if(lease!=null) values["FM_HOME"]=lease.Home.Replace('\\','/');
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
                var operation=StartOwnerOperation(job,env,home,ref startup);
                scopes.Add(operation);
                if(ResumeThread(operation.process.thread)==0xffffffff) throw Error("Resume owner operation");
            }
            if(values.ContainsKey("FM_PROBE_BOUNDARIES")) {
                foreach(string role in new [] {"worker","nested-primary"}) scopes.Add(StartFixtureScope(role,job,env,home,ref startup));
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
                {"authorityImplemented",false},{"notificationConsumed",consumed},{"observations",observations}
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
    public static int Main(string[] args) {
        try {
            int ownerResult;
            if(args.Length==1 && args[0]=="receipt-tests") { ReceiptTests.Run();return TestOperationLifetime(); }
            if(args.Length==1 && args[0]=="environment-tests") return EnvironmentTests();
            if(TryOwnerCommand(args,out ownerResult)) return ownerResult;
            if(args.Length>0 && args[0]=="client") return Client(args.Length>1 ? args[1] : "agent-tool");
            if(args.Length==2 && args[0]=="sleep") { int ms=int.Parse(args[1]); if(ms<0 || ms>15000) throw new ArgumentException("Sleep must be bounded"); Thread.Sleep(ms); return 0; }
            if(args.Length==2 && args[0]=="operation-parent") {
                using(var descendant=Process.Start(new ProcessStartInfo(OwnExe,"sleep 10000") {UseShellExecute=false})) {
                    File.WriteAllText(args[1],descendant.Id.ToString());descendant.WaitForExit();return descendant.ExitCode;
                }
            }
            if(args.Length==3 && args[0]=="lease-check") return LeaseCheck(args[1],args[2]);
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

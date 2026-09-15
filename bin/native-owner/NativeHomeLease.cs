// Experimental home reservation. The temporary-home restriction deliberately
// remains until the production lifecycle and path checks are validated.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
public sealed class NativeHomeLease : IDisposable {
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    FileStream file;
    public readonly string Home;
    public string PreviousGeneration { get; private set; }
    [StructLayout(LayoutKind.Sequential)] struct FT { public uint low,high; }
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(IntPtr process,out FT created,out FT exited,out FT kernel,out FT user);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static string Filename(string home) {
        string full=Path.GetFullPath(home);
        if(!full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Probe leases require a temporary home");
        return Path.Combine(full,"owner-probe.json");
    }
    static Dictionary<string,object> Read(Stream stream) {
        stream.Position=0;
        using(var reader=new StreamReader(stream,Encoding.UTF8,false,1024,true)) return Json.Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
    }
    static bool RootAlive(Dictionary<string,object> record) {
        if(record==null || !record.ContainsKey("state") || (string)record["state"]!="live") throw new InvalidOperationException("Ambiguous or incomplete owner record; preserved");
        uint pid=Convert.ToUInt32(record["rootPid"]);
        if(pid==0) throw new InvalidOperationException("Invalid recorded owner");
        IntPtr handle=OpenProcess(0x1000|0x100000,false,pid);
        if(handle==IntPtr.Zero) {
            int error=Marshal.GetLastWin32Error();
            if(error==87) return false;
            throw new Win32Exception(error,"Recorded owner unreadable; preserved");
        }
        try {
            FT born,exit,kernel,user;
            if(!GetProcessTimes(handle,out born,out exit,out kernel,out user)) throw new Win32Exception(Marshal.GetLastWin32Error());
            ulong creation=((ulong)born.high<<32)|born.low;
            if(creation!=Convert.ToUInt64(record["rootCreationFileTime"])) return false;
            uint wait=WaitForSingleObject(handle,0);
            if(wait==0) return false;
            if(wait!=258) throw new InvalidOperationException("Recorded owner liveness unreadable");
            return true;
        } finally { CloseHandle(handle); }
    }
    public NativeHomeLease(string home) {
        Home=Path.GetFullPath(home);
        string name=Filename(Home);
        Directory.CreateDirectory(Home);
        var security=new FileSecurity(); security.SetAccessRuleProtection(true,false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,FileSystemRights.FullControl,AccessControlType.Allow));
        // The OS arbitrates concurrent controllers before either reads or writes.
        bool created=false;
        try { file=new FileStream(name,FileMode.CreateNew,FileSystemRights.Read|FileSystemRights.Write,FileShare.Read,4096,FileOptions.None,security); created=true; }
        catch(IOException) { file=new FileStream(name,FileMode.Open,FileSystemRights.Read|FileSystemRights.Write,FileShare.Read,4096,FileOptions.None,security); }
        try {
            if(!created && file.Length==0) throw new InvalidOperationException("Empty existing owner record is ambiguous; preserved");
            if(file.Length>0) {
                var previous=Read(file);
                if(RootAlive(previous)) throw new InvalidOperationException("Recorded primary is still alive; refusing replacement");
                PreviousGeneration=previous.ContainsKey("generation") ? (string)previous["generation"] : null;
            }
            Write(new Dictionary<string,object>{{"state","pending"},{"controllerPid",Process.GetCurrentProcess().Id}});
        } catch { Dispose(); throw; }
    }
    void Write(Dictionary<string,object> value) {
        byte[] bytes=Encoding.UTF8.GetBytes(Json.Serialize(value));
        file.Position=0; file.SetLength(0); file.Write(bytes,0,bytes.Length); file.Flush(true);
    }
    public void Publish(uint pid,ulong created,string generation,string pipe) {
        using(var self=Process.GetCurrentProcess()) Write(new Dictionary<string,object>{{"state","live"},{"rootPid",pid},{"rootCreationFileTime",created},{"generation",generation},{"pipe",pipe},{"controllerPid",self.Id},{"controllerCreated",self.StartTime.ToUniversalTime().ToFileTimeUtc()}});
    }
    public static Dictionary<string,object> Binding(string state) {
        string canonical=Path.GetFullPath(state).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        if(!string.Equals(Path.GetFileName(canonical),"state",StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected state directory");
        using(var reader=new FileStream(Filename(Path.GetDirectoryName(canonical)),FileMode.Open,FileAccess.Read,FileShare.ReadWrite)) {
            var record=Read(reader);
            if(!RootAlive(record)) throw new InvalidOperationException("Primary no longer live");
            return record;
        }
    }
    public static string Check(string home,string generation) {
        using(var reader=new FileStream(Filename(home),FileMode.Open,FileAccess.Read,FileShare.ReadWrite)) {
            var record=Read(reader);
            bool same=record.ContainsKey("generation") && (string)record["generation"]==generation;
            return Json.Serialize(new Dictionary<string,object>{{"probeOwnerCurrent",same && RootAlive(record)},{"generationMatches",same},{"authorityGranted",false}});
        }
    }
    public void Dispose() { if(file!=null) { file.Dispose(); file=null; } }
}

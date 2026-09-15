// Durable receipt transitions for a controller that already holds the home lease.
// This is not an authority source: only the authenticated controller may call it.
// Append and flush intent before mutation; ambiguous attempts require reconciliation.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

public sealed class NativeReceiptJournal : IDisposable {
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly Dictionary<string, Dictionary<string,object>> receipts = new Dictionary<string, Dictionary<string,object>>();
    readonly string home, generation;
    readonly NativeHomeLease lease;
    FileStream file;
    const long Limit = 16 * 1024 * 1024;
    [StructLayout(LayoutKind.Sequential)] struct Info {
        public uint attributes, createdLow, createdHigh, accessLow, accessHigh, writeLow, writeHigh;
        public uint volume, sizeHigh, sizeLow, links, indexHigh, indexLow;
    }
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandle(IntPtr handle, out Info info);
    public NativeReceiptJournal(NativeHomeLease ownedLease, string ownerGeneration) {
        if(ownedLease==null || !ownedLease.IsHeld) throw new InvalidOperationException("An active native home lease is required");
        lease=ownedLease;
        if(ownerGeneration==null || ownerGeneration.Length!=32 || !IsHex(ownerGeneration)) throw new ArgumentException("Invalid owner generation");
        home=Path.GetFullPath(ownedLease.Home).TrimEnd('\\','/'); generation=ownerGeneration;
        // The native home lease owns directory validation and exclusion. Never
        // create a directory or select another home based on tool arguments.
        if(!Directory.Exists(home)) throw new IOException("Owned home is absent");
        string name=Path.Combine(home,"owner-receipts.jsonl");
        var security=new FileSecurity();security.SetAccessRuleProtection(true,false);
        var user=WindowsIdentity.GetCurrent().User;
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user,FileSystemRights.FullControl,AccessControlType.Allow));
        bool created=false;
        try {
            try { file=new FileStream(name,FileMode.CreateNew,FileSystemRights.Read|FileSystemRights.Write,FileShare.Read,4096,FileOptions.None,security);created=true; }
            catch(IOException) {
                if((File.GetAttributes(name)&FileAttributes.ReparsePoint)!=0) throw new IOException("Receipt journal is a reparse point");
                file=new FileStream(name,FileMode.Open,FileSystemRights.Read|FileSystemRights.Write,FileShare.Read,4096,FileOptions.None,security);
            }
            var access=file.GetAccessControl();
            if(!access.AreAccessRulesProtected || !access.GetOwner(typeof(SecurityIdentifier)).Equals(user)) throw new IOException("Receipt journal security differs; preserved");
            foreach(FileSystemAccessRule rule in access.GetAccessRules(true,true,typeof(SecurityIdentifier))) if(rule.AccessControlType==AccessControlType.Allow && !rule.IdentityReference.Equals(user)) throw new IOException("Receipt journal grants unexpected access; preserved");
            Info info;
            if(!GetFileInformationByHandle(file.SafeFileHandle.DangerousGetHandle(),out info) || info.links!=1 || (info.attributes&0x400)!=0) throw new IOException("Receipt journal file identity is unsafe");
            if(file.Length>Limit || (!created && file.Length==0)) throw new IOException("Receipt journal is empty or oversized; preserved");
            if(!created) {
                string content;
                using(var reader=new StreamReader(file,new UTF8Encoding(false,true),true,4096,true)) content=reader.ReadToEnd();
                if(!content.EndsWith("\n",StringComparison.Ordinal)) throw new IOException("Interrupted receipt record; preserved");
                foreach(string line in content.Split(new [] {'\n'},StringSplitOptions.RemoveEmptyEntries)) Apply(json.Deserialize<Dictionary<string,object>>(line));
            }
            Append("session",null,null);
        } catch { Dispose();throw; }
    }
    static bool IsHex(string value) { foreach(char c in value) if(!(c>='0'&&c<='9')&&!(c>='a'&&c<='f')) return false;return true; }
    public bool NeedsReconciliation { get { foreach(var row in receipts.Values) if((string)row["event"]=="ack-started") return true;return false; } }
    Dictionary<string,object> Copy(Dictionary<string,object> value) { return json.Deserialize<Dictionary<string,object>>(json.Serialize(value)); }
    void EnsureOpen() { if(file==null || !lease.IsHeld) throw new InvalidOperationException("Receipt journal or owning lease is closed"); }
    public Dictionary<string,object> Present(Dictionary<string,object> payload) {
        EnsureOpen();
        if(NeedsReconciliation) throw new IOException("An acknowledgement was interrupted; reconcile durable work before proceeding");
        foreach(var row in receipts.Values) if((string)row["generation"]==generation && (string)row["event"]=="presented") return Delivery(row);
        if(payload==null || !payload.ContainsKey("challenge") || !(payload["challenge"] is string)) throw new ArgumentException("Notification payload is incomplete");
        string receipt=Guid.NewGuid().ToString("N");
        Append("presented",receipt,Copy(payload));
        return Delivery(receipts[receipt]);
    }
    Dictionary<string,object> Delivery(Dictionary<string,object> row) {
        var result=Copy((Dictionary<string,object>)row["payload"]);result["receipt"]=row["receipt"];return result;
    }
    public Dictionary<string,object> BeginAcknowledgement(string receipt,string observed) {
        EnsureOpen();
        Dictionary<string,object> row;
        if(NeedsReconciliation || receipt==null || !receipts.TryGetValue(receipt,out row) || (string)row["generation"]!=generation || (string)row["event"]!="presented") throw new InvalidOperationException("Receipt is not eligible");
        var payload=(Dictionary<string,object>)row["payload"];
        if((string)payload["challenge"]!=observed) throw new InvalidOperationException("Notification was not observed");
        Append("ack-started",receipt,Copy(payload));
        return Delivery(receipts[receipt]);
    }
    public void CompleteAcknowledgement(string receipt) {
        EnsureOpen();
        Dictionary<string,object> row;
        if(receipt==null || !receipts.TryGetValue(receipt,out row) || (string)row["generation"]!=generation || (string)row["event"]!="ack-started") throw new InvalidOperationException("No matching acknowledgement attempt");
        Append("acknowledged",receipt,Copy((Dictionary<string,object>)row["payload"]));
    }
    void Append(string kind,string receipt,Dictionary<string,object> payload) {
        var row=new Dictionary<string,object>{{"version",1},{"home",home},{"generation",generation},{"event",kind},{"receipt",receipt},{"payload",payload}};
        byte[] bytes=new UTF8Encoding(false,true).GetBytes(json.Serialize(row)+"\n");
        if(bytes.Length>65536 || file.Length+bytes.Length>Limit) throw new IOException("Receipt journal capacity exceeded; durable work preserved");
        try { file.Position=file.Length;file.Write(bytes,0,bytes.Length);file.Flush(true);Apply(row); }
        catch { Dispose();throw; }
    }
    void Apply(Dictionary<string,object> row) {
        if(row==null || Convert.ToInt32(row["version"])!=1 || !string.Equals((string)row["home"],home,StringComparison.OrdinalIgnoreCase)) throw new IOException("Receipt journal binding is invalid");
        string gen=(string)row["generation"],kind=(string)row["event"];
        if(gen==null || gen.Length!=32 || !IsHex(gen)) throw new IOException("Invalid receipt generation");
        if(kind=="session") return;
        string id=(string)row["receipt"];
        if(id==null || id.Length!=32 || !IsHex(id) || !(row["payload"] is Dictionary<string,object>)) throw new IOException("Malformed receipt; preserved");
        Dictionary<string,object> previous;
        bool exists=receipts.TryGetValue(id,out previous);
        if(kind=="presented") { if(exists) throw new IOException("Duplicate receipt; preserved"); }
        else if(kind=="ack-started" || kind=="acknowledged") {
            string expected=kind=="ack-started" ? "presented" : "ack-started";
            if(!exists || (string)previous["event"]!=expected || (string)previous["generation"]!=gen || json.Serialize(previous["payload"])!=json.Serialize(row["payload"])) throw new IOException("Invalid receipt transition; preserved");
        } else throw new IOException("Unknown receipt transition; preserved");
        receipts[id]=row;
    }
    public void Dispose() { if(file!=null) { file.Dispose();file=null; } }
}

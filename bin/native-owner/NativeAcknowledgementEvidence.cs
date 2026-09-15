// Read-only evidence for an interrupted inbox + wake acknowledgement.
// Only complete, matching durable postconditions permit recovery. Partial,
// missing, malformed, or changed evidence never authorizes a retry or deletion.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

public sealed class NativeAcknowledgementEvidence {
    internal readonly NativeHomeLease Lease;
    readonly Dictionary<string,object> record;
    NativeAcknowledgementEvidence(NativeHomeLease lease,Dictionary<string,object> value) { Lease=lease;record=value; }
    internal Dictionary<string,object> Record() { var json=new JavaScriptSerializer();return json.Deserialize<Dictionary<string,object>>(json.Serialize(record)); }
    static string Note(Dictionary<string,object> payload) {
        object value;
        if(!payload.TryGetValue("note",out value) || !(value is string) || !Regex.IsMatch((string)value,@"\A[A-Za-z0-9_-]+\z")) throw new IOException("Invalid inbox target");
        return (string)value;
    }
    static byte[] Read(string home,string relative) {
        home=Path.GetFullPath(home).TrimEnd('\\','/');
        if((File.GetAttributes(home)&FileAttributes.ReparsePoint)!=0) throw new IOException("Reparse point in acknowledgement home");
        string full=Path.Combine(home,relative), parent=full;
        while(!string.Equals(parent,home,StringComparison.OrdinalIgnoreCase)) {
            if((File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0) throw new IOException("Reparse point in acknowledgement evidence");
            parent=Path.GetDirectoryName(parent);
            if(parent==null) throw new IOException("Evidence escaped its home");
        }
        using(var file=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            if(file.Length>16*1024*1024) throw new IOException("Acknowledgement evidence exceeds its bound");
            using(var data=new MemoryStream()) { file.CopyTo(data);return data.ToArray(); }
        }
    }
    static string Hash(byte[] value) { using(var hash=SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(value)).Replace("-","").ToLowerInvariant(); }
    static List<string> Rows(byte[] bytes,ulong cutoff) {
        string text=new UTF8Encoding(false,true).GetString(bytes);
        if(text.Length>0 && !text.EndsWith("\n",StringComparison.Ordinal)) throw new IOException("Incomplete wake queue");
        var result=new List<string>();
        foreach(string line in text.Split(new [] {'\n'},StringSplitOptions.RemoveEmptyEntries)) {
            string[] fields=line.Split('\t');ulong sequence,epoch;
            if(fields.Length<5 || !ulong.TryParse(fields[0],NumberStyles.None,CultureInfo.InvariantCulture,out epoch) || !ulong.TryParse(fields[1],NumberStyles.None,CultureInfo.InvariantCulture,out sequence) || sequence==0 || fields[3].Length==0 || (fields[2]!="check" && fields[2]!="signal" && fields[2]!="stale" && fields[2]!="heartbeat")) throw new IOException("Malformed wake queue");
            if(sequence<=cutoff) result.Add(line);
        }
        return result;
    }
    public static NativeAcknowledgementEvidence Capture(NativeHomeLease lease,Dictionary<string,object> payload) {
        if(lease==null || !lease.IsHeld) throw new InvalidOperationException("An active home lease is required");
        string note=Note(payload);ulong cutoff;
        if(!payload.ContainsKey("seq") || !(payload["seq"] is string) || !ulong.TryParse((string)payload["seq"],NumberStyles.None,CultureInfo.InvariantCulture,out cutoff) || cutoff==0) throw new IOException("Invalid wake cutoff");
        var rows=Rows(Read(lease.Home,Path.Combine("state",".wake-queue")),cutoff);
        bool found=false;foreach(string row in rows) if(row.Split('\t')[3]=="inbox:"+note) found=true;
        if(!found) throw new IOException("Inbox wake is not present at the acknowledged cutoff");
        if(File.Exists(Path.Combine(lease.Home,"state","inbox","handled",note+".note"))) throw new IOException("Inbox target is already handled or ambiguous");
        string digest=Hash(Read(lease.Home,Path.Combine("state","inbox",note+".note")));
        return new NativeAcknowledgementEvidence(lease,new Dictionary<string,object>{{"version",1},{"note",note},{"cutoff",cutoff.ToString(CultureInfo.InvariantCulture)},{"noteSha256",digest},{"rows",rows.ToArray()}});
    }
    internal static bool Completed(NativeHomeLease lease,Dictionary<string,object> evidence) {
        if(lease==null || !lease.IsHeld || evidence==null) return false;
        try {
            if(Convert.ToInt32(evidence["version"])!=1) return false;
            string note=Note(evidence), digest=(string)evidence["noteSha256"];ulong cutoff;
            if(!Regex.IsMatch(digest,@"\A[0-9a-f]{64}\z") || !ulong.TryParse((string)evidence["cutoff"],NumberStyles.None,CultureInfo.InvariantCulture,out cutoff) || cutoff==0) return false;
            // A captured, nonempty target set is mandatory; an empty queue on
            // its own is not evidence that an acknowledgement happened.
            var targets=evidence["rows"] as System.Collections.IList;
            if(targets==null || targets.Count==0) return false;
            var saved=new List<string>();foreach(object target in targets) { if(!(target is string)) return false;saved.Add((string)target); }
            var validated=Rows(new UTF8Encoding(false,true).GetBytes(string.Join("\n",saved.ToArray())+"\n"),cutoff);
            if(validated.Count!=targets.Count || !validated.Exists(row=>row.Split('\t')[3]=="inbox:"+note)) return false;
            if(File.Exists(Path.Combine(lease.Home,"state","inbox",note+".note"))) return false;
            if(Hash(Read(lease.Home,Path.Combine("state","inbox","handled",note+".note")))!=digest) return false;
            return Rows(Read(lease.Home,Path.Combine("state",".wake-queue")),cutoff).Count==0;
        } catch(IOException) { return false; }
        catch(UnauthorizedAccessException) { return false; }
        catch(ArgumentException) { return false; }
        catch(KeyNotFoundException) { return false; }
        catch(InvalidCastException) { return false; }
        catch(FormatException) { return false; }
    }
}

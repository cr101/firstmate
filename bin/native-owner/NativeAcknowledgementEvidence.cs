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
    static string RecoveryGeneration(Dictionary<string,object> payload) {
        object value;
        if(!payload.TryGetValue("generation",out value) || !(value is string) || !Regex.IsMatch((string)value,@"\A[A-Za-z0-9._-]+\z")) throw new IOException("Invalid recovery generation");
        return (string)value;
    }
    static string RecoveryMarker(NativeHomeLease lease) {
        string value=new UTF8Encoding(false,true).GetString(Read(lease.Home,Path.Combine("state",".watcher-down")));
        if(!value.EndsWith("\n",StringComparison.Ordinal) || value.IndexOf('\n')!=value.Length-1) throw new IOException("Invalid recovery marker");
        return value.Substring(0,value.Length-1);
    }
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
        ulong cutoff;
        if(!payload.ContainsKey("seq") || !(payload["seq"] is string) || !ulong.TryParse((string)payload["seq"],NumberStyles.None,CultureInfo.InvariantCulture,out cutoff)) throw new IOException("Invalid wake cutoff");
        var queue=Read(lease.Home,Path.Combine("state",".wake-queue"));
        var rows=Rows(queue,cutoff);
        var ids=new HashSet<string>();
        if(payload.ContainsKey("notes")) {
            var input=payload["notes"] as System.Collections.IList;
            if(input==null)throw new IOException("Invalid inbox target list");
            foreach(object id in input)ids.Add(Note(new Dictionary<string,object>{{"note",id}}));
        } else ids.Add(Note(payload));
        var queued=new HashSet<string>();
        foreach(string row in rows) {string key=row.Split('	')[3];if(key.StartsWith("inbox:",StringComparison.Ordinal))queued.Add(key.Substring(6));}
        if(!ids.SetEquals(queued))throw new IOException("Inbox targets differ from the captured queue");
        if(cutoff==0) {
            if(queue.Length!=0 || rows.Count!=0 || ids.Count!=0)throw new IOException("Invalid zero-row recovery target");
            string generation=RecoveryGeneration(payload),marker=RecoveryMarker(lease);
            if(marker!="pending:handling:"+generation && marker!="announced:handling:"+generation)throw new IOException("Recovery target differs from the captured generation");
            return new NativeAcknowledgementEvidence(lease,new Dictionary<string,object>{{"version",3},{"notes",new object[0]},{"cutoff","0"},{"rows",new object[0]},{"recoveryGeneration",generation},{"recoveryMarker",marker}});
        }
        if(rows.Count==0) throw new IOException("No queued targets remain");
        var notes=new List<Dictionary<string,object>>();
        foreach(string id in ids) {
            if(File.Exists(Path.Combine(lease.Home,"state","inbox","handled",id+".note")))throw new IOException("Inbox target is already handled or ambiguous");
            notes.Add(new Dictionary<string,object>{{"note",id},{"noteSha256",Hash(Read(lease.Home,Path.Combine("state","inbox",id+".note")))}});
        }
        var record=new Dictionary<string,object>{{"version",2},{"notes",notes},{"cutoff",cutoff.ToString(CultureInfo.InvariantCulture)},{"rows",rows.ToArray()}};
        if(notes.Count==1) {record["note"]=notes[0]["note"];record["noteSha256"]=notes[0]["noteSha256"];}
        return new NativeAcknowledgementEvidence(lease,record);
    }
    internal static bool Completed(NativeHomeLease lease,Dictionary<string,object> evidence) {
        if(lease==null || !lease.IsHeld || evidence==null) return false;
        try {
            int version=Convert.ToInt32(evidence["version"]);ulong cutoff;
            if(version!=1 && version!=2 && version!=3)return false;
            if(!ulong.TryParse((string)evidence["cutoff"],NumberStyles.None,CultureInfo.InvariantCulture,out cutoff))return false;
            var targets=evidence["rows"] as System.Collections.IList;
            if(targets==null)return false;
            if(cutoff==0) {
                if(version!=3 || targets.Count!=0)return false;
                var zeroNotes=evidence["notes"] as System.Collections.IList;
                string generation=evidence["recoveryGeneration"] as string,marker=evidence["recoveryMarker"] as string;
                if(zeroNotes==null || zeroNotes.Count!=0 || generation==null || !Regex.IsMatch(generation,@"\A[A-Za-z0-9._-]+\z"))return false;
                if(marker!="pending:handling:"+generation && marker!="announced:handling:"+generation)return false;
                return RecoveryMarker(lease)=="acked:handling:"+generation && Read(lease.Home,Path.Combine("state",".wake-queue")).Length==0;
            }
            if(version==3 || targets.Count==0)return false;
            var saved=new List<string>();foreach(object target in targets){if(!(target is string))return false;saved.Add((string)target);}
            var validated=Rows(new UTF8Encoding(false,true).GetBytes(string.Join("\n",saved.ToArray())+"\n"),cutoff);
            if(validated.Count!=targets.Count)return false;
            var notes=new List<Dictionary<string,object>>();
            if(version==1)notes.Add(evidence);
            else {
                var list=evidence["notes"] as System.Collections.IList;if(list==null)return false;
                foreach(object entry in list){var note=entry as Dictionary<string,object>;if(note==null)return false;notes.Add(note);}
            }
            var ids=new HashSet<string>();
            foreach(var entry in notes) {
                string note=Note(entry),digest=(string)entry["noteSha256"];
                if(!ids.Add(note)||!Regex.IsMatch(digest,@"\A[0-9a-f]{64}\z"))return false;
                if(File.Exists(Path.Combine(lease.Home,"state","inbox",note+".note")))return false;
                if(Hash(Read(lease.Home,Path.Combine("state","inbox","handled",note+".note")))!=digest)return false;
            }
            var queued=new HashSet<string>();foreach(string row in validated){string key=row.Split('	')[3];if(key.StartsWith("inbox:",StringComparison.Ordinal))queued.Add(key.Substring(6));}
            return ids.SetEquals(queued)&&Rows(Read(lease.Home,Path.Combine("state",".wake-queue")),cutoff).Count==0;
        } catch(IOException){return false;} catch(UnauthorizedAccessException){return false;}
        catch(ArgumentException){return false;} catch(KeyNotFoundException){return false;}
        catch(InvalidCastException){return false;} catch(FormatException){return false;} catch(OverflowException){return false;}
    }
}

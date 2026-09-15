using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
public static partial class NativeOwner {
    static PI LifetimeChild(IntPtr job) {
        PI child=new PI();var startup=new SI { cb=Marshal.SizeOf(typeof(SI)) };
        if(!CreateProcess(OwnExe,new StringBuilder(Quote(OwnExe)+" sleep 10000"),IntPtr.Zero,IntPtr.Zero,false,0x4|0x400,IntPtr.Zero,Path.GetTempPath(),ref startup,out child)) throw Error("Create lifetime child");
        try {
            if(!AssignProcessToJobObject(job,child.process)) throw Error("Assign lifetime child");
            if(ResumeThread(child.thread)==0xffffffff) throw Error("Resume lifetime child");
            return child;
        } catch { TerminateProcess(child.process,125);CloseHandle(child.thread);CloseHandle(child.process);throw; }
    }
    static int TestOperationLifetime() {
        using(var independent=Process.Start(new ProcessStartInfo(OwnExe,"sleep 15000") { UseShellExecute=false })) {
            try {
                foreach(bool close in new [] {false,true}) {
                    IntPtr job=CreateJobObject(IntPtr.Zero,null);PI child=new PI();
                    if(job==IntPtr.Zero) throw Error("Create lifetime job");
                    try {
                        NativeOperationLifetime.Configure(job);child=LifetimeChild(job);
                        if(WaitForSingleObject(child.process,0)!=WAIT_TIMEOUT) throw new Exception("Operation was not initially live");
                        if(close) { CloseHandle(job);job=IntPtr.Zero; }
                        else NativeOperationLifetime.Stop(job,3000);
                        if(WaitForSingleObject(child.process,3000)!=0) throw new Exception("Fixed operation survived shutdown");
                        if(independent.HasExited) throw new Exception("Independent process was stopped");
                        Console.WriteLine("PASS: operation "+(close ? "last-handle close" : "bounded stop")+" preserves independent process");
                    } finally {
                        if(child.process!=IntPtr.Zero) { if(WaitForSingleObject(child.process,0)==WAIT_TIMEOUT) TerminateProcess(child.process,125);CloseHandle(child.thread);CloseHandle(child.process); }
                        if(job!=IntPtr.Zero) CloseHandle(job);
                    }
                }
            } finally { if(!independent.HasExited) independent.Kill();independent.WaitForExit(3000); }
        }
        return 0;
    }
}

using System.Collections;
using System.Reflection;
using StateMachine;

// Real FileSystemWatcher events against files owned by this test; no user files are touched.
var root=Path.Combine(Path.GetTempPath(),"Alcyone.PackingChecks."+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var files=new[]{"native.txt","intercepted.txt","cancelled.txt"}.Select(name=>Path.Combine(root,name)).ToArray();
foreach(var path in files)File.WriteAllText(path,"packing check");
var type=typeof(DesktopWallpaperService).Assembly.GetType("StateMachine.DesktopFilePacking",true)!;
using var ready=new SemaphoreSlim(0);
using var observer=(IDisposable)Activator.CreateInstance(type,new object[]{(Action)(()=>ready.Release()),new[]{root}})!;
try
{
    var snapshotType=type.GetNestedType("Snapshot")!;
    var itemType=type.GetNestedType("Item")!;
    var pixels="data:image/png;base64,unchanged-test-pixels";
    var snapshot=Activator.CreateInstance(snapshotType,new object[]{pixels,-80,200,96,120})!;
    var cache=(IDictionary)type.GetField("_snapshots",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(observer)!;
    foreach(var path in files)cache[path]=Activator.CreateInstance(itemType,new object?[]{Path.GetFileName(path),-32,230,null,snapshot});
    File.Delete(files[0]);
    Check(await ready.WaitAsync(TimeSpan.FromSeconds(5)),"Native deletion immediately wakes the animation host");
    var first=Take();
    Check(first is not null,"Native deletion queues an animation");
    Check(ReferenceEquals(itemType.GetProperty("Original")!.GetValue(first),snapshot),"Snapshot pixels and physical bounds survive the watcher unchanged");
    Check(Take() is null,"A deletion queues only one animation");

    Invoke("SuppressObservedDelete",files[1]);
    File.Delete(files[1]);
    Check(!await ready.WaitAsync(TimeSpan.FromMilliseconds(300)),"Intercepted deletion is not animated twice");
    Check(Take() is null,"Intercepted deletion leaves no duplicate replica");

    Invoke("SuppressObservedDelete",files[2]);Invoke("ResumeObservation",files[2]);
    File.Delete(files[2]);
    Check(await ready.WaitAsync(TimeSpan.FromSeconds(5)),"Cancelled interception restores native observation");
    Check(Take() is not null,"Subsequent native deletion is animated");
    Console.WriteLine("All native deletion observer checks passed.");
}
finally
{
    observer.Dispose();
    foreach(var path in files)if(File.Exists(path))File.Delete(path);
    Directory.Delete(root);
}
object? Take()
{
    object?[] arguments={null};
    return (bool)type.GetMethod("TryTake")!.Invoke(observer,arguments)! ? arguments[0] : null;
}
void Invoke(string name,string path)=>type.GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(observer,new object[]{path});
void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS: "+message);}

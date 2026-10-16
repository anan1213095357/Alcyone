using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Drawing.Imaging;
using static StateMachine.DesktopNative;
namespace StateMachine;

// Visual observer only: never deletes or moves user files.
internal sealed class DesktopFilePacking : IDisposable
{
    public sealed record Snapshot(string Image,int Left,int Top,int Width,int Height);
    public sealed record Item(string Name,int X,int Y,string? Id=null,Snapshot? Original=null);
    private readonly ConcurrentDictionary<string,NativePoint> _positions=new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Item> _pending=new();
    private readonly List<FileSystemWatcher> _watchers=new();
    private readonly ConcurrentDictionary<string,Item> _snapshots=new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,byte> _intercepted=new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _directories;
    private readonly Action _ready;
    private readonly Timer _timer;
    private int _sampling,_nextIndex;
    private volatile bool _disposed;
    public DesktopFilePacking(Action ready,string[]? directories=null)
    {
        _ready=ready;
        _directories=(directories ?? new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)}).Distinct(StringComparer.OrdinalIgnoreCase).Where(System.IO.Directory.Exists).ToArray();
        foreach(var directory in _directories)
        {
            var watcher=new FileSystemWatcher(directory) { NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName };
            watcher.Deleted+=(_,e)=>Deleted(e.FullPath);
            watcher.Created+=(sender,e)=>{_snapshots.TryRemove(e.FullPath,out _);_intercepted.TryRemove(e.FullPath,out _);};
            watcher.Renamed+=(sender,e)=>{_snapshots.TryRemove(e.OldFullPath,out _);_intercepted.TryRemove(e.OldFullPath,out _);};
            _watchers.Add(watcher);
            watcher.EnableRaisingEvents=true;
        }
        _timer=new Timer(_=>Sample(),null,0,400);
    }
    internal void RefreshSnapshots(){if(!_disposed)_timer.Change(40,400);}
    internal void SuppressObservedDelete(string path)=>_intercepted[path]=0;
    internal void ResumeObservation(string path)=>_intercepted.TryRemove(path,out _);
    private void Deleted(string path)
    {
        var hasSnapshot=_snapshots.TryRemove(path,out var item);
        if(_disposed || _intercepted.TryRemove(path,out _) || !hasSnapshot || item is null)return;
        _pending.Enqueue(item);
        _ready();
    }
    public bool TryTake(out Item? item)=>_pending.TryDequeue(out item);
    internal sealed record Selection(string Path,string Name,int X,int Y,DateTime Created,DateTime Written,bool Directory);
    internal List<Selection> SelectedFiles()
    {
        var result=new List<Selection>(); var list=DesktopIconAvoidance.FindIconList();if(list==0)return result;
        GetWindowThreadProcessId(list,out var pid);var process=OpenProcess(0x38,false,pid);if(process==0)return result;
        var buffer=VirtualAllocEx(process,0,2048,0x3000,4);if(buffer==0){CloseHandle(process);return result;}
        var timedOut=false;
        try
        {
            var clock=System.Diagnostics.Stopwatch.StartNew();
            var names=new List<string>();var index=unchecked((nuint)(-1));
            while(names.Count<24 && Send(list,0x100c,index,2,out var found) && unchecked((long)found)>=0)
            {
                if(clock.ElapsedMilliseconds>250)return new();
                index=found;var item=new byte[88];BitConverter.GetBytes((int)found).CopyTo(item,4);BitConverter.GetBytes((buffer+128).ToInt64()).CopyTo(item,24);BitConverter.GetBytes(512).CopyTo(item,32);
                if(!WriteProcessMemory(process,buffer,item,88,out _) || !Send(list,0x1073,found,buffer,out _)){timedOut=true;return new();}
                var text=new byte[1024];if(!ReadProcessMemory(process,buffer+128,text,1024,out _))return new();
                var name=Encoding.Unicode.GetString(text).Split('\0')[0];names.Add(name);
                var rectangle=new byte[16];BitConverter.GetBytes(1).CopyTo(rectangle,0);
                if(WriteProcessMemory(process,buffer,rectangle,16,out _))
                {
                    if(!Send(list,0x100e,index,buffer,out _)){timedOut=true;return new();}
                    if(ReadProcessMemory(process,buffer,rectangle,16,out _))
                    {
                        var point=new NativePoint {X=(BitConverter.ToInt32(rectangle,0)+BitConverter.ToInt32(rectangle,8))/2,Y=(BitConverter.ToInt32(rectangle,4)+BitConverter.ToInt32(rectangle,12))/2};
                        MapWindowPoints(list,0,ref point,1);_positions[name]=point;
                    }
                }
            }
            if(names.Count>=24)return new();
            var paths=new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)}.Distinct(StringComparer.OrdinalIgnoreCase).Where(System.IO.Directory.Exists).SelectMany(System.IO.Directory.EnumerateFileSystemEntries).ToArray();
            foreach(var name in names)
            {
                var candidates=paths.Where(path=>Path.GetFileName(path).Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
                if(candidates.Length==0)candidates=paths.Where(path=>Path.GetFileNameWithoutExtension(path).Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
                // Ambiguous or virtual shell icons are left to Windows, never guessed.
                if(candidates.Length!=1)return new();
                var path=candidates[0];var isDirectory=System.IO.Directory.Exists(path);
                FileSystemInfo info=isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
                if(!_positions.TryGetValue(name,out var point))GetCursorPos(out point);
                result.Add(new(path,Path.GetFileName(path),point.X,point.Y,info.CreationTimeUtc,info.LastWriteTimeUtc,isDirectory));
            }
            return result;
        }
        catch{return new();}
        finally{if(!timedOut)VirtualFreeEx(process,buffer,0,0x8000);CloseHandle(process);}
    }
    private void Sample()
    {
        if(_disposed||Interlocked.Exchange(ref _sampling,1)!=0) return;
        var dpi=SetThreadDpiAwarenessContext(-4);
        nint process=0,buffer=0; var timedOut=false;
        try
        {
            var list=DesktopIconAvoidance.FindIconList();
            if(list==0||!Send(list,0x1004,0,0,out var count)||count>500) return;
            GetWindowThreadProcessId(list,out var pid);process=OpenProcess(0x38,false,pid);if(process==0)return;
            buffer=VirtualAllocEx(process,0,2048,0x3000,4);if(buffer==0)return;
            var clock=System.Diagnostics.Stopwatch.StartNew();
            var paths=_directories.SelectMany(System.IO.Directory.EnumerateFileSystemEntries).ToArray();
            for(var visited=0;visited<(int)count && !_disposed && clock.ElapsedMilliseconds<200;visited++)
            {
                var index=_nextIndex%(int)count;_nextIndex=index+1;
                var item=new byte[88];BitConverter.GetBytes(index).CopyTo(item,4);BitConverter.GetBytes((buffer+128).ToInt64()).CopyTo(item,24);BitConverter.GetBytes(512).CopyTo(item,32);
                if(!WriteProcessMemory(process,buffer,item,88,out _))break;
                if(!Send(list,0x1073,(nuint)index,buffer,out _)){timedOut=true;break;}
                var text=new byte[1024];if(!ReadProcessMemory(process,buffer+128,text,1024,out _))continue;
                var name=Encoding.Unicode.GetString(text).Split('\0')[0];if(name.Length==0)continue;
                var rectangle=new byte[16];BitConverter.GetBytes(1).CopyTo(rectangle,0);WriteProcessMemory(process,buffer,rectangle,16,out _);
                if(!Send(list,0x100e,(nuint)index,buffer,out _)){timedOut=true;break;}
                if(!ReadProcessMemory(process,buffer,rectangle,16,out _))continue;
                var point=new NativePoint { X=(BitConverter.ToInt32(rectangle,0)+BitConverter.ToInt32(rectangle,8))/2,Y=(BitConverter.ToInt32(rectangle,4)+BitConverter.ToInt32(rectangle,12))/2 };
                MapWindowPoints(list,0,ref point,1);_positions[name]=point;
                var matches=paths.Where(path=>Path.GetFileName(path).Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
                if(matches.Length==0)matches=paths.Where(path=>Path.GetFileNameWithoutExtension(path).Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
                if(matches.Length!=1)continue;
                var path=matches[0];
                // Cache every visible icon once, then refresh selected icons before a context menu opens.
                if(_snapshots.ContainsKey(path) && (!Send(list,0x102c,(nuint)index,2,out var state) || (state&2)==0))continue;
                Array.Clear(rectangle); // LVIR_BOUNDS includes Explorer's actual filename and selection.
                if(!WriteProcessMemory(process,buffer,rectangle,16,out _))continue;
                if(!Send(list,0x100e,(nuint)index,buffer,out _)){timedOut=true;break;}
                if(!ReadProcessMemory(process,buffer,rectangle,16,out _))continue;
                var start=new NativePoint {X=BitConverter.ToInt32(rectangle,0),Y=BitConverter.ToInt32(rectangle,4)};
                var end=new NativePoint {X=BitConverter.ToInt32(rectangle,8),Y=BitConverter.ToInt32(rectangle,12)};
                MapWindowPoints(list,0,ref start,1);MapWindowPoints(list,0,ref end,1);
                var snapshot=Capture(list,start,end);
                if(snapshot is not null)_snapshots[path]=new(Path.GetFileName(path),point.X,point.Y,Original:snapshot);
            }
        }
        catch { /* Explorer may restart while its icon list is being sampled. */ }
        finally
        {
            // Explorer may still hold a pointer after a timed-out message.
            if(buffer!=0&&!timedOut)VirtualFreeEx(process,buffer,0,0x8000);
            if(process!=0)CloseHandle(process);
            if(dpi!=0)SetThreadDpiAwarenessContext(dpi);
            Interlocked.Exchange(ref _sampling,0);
        }
    }
    private static Snapshot? Capture(nint list,NativePoint start,NativePoint end)
    {
        var width=end.X-start.X;var height=end.Y-start.Y;
        if(width<=0 || height<=0 || width>1024 || height>512)return null;
        // Never cache a context menu, another app, or a clipped/off-screen icon.
        foreach(var x in new[]{start.X,(start.X+end.X)/2,end.X-1})
            foreach(var y in new[]{start.Y,(start.Y+end.Y)/2,end.Y-1})
                if(WindowFromPoint(new NativePoint {X=x,Y=y})!=list)return null;
        using var bitmap=new Bitmap(width,height,PixelFormat.Format24bppRgb);
        using(var graphics=Graphics.FromImage(bitmap))graphics.CopyFromScreen(start.X,start.Y,0,0,new Size(width,height),CopyPixelOperation.SourceCopy);
        using var stream=new MemoryStream();bitmap.Save(stream,ImageFormat.Png);
        return new("data:image/png;base64,"+Convert.ToBase64String(stream.ToArray()),start.X,start.Y,width,height);
    }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    private static bool Send(nint window,uint message,nuint wp,nint lp,out nuint result)=>SendMessageTimeoutW(window,message,wp,lp,2,100,out result)!=0;
    public void Dispose(){_disposed=true;_timer.Dispose();foreach(var watcher in _watchers)watcher.Dispose();_watchers.Clear();}
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll")] private static extern nint VirtualAllocEx(nint process,nint address,nuint size,uint allocation,uint protection);
    [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(nint process,nint address,nuint size,uint type);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(nint process,nint address,byte[] bytes,nuint size,out nuint read);
    [DllImport("kernel32.dll")] private static extern bool WriteProcessMemory(nint process,nint address,byte[] bytes,nuint size,out nuint written);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}

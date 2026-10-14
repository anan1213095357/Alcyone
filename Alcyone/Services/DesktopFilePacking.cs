using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using static StateMachine.DesktopNative;
namespace StateMachine;

// Visual observer only: never deletes or moves user files.
internal sealed class DesktopFilePacking : IDisposable
{
    public sealed record Item(string Name,int X,int Y,string? Id=null);
    private readonly ConcurrentDictionary<string,NativePoint> _positions=new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Item> _pending=new();
    private readonly List<FileSystemWatcher> _watchers=new();
    private readonly Timer _timer;
    private int _sampling;
    private volatile bool _disposed;
    public DesktopFilePacking()
    {
        _timer=new Timer(_=>Sample(),null,0,4000);
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
        nint process=0,buffer=0; var timedOut=false;
        try
        {
            var list=DesktopIconAvoidance.FindIconList();
            if(list==0||!Send(list,0x1004,0,0,out var count)||count>500) return;
            GetWindowThreadProcessId(list,out var pid);process=OpenProcess(0x38,false,pid);if(process==0)return;
            buffer=VirtualAllocEx(process,0,2048,0x3000,4);if(buffer==0)return;
            var clock=System.Diagnostics.Stopwatch.StartNew();
            for(var index=0;index<(int)count && !_disposed && clock.ElapsedMilliseconds<300;index++)
            {
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
            }
        }
        catch { /* Explorer may restart while its icon list is being sampled. */ }
        finally
        {
            // Explorer may still hold a pointer after a timed-out message.
            if(buffer!=0&&!timedOut)VirtualFreeEx(process,buffer,0,0x8000);
            if(process!=0)CloseHandle(process);Interlocked.Exchange(ref _sampling,0);
        }
    }
    private static bool Send(nint window,uint message,nuint wp,nint lp,out nuint result)=>SendMessageTimeoutW(window,message,wp,lp,2,100,out result)!=0;
    public void Dispose(){_disposed=true;_timer.Dispose();foreach(var watcher in _watchers)watcher.Dispose();_watchers.Clear();}
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll")] private static extern nint VirtualAllocEx(nint process,nint address,nuint size,uint allocation,uint protection);
    [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(nint process,nint address,nuint size,uint type);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(nint process,nint address,byte[] bytes,nuint size,out nuint read);
    [DllImport("kernel32.dll")] private static extern bool WriteProcessMemory(nint process,nint address,byte[] bytes,nuint size,out nuint written);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}

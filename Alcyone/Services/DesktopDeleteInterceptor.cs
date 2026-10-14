using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using static StateMachine.DesktopNative;
namespace StateMachine;

internal sealed class DesktopDeleteInterceptor : IDisposable
{
    private delegate nint Hook(int code,nuint message,nint data);
    private readonly Hook _mouseProc,_keyProc;
    private readonly Func<List<DesktopFilePacking.Selection>> _selection;
    private readonly Action<List<DesktopFilePacking.Selection>> _request;
    private nint _mouse,_key;
    private List<DesktopFilePacking.Selection>? _context,_drag;
    private NativePoint _down;
    private bool _desktopDown,_dropArmed;
    public DesktopDeleteInterceptor(Func<List<DesktopFilePacking.Selection>> selection,Action<List<DesktopFilePacking.Selection>> request)
    {
        _selection=selection;_request=request;_mouseProc=Mouse;_keyProc=Keyboard;
        _mouse=SetWindowsHookExW(14,_mouseProc,GetModuleHandleW(null),0);_key=SetWindowsHookExW(13,_keyProc,GetModuleHandleW(null),0);
        if(_mouse==0 || _key==0){Dispose();throw new System.ComponentModel.Win32Exception();}
    }
    private static bool DesktopWindow(nint window)
    {
        var root=GetAncestor(window,2);var name=new StringBuilder(128);GetClassNameW(root,name,name.Capacity);
        return name.ToString() is "Progman" or "WorkerW";
    }
    private nint Keyboard(int code,nuint message,nint data)
    {
        try
        {
            if(code>=0 && message is 0x0100 or 0x0104 && Marshal.ReadInt32(data)==0x2e && DesktopWindow(GetForegroundWindow()) && GetAsyncKeyState(0x10)>=0)
            {
                var selected=_selection();if(selected.Count>0){_request(selected);return 1;}
            }
        }catch{}
        return CallNextHookEx(_key,code,message,data);
    }
    private nint Mouse(int code,nuint message,nint data)
    {
        try
        {
            if(code<0)return CallNextHookEx(_mouse,code,message,data);
            var point=Marshal.PtrToStructure<MouseData>(data).Point;var window=WindowFromPoint(point);
            if(message==0x0204) _context=null;
            if(message==0x0205 && DesktopWindow(window)) _context=_selection();
            if(message==0x0201)
            {
                if(_context is {Count:>0} && IsDeleteMenu(point))
                {var selected=_context;_context=null;Escape();_request(selected);return 1;}
                _context=null;_down=point;_desktopDown=DesktopWindow(window)&&DesktopIconHitTest.IsIcon(window,point);_drag=null;_dropArmed=false;
            }
            if(message==0x0200 && _desktopDown && Math.Abs(point.X-_down.X)+Math.Abs(point.Y-_down.Y)>10)
            {
                _drag??=_selection();
                if(!_dropArmed && _drag.Count>0 && IsRecycleBin(point)){_dropArmed=true;Escape();}
            }
            if(message==0x0202 && _desktopDown)
            {
                _desktopDown=false;
                if(_dropArmed && _drag is {Count:>0} && IsRecycleBin(point)){_request(_drag);_drag=null;_dropArmed=false;return 1;}
                _drag=null;_dropArmed=false;
            }
        }catch{}
        return CallNextHookEx(_mouse,code,message,data);
    }
    private static string AccessibleName(NativePoint point)
    {
        object? accessible=null;
        try
        {
            if(AccessibleObjectFromPoint(point,out accessible,out var child)!=0 || accessible is null)return "";
            return accessible.GetType().InvokeMember("accName",BindingFlags.GetProperty,null,accessible,new[]{child})?.ToString() ?? "";
        }
        catch{return "";}
        finally{if(accessible is not null && Marshal.IsComObject(accessible))Marshal.ReleaseComObject(accessible);}
    }
    private static bool IsDeleteMenu(NativePoint point)
    {
        var root=GetAncestor(WindowFromPoint(point),2);var name=new StringBuilder(64);GetClassNameW(root,name,name.Capacity);
        if(name.ToString() is "Progman" or "WorkerW")return false;
        GetWindowThreadProcessId(root,out var menuProcess);GetWindowThreadProcessId(DesktopIconAvoidance.FindIconList(),out var desktopProcess);
        if(menuProcess!=desktopProcess)return false;
        var label=AccessibleName(point).Replace("&","").Trim();return label=="Delete"||label.StartsWith("删除")||label.StartsWith("Delete (");
    }
    private static bool IsRecycleBin(NativePoint point)=>DesktopWindow(WindowFromPoint(point)) && AccessibleName(point).Trim() is "回收站" or "Recycle Bin";
    private static void Escape(){keybd_event(0x1b,0,0,0);keybd_event(0x1b,0,2,0);}
    public void Dispose(){if(_mouse!=0)UnhookWindowsHookEx(_mouse);if(_key!=0)UnhookWindowsHookEx(_key);_mouse=0;_key=0;}
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public NativePoint Point;public uint Data,Flags,Time;public nuint Extra; }
    [DllImport("user32.dll")] private static extern nint SetWindowsHookExW(int id,Hook callback,nint module,uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook,int code,nuint message,nint data);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window,uint flag);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key,byte scan,uint flags,nuint extra);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromPoint(NativePoint point,[MarshalAs(UnmanagedType.Interface)] out object? accessible,[MarshalAs(UnmanagedType.Struct)] out object child);
}

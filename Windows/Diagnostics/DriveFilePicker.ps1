#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][long] $OwnerWindow,
    [Parameter(Mandatory)][string] $FilePath,
    [switch] $Cancel
)

$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PickerOwner {
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Key { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData { [FieldOffset(0)] public Mouse Mouse; [FieldOffset(0)] public Key Key; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    private delegate bool Callback(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, Callback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    public static void Click(IntPtr dialog, IntPtr button, long owner) {
        var context = SetThreadDpiAwarenessContext(new IntPtr(-4));
        Point original;
        Rect bounds;
        if (!GetCursorPos(out original) || !GetWindowRect(button, out bounds))
            throw new InvalidOperationException("The private picker button has no bounds.");
        var point = new Point { X=(bounds.Left+bounds.Right)/2, Y=(bounds.Top+bounds.Bottom)/2 };
        try {
            SetForegroundWindow(dialog);
            System.Threading.Thread.Sleep(150);
            if (GetAncestor(GetForegroundWindow(), 3).ToInt64()!=owner)
                throw new InvalidOperationException("The private picker lost foreground ownership.");
            SetCursorPos(point.X,point.Y);
            if (GetAncestor(WindowFromPoint(point),3).ToInt64()!=owner)
                throw new InvalidOperationException("The private picker lost pointer ownership.");
            var input = new[] { new Input { Data=new InputData { Mouse=new Mouse { Flags=2 } } }, new Input { Data=new InputData { Mouse=new Mouse { Flags=4 } } } };
            if (SendInput(2,input,Marshal.SizeOf(typeof(Input)))!=2)
                throw new InvalidOperationException("The private picker click was not delivered.");
            System.Threading.Thread.Sleep(150);
        } finally {
            Point current;
            if (GetCursorPos(out current) && current.X==point.X && current.Y==point.Y) SetCursorPos(original.X,original.Y);
            if(context!=IntPtr.Zero) SetThreadDpiAwarenessContext(context);
        }
    }
    private static Input Keyboard(ushort key, uint flags=0, ushort scan=0) {
        return new Input { Type=1, Data=new InputData { Key=new Key { VirtualKey=key, Flags=flags, Scan=scan } } };
    }
    public static void TypeFileName(IntPtr dialog, IntPtr field, long owner, string path) {
        Click(dialog,field,owner);
        if(GetAncestor(GetForegroundWindow(),3).ToInt64()!=owner)
            throw new InvalidOperationException("The private filename field lost foreground ownership.");
        var select = new[] { Keyboard(17), Keyboard(65), Keyboard(65,2), Keyboard(17,2) };
        if(SendInput(4,select,Marshal.SizeOf(typeof(Input)))!=4)
            throw new InvalidOperationException("Filename selection input failed.");
        var text = new Input[path.Length*2];
        for(int i=0;i<path.Length;i++) { text[i*2]=Keyboard(0,4,path[i]); text[i*2+1]=Keyboard(0,6,path[i]); }
        if(SendInput((uint)text.Length,text,Marshal.SizeOf(typeof(Input)))!=(uint)text.Length)
            throw new InvalidOperationException("Filename typing input failed.");
        System.Threading.Thread.Sleep(300);
    }
    public static IntPtr Find(long owner) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, data) => {
            if (!IsWindowVisible(window) || GetAncestor(window, 3).ToInt64() != owner) return true;
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() != "#32770") return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }
    public static IntPtr FileNameField(IntPtr dialog) {
        IntPtr found=IntPtr.Zero;
        int matches=0;
        EnumChildWindows(dialog,(window,data)=>{
            int id=GetDlgCtrlID(window);
            if((id!=1001 && id!=1148) || !IsWindowVisible(window)) return true;
            var name=new StringBuilder(256);
            GetClassName(window,name,name.Capacity);
            if(name.ToString()!="Edit") return true;
            found=window;
            matches++;
            return true;
        },IntPtr.Zero);
        return matches==1 ? found : IntPtr.Zero;
    }
}
'@

$deadline = [DateTime]::UtcNow.AddSeconds(30)
$handle = [IntPtr]::Zero
while ([DateTime]::UtcNow -lt $deadline) {
    $handle = [PickerOwner]::Find($OwnerWindow)
    if ($handle -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 100
}
if ($handle -eq [IntPtr]::Zero) { throw 'The private reader did not open an owned file picker.' }

if (-not $Cancel) {
    $fileName = [IntPtr]::Zero
    while ([DateTime]::UtcNow -lt $deadline) {
        $fileName = [PickerOwner]::FileNameField($handle)
        if ($fileName -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($fileName -eq [IntPtr]::Zero) { throw 'The owned picker has no unambiguous native file-name field.' }
    [PickerOwner]::TypeFileName($handle, $fileName, $OwnerWindow, $FilePath)
}
$buttonId = if ($Cancel) { 2 } else { 1 }
$button = [PickerOwner]::GetDlgItem($handle, $buttonId)
if ($button -eq [IntPtr]::Zero) { throw 'The owned picker has no native accept/cancel button.' }
[PickerOwner]::Click($handle, $button, $OwnerWindow)
"Owned file picker $buttonId invoked."

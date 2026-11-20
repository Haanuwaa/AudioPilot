function Set-WindowsAppResources {
    param([string[]]$Files,[string]$Icon,[string]$Manifest)
    if (-not ('AudioPilot.Packaging.AppResources' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
namespace AudioPilot.Packaging {
    public static class AppResources {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)] private static extern IntPtr BeginUpdateResourceW(string path, bool deleteExisting);
        [DllImport("kernel32.dll", ExactSpelling=true, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateResourceW(IntPtr handle, IntPtr type, IntPtr name, ushort language, byte[] data, uint length);
        [DllImport("kernel32.dll", ExactSpelling=true, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool EndUpdateResourceW(IntPtr handle, bool discard);
        private static void Put(IntPtr handle, int type, int id, byte[] bytes) {
            if(!UpdateResourceW(handle,new IntPtr(type),new IntPtr(id),0,bytes,(uint)bytes.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public static void Apply(string path, byte[] icon, byte[] manifest) {
            if(icon.Length<6 || BitConverter.ToUInt16(icon,0)!=0 || BitConverter.ToUInt16(icon,2)!=1) throw new InvalidDataException("Invalid app icon.");
            int count=BitConverter.ToUInt16(icon,4);
            if(count<1 || count>32 || 6+16*count>icon.Length || manifest.Length>65536) throw new InvalidDataException("Invalid resource bounds.");
            var group=new byte[6+14*count]; Buffer.BlockCopy(icon,0,group,0,6);
            var handle=BeginUpdateResourceW(path,false); if(handle==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                for(int i=0;i<count;i++) {
                    int entry=6+i*16; uint length=BitConverter.ToUInt32(icon,entry+8), offset=BitConverter.ToUInt32(icon,entry+12);
                    if(length==0 || (ulong)offset+length>(ulong)icon.Length) throw new InvalidDataException("Truncated icon image.");
                    var frame=new byte[checked((int)length)]; Buffer.BlockCopy(icon,checked((int)offset),frame,0,frame.Length);
                    Put(handle,3,i+1,frame); Buffer.BlockCopy(icon,entry,group,6+i*14,12);
                    Buffer.BlockCopy(BitConverter.GetBytes((ushort)(i+1)),0,group,6+i*14+12,2);
                }
                Put(handle,14,1,group); Put(handle,24,1,manifest);
                if(!EndUpdateResourceW(handle,false)) throw new Win32Exception(Marshal.GetLastWin32Error());
                handle=IntPtr.Zero;
            } finally { if(handle!=IntPtr.Zero) EndUpdateResourceW(handle,true); }
        }
    }
}
'@
    }
    foreach($path in @($Icon,$Manifest)+$Files) {
        for($current=[IO.Path]::GetFullPath($path);$current;$current=[IO.Path]::GetDirectoryName($current)) {
            if((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Redirected Windows resource input.'}
        }
    }
    $iconBytes=[IO.File]::ReadAllBytes($Icon);$manifestBytes=[IO.File]::ReadAllBytes($Manifest)
    foreach($file in $Files){[AudioPilot.Packaging.AppResources]::Apply($file,$iconBytes,$manifestBytes)}
}

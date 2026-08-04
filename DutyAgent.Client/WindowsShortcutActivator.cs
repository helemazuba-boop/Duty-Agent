using System.Runtime.InteropServices;
using System.Text;

namespace DutyAgent.Client;

internal static class WindowsShortcutActivator
{
    public const string AppUserModelId = "DutyAgent.Client";

    public static void EnsureShortcut()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            "Duty-Agent.lnk");
        if (File.Exists(shortcutPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        var shellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))
            ?? throw new InvalidOperationException("Unable to create ShellLink COM type.");
        var shellLinkObject = Activator.CreateInstance(shellLinkType)
            ?? throw new InvalidOperationException("Unable to create ShellLink COM object.");
        var shellLink = (IShellLinkW)shellLinkObject;
        shellLink.SetPath(Application.ExecutablePath);
        shellLink.SetArguments("");
        shellLink.SetIconLocation(Application.ExecutablePath, 0);

        var propertyStore = (IPropertyStore)shellLinkObject;
        using var appId = new PropVariant(AppUserModelId);
        propertyStore.SetValue(PropertyKeys.AppUserModelId, appId);
        propertyStore.Commit();

        var persistFile = (IPersistFile)shellLinkObject;
        persistFile.Save(shortcutPath, true);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("00000138-0000-0000-C000-000000000046")]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, PropVariant pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    private static class PropertyKeys
    {
        public static PropertyKey AppUserModelId = new()
        {
            FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            PropertyId = 5,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class PropVariant : IDisposable
    {
        private ushort _valueType;
        private ushort _wReserved1;
        private ushort _wReserved2;
        private ushort _wReserved3;
        private IntPtr _value;
        private int _value2;

        public PropVariant(string value)
        {
            _valueType = 31;
            _value = Marshal.StringToCoTaskMemUni(value);
        }

        ~PropVariant()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_value != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_value);
                _value = IntPtr.Zero;
            }

            GC.SuppressFinalize(this);
        }
    }
}

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.VisualStudio.Shell.Interop;

[ComImport]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid service, ref Guid iid, out IntPtr instance);
}

internal static class InspectVsProject
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx context);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: InspectVsProject <DTE moniker> <project path>");
            return 2;
        }

        object dte = GetRunningObject(args[0]);
        IVsSolution solution = GetSolution(dte);
        Guid typeGuid;
        int hr = solution.GetProjectTypeGuid(0, args[1], out typeGuid);
        Console.WriteLine("GetProjectTypeGuid hr=0x{0:X8} guid={1}", hr, typeGuid);

        IVsProjectFactory factory;
        hr = solution.GetProjectFactory(0, null, args[1], out factory);
        Console.WriteLine("GetProjectFactory hr=0x{0:X8} null={1}", hr, factory == null);
        if (factory != null)
        {
            int canCreate;
            hr = factory.CanCreateProject(args[1], 0, out canCreate);
            Console.WriteLine("CanCreateProject hr=0x{0:X8} can={1}", hr, canCreate);
        }

        IEnumHierarchies hierarchyEnum;
        hr = solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_ALLPROJECTS, Guid.Empty, out hierarchyEnum);
        Console.WriteLine("GetProjectEnum hr=0x{0:X8}", hr);
        IVsHierarchy[] hierarchies = new IVsHierarchy[1];
        uint fetched;
        while (hierarchyEnum.Next(1, hierarchies, out fetched) == 0 && fetched == 1)
        {
            Guid projectGuid;
            int guidHr = solution.GetGuidOfProject(hierarchies[0], out projectGuid);
            Console.WriteLine("Hierarchy type={0} guidHr=0x{1:X8} guid={2}",
                hierarchies[0].GetType().FullName, guidHr, projectGuid);
            PrintProperty(hierarchies[0], __VSHPROPID.VSHPROPID_Name);
            PrintProperty(hierarchies[0], __VSHPROPID.VSHPROPID_Caption);
            PrintProperty(hierarchies[0], __VSHPROPID.VSHPROPID_ProjectDir);
            PrintProperty(hierarchies[0], __VSHPROPID.VSHPROPID_ProjectName);
            PrintProperty(hierarchies[0], __VSHPROPID.VSHPROPID_TypeName);
        }

        return 0;
    }

    private static void PrintProperty(IVsHierarchy hierarchy, __VSHPROPID property)
    {
        object value;
        int hr = hierarchy.GetProperty(0xFFFFFFFE, (int)property, out value);
        Console.WriteLine("  {0} hr=0x{1:X8} value={2}", property, hr, value ?? "<null>");
    }

    private static object GetRunningObject(string requestedName)
    {
        IRunningObjectTable table;
        IBindCtx context;
        Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out table));
        Marshal.ThrowExceptionForHR(CreateBindCtx(0, out context));
        IEnumMoniker monikers;
        table.EnumRunning(out monikers);
        IMoniker[] current = new IMoniker[1];
        while (monikers.Next(1, current, IntPtr.Zero) == 0)
        {
            string name;
            current[0].GetDisplayName(context, null, out name);
            if (string.Equals(name, requestedName, StringComparison.Ordinal))
            {
                object value;
                table.GetObject(current[0], out value);
                return value;
            }
        }
        throw new InvalidOperationException("Running object not found: " + requestedName);
    }

    private static IVsSolution GetSolution(object dte)
    {
        IntPtr unknown = Marshal.GetIUnknownForObject(dte);
        IntPtr providerPointer = IntPtr.Zero;
        IntPtr solutionPointer = IntPtr.Zero;
        try
        {
            Guid providerIid = typeof(IOleServiceProvider).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref providerIid, out providerPointer));
            IOleServiceProvider provider = (IOleServiceProvider)Marshal.GetTypedObjectForIUnknown(
                providerPointer, typeof(IOleServiceProvider));
            Guid service = typeof(SVsSolution).GUID;
            Guid iid = typeof(IVsSolution).GUID;
            Marshal.ThrowExceptionForHR(provider.QueryService(ref service, ref iid, out solutionPointer));
            return (IVsSolution)Marshal.GetTypedObjectForIUnknown(solutionPointer, typeof(IVsSolution));
        }
        finally
        {
            if (solutionPointer != IntPtr.Zero) Marshal.Release(solutionPointer);
            if (providerPointer != IntPtr.Zero) Marshal.Release(providerPointer);
            Marshal.Release(unknown);
        }
    }
}

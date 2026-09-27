using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace FaceTrackingClone.VrcftModule;

/// <summary>
/// Teaches the host how to find this module's dependencies.
///
/// VRCFaceTracking loads the module DLL by path but resolves its dependencies against the host
/// process directory, not the module folder. Everything we ship alongside the module --
/// FaceTrackingClone.Core, Microsoft.ML.OnnxRuntime, and the native onnxruntime.dll -- therefore
/// fails to load with FileNotFoundException the moment a type from it is touched.
///
/// This runs as a module initializer, which the runtime executes before the first access to any
/// type in this assembly. That timing is the whole point: a resolver registered inside
/// Initialize() would be too late, because JITting Initialize() is itself what triggers the
/// failed load.
/// </summary>
internal static class ModuleBootstrap
{
    private static string? _moduleDirectory;

    // CA2255 warns that ModuleInitializer is for application code or source generators. This is
    // the exact scenario it carves out: a plugin that must install an assembly resolver before
    // the host touches any of its types. There is no earlier hook available to us.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        Assembly self = typeof(ModuleBootstrap).Assembly;
        _moduleDirectory = Path.GetDirectoryName(self.Location);
        if (string.IsNullOrEmpty(_moduleDirectory)) return;

        // Hook the context this module was actually loaded into. VRCFT may use a custom
        // AssemblyLoadContext for isolation, in which case handlers on Default never fire.
        AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(self)
                                      ?? AssemblyLoadContext.Default;

        context.Resolving += ResolveManaged;
        context.ResolvingUnmanagedDll += ResolveNative;

        if (!ReferenceEquals(context, AssemblyLoadContext.Default))
        {
            AssemblyLoadContext.Default.Resolving += ResolveManaged;
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNative;
        }
    }

    private static Assembly? ResolveManaged(AssemblyLoadContext context, AssemblyName name)
    {
        if (_moduleDirectory is null || name.Name is null) return null;

        string candidate = Path.Combine(_moduleDirectory, name.Name + ".dll");
        if (!File.Exists(candidate)) return null;

        try { return context.LoadFromAssemblyPath(candidate); }
        catch (FileLoadException) { return null; } // Already loaded elsewhere; let the host win.
    }

    private static IntPtr ResolveNative(Assembly requesting, string libraryName)
    {
        if (_moduleDirectory is null) return IntPtr.Zero;

        foreach (string name in new[] { libraryName, libraryName + ".dll" })
        {
            string candidate = Path.Combine(_moduleDirectory, name);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
                return handle;
        }

        return IntPtr.Zero;
    }
}

using System.Reflection;

// Dumps the public API of assemblies we have to compile against but cannot load
// directly (VRCFT 5.4 targets net10.0; this tool runs on net7.0).
// Usage: apidump <assembly-dir> <TypeNameFilter> [more filters...]

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: apidump <assembly-dir> <type-name-substring> [...]");
    return 2;
}

string dir = args[0];
string[] filters = args[1..];

var assemblies = Directory.GetFiles(dir, "*.dll").ToList();

// The resolver needs exactly one core library. A self-contained app directory (like
// VRCFT's) already ships System.Private.CoreLib; adding our own runtime on top makes
// MetadataLoadContext throw "mscorlib has already been loaded".
bool selfContained = assemblies.Any(a =>
    Path.GetFileName(a).Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase));

if (!selfContained)
{
    string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
    assemblies.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));
}

var resolver = new PathAssemblyResolver(assemblies);
using var mlc = new MetadataLoadContext(resolver);

foreach (string file in Directory.GetFiles(dir, "VRCFaceTracking*.dll"))
{
    Assembly asm;
    try { asm = mlc.LoadFromAssemblyPath(file); }
    catch (Exception ex) { Console.Error.WriteLine($"skip {Path.GetFileName(file)}: {ex.Message}"); continue; }

    Type[] types;
    try { types = asm.GetTypes(); }
    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }

    foreach (var t in types.Where(t => t.IsPublic && filters.Any(f =>
                 t.FullName?.Contains(f, StringComparison.OrdinalIgnoreCase) == true)))
    {
        Console.WriteLine();
        Console.WriteLine($"=== {t.FullName}   ({Path.GetFileName(file)})");
        Console.WriteLine($"    {(t.IsAbstract ? "abstract " : "")}{(t.IsSealed ? "sealed " : "")}" +
                          $"{(t.IsEnum ? "enum" : t.IsInterface ? "interface" : "class")}" +
                          $"{(t.BaseType is not null && !t.IsEnum ? $" : {t.BaseType.Name}" : "")}");

        if (t.IsEnum)
        {
            var names = t.GetFields(BindingFlags.Public | BindingFlags.Static);
            Console.WriteLine($"    [{names.Length} values]");
            foreach (var f in names.Take(80)) Console.WriteLine($"      {f.Name}");
            if (names.Length > 80) Console.WriteLine($"      ... +{names.Length - 80} more");
            continue;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var p in t.GetProperties(flags))
        {
            var acc = p.GetMethod ?? p.SetMethod;
            if (acc is null || (!acc.IsPublic && !acc.IsFamily)) continue;
            Console.WriteLine($"    prop {Sig(p.PropertyType)} {p.Name} " +
                              $"{{{(p.CanRead ? " get;" : "")}{(p.CanWrite ? " set;" : "")} }}" +
                              $"{(acc.IsAbstract ? "  [abstract]" : acc.IsVirtual ? "  [virtual]" : "")}");
        }

        foreach (var f in t.GetFields(flags))
        {
            if (!f.IsPublic && !f.IsFamily) continue;
            Console.WriteLine($"    field {Sig(f.FieldType)} {f.Name}");
        }

        foreach (var m in t.GetMethods(flags))
        {
            if (!m.IsPublic && !m.IsFamily) continue;
            if (m.IsSpecialName) continue;
            string ps = string.Join(", ", m.GetParameters().Select(p =>
                $"{(p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "")}{Sig(p.ParameterType)} {p.Name}"));
            Console.WriteLine($"    {Sig(m.ReturnType)} {m.Name}({ps})" +
                              $"{(m.IsAbstract ? "  [abstract]" : m.IsVirtual ? "  [virtual]" : "")}");
        }
    }
}

return 0;

static string Sig(Type t)
{
    if (t.IsByRef) t = t.GetElementType()!;
    if (!t.IsGenericType) return t.Name;
    string args = string.Join(", ", t.GetGenericArguments().Select(Sig));
    string name = t.Name;
    int tick = name.IndexOf('`');
    if (tick >= 0) name = name[..tick];
    return $"{name}<{args}>";
}

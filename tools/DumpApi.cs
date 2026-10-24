using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

// Reflection-only API surface dumper for Cultist Simulator assemblies.
// Usage: DumpApi.exe <managedDir> <assemblyFile> [typeNameFilter]
internal static class DumpApi
{
    private static readonly List<string> ResolveDirs = new List<string>();

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: DumpApi <managedDir> <assemblyFile> [filter]");
            return 2;
        }

        string managedDir = Path.GetFullPath(args[0]);
        string asmFile = args[1];
        string filter = args.Length > 2 ? args[2] : null;

        ResolveDirs.Add(managedDir);
        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += OnReflectionOnlyResolve;
        AppDomain.CurrentDomain.AssemblyResolve += OnResolve;

        string full = Path.Combine(managedDir, asmFile);
        if (!File.Exists(full))
        {
            Console.Error.WriteLine("missing assembly: " + full);
            return 2;
        }

        Assembly asm = Assembly.ReflectionOnlyLoadFrom(full);
        Console.Out.WriteLine("# assembly: " + asm.GetName().Name + " v" + asm.GetName().Version);
        Console.Out.WriteLine("# location: " + full);
        Console.Out.WriteLine();

        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray();
            Console.Error.WriteLine("warn: partial type load, " + ex.LoaderExceptions.Length + " loader errors");
            foreach (Exception le in ex.LoaderExceptions.Take(3))
            {
                Console.Error.WriteLine("  " + le.Message);
            }
        }

        foreach (Type t in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (!Matches(t, filter)) continue;
            DumpType(t);
        }

        return 0;
    }

    private static bool Matches(Type t, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        string n = t.FullName ?? t.Name;
        return n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void DumpType(Type t)
    {
        var sb = new StringBuilder();
        string kind = t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : "class";
        string mods = (t.IsPublic || t.IsNestedPublic) ? "public " : "internal ";
        if (t.IsAbstract && t.IsSealed) mods += "static ";
        else if (t.IsAbstract) mods += "abstract ";
        else if (t.IsSealed && !t.IsValueType) mods += "sealed ";

        sb.Append(mods).Append(kind).Append(' ').Append(t.FullName);
        Type bt = SafeBase(t);
        if (bt != null && bt != typeof(object) && bt != typeof(ValueType) && bt != typeof(Enum))
        {
            sb.Append(" : ").Append(bt.FullName);
        }
        Console.Out.WriteLine(sb.ToString());

        if (t.IsEnum)
        {
            try
            {
                foreach (string name in Enum.GetNames(t))
                {
                    Console.Out.WriteLine("    " + name + " = " + Convert.ToInt64(Enum.Parse(t, name)));
                }
            }
            catch (Exception e) { Console.Out.WriteLine("    <enum values unavailable: " + e.GetType().Name + ">"); }
            Console.Out.WriteLine();
            return;
        }

        foreach (FieldInfo f in SafeFields(t))
        {
            if (f.IsPrivate) continue;
            Console.Out.WriteLine("    field " + (f.IsStatic ? "static " : "") + (f.IsInitOnly ? "readonly " : "") + Pretty(f.FieldType) + " " + f.Name);
        }

        foreach (PropertyInfo p in SafeProps(t))
        {
            MethodInfo g = SafeGet(p.GetGetMethod, p);
            MethodInfo s = SafeGet(p.GetSetMethod, p);
            if (g == null && s == null) continue;
            Console.Out.WriteLine("    prop " + Pretty(p.PropertyType) + " " + p.Name
                + " { " + (g != null ? "get; " : "") + (s != null ? "set; " : "") + "}");
        }

        foreach (MethodInfo m in SafeMethods(t))
        {
            if (m.IsPrivate) continue;
            if (m.IsSpecialName && !m.Name.StartsWith("op_", StringComparison.Ordinal)) continue;
            string stat = m.IsStatic ? "static " : "";
            string ps = string.Join(", ", m.GetParameters()
                .Select(x => Pretty(x.ParameterType) + " " + x.Name).ToArray());
            Console.Out.WriteLine("    method " + stat + Pretty(m.ReturnType) + " " + m.Name + "(" + ps + ")");
        }

        Console.Out.WriteLine();
    }

    private static Type SafeBase(Type t)
    {
        try { return t.BaseType; } catch { return null; }
    }

    private static FieldInfo[] SafeFields(Type t)
    {
        try { return t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); }
        catch { return new FieldInfo[0]; }
    }

    private static PropertyInfo[] SafeProps(Type t)
    {
        try { return t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); }
        catch { return new PropertyInfo[0]; }
    }

    private static MethodInfo[] SafeMethods(Type t)
    {
        try { return t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); }
        catch { return new MethodInfo[0]; }
    }

    private static MethodInfo SafeGet(Func<MethodInfo> get, PropertyInfo p)
    {
        try { return get(); } catch { return null; }
    }

    private static string Pretty(Type t)
    {
        if (t == null) return "?";
        if (!t.IsGenericType) return t.Name;
        string baseName = t.Name;
        int tick = baseName.IndexOf('`');
        if (tick > 0) baseName = baseName.Substring(0, tick);
        return baseName + "<" + string.Join(", ", t.GetGenericArguments().Select(Pretty).ToArray()) + ">";
    }

    private static Assembly OnReflectionOnlyResolve(object sender, ResolveEventArgs args)
    {
        return Probe(args.Name);
    }

    private static Assembly OnResolve(object sender, ResolveEventArgs args)
    {
        return Probe(args.Name);
    }

    private static Assembly Probe(string name)
    {
        try
        {
            var an = new AssemblyName(name);
            foreach (string dir in ResolveDirs)
            {
                string candidate = Path.Combine(dir, an.Name + ".dll");
                if (File.Exists(candidate))
                {
                    try { return Assembly.ReflectionOnlyLoadFrom(candidate); }
                    catch { }
                    try { return Assembly.LoadFrom(candidate); }
                    catch { }
                }
            }
        }
        catch { }
        return null;
    }
}

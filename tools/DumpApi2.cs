using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

// Cecil-based API surface dumper for Cultist Simulator assemblies.
// Reads raw metadata, so modern IL (default interface methods) is not a problem.
//
// usage: DumpApi2 <assembly> [--filter=substr] [--il=TypeName] [--members] [--strings]
internal static class DumpApi2
{
    private static string Filter = null;
    private static string IlTarget = null;
    private static bool ShowStrings;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: DumpApi2 <assembly> [--filter=..] [--il=TypeName] [--strings]");
            return 2;
        }

        string path = args[0];
        foreach (string a in args.Skip(1))
        {
            if (a.StartsWith("--filter=")) Filter = a.Substring(9);
            else if (a.StartsWith("--il=")) IlTarget = a.Substring(5);
            else if (a == "--strings") ShowStrings = true;
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        resolver.AddSearchDirectory(@"D:\SteamLibrary\steamapps\common\Cultist Simulator\cultistsimulator_Data\Managed");

        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false, InMemory = true };
        AssemblyDefinition asm;
        try
        {
            asm = AssemblyDefinition.ReadAssembly(path, rp);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("READ FAILED: " + e.GetType().Name + ": " + e.Message);
            return 3;
        }

        Console.Out.WriteLine("# " + asm.Name.Name + " " + asm.Name.Version);
        Console.Out.WriteLine();

        if (IlTarget != null)
        {
            var target = AllTypes(asm).FirstOrDefault(t =>
                string.Equals(t.FullName, IlTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Name, IlTarget, StringComparison.OrdinalIgnoreCase));
            if (target == null) { Console.Error.WriteLine("type not found: " + IlTarget); return 4; }
            DumpIl(target);
            return 0;
        }

        if (ShowStrings)
        {
            foreach (var t in AllTypes(asm))
            {
                foreach (var s in TypeStrings(t))
                {
                    Console.Out.WriteLine(t.FullName + "\t" + s);
                }
            }
            return 0;
        }

        foreach (var t in AllTypes(asm)
                     .Where(t => Filter == null || t.FullName.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0)
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            DumpType(t);
        }

        return 0;
    }

    private static IEnumerable<TypeDefinition> AllTypes(AssemblyDefinition asm)
    {
        foreach (var m in asm.Modules)
        {
            foreach (var t in AllTypes(m)) yield return t;
        }
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m)
    {
        var stack = new Stack<TypeDefinition>(m.Types);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            yield return t;
            foreach (var n in t.NestedTypes) stack.Push(n);
        }
    }

    private static void DumpType(TypeDefinition t)
    {
        if (t.Name.StartsWith("<") && t.Name != "<Module>") { /* compiler generated, still show briefly */ }

        var sb = new StringBuilder();
        sb.Append(Vis(t)).Append(' ');
        if (t.IsInterface) sb.Append("interface ");
        else if (t.IsEnum) sb.Append("enum ");
        else if (t.IsValueType) sb.Append("struct ");
        else if (t.IsAbstract && t.IsSealed) sb.Append("static class ");
        else sb.Append("class ");
        sb.Append(t.FullName.Replace('/', '+'));
        if (t.BaseType != null && t.BaseType.FullName != "System.Object" &&
            t.BaseType.FullName != "System.ValueType" && t.BaseType.FullName != "System.Enum")
        {
            sb.Append(" : ").Append(t.BaseType.FullName);
        }
        foreach (var i in t.Interfaces)
        {
            sb.Append(t.BaseType == null || (t.BaseType.FullName == "System.Object" || t.BaseType.FullName == "System.ValueType" || t.BaseType.FullName == "System.Enum") ? " : " : ", ")
              .Append(i.InterfaceType.FullName);
        }
        Attrs(sb, t);
        Console.Out.WriteLine(sb.ToString());

        if (t.IsEnum)
        {
            foreach (var f in t.Fields.Where(f => f.IsStatic))
            {
                Console.Out.WriteLine("    " + f.Name + " = " + (f.HasConstant ? Convert.ToString(f.Constant) : "?"));
            }
            Console.Out.WriteLine();
            return;
        }

        foreach (var f in t.Fields)
        {
            if (f.IsPrivate && !f.IsStatic) continue;
            var fs = new StringBuilder("    field ");
            fs.Append(f.IsStatic ? "static " : "").Append(f.IsInitOnly ? "readonly " : "").Append(Vis(f)).Append(' ').Append(Type(f.FieldType)).Append(' ').Append(f.Name);
            Attrs(fs, f);
            Console.Out.WriteLine(fs.ToString());
        }

        foreach (var p in t.Properties)
        {
            var g = p.GetMethod; var s = p.SetMethod;
            if (!Visible(g) && !Visible(s)) continue;
            var ps = new StringBuilder("    prop ").Append(Type(p.PropertyType)).Append(' ').Append(p.Name).Append(" { ");
            if (Visible(g)) ps.Append("get; ");
            if (Visible(s)) ps.Append("set; ");
            ps.Append('}');
            Attrs(ps, p);
            Console.Out.WriteLine(ps.ToString());
        }

        foreach (var m in t.Methods)
        {
            if (!Visible(m)) continue;
            if (m.IsGetter || m.IsSetter || m.IsAddOn || m.IsRemoveOn) continue;
            var ms = new StringBuilder("    method ");
            ms.Append(Vis(m)).Append(' ');
            if (m.IsStatic) ms.Append("static ");
            if (m.IsVirtual && !m.IsNewSlot && !m.IsFinal) ms.Append("override ");
            else if (m.IsVirtual && m.IsNewSlot) ms.Append("virtual ");
            if (m.IsAbstract) ms.Append("abstract ");
            ms.Append(Type(m.ReturnType)).Append(' ').Append(m.Name);
            if (m.HasGenericParameters)
                ms.Append('<').Append(string.Join(", ", m.GenericParameters.Select(g => g.Name).ToArray())).Append('>');
            ms.Append('(').Append(string.Join(", ", m.Parameters.Select(p => Type(p.ParameterType) + " " + p.Name).ToArray())).Append(')');
            Attrs(ms, m);
            Console.Out.WriteLine(ms.ToString());
        }

        Console.Out.WriteLine();
    }

    private static IEnumerable<string> TypeStrings(TypeDefinition t)
    {
        foreach (var m in t.Methods.Where(m => m.HasBody))
        {
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode.Code == Mono.Cecil.Cil.Code.Ldstr)
                    yield return m.Name + " :: " + ins.Operand;
            }
        }
    }

    private static void DumpIl(TypeDefinition t)
    {
        Console.Out.WriteLine("### IL for " + t.FullName);
        foreach (var m in t.Methods.Where(m => m.HasBody))
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("--- " + m.Name + "(" + string.Join(", ", m.Parameters.Select(p => Type(p.ParameterType) + " " + p.Name).ToArray()) + ") : " + Type(m.ReturnType));
            foreach (var v in m.Body.Variables)
                Console.Out.WriteLine("    loc " + v.Index + " " + Type(v.VariableType));
            foreach (var ins in m.Body.Instructions)
            {
                string operand = ins.Operand == null ? "" :
                    ins.Operand is string ? "\"" + ins.Operand + "\"" :
                    ins.Operand is Mono.Cecil.MethodReference mr ? mr.DeclaringType.FullName + "::" + mr.Name :
                    ins.Operand is Mono.Cecil.FieldReference fr ? fr.DeclaringType.FullName + "::" + fr.Name :
                    ins.Operand is Mono.Cecil.TypeReference tr ? tr.FullName :
                    ins.Operand.ToString();
                Console.Out.WriteLine("    " + ins.Offset.ToString("X4") + "  " + ins.OpCode.Name.PadRight(12) + " " + operand);
            }
        }
    }

    private static bool Visible(MethodDefinition m)
    {
        if (m == null) return false;
        if (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly) return true;
        return false;
    }

    private static string Vis(TypeDefinition t)
    {
        if (t.IsPublic || t.IsNestedPublic) return "public";
        if (t.IsNestedFamily) return "protected";
        return "internal";
    }

    private static string Vis(FieldDefinition f)
    {
        if (f.IsPublic) return "public";
        if (f.IsFamily) return "protected";
        if (f.IsAssembly) return "internal";
        if (f.IsFamilyOrAssembly) return "protected internal";
        return "private";
    }

    private static string Vis(MethodDefinition m)
    {
        if (m.IsPublic) return "public";
        if (m.IsFamily) return "protected";
        if (m.IsAssembly) return "internal";
        if (m.IsFamilyOrAssembly) return "protected internal";
        return "private";
    }

    private static void Attrs(StringBuilder sb, ICustomAttributeProvider p)
    {
        if (!p.HasCustomAttributes) return;
        var names = p.CustomAttributes
            .Select(a => a.AttributeType.Name)
            .Where(n => n != "CompilerGeneratedAttribute" && n != "DebuggerBrowsableAttribute")
            .ToArray();
        if (names.Length == 0) return;
        sb.Append("   [").Append(string.Join(", ", names)).Append(']');
    }

    private static string Type(TypeReference t)
    {
        if (t == null) return "?";
        string name;
        if (t is GenericInstanceType g)
        {
            name = g.ElementType.Name;
            int tick = name.IndexOf('`');
            if (tick > 0) name = name.Substring(0, tick);
            return name + "<" + string.Join(", ", g.GenericArguments.Select(Type).ToArray()) + ">";
        }
        if (t is ArrayType a) return Type(a.ElementType) + "[]";
        if (t is ByReferenceType b) return "ref " + Type(b.ElementType);
        name = t.Name;
        int backtick = name.IndexOf('`');
        if (backtick > 0) name = name.Substring(0, backtick);
        var gp = t as GenericParameter;
        if (gp != null) name = gp.Name;
        return name;
    }
}

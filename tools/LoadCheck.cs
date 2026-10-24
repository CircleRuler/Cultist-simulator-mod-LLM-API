using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

// Mirrors exactly what Cultist Simulator's ModManager does when it loads a DLL mod, so the
// discovery can be tested without launching the game. Run under the *game's* mono.
//
//   mono.exe LoadCheck.exe <modsFolder> <modId>
//
// It reproduces:
//   path = Path.Combine(modRoot, "dll", Regex.Replace(name, "[^a-zA-Z0-9_]+", "") + ".dll")
//   if (!File.Exists(path)) -> the loader silently gives up
//   Assembly.LoadFrom(path); assembly.GetType(nameAlphanumericsOnly)
//   type.GetMethod("Initialise", Public|Static)
internal static class LoadCheck
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: LoadCheck <modsFolder> <modId>");
            return 2;
        }

        string modsFolder = args[0];
        string modId = args[1];
        string modRoot = Path.Combine(modsFolder, modId);
        int failures = 0;

        // The mod references UnityEngine and SecretHistories.Main. The real game process has
        // those loaded already, which is how the loader gets away with a bare LoadFrom; a
        // standalone runtime needs them on the search path.
        string managed = args.Length > 2
            ? args[2]
            : @"D:\SteamLibrary\steamapps\common\Cultist Simulator\cultistsimulator_Data\Managed";
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            try
            {
                string simple = new AssemblyName(e.Name).Name;
                string candidate = Path.Combine(managed, simple + ".dll");
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            catch { }
            return null;
        };

        Console.WriteLine("== load check for mod '" + modId + "' ==");
        Console.WriteLine("mod root: " + modRoot);

        // 1. synopsis.json -> name (this is what GetNameAlphanumericsOnly uses)
        string synopsisPath = Path.Combine(modRoot, "synopsis.json");
        if (!File.Exists(synopsisPath))
        {
            Console.WriteLine("FAIL  synopsis.json missing - ModManager.CatalogueMod marks the mod invalid");
            return 1;
        }

        string raw = File.ReadAllText(synopsisPath);
        Match m = Regex.Match(raw, "\"name\"\\s*:\\s*\"([^\"]*)\"");
        if (!m.Success)
        {
            Console.WriteLine("FAIL  could not read \"name\" from synopsis.json");
            return 1;
        }

        string name = m.Groups[1].Value;
        string nameAlnum = Regex.Replace(name, "[^a-zA-Z0-9_]+", "");
        Console.WriteLine("synopsis name = '" + name + "'");
        Console.WriteLine("name alnum    = '" + nameAlnum + "'");

        // 2. the exact path the loader probes
        string dllPath = Path.Combine(modRoot, "dll", nameAlnum + ".dll");
        Console.WriteLine("loader probes = " + dllPath);
        if (!File.Exists(dllPath))
        {
            string fallback = Path.Combine(modRoot, "dll", "main.dll");
            Console.WriteLine("FAIL  not found. fallback main.dll exists: " + File.Exists(fallback));
            Console.WriteLine("      TryLoadDllsForMod returns here WITHOUT logging anything.");
            return 1;
        }
        Console.WriteLine("PASS  dll found (" + new FileInfo(dllPath).Length + " bytes)");

        // 3. does it actually load, and into which type?
        Assembly asm;
        try
        {
            asm = Assembly.LoadFrom(dllPath);
            Console.WriteLine("PASS  Assembly.LoadFrom succeeded: " + asm.GetName().Name);
        }
        catch (Exception e)
        {
            Console.WriteLine("FAIL  Assembly.LoadFrom threw: " + e.GetType().Name + ": " + e.Message);
            return 1;
        }

        Type type = asm.GetType(nameAlnum);
        Console.WriteLine("Assembly.GetType(\"" + nameAlnum + "\") -> " + (type == null ? "NULL" : type.FullName));
        if (type == null)
        {
            Console.WriteLine("FAIL  TryInitialiseAssembly would log \"Can't find a class named " + nameAlnum + "\"");
            failures++;
        }

        // 4. the entry point signature the framework requires
        if (type != null)
        {
            MethodInfo init = type.GetMethod("Initialise", BindingFlags.Public | BindingFlags.Static);
            Console.WriteLine("public static Initialise() -> " + (init == null ? "MISSING" : "FOUND on " + init.DeclaringType.FullName));
            if (init == null) failures++;
        }

        // 5. can its dependencies be resolved on this runtime? Game assemblies are already
        //    loaded by the game process, so bind by simple name the way the AppDomain would.
        try
        {
            Type[] referenced = asm.GetReferencedAssemblies().Length > 0
                ? Type.EmptyTypes
                : Type.EmptyTypes;
            foreach (AssemblyName an in asm.GetReferencedAssemblies())
            {
                string resolved = "not loaded here";
                try
                {
                    Assembly already = Assembly.Load(an.Name);
                    resolved = "resolvable";
                    if (already == null) resolved = "NOT resolvable";
                }
                catch { resolved = "not in this runtime (expected for game/unity assemblies)"; }
                Console.WriteLine("  ref " + an.Name + " -> " + resolved);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("  (reference walk failed: " + e.Message + ")");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "RESULT: the loader would find and load this mod." : "RESULT: " + failures + " problem(s).");
        return failures == 0 ? 0 : 1;
    }
}

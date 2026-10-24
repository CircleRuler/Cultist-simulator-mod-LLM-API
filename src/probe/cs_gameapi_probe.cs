using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace cs_gameapi_probe
{
    /// <summary>
    /// Minimal probe mod: proves that custom DLL code executes inside the game process
    /// and that the core game services (FucineRoot / HornedAxe / Heart) are reachable.
    /// Writes diagnostics to a file inside the workspace.
    /// </summary>
    public class cs_gameapi_probe : MonoBehaviour
    {
        private const string LogPath = @"F:\WorkSpace\cs_gameapi\logs\probe.log";
        private static readonly StringBuilder Sb = new StringBuilder();
        private static bool _dumped;

        public static void Initialise()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                Line("=== cs_gameapi_probe Initialise() called ===");
                Line("utc=" + DateTime.UtcNow.ToString("o"));
                Line("unityVersion=" + Application.unityVersion);
                Line("gameVersion=" + Application.version);
                Line("dataPath=" + Application.dataPath);
                Line("isEditor=" + Application.isEditor);

                var go = new GameObject("cs_gameapi_probe_host");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<cs_gameapi_probe>();
                Line("host GameObject created + DontDestroyOnLoad; waiting for scene to settle.");
            }
            catch (Exception e)
            {
                try
                {
                    File.AppendAllText(@"F:\WorkSpace\cs_gameapi\logs\probe_fatal.log",
                        DateTime.UtcNow.ToString("o") + " INITIALISE THREW: " + e + Environment.NewLine);
                }
                catch { }
            }
        }

        private float _elapsed;

        private void Update()
        {
            _elapsed += Time.unscaledDeltaTime;
            if (_dumped || _elapsed < 3f) return;
            _dumped = true;
            try
            {
                DumpState();
            }
            catch (Exception e)
            {
                Line("DUMP THREW: " + e);
            }
            Flush();
        }

        private static void DumpState()
        {
            Line("");
            Line("=== SERVICE PROBE ===");

            // --- Watchman service locator -------------------------------------
            Type heartType = FindType("Heart, SecretHistories.Main")
                          ?? FindType("SecretHistories.Infrastructure.Heart, SecretHistories.Main")
                          ?? FindType("Heart, Assembly-CSharp");
            Line("Heart type resolved: " + (heartType != null ? heartType.FullName : "NULL"));
            if (heartType != null)
            {
                var heartInst = SceneObjectsOfType(heartType);
                Line("Heart instances in scene: " + (heartInst == null ? -1 : heartInst.Length));
            }

            Type hornedAxeType = FindType("SecretHistories.Entities.HornedAxe, SecretHistories.Main");
            Line("HornedAxe type resolved: " + (hornedAxeType != null ? hornedAxeType.FullName : "NULL"));

            Type watchmanType = FindType("SecretHistories.UI.Watchman, SecretHistories.Main");
            Line("Watchman type resolved: " + (watchmanType != null ? watchmanType.FullName : "NULL"));

            // --- FucineRoot ----------------------------------------------------
            Type rootType = FindType("SecretHistories.Entities.FucineRoot, SecretHistories.Main");
            if (rootType != null)
            {
                var getMethod = rootType.GetMethod("Get", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                Line("FucineRoot.Get found: " + (getMethod != null));
                object root = getMethod != null ? getMethod.Invoke(null, null) : null;
                Line("FucineRoot instance: " + (root == null ? "NULL" : root.GetType().FullName));

                if (root != null)
                {
                    var spheres = rootType.GetMethod("GetSpheres").Invoke(root, null) as System.Collections.IEnumerable;
                    int count = 0;
                    if (spheres != null)
                    {
                        foreach (object s in spheres)
                        {
                            count++;
                            Line("  ROOT SPHERE: " + DescribeSphere(s));
                        }
                    }
                    Line("root sphere count = " + count);
                }
            }
            else
            {
                Line("FucineRoot type NOT FOUND");
            }
        }

        private static string DescribeSphere(object sphere)
        {
            if (sphere == null) return "<null>";
            Type t = sphere.GetType();
            var sb = new StringBuilder();
            sb.Append(t.Name);

            object id = GetProp(sphere, "Id");
            sb.Append(" Id=").Append(id);

            object spec = GetProp(sphere, "GoverningSphereSpec");
            if (spec != null)
            {
                object label = GetProp(spec, "Label");
                object specId = GetProp(spec, "Id");
                sb.Append(" specId=").Append(specId).Append(" label=").Append(label);
            }

            object tokens = GetProp(sphere, "Tokens");
            var list = tokens as System.Collections.IEnumerable;
            int n = 0;
            if (list != null) foreach (object _ in list) n++;
            sb.Append(" tokens=").Append(n);

            try
            {
                object path = t.GetMethod("GetAbsolutePath").Invoke(sphere, null);
                sb.Append(" path=").Append(path);
            }
            catch (Exception e) { sb.Append(" path=<err ").Append(e.GetType().Name).Append('>'); }

            return sb.ToString();
        }

        private static object GetProp(object o, string name)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(name);
            if (p == null) return null;
            try { return p.GetValue(o, null); } catch { return null; }
        }

        private static Type FindType(string assemblyQualified)
        {
            try { return Type.GetType(assemblyQualified, false); }
            catch { return null; }
        }

        private static UnityEngine.Object[] SceneObjectsOfType(Type t)
        {
            try { return UnityEngine.Object.FindObjectsOfType(t); }
            catch { return null; }
        }

        private static void Line(string s)
        {
            Sb.Append(s).Append(Environment.NewLine);
        }

        private static void Flush()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.WriteAllText(LogPath, Sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}

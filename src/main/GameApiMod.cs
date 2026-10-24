using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

// The mod's helper types all live in CsGameApi; this import keeps the entry class below
// readable without namespace qualifiers.
using CsGameApi;

// ---------------------------------------------------------------------------------------------
// NOTE ON THE GLOBAL NAMESPACE - DO NOT move this class into a namespace.
//
// ModManager.TryInitialiseAssembly() does:
//     string className = GetNameAlphanumericsOnly();          // "LLM_API"
//     Type type = LoadedAssembly.GetType(className);
//
// Assembly.GetType("LLM_API") matches the FULL type name only; it does not search namespaces.
// A type declared as `namespace LLM_API { class LLM_API }` has the full name "LLM_API.LLM_API"
// and is therefore never found - the game logs "Can't find a class named LLM_API" and the mod
// silently never runs.
//
// The class name must also equal the module id exactly, because the same
// GetNameAlphanumericsOnly() derives the DLL file name the loader looks for:
//     Path.Combine(mod.ModRootFolder, "dll", GetNameAlphanumericsOnly() + ".dll")
// So folder name, DLL file name and this class name are one and the same string, and it may
// only contain [a-zA-Z0-9_].
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Entry point for the LLM_API mod.
///
/// The game loads this DLL at startup and calls the static Initialise() below, which is the
/// contract described in the game's MODDING_README.txt ("CUSTOM DLL LOADING").
///
/// Threading model: the HTTP server runs on a background thread and never touches the game.
/// Mutating requests are queued here and executed from Update(), i.e. on the Unity main
/// thread, which is the only place game objects may be touched.
/// </summary>
public class LLM_API : MonoBehaviour
{
    internal const string Version = "1.0.2";

        private static readonly ConcurrentQueue<GameCommand> Commands = new ConcurrentQueue<GameCommand>();
        private static ApiHttpServer _server;
        private static bool _initialised;

        private float _sinceStart;
        private bool _selfTestDone;

        // --- framework entry point --------------------------------------------------------

        public static void Initialise()
        {
            if (_initialised)
            {
                Reflect.Log("Initialise() called twice; ignoring the second call.");
                return;
            }
            _initialised = true;

            try
            {
                string modFolder = ResolveModFolder();
                ApiConfig.Load(modFolder);

                Reflect.Log("=== LLM_API " + Version + " starting ===");
                Reflect.Log("modFolder=" + modFolder);
                Reflect.Log("logDirectory=" + ApiConfig.Directory);
                Reflect.Log("unity=" + Application.unityVersion + " game=" + Application.version);
                Reflect.Log("port=" + ApiConfig.Port + " enabled=" + ApiConfig.Enabled + " selftest=" + ApiConfig.SelfTest);

                if (ApiConfig.Enabled)
                {
                    string error;
                    _server = new ApiHttpServer(ApiConfig.Port, Commands);
                    if (_server.Start(out error))
                    {
                        Reflect.Log("HTTP API listening on http://127.0.0.1:" + _server.Port + "/");
                    }
                    else
                    {
                        _server = null;
                        Reflect.Log("FAILED to start HTTP server on port " + ApiConfig.Port + ": " + error);
                    }
                }
                else
                {
                    Reflect.Log("HTTP server disabled by config; running in self-test only mode.");
                }

                var host = new GameObject("LLM_API_host");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<LLM_API>();
                Reflect.Log("main-thread pump installed.");
            }
            catch (Exception e)
            {
                Reflect.LogError("Initialise", e);
            }
        }

        private static string ResolveModFolder()
        {
            try
            {
                // The DLL is loaded from <mods>/<modid>/dll/<modid>.dll, so the mod root is two
                // levels up; config.txt therefore sits next to synopsis.json as documented.
                string location = typeof(LLM_API).Assembly.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    var dir = new DirectoryInfo(Path.GetDirectoryName(location));
                    if (dir != null && dir.Parent != null) return dir.Parent.FullName;
                }
            }
            catch { }

            try
            {
                string localLow = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                if (!string.IsNullOrEmpty(localLow))
                    return Path.Combine(localLow, "LocalLow", "Weather Factory", "Cultist Simulator", "mods", "LLM_API");
            }
            catch { }

            return "LLM_API";
        }

        // --- main-thread pump -------------------------------------------------------------

        private void Update()
        {
            _sinceStart += Time.unscaledDeltaTime;

            // Execute whatever the HTTP thread queued since the last frame.
            GameCommand command;
            int drained = 0;
            while (drained < 16 && Commands.TryDequeue(out command))
            {
                drained++;
                try
                {
                    command.Complete(command.Work());
                }
                catch (Exception e)
                {
                    command.Fail(Reflect.Unwrap(e));
                }
            }

            if (ApiConfig.SelfTest && !_selfTestDone && _sinceStart > 6f)
            {
                _selfTestDone = true;
                try
                {
                    SelfTest.Run();
                }
                catch (Exception e)
                {
                    Reflect.LogError("self-test", e);
                }
            }
        }

        private void OnApplicationQuit()
        {
            Reflect.Log("game quitting; stopping HTTP server.");
            if (_server != null) _server.Stop();
        }
}

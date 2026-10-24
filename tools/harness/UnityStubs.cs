using System;
using System.Collections.Generic;

// Minimal stand-ins for the UnityEngine types that cs_gameapi or FakeGame.cs touch.
// They exist only so the mod source can be compiled into the harness executable; the harness
// never constructs a GameObject or relies on Unity behaviour.

namespace UnityEngine
{
    public struct Vector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public Vector3(float x, float y)
        {
            this.x = x;
            this.y = y;
            this.z = 0f;
        }

        public override string ToString()
        {
            return "(" + x + ", " + y + ", " + z + ")";
        }
    }

    public static class Application
    {
        public static string version = "2026.1.g.2-harness";
        public static string unityVersion = "2022.3.62f3-harness";
        public static string dataPath = "harness";
        public static bool isEditor = false;
    }

    public static class Time
    {
        public static float unscaledDeltaTime = 0.016f;
    }

    public class Object
    {
        public static Object[] FindObjectsOfType(Type type)
        {
            return new Object[0];
        }

        public static void DontDestroyOnLoad(Object target)
        {
        }
    }

    public class GameObject : Object
    {
        public string name;

        public GameObject()
        {
        }

        public GameObject(string name)
        {
            this.name = name;
        }

        public T AddComponent<T>() where T : new()
        {
            return new T();
        }
    }

    public class MonoBehaviour : Object
    {
    }
}

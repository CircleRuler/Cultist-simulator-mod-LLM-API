using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace CsGameApi
{
    /// <summary>
    /// Thin, allocation-light helpers over Newtonsoft.Json for reading request bodies.
    /// Every accessor is total: a missing or ill-typed field yields a default instead of throwing,
    /// so a malformed request can never take the game down.
    /// </summary>
    internal static class Json
    {
        public static JObject ParseObject(string body)
        {
            if (string.IsNullOrEmpty(body)) return new JObject();
            try
            {
                var token = JToken.Parse(body);
                return token as JObject ?? new JObject();
            }
            catch (Exception e)
            {
                throw new ApiException(400, "invalid JSON body: " + e.Message);
            }
        }

        public static string Str(JObject o, string key, string fallback = null)
        {
            if (o == null) return fallback;
            JToken t;
            if (!o.TryGetValue(key, out t) || t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type == JTokenType.String) return t.Value<string>();
            return t.ToString();
        }

        public static int? Int(JObject o, string key)
        {
            if (o == null) return null;
            JToken t;
            if (!o.TryGetValue(key, out t) || t == null || t.Type == JTokenType.Null) return null;
            try
            {
                if (t.Type == JTokenType.Integer) return t.Value<int>();
                if (t.Type == JTokenType.Float) return (int)t.Value<double>();
                if (t.Type == JTokenType.String)
                {
                    int parsed;
                    if (int.TryParse(t.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
                }
            }
            catch { }
            return null;
        }

        public static float? Float(JObject o, string key)
        {
            if (o == null) return null;
            JToken t;
            if (!o.TryGetValue(key, out t) || t == null || t.Type == JTokenType.Null) return null;
            try
            {
                if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return t.Value<float>();
                if (t.Type == JTokenType.String)
                {
                    float parsed;
                    if (float.TryParse(t.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
                }
            }
            catch { }
            return null;
        }

        public static bool? Bool(JObject o, string key)
        {
            if (o == null) return null;
            JToken t;
            if (!o.TryGetValue(key, out t) || t == null || t.Type == JTokenType.Null) return null;
            try
            {
                if (t.Type == JTokenType.Boolean) return t.Value<bool>();
                if (t.Type == JTokenType.Integer) return t.Value<int>() != 0;
                if (t.Type == JTokenType.String) return string.Equals(t.Value<string>(), "true", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            return null;
        }

        public static object Vec3ToJson(UnityEngine.Vector3 v)
        {
            return new Dictionary<string, object>
            {
                { "x", Round(v.x) },
                { "y", Round(v.y) },
                { "z", Round(v.z) }
            };
        }

        public static double Round(float f)
        {
            return Math.Round((double)f, 3);
        }

        /// <summary>Serialise with Unity's value types neutralised so Newtonsoft cannot choke on them.</summary>
        public static string Serialize(object value, bool indented = false)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(
                value,
                indented ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None,
                new Newtonsoft.Json.JsonSerializerSettings
                {
                    ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore,
                    NullValueHandling = Newtonsoft.Json.NullValueHandling.Include,
                    DateFormatHandling = Newtonsoft.Json.DateFormatHandling.IsoDateFormat
                });
        }
    }

    /// <summary>An error that carries an HTTP status back to the client.</summary>
    internal class ApiException : Exception
    {
        public int Status { get; private set; }

        public ApiException(int status, string message) : base(message)
        {
            Status = status;
        }
    }
}

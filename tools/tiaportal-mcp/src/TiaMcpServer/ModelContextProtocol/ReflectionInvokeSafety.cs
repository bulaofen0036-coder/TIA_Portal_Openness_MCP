using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// The read-only gate of the reflection bridge (InvokeObject / InvokeService) and the argument
    /// handling that goes with it. Zero-dependency so the offline suite can test it.
    /// </summary>
    internal static class ReflectionInvokeSafety
    {
        // Reads only, all four. GetAttributes is the bulk accessor: it returns several attribute
        // values in ONE round trip. It used to be refused here, which left one call per attribute
        // as the only route — thousands of calls to read one HMI.
        private static readonly HashSet<string> ReadOnlyMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ToString",
            "GetAttribute",
            "GetAttributes",
            "GetAttributeInfos"
        };

        public static bool IsReadOnlyMethod(string? methodName)
            => !string.IsNullOrWhiteSpace(methodName) && ReadOnlyMethods.Contains(methodName!);

        /// <summary>
        /// JSON arguments as CLR values. A nested array is ONE argument and becomes a string list —
        /// GetAttributes(names) is unusable otherwise, because the array used to arrive as its own
        /// JSON text.
        /// </summary>
        public static List<object?> ToArgValues(JsonArray? args)
        {
            var values = new List<object?>();
            if (args == null) return values;

            foreach (var arg in args)
            {
                if (arg == null) { values.Add(null); continue; }

                if (arg is JsonArray nested)
                {
                    var list = new JsonStringList(nested.ToJsonString());
                    foreach (var element in nested)
                    {
                        if (element == null) continue;
                        list.Add(element is JsonValue ev && ev.TryGetValue<string>(out var s) ? s : element.ToString());
                    }
                    values.Add(list);
                    continue;
                }

                if (arg is JsonValue jv)
                {
                    if (jv.TryGetValue<string>(out var str)) { values.Add(str); continue; }
                    if (jv.TryGetValue<int>(out var i)) { values.Add(i); continue; }
                    if (jv.TryGetValue<long>(out var l)) { values.Add(l); continue; }
                    if (jv.TryGetValue<double>(out var d)) { values.Add(d); continue; }
                    if (jv.TryGetValue<bool>(out var b)) { values.Add(b); continue; }
                    values.Add(jv.ToString());
                    continue;
                }

                values.Add(arg.ToString());
            }

            return values;
        }

        /// <summary>
        /// The overload whose parameter types actually fit the arguments. Picking by parameter count
        /// alone lands on the wrong GetAttributes: both overloads take exactly one parameter.
        /// Null when nothing fits.
        /// </summary>
        public static MethodInfo? SelectOverload(IEnumerable<MethodInfo> candidates, IReadOnlyList<object?> argValues)
        {
            MethodInfo? fallback = null;
            MethodInfo? best = null;
            var bestScore = int.MinValue;

            foreach (var method in candidates)
            {
                var parameters = method.GetParameters();
                if (parameters.Length != argValues.Count) continue;

                fallback ??= method;

                var score = 0;
                for (var i = 0; i < parameters.Length; i++)
                {
                    score += ParameterScore(parameters[i].ParameterType, argValues[i]);
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = method;
                }
            }

            return best ?? fallback;
        }

        /// <summary>
        /// Arguments in the shape the chosen method declares: string lists become
        /// IEnumerable&lt;string&gt;, enum parameters are parsed from their name, the rest is
        /// converted by the usual primitive rules and otherwise passed through untouched.
        /// </summary>
        public static object?[] ConvertArguments(MethodInfo method, IReadOnlyList<object?> argValues)
        {
            var parameters = method.GetParameters();
            var converted = new object?[parameters.Length];

            for (var i = 0; i < parameters.Length && i < argValues.Count; i++)
            {
                converted[i] = ConvertArgument(parameters[i].ParameterType, argValues[i]);
            }

            return converted;
        }

        /// <summary>One argument into one parameter type; null and unconvertible values pass through.</summary>
        public static object? ConvertArgument(Type parameterType, object? value)
        {
            if (value == null) return null;

            if (value is IReadOnlyList<string> strings && IsStringSequence(parameterType))
            {
                var array = new string[strings.Count];
                for (var i = 0; i < strings.Count; i++) array[i] = strings[i];
                return array;
            }

            if (parameterType.IsEnum && value is string enumName)
            {
                try { return Enum.Parse(parameterType, enumName, ignoreCase: true); }
                catch { return value; }
            }

            try
            {
                if (parameterType == typeof(string)) return value.ToString();
                if (parameterType == typeof(int)) return Convert.ToInt32(value);
                if (parameterType == typeof(long)) return Convert.ToInt64(value);
                if (parameterType == typeof(double)) return Convert.ToDouble(value);
                if (parameterType == typeof(bool)) return Convert.ToBoolean(value);
            }
            catch
            {
                return value;
            }

            return value;
        }

        /// <summary>True for string[], IEnumerable&lt;string&gt;, IList&lt;string&gt; and friends.</summary>
        public static bool IsStringSequence(Type parameterType)
        {
            if (parameterType == typeof(string)) return false;
            if (parameterType == typeof(string[])) return true;
            if (!parameterType.IsGenericType) return false;

            var args = parameterType.GetGenericArguments();
            return args.Length == 1 && args[0] == typeof(string) && parameterType.IsAssignableFrom(typeof(List<string>));
        }

        /// <summary>
        /// A JSON array as a string list that still prints as its original JSON text: a method
        /// expecting a string keeps receiving exactly what it received before this change.
        /// </summary>
        private sealed class JsonStringList : List<string>
        {
            private readonly string _json;
            public JsonStringList(string json) { _json = json; }
            public override string ToString() => _json;
        }

        private static int ParameterScore(Type parameterType, object? value)
        {
            if (value == null) return parameterType.IsValueType ? -1 : 1;

            if (value is IReadOnlyList<string>) return IsStringSequence(parameterType) ? 3 : -3;
            if (parameterType.IsInstanceOfType(value)) return 3;

            if (value is string)
            {
                if (parameterType == typeof(string)) return 3;
                if (parameterType.IsEnum) return 2;
                return -1;
            }

            if (value is int || value is long || value is double)
            {
                return parameterType.IsPrimitive || parameterType.IsEnum ? 2 : -1;
            }

            if (value is bool) return parameterType == typeof(bool) ? 3 : -1;

            return 0;
        }
    }
}

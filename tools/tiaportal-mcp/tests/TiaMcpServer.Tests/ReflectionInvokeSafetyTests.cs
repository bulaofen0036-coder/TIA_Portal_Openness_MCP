using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// InvokeObject's read-only gate and its argument handling.
    ///
    /// GetAttributes(names) is the bulk accessor of the Openness API — it reads several attributes
    /// in one round trip and writes nothing — but it was refused as "not allowed (read-only mode)",
    /// so every caller was forced into one call per attribute. Letting it through is only half the
    /// fix: a list argument arrived as the string "[\"Address\"]" and the two overloads were picked
    /// by parameter COUNT, so the call would have failed or hit the wrong overload anyway.
    ///
    /// All of it is plain reflection over argument shapes, so it lives in a zero-dependency file and
    /// is tested here against a stand-in type with the same two overloads Openness has.
    /// </summary>
    internal static class ReflectionInvokeSafetyTests
    {
        internal static void Run(Action<bool, string> check)
        {
            RunReadOnlyGate(check);
            RunArgumentParsing(check);
            RunOverloadSelection(check);
            RunArgumentConversion(check);
        }

        private static void RunReadOnlyGate(Action<bool, string> check)
        {
            check(ReflectionInvokeSafety.IsReadOnlyMethod("GetAttributes"),
                "GetAttributes is readable-only and must pass the gate — it is the one call that reads many attributes at once");
            check(ReflectionInvokeSafety.IsReadOnlyMethod("GetAttribute"), "GetAttribute keeps passing the gate");
            check(ReflectionInvokeSafety.IsReadOnlyMethod("GetAttributeInfos"), "GetAttributeInfos keeps passing the gate");
            check(ReflectionInvokeSafety.IsReadOnlyMethod("ToString"), "ToString keeps passing the gate");
            check(ReflectionInvokeSafety.IsReadOnlyMethod("getattributes"), "the gate ignores case, as it did before");

            // Reverse sentinels: opening one door must not open the neighbouring ones.
            check(!ReflectionInvokeSafety.IsReadOnlyMethod("SetAttributes"),
                "SetAttributes stays refused — one letter away from the method we just allowed");
            check(!ReflectionInvokeSafety.IsReadOnlyMethod("SetAttribute"), "SetAttribute stays refused");
            check(!ReflectionInvokeSafety.IsReadOnlyMethod("Delete"), "Delete stays refused");
            check(!ReflectionInvokeSafety.IsReadOnlyMethod(""), "an empty method name is refused");
            check(!ReflectionInvokeSafety.IsReadOnlyMethod(null), "a null method name is refused");
        }

        private static void RunArgumentParsing(Action<bool, string> check)
        {
            var args = new JsonArray("Address", 15, true);
            var values = ReflectionInvokeSafety.ToArgValues(args);
            check(values.Count == 3 && (values[0] as string) == "Address" && values[1] is int && values[2] is bool,
                "scalars keep their JSON type");

            var nested = new JsonArray(new JsonArray("PlcTag", "Address", "Connection"));
            var nestedValues = ReflectionInvokeSafety.ToArgValues(nested);
            check(nestedValues.Count == 1, "a nested array is ONE argument, not three");
            check(nestedValues.Count == 1 && nestedValues[0] is IReadOnlyList<string> list && list.Count == 3 && list[0] == "PlcTag",
                "a nested array becomes a string list — before this it arrived as the literal text \"[\\\"PlcTag\\\",...]\"");

            check(ReflectionInvokeSafety.ToArgValues(null).Count == 0, "no arguments is an empty list, not a crash");

            // Regression sentinel: an array handed to a string parameter used to arrive as its JSON
            // text. Turning arrays into lists must not silently change that into "System.Collections…".
            check(ReflectionInvokeSafety.ConvertArgument(typeof(string), nestedValues[0]) as string == "[\"PlcTag\",\"Address\",\"Connection\"]",
                "an array passed where a string is expected keeps its JSON text, as before");
        }

        private static void RunOverloadSelection(Action<bool, string> check)
        {
            var candidates = typeof(FakeOpennessObject)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "GetAttributes")
                .ToList();
            check(candidates.Count == 2, "the stand-in type has both overloads, like a real Openness object");

            var byNames = ReflectionInvokeSafety.SelectOverload(candidates,
                new object?[] { new List<string> { "Address" } });
            check(byNames?.GetParameters()[0].ParameterType != typeof(FakeAccessOptions),
                "a list argument picks the names overload, not the options overload that happens to have one parameter too");

            var byOptions = ReflectionInvokeSafety.SelectOverload(candidates, new object?[] { "All" });
            check(byOptions?.GetParameters()[0].ParameterType == typeof(FakeAccessOptions),
                "a plain string picks the enum overload");

            var single = typeof(FakeOpennessObject).GetMethods().Where(m => m.Name == "GetAttribute").ToList();
            check(ReflectionInvokeSafety.SelectOverload(single, new object?[] { "Address" }) != null,
                "a single candidate with a matching parameter count is still selected");
            check(ReflectionInvokeSafety.SelectOverload(single, new object?[] { "A", "B" }) == null,
                "no candidate with that parameter count means no method, not a wrong one");
        }

        private static void RunArgumentConversion(Action<bool, string> check)
        {
            var namesOverload = typeof(FakeOpennessObject)
                .GetMethods()
                .First(m => m.Name == "GetAttributes"
                            && m.GetParameters()[0].ParameterType != typeof(FakeAccessOptions));

            var converted = ReflectionInvokeSafety.ConvertArguments(namesOverload,
                new object?[] { new List<string> { "Address", "PlcTag" } });
            check(converted.Length == 1 && converted[0] is IEnumerable<string>,
                "a string list is handed over as IEnumerable<string>, the shape the method declares");

            var target = new FakeOpennessObject();
            var result = converted.Length == 1 && converted[0] is IEnumerable<string>
                ? namesOverload.Invoke(target, converted) as IList<object>
                : null;
            check(result != null && result.Count == 2 && (result[0] as string) == "value:Address",
                "the converted call actually runs and returns both values in one round trip");

            var optionsOverload = typeof(FakeOpennessObject)
                .GetMethods()
                .First(m => m.Name == "GetAttributes" && m.GetParameters()[0].ParameterType == typeof(FakeAccessOptions));
            var convertedEnum = ReflectionInvokeSafety.ConvertArguments(optionsOverload, new object?[] { "All" });
            check(convertedEnum.Length == 1 && convertedEnum[0] is FakeAccessOptions opt && opt == FakeAccessOptions.All,
                "an enum parameter is parsed from its name instead of being passed as a string");

            var getAttribute = typeof(FakeOpennessObject).GetMethods().First(m => m.Name == "GetAttribute");
            check(ReflectionInvokeSafety.ConvertArguments(getAttribute, new object?[] { "Address" }).FirstOrDefault() as string == "Address",
                "an ordinary string argument is unchanged");
        }

        private enum FakeAccessOptions { None, All }

        private sealed class FakeOpennessObject
        {
            public object GetAttribute(string name) => "value:" + name;

            public IList<object> GetAttributes(IEnumerable<string> names)
                => names.Select(n => (object)("value:" + n)).ToList();

            public IList<object> GetAttributes(FakeAccessOptions options)
                => new List<object> { "options:" + options };
        }
    }
}

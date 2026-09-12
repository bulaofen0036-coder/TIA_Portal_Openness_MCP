using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    internal static class HmiScreenTraversalTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var rootScreen = new Screen("Main");
            var nestedScreen = new Screen("电机控制");
            var siblingScreen = new Screen("Alarms");
            var duplicateName = new Screen("Main");
            var nested = new UnifiedGroup { Screens = { nestedScreen } };
            var branch = new UnifiedGroup { Groups = { new UnifiedGroup(), nested } };
            var unified = new UnifiedRoot
            {
                Screens = { rootScreen },
                ScreenGroups = { branch, new UnifiedGroup { Screens = { siblingScreen, duplicateName } } }
            };

            check(HmiScreenTraversal.ListNames(unified).SequenceEqual(new[] { "Main", "电机控制", "Alarms", "Main" }),
                "Unified 列表覆盖根层、多层子组、空组之后的子组和兄弟组");
            check(ReferenceEquals(HmiScreenTraversal.FindByName(unified, "电机控制"), nestedScreen),
                "按名称能找到深层子组画面的真实对象");
            check(ReferenceEquals(HmiScreenTraversal.FindByName(unified, "aLaRmS"), siblingScreen),
                "子组查找保持大小写不敏感，并能越过不匹配的兄弟组");
            check(ReferenceEquals(HmiScreenTraversal.FindByName(unified, "MAIN"), rootScreen),
                "根目录同名画面的匹配优先级保持不变");
            check(HmiScreenTraversal.FindByName(unified, "Missing") == null,
                "遍历完整树后仍不存在的画面返回 null");
            unified.Screens.Clear();
            check(HmiScreenTraversal.ListNames(unified).Contains("电机控制"),
                "根目录没有画面时仍遍历子组");

            var classic = new ClassicRoot
            {
                ScreenFolder = new ClassicFolder
                {
                    Screens = { rootScreen },
                    Folders =
                    {
                        new ClassicFolder(),
                        new ClassicFolder { Folders = { new ClassicFolder { Screens = { nestedScreen } } } },
                        new ClassicFolder { Screens = { siblingScreen } }
                    }
                }
            };
            check(HmiScreenTraversal.ListNames(classic).SequenceEqual(new[] { "Main", "电机控制", "Alarms" }),
                "Classic 列表遍历 ScreenFolder.Screens 及多层 Folders");
            check(ReferenceEquals(HmiScreenTraversal.FindByName(classic, "Main"), rootScreen),
                "Classic 根 ScreenFolder 对象不被误当成画面集合");
            check(ReferenceEquals(HmiScreenTraversal.FindByName(classic, "电机控制"), nestedScreen),
                "Classic 深层文件夹中的画面可按名称定位");
            classic.ScreenFolder.Screens.Clear();
            check(HmiScreenTraversal.ListNames(classic).SequenceEqual(new[] { "电机控制", "Alarms" }),
                "Classic 根文件夹为空也不遗漏子文件夹");

            // API aliases or repeated references must not duplicate entries or loop forever.
            branch.Groups.Add(branch);
            unified.ScreenGroups.Add(branch);
            nested.Screens.Add(nestedScreen);
            check(HmiScreenTraversal.ListNames(unified).SequenceEqual(new[] { "电机控制", "Alarms", "Main" }),
                "重复对象和组循环引用不导致重复结果或无限递归");
            check(HmiScreenTraversal.FindByName(unified, "FolderOnly") == null,
                "文件夹名称不能作为画面匹配");
            check(HmiScreenTraversal.ListNames(new object()).Count == 0 &&
                  HmiScreenTraversal.FindByName(new object(), "Main") == null,
                "缺少画面属性的对象返回空结果");
            check(HmiScreenTraversal.ListNames(new UnifiedRoot()).Count == 0,
                "空 HMI 返回空列表");
            var sparse = new UnifiedRoot();
            sparse.Screens.Add(null!);
            sparse.Screens.Add(new Screen(" "));
            sparse.Screens.Add(rootScreen);
            check(HmiScreenTraversal.ListNames(sparse).SequenceEqual(new[] { "Main" }),
                "忽略空元素和空名称，仍返回后续有效画面");

            var unreadable = new UnreadableRoot();
            check(ReferenceEquals(HmiScreenTraversal.FindByName(unreadable, "Main"), unreadable.Screens[0]),
                "根层命中后立即返回，不访问后续子组");
            check(ThrowsReadFailure(() => HmiScreenTraversal.FindByName(unreadable, "Missing")),
                "读取子组失败不能伪装成不存在，防止 Ensure 创建重复画面");
            check(ThrowsReadFailure(() => HmiScreenTraversal.ListNames(unreadable)),
                "读取子组失败不能返回看似完整的根层列表");
        }

        private static bool ThrowsReadFailure(Action action)
        {
            try { action(); return false; }
            catch (TargetInvocationException ex) { return ex.InnerException is InvalidOperationException; }
        }

        private sealed class Screen
        {
            public Screen(string name) { Name = name; }
            public string Name { get; }
        }

        private sealed class UnifiedRoot
        {
            public List<Screen> Screens { get; } = new List<Screen>();
            public List<UnifiedGroup> ScreenGroups { get; } = new List<UnifiedGroup>();
        }

        private sealed class UnifiedGroup
        {
            public string Name => "FolderOnly";
            public List<Screen> Screens { get; } = new List<Screen>();
            public List<UnifiedGroup> Groups { get; } = new List<UnifiedGroup>();
        }

        private sealed class ClassicRoot
        {
            public ClassicFolder ScreenFolder { get; set; } = new ClassicFolder();
        }

        private sealed class ClassicFolder
        {
            public List<Screen> Screens { get; } = new List<Screen>();
            public List<ClassicFolder> Folders { get; } = new List<ClassicFolder>();
        }

        private sealed class UnreadableRoot
        {
            public List<Screen> Screens { get; } = new List<Screen> { new Screen("Main") };
            public object ScreenGroups => throw new InvalidOperationException("Cannot read screen groups");
        }
    }
}

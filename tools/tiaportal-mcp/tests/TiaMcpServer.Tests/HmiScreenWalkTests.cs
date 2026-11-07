using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// PR #41：画面工具原来只读 HMI 软件根上的 Screens，放进画面分组（Unified 的 ScreenGroups，
    /// 经典 HMI 的 ScreenFolder/Folders）里的画面列不出、按名找不到、导不出，只报一句「找不到」。
    /// 这里用和 Openness 同形的假对象喂遍历器，两种对象模型都喂。
    /// </summary>
    internal static class HmiScreenWalkTests
    {
        internal static void Run(Action<bool, string> check)
        {
            // Unified：根 Screens + ScreenGroups，分组下再套 Groups（两层深，同 PR 里的 00_ScreenLayout/PopUps/LogOff）。
            var popUps = new FakeGroup("PopUps", new[] { new FakeScreen("LogOff"), new FakeScreen("ModeRequests") });
            var layout = new FakeGroup("00_ScreenLayout", new[] { new FakeScreen("Header") }, groups: new[] { popUps });
            var unified = new FakeUnifiedSoftware(new[] { new FakeScreen("Main") }, new[] { layout });

            var names = HmiScreenWalk.ListNames(unified);
            check(names.SequenceEqual(new[] { "Main", "Header", "LogOff", "ModeRequests" }),
                "Unified：根画面 + 两层分组里的画面都要列出来（实际：" + string.Join(",", names) + "）");
            check(HmiScreenWalk.FindByName(unified, "LogOff") is FakeScreen s1 && s1.Name == "LogOff",
                "Unified：两层分组深处的画面要能按名找到");
            check(HmiScreenWalk.FindByName(unified, "logoff") is FakeScreen,
                "按名查找保持大小写不敏感（与原 TryFindByNameInCollection 一致）");

            // 经典 HMI：根上的 ScreenFolder 是单个文件夹对象，子文件夹在 Folders 里。
            var sub = new FakeFolder(new[] { new FakeScreen("Alarms") });
            var classic = new FakeClassicTarget(new FakeFolder(new[] { new FakeScreen("Root") }, new[] { sub }));
            check(HmiScreenWalk.ListNames(classic).SequenceEqual(new[] { "Root", "Alarms" }),
                "经典 HMI：ScreenFolder 及其子文件夹里的画面都要列出来");
            check(HmiScreenWalk.FindByName(classic, "Alarms") != null, "经典 HMI：子文件夹里的画面要能按名找到");

            // 反向哨兵：不存在的名字必须是 null，不能把「随便返回一个」当找到。
            check(HmiScreenWalk.FindByName(unified, "PopUps") == null,
                "分组本身的名字不许被当成画面找到");
            check(HmiScreenWalk.FindByName(unified, "NoSuchScreen") == null, "不存在的画面必须返回 null");

            // 读属性炸了：保持原契约——列表尽力而为、查找当「找不到」，都不往外抛。
            var broken = new FakeThrowingSoftware();
            check(HmiScreenWalk.ListNames(broken).SequenceEqual(new[] { "Main" }), "枚举中途抛异常时列表返回已读到的部分而不是抛出");
            check(HmiScreenWalk.FindByName(broken, "Other") == null, "枚举中途抛异常时查找返回 null 而不是抛出");

            // 环：同一个分组对象出现两次（引用相同）不许死循环、不许重复列出。
            var loop = new FakeGroup("Loop", new[] { new FakeScreen("Once") });
            loop.Groups = new[] { loop };
            check(HmiScreenWalk.ListNames(new FakeUnifiedSoftware(Array.Empty<FakeScreen>(), new[] { loop })).Count == 1,
                "分组自引用时只走一次");
        }

        private sealed class FakeScreen
        {
            public FakeScreen(string name) { Name = name; }
            public string Name { get; }
        }

        private sealed class FakeGroup
        {
            public FakeGroup(string name, FakeScreen[] screens, FakeGroup[]? groups = null)
            {
                Name = name; Screens = screens; Groups = groups ?? Array.Empty<FakeGroup>();
            }
            public string Name { get; }
            public FakeScreen[] Screens { get; }
            public FakeGroup[] Groups { get; set; }
        }

        private sealed class FakeUnifiedSoftware
        {
            public FakeUnifiedSoftware(FakeScreen[] screens, FakeGroup[] groups) { Screens = screens; ScreenGroups = groups; }
            public FakeScreen[] Screens { get; }
            public FakeGroup[] ScreenGroups { get; }
        }

        private sealed class FakeFolder
        {
            public FakeFolder(FakeScreen[] screens, FakeFolder[]? folders = null)
            {
                Screens = screens; Folders = folders ?? Array.Empty<FakeFolder>();
            }
            public FakeScreen[] Screens { get; }
            public FakeFolder[] Folders { get; }
        }

        private sealed class FakeClassicTarget
        {
            public FakeClassicTarget(FakeFolder folder) { ScreenFolder = folder; }
            public FakeFolder ScreenFolder { get; }
        }

        private sealed class FakeThrowingSoftware
        {
            public IEnumerable<FakeScreen> Screens => Throw();
            private static IEnumerable<FakeScreen> Throw()
            {
                yield return new FakeScreen("Main");
                throw new InvalidOperationException("Openness proxy disposed");
            }
        }
    }
}

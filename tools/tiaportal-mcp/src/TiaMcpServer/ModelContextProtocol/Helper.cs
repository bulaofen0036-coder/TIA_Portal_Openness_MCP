using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace TiaMcpServer.ModelContextProtocol
{
    public class Helper
    {
        public static List<Attribute> GetAttributeList(IEngineeringObject obj)
        {
            var attributes = new List<Attribute>();

            if (obj != null)
            {
                foreach (var attr in obj.GetAttributeInfos())
                {
                    object value;
                    try { value = obj.GetAttribute(attr.Name); }
                    catch (Exception ex) { value = $"<unreadable: {ex.GetType().Name}>"; }
                    attributes.Add(new Attribute
                    {
                        Name = attr.Name,
                        Value = ToSerializableValue(value),
                        AccessMode = Enum.GetName(typeof(EngineeringAttributeAccessMode), attr.AccessMode)
                    });
                }
            }

            return attributes;
        }

        private static object ToSerializableValue(object value)
        {
            if (value == null) return null!;
            var t = value.GetType();
            if (t.IsPrimitive || value is string || value is decimal || value is DateTime || value is TimeSpan || value is Guid || t.IsEnum)
                return value;
            try { return value.ToString(); }
            catch { return $"<{t.Name}>"; }
        }

        public static BlockGroupInfo BuildBlockHierarchy(PlcBlockGroup group)
        {
            var groupInfo = new BlockGroupInfo
            {
                Name = group.Name
            };

            var blockList = new List<ResponseBlockInfo>();
            foreach (var block in group.Blocks)
            {
                var attributes = Helper.GetAttributeList(block);
                blockList.Add(new ResponseBlockInfo
                {
                    Name = block.Name,
                    TypeName = block.GetType().Name,
                    Namespace = GetOptionalStringProperty(block, "Namespace"),
                    ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage), block.ProgrammingLanguage),
                    MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                    IsConsistent = block.IsConsistent,
                    HeaderName = block.HeaderName,
                    ModifiedDate = block.ModifiedDate,
                    IsKnowHowProtected = block.IsKnowHowProtected,
                    Attributes = attributes,
                    Description = block.ToString()
                });
            }
            groupInfo.Blocks = blockList;

            var groupList = new List<BlockGroupInfo>();
            foreach (var subGroup in group.Groups)
            {
                groupList.Add(BuildBlockHierarchy(subGroup));
            }
            groupInfo.Groups = groupList;

            return groupInfo;
        }

        public static string BuildBlockPath(PlcBlock block)
        {
            var parts = new List<string> { block.Name };
            var parent = block.Parent;
            while (parent != null)
            {
                if (parent is PlcBlockSystemGroup) break;
                if (parent is PlcBlockGroup grp)
                {
                    parts.Insert(0, grp.Name);
                    parent = grp.Parent;
                    continue;
                }
                break;
            }
            return string.Join("/", parts);
        }

        public static string BuildTypePath(PlcType type)
        {
            var parts = new List<string> { type.Name };
            var parent = type.Parent;
            while (parent != null)
            {
                if (parent is PlcTypeSystemGroup) break;
                if (parent is PlcTypeGroup grp)
                {
                    parts.Insert(0, grp.Name);
                    parent = grp.Parent;
                    continue;
                }
                break;
            }
            return string.Join("/", parts);
        }

        public static string? GetOptionalStringProperty(object target, string propertyName)
        {
            try
            {
                var prop = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                return prop?.GetValue(target)?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}

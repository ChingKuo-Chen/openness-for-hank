using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;

internal static class TechnologyBuilder
{
    private sealed class AxisSpec
    {
        public string Name;
        public string Kind;          // SpeedAxis or PositionAxis
        public string PulseTag;
        public string DirectionTag;
        public int PtoIndex;
        public int SignalType;
    }

    private static readonly Dictionary<string, AxisSpec[]> RequiredByPlc =
        new Dictionary<string, AxisSpec[]>(StringComparer.OrdinalIgnoreCase)
    {
        {
            "KP_23019_Main2",
            new[]
            {
                Spec("Axis_630_main_speed", "Axis_630_main_speed_Signal_A", "Axis_630_main_speed_Signal_B", 0, 4),
                Spec("Die_Rotate_DB", "Die_Rotate_DB_Pulse", "Die_Rotate_DB_Direction", 1, 2),
                Spec("Die_Stand_inlet_DB", "Die_Stand_1_DB_Pulse", "Die_Stand_1_DB_Direction", 2, 2),
                Spec("Die_Stand_outlet_DB", "Die_Stand_2_DB_Pulse", "Die_Stand_2_DB_Direction", 3, 2),
            }
        },
        {
            "KP_23019_Inside_PLC1",
            new[]
            {
                Spec("Axis_630_1", "Axis_630_1_Pulse", "Axis_630_1_Direction", 0, 2),
                Spec("Axis_630_2", "Axis_630_2_Pulse", "Axis_630_2_Direction", 1, 2),
                Spec("Axis_630_3", "Axis_630_3_Pulse", "Axis_630_3_Direction", 2, 2),
            }
        },
        {
            "KP_23019_Inside_PLC2",
            new[]
            {
                // Inside_PLC2 的三軸 TO 仍綁 Axis_630_1~3 脈衝標籤（與 23019 現場一致）。
                Spec("Axis_630_4", "Axis_630_1_Pulse", "Axis_630_1_Direction", 0, 2),
                Spec("Axis_630_5", "Axis_630_2_Pulse", "Axis_630_2_Direction", 1, 2),
                Spec("Axis_630_6", "Axis_630_3_Pulse", "Axis_630_3_Direction", 2, 2),
            }
        },
    };

    public static void Dump(List<PlcSoftware> plcSoftwares, string outputPath)
    {
        List<string> lines = new List<string>();
        foreach (PlcSoftware plc in plcSoftwares)
        {
            WalkTechnology(plc.TechnologicalObjectGroup, plc.Name, string.Empty, lines);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        File.WriteAllLines(outputPath, lines, Encoding.UTF8);
        Console.WriteLine("工艺对象清单：" + outputPath + "（" + lines.Count + " 笔）");
    }

    public static void Build(Project project, List<PlcSoftware> plcSoftwares, string templateRoot)
    {
        foreach (PlcSoftware plc in plcSoftwares)
        {
            AxisSpec[] specs;
            if (!RequiredByPlc.TryGetValue(plc.Name, out specs))
            {
                specs = DiscoverFromProgram(templateRoot, plc.Name);
            }

            if (specs == null || specs.Length == 0)
            {
                continue;
            }

            Console.WriteLine(new string('-', 40));
            Console.WriteLine("工艺对象：" + plc.Name);

            TechnologicalInstanceDBComposition objects =
                plc.TechnologicalObjectGroup.TechnologicalObjects;

            foreach (AxisSpec spec in specs)
            {
                CreateSpeedAxis(objects, spec);
            }

            ConfigureAxes(objects, Path.Combine(templateRoot, "PLC_" + plc.Name, "TagTables", "Hardware.xml"));
        }

        project.Save();
        Console.WriteLine("工艺对象建立完成。");
    }

    public static void DumpParameters(PlcSoftware plc, string name)
    {
        TechnologicalInstanceDB axis = plc.TechnologicalObjectGroup.TechnologicalObjects.Find(name);
        if (axis == null)
        {
            Console.WriteLine("找不到工艺对象：" + name);
            return;
        }

        Console.WriteLine("参数 " + name + "：");
        foreach (TechnologicalParameter parameter in axis.Parameters)
        {
            object value = null;
            try
            {
                value = parameter.Value;
            }
            catch
            {
                value = "<unreadable>";
            }

            Console.WriteLine("  " + parameter.Name + " = " + value);
        }

        Console.WriteLine("属性 " + name + "（含 Unit/Config）：");
        try
        {
            foreach (EngineeringAttributeInfo info in axis.GetAttributeInfos())
            {
                string attrName = info.Name ?? string.Empty;
                if (attrName.IndexOf("Unit", StringComparison.OrdinalIgnoreCase) < 0 &&
                    attrName.IndexOf("Config", StringComparison.OrdinalIgnoreCase) < 0 &&
                    attrName.IndexOf("Motion", StringComparison.OrdinalIgnoreCase) < 0 &&
                    attrName.IndexOf("Dynamic", StringComparison.OrdinalIgnoreCase) < 0 &&
                    attrName.IndexOf("Velocity", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                object value = null;
                try
                {
                    value = axis.GetAttribute(attrName);
                }
                catch
                {
                    value = "<unreadable>";
                }

                Console.WriteLine("  ATTR " + attrName + " = " + value);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  无法列出属性：" + Describe(ex));
        }
    }

    public static void ExportTechnology(PlcSoftware plc, string name, string path)
    {
        TechnologicalInstanceDB axis = plc.TechnologicalObjectGroup.TechnologicalObjects.Find(name);
        if (axis == null)
        {
            throw new InvalidOperationException("找不到工艺对象：" + name);
        }

        axis.Export(new FileInfo(path), ExportOptions.WithDefaults);
        Console.WriteLine("已导出工艺对象：" + path);
    }

    private static void ConfigureAxes(
        TechnologicalInstanceDBComposition objects,
        string hardwareTagsPath)
    {
        if (!File.Exists(hardwareTagsPath))
        {
            return;
        }

        Dictionary<string, string> tagAddresses = LoadTagAddresses(hardwareTagsPath);
        foreach (TechnologicalInstanceDB axis in objects)
        {
            AxisSpec spec = RequiredByPlc.Values
                .SelectMany(items => items)
                .FirstOrDefault(item => string.Equals(item.Name, axis.Name, StringComparison.OrdinalIgnoreCase));

            if (spec == null)
            {
                continue;
            }

            if (!tagAddresses.ContainsKey(spec.PulseTag) ||
                !tagAddresses.ContainsKey(spec.DirectionTag))
            {
                Console.WriteLine("  略过输出绑定 " + axis.Name + "（找不到脉冲标籤）");
                continue;
            }

            // TO parameters store PLC tag names, not %Q/%I addresses.
            string pulse = spec.PulseTag;
            string direction = spec.DirectionTag;
            if (TryBindPulse(axis, spec))
            {
                Console.WriteLine("  绑定输出 " + axis.Name + "：" + pulse + " / " + direction);
            }
            else
            {
                Console.WriteLine("  绑定失败 " + axis.Name + "（参数写入未成功）");
            }
        }
    }

    private static bool TryBindPulse(TechnologicalInstanceDB axis, AxisSpec spec)
    {
        string pulse = spec.PulseTag;
        string direction = spec.DirectionTag;
        TrySetParameter(axis, "_Actor.Interface.PTO", spec.PtoIndex);
        TrySetParameter(axis, "_Actor.Interface.PTO_SignalType", spec.SignalType);
        TrySetParameter(axis, "_Actor.Interface.PTO_OutputBEnable", true);

        string[][] candidates =
        {
            new[] { "_Actor.Interface.PTO_OutputA", "_Actor.Interface.PTO_OutputB" },
            new[] { "PulseOutput", "DirectionOutput" },
            new[] { "PTO.PulseOutput", "PTO.DirectionOutput" },
            new[] { "DriveInterface.PulseOutput", "DriveInterface.DirectionOutput" },
            new[] { "OutputPulse", "OutputDirection" },
        };

        foreach (string[] pair in candidates)
        {
            if (TrySetParameter(axis, pair[0], pulse) && TrySetParameter(axis, pair[1], direction))
            {
                return true;
            }
        }

        foreach (TechnologicalParameter parameter in axis.Parameters)
        {
            string name = parameter.Name ?? string.Empty;
            if (name.IndexOf("Pulse", StringComparison.OrdinalIgnoreCase) >= 0 &&
                name.IndexOf("Output", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                TrySetParameterValue(parameter, pulse);
            }

            if (name.IndexOf("Direction", StringComparison.OrdinalIgnoreCase) >= 0 &&
                name.IndexOf("Output", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                TrySetParameterValue(parameter, direction);
            }
        }

        return false;
    }

    private static AxisSpec[] DiscoverFromProgram(string templateRoot, string plcName)
    {
        string blocksFolder = Path.Combine(templateRoot, "PLC_" + plcName, "Blocks");
        if (!Directory.Exists(blocksFolder))
        {
            return new AxisSpec[0];
        }

        HashSet<string> axisNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(blocksFolder, "*.xml"))
        {
            foreach (Match match in Regex.Matches(
                File.ReadAllText(file, Encoding.UTF8),
                @"<Component Name=""(Axis_[A-Za-z0-9_]+)"""))
            {
                axisNames.Add(match.Groups[1].Value);
            }
        }

        return axisNames.Select(name => Spec(name, name + "_Pulse", name + "_Direction", 0, 2)).ToArray();
    }

    private static void CreateSpeedAxis(TechnologicalInstanceDBComposition objects, AxisSpec spec)
    {
        if (objects.Find(spec.Name) != null)
        {
            Console.WriteLine("  已有 " + spec.Name);
            return;
        }

        try
        {
            // Created from the STEP 7 system library, not copied from another project.
            TechnologicalInstanceDB axis = objects.Create(
                spec.Name,
                "TO_PositioningAxis",
                new Version(8, 0));

            TrySetParameter(axis, "PulseOutput", spec.PulseTag);
            TrySetParameter(axis, "DirectionOutput", spec.DirectionTag);
            Console.WriteLine("  建立 " + spec.Name);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  失敗 " + spec.Name + "：" + Describe(ex));
        }
    }

    private static bool TrySetParameter(TechnologicalInstanceDB axis, string name, object value)
    {
        if (value == null)
        {
            return false;
        }

        try
        {
            TechnologicalParameter parameter = axis.Parameters.Find(name);
            if (parameter == null)
            {
                return false;
            }

            parameter.Value = value;
            return true;
        }
        catch
        {
            try
            {
                axis.SetAttribute(name, value);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool TrySetParameter(TechnologicalInstanceDB axis, string name, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            TechnologicalParameter parameter = axis.Parameters.Find(name);
            if (parameter == null)
            {
                return false;
            }

            return TrySetParameterValue(parameter, value);
        }
        catch
        {
            try
            {
                axis.SetAttribute(name, value);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool TrySetParameterValue(TechnologicalParameter parameter, string value)
    {
        try
        {
            parameter.Value = value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> LoadTagAddresses(string hardwareTagsPath)
    {
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        XDocument doc = XDocument.Load(hardwareTagsPath);
        foreach (XElement tag in doc.Descendants("SW.Tags.PlcTag"))
        {
            XElement attributeList = tag.Element("AttributeList");
            if (attributeList == null)
            {
                continue;
            }

            XElement name = attributeList.Element("Name");
            XElement address = attributeList.Element("LogicalAddress");
            if (name == null || address == null)
            {
                continue;
            }

            map[name.Value] = address.Value;
        }

        return map;
    }

    private static string AddressOf(Dictionary<string, string> tagAddresses, string tagName)
    {
        string address;
        return tagAddresses.TryGetValue(tagName, out address) ? address : null;
    }

    public static void Configure(Project project, List<PlcSoftware> plcSoftwares, string templateRoot)
    {
        foreach (PlcSoftware plc in plcSoftwares)
        {
            ConfigurePlc(plc, templateRoot);
        }

        project.Save();
    }

    public static void ConfigurePlc(PlcSoftware plc, string templateRoot)
    {
        Console.WriteLine("配置工艺对象：" + plc.Name);
        string pulseTagSource = ResolvePulseTagTable(plc.Name, templateRoot);
        ImportHardwareTagsIfMissing(plc, pulseTagSource);
        ConfigureAxes(plc.TechnologicalObjectGroup.TechnologicalObjects, pulseTagSource);
    }

    private static void ImportHardwareTagsIfMissing(PlcSoftware plc, string hardwareTags)
    {
        if (!File.Exists(hardwareTags))
        {
            return;
        }

        AxisSpec[] specs;
        if (!RequiredByPlc.TryGetValue(plc.Name, out specs) || specs.Length == 0)
        {
            return;
        }

        HashSet<string> existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            foreach (PlcTag tag in table.Tags)
            {
                if (!string.IsNullOrEmpty(tag.Name))
                {
                    existing.Add(tag.Name);
                }
            }
        }

        bool missing = specs.Any(spec =>
            !existing.Contains(spec.PulseTag) || !existing.Contains(spec.DirectionTag));
        if (!missing)
        {
            return;
        }

        try
        {
            plc.TagTableGroup.TagTables.Import(new FileInfo(hardwareTags), ImportOptions.Override);
            Console.WriteLine("  已匯入 Hardware 標籤表");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Hardware 標籤匯入失敗：" + Describe(ex));
        }
    }

    // TIA invents names like "Foo(1)" when a pulse output is bound to a channel
    // whose tag already exists, so duplicates have to be findable and renameable.
    public static void ListTags(PlcSoftware plc, string filter)
    {
        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            foreach (PlcTag tag in table.Tags)
            {
                if (filter != null &&
                    tag.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (tag.LogicalAddress ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                Console.WriteLine("  " + table.Name + " / " + tag.Name +
                    "  " + tag.LogicalAddress + "  " + tag.DataTypeName);
            }
        }
    }

    public static bool RenameTag(PlcSoftware plc, string oldName, string newName)
    {
        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            foreach (PlcTag tag in table.Tags)
            {
                if (!string.Equals(tag.Name, oldName, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    tag.Name = newName;
                    Console.WriteLine("  已改名：" + table.Name + " / " + oldName + " -> " + newName);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  改名失敗：" + Describe(ex));
                    return false;
                }
            }
        }

        Console.WriteLine("  找不到變數：" + oldName);
        return false;
    }

    public static bool DeleteTag(PlcSoftware plc, string name)
    {
        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            foreach (PlcTag tag in table.Tags)
            {
                if (!string.Equals(tag.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    tag.Delete();
                    Console.WriteLine("  已刪除變數：" + name);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  刪除失敗：" + Describe(ex));
                    return false;
                }
            }
        }

        return false;
    }

    private static IEnumerable<PlcTagTable> EnumerateTagTables(PlcTagTableGroup group)
    {
        foreach (PlcTagTable table in group.TagTables)
        {
            yield return table;
        }

        foreach (PlcTagTableUserGroup child in group.Groups)
        {
            foreach (PlcTagTable nested in EnumerateTagTables(child))
            {
                yield return nested;
            }
        }
    }

    private static string ResolvePulseTagTable(string plcName, string templateRoot)
    {
        if (plcName.Equals("KP_23019_Inside_PLC2", StringComparison.OrdinalIgnoreCase))
        {
            string plc1Hardware = Path.Combine(
                templateRoot,
                "PLC_KP_23019_Inside_PLC1",
                "TagTables",
                "Hardware.xml");

            if (File.Exists(plc1Hardware))
            {
                return plc1Hardware;
            }
        }

        return Path.Combine(templateRoot, "PLC_" + plcName, "TagTables", "Hardware.xml");
    }

    private static void TrySetAttribute(IEngineeringObject target, string name, object value)
    {
        try
        {
            target.SetAttribute(name, value);
        }
        catch
        {
        }
    }

    private static void WalkTechnology(
        TechnologicalInstanceDBGroup group,
        string plc,
        string prefix,
        List<string> lines)
    {
        foreach (TechnologicalInstanceDB item in group.TechnologicalObjects)
        {
            lines.Add(string.Join("\t", "tech", plc, prefix, item.Name,
                item.OfSystemLibElement, item.OfSystemLibVersion));
        }

        foreach (TechnologicalInstanceDBUserGroup child in group.Groups)
        {
            WalkTechnology(child, plc, Join(prefix, child.Name), lines);
        }
    }

    private static string Join(string prefix, string name)
    {
        return prefix.Length == 0 ? name : prefix + "/" + name;
    }

    private static AxisSpec Spec(string name, string pulse, string direction, int ptoIndex, int signalType)
    {
        return new AxisSpec
        {
            Name = name,
            Kind = "SpeedAxis",
            PulseTag = pulse,
            DirectionTag = direction,
            PtoIndex = ptoIndex,
            SignalType = signalType,
        };
    }

    // TIA computes FollowingError/Modulo/Window/Resolution from the position unit at
    // creation time and never rescales them, so the unit must be set on a fresh TO.
    public static void RecreateAxes(
        Project project,
        List<PlcSoftware> plcSoftwares,
        string configPath,
        string onlyPlc)
    {
        if (!File.Exists(configPath))
        {
            Console.WriteLine("找不到設定檔：" + configPath);
            return;
        }

        Dictionary<string, Dictionary<string, string>> sections = LoadConfig(configPath);

        foreach (KeyValuePair<string, Dictionary<string, string>> section in sections)
        {
            string[] parts = section.Key.Split(new[] { '/' }, 2);
            if (parts.Length != 2)
            {
                continue;
            }

            string plcName = parts[0];
            string axisName = parts[1];
            if (onlyPlc != null &&
                !string.Equals(plcName, onlyPlc, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            PlcSoftware plc = plcSoftwares.FirstOrDefault(
                candidate => string.Equals(candidate.Name, plcName, StringComparison.OrdinalIgnoreCase));
            if (plc == null)
            {
                continue;
            }

            TechnologicalInstanceDB existing = FindAxis(plc.TechnologicalObjectGroup, axisName);
            if (existing == null)
            {
                continue;
            }

            string libElement = existing.OfSystemLibElement;
            Version libVersion = existing.OfSystemLibVersion;
            int number = existing.Number;

            string unitText;
            if (!section.Value.TryGetValue("Units.LengthUnit", out unitText))
            {
                Console.WriteLine("略過（設定檔沒有 Units.LengthUnit）：" + section.Key);
                continue;
            }

            Console.WriteLine(new string('-', 40));
            Console.WriteLine("重建工藝對象：" + section.Key +
                "（" + libElement + " " + libVersion + " / DB" + number + "）");

            try
            {
                existing.Delete();
            }
            catch (Exception ex)
            {
                Console.WriteLine("  刪除失敗：" + Describe(ex));
                continue;
            }

            TechnologicalInstanceDB created;
            try
            {
                created = plc.TechnologicalObjectGroup.TechnologicalObjects.Create(
                    axisName,
                    libElement,
                    libVersion ?? new Version(8, 0));
            }
            catch (Exception ex)
            {
                Console.WriteLine("  重建失敗：" + Describe(ex));
                continue;
            }

            // Position unit must be the very first write on the fresh object.
            string unitError;
            if (AssignParameter(created, "Units.LengthUnit", unitText, out unitError))
            {
                Console.WriteLine("  單位已設：Units.LengthUnit = " + unitText);
            }
            else
            {
                Console.WriteLine("  單位寫入失敗：" + unitError);
            }

            try
            {
                created.Number = number;
                created.AutoNumber = false;
            }
            catch
            {
            }

            project.Save();
        }

        ApplyConfig(project, plcSoftwares, configPath, onlyPlc);
    }

    public static void DumpConfig(List<PlcSoftware> plcSoftwares, string path, string onlyPlc)
    {
        List<string> lines = new List<string>();
        foreach (PlcSoftware plc in plcSoftwares)
        {
            if (onlyPlc != null &&
                !string.Equals(plc.Name, onlyPlc, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (TechnologicalInstanceDB axis in EnumerateAxes(plc.TechnologicalObjectGroup))
            {
                lines.Add("[" + plc.Name + "/" + axis.Name + "]");
                foreach (TechnologicalParameter parameter in axis.Parameters)
                {
                    object value = ReadParameter(parameter);
                    if (!IsTransferable(parameter.Name, value))
                    {
                        continue;
                    }

                    lines.Add(parameter.Name + "=" + FormatValue(value));
                }

                lines.Add(string.Empty);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        Console.WriteLine("已匯出工藝對象設定：" + path + "（" + lines.Count + " 行）");
    }

    public static void ApplyConfig(Project project, List<PlcSoftware> plcSoftwares, string path, string onlyPlc)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine("找不到設定檔：" + path);
            return;
        }

        Dictionary<string, Dictionary<string, string>> sections = LoadConfig(path);
        int axes = 0;
        int written = 0;
        int failed = 0;

        foreach (KeyValuePair<string, Dictionary<string, string>> section in sections)
        {
            string[] parts = section.Key.Split(new[] { '/' }, 2);
            if (parts.Length != 2)
            {
                continue;
            }

            string plcName = parts[0];
            string axisName = parts[1];
            if (onlyPlc != null &&
                !string.Equals(plcName, onlyPlc, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            PlcSoftware plc = plcSoftwares.FirstOrDefault(
                candidate => string.Equals(candidate.Name, plcName, StringComparison.OrdinalIgnoreCase));
            if (plc == null)
            {
                Console.WriteLine("  略過（找不到 PLC）：" + section.Key);
                continue;
            }

            TechnologicalInstanceDB axis = FindAxis(plc.TechnologicalObjectGroup, axisName);
            if (axis == null)
            {
                Console.WriteLine("  略過（找不到軸）：" + section.Key);
                continue;
            }

            Console.WriteLine("套用設定：" + section.Key);
            axes++;

            List<string> pending = OrderedParameterNames(section.Value.Keys)
                .Where(name => !string.Equals(name, "Configuration.Module", StringComparison.OrdinalIgnoreCase))
                .ToList();

            for (int pass = 1; pass <= 3 && pending.Count > 0; pass++)
            {
                List<string> failedNames = new List<string>();
                foreach (string name in pending)
                {
                    string error;
                    if (AssignParameter(axis, name, section.Value[name], out error))
                    {
                        written++;
                    }
                    else
                    {
                        failedNames.Add(name);
                        if (pass == 3)
                        {
                            failed++;
                            Console.WriteLine("    寫入失敗 " + name + " = " + section.Value[name] +
                                (string.IsNullOrEmpty(error) ? string.Empty : "（" + error + "）"));
                        }
                    }
                }

                pending = failedNames;
            }
        }

        project.Save();
        Console.WriteLine("工藝對象設定完成：軸 " + axes + "、寫入 " + written + "、失敗 " + failed);
    }

    private static IEnumerable<TechnologicalInstanceDB> EnumerateAxes(TechnologicalInstanceDBGroup group)
    {
        foreach (TechnologicalInstanceDB item in group.TechnologicalObjects)
        {
            yield return item;
        }

        foreach (TechnologicalInstanceDBUserGroup child in group.Groups)
        {
            foreach (TechnologicalInstanceDB nested in EnumerateAxes(child))
            {
                yield return nested;
            }
        }
    }

    private static TechnologicalInstanceDB FindAxis(TechnologicalInstanceDBGroup group, string name)
    {
        return EnumerateAxes(group).FirstOrDefault(
            axis => string.Equals(axis.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, Dictionary<string, string>> LoadConfig(string path)
    {
        Dictionary<string, Dictionary<string, string>> sections =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> current = null;

        foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) &&
                line.EndsWith("]", StringComparison.Ordinal))
            {
                string key = line.Substring(1, line.Length - 2);
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[key] = current;
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0 || current == null)
            {
                continue;
            }

            current[line.Substring(0, eq)] = line.Substring(eq + 1);
        }

        return sections;
    }

    private static IEnumerable<string> OrderedParameterNames(IEnumerable<string> names)
    {
        return names.OrderBy(name => ParameterRank(name)).ThenBy(name => name, StringComparer.OrdinalIgnoreCase);
    }

    private static int ParameterRank(string name)
    {
        if (name.StartsWith("Units.", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (name.StartsWith("Sensor[1].System", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Sensor[1].Type", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Sensor[1].Interface.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("_Sensor[1].Interface.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Sensor[1].MountingMode", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Sensor[1].InverseDirection", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Sensor[1].DataAdaption", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("_Sensor[1].DataAdaption", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (name.IndexOf("PulsesPerDriveRevolution", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.StartsWith("Mechanics.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Sensor[1].Parameter.", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (name.StartsWith("_Actor.Interface.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Actor.", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        // TIA recalculates accel/decel when max speed changes; write limits first.
        if (name.StartsWith("DynamicLimits.", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (name.StartsWith("DynamicDefaults.", StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        if (name.StartsWith("FollowingError.", StringComparison.OrdinalIgnoreCase))
        {
            if (name.EndsWith("EnableMonitoring", StringComparison.OrdinalIgnoreCase))
            {
                return 6;
            }

            if (name.EndsWith("MinValue", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("MinVelocity", StringComparison.OrdinalIgnoreCase))
            {
                return 7;
            }

            return 8;
        }

        if (name.StartsWith("Homing.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("PositioningMonitoring.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("StandstillSignal.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Modulo.", StringComparison.OrdinalIgnoreCase))
        {
            return 9;
        }

        return 10;
    }

    private static bool AssignParameter(TechnologicalInstanceDB axis, string name, string text)
    {
        string unused;
        return AssignParameter(axis, name, text, out unused);
    }

    private static bool AssignParameter(TechnologicalInstanceDB axis, string name, string text, out string error)
    {
        error = null;
        List<object> candidates = CoerceCandidates(text);
        TechnologicalParameter parameter = axis.Parameters.Find(name);

        foreach (object candidate in candidates)
        {
            // Prefer SetAttribute: many TO parameters expose Value as read-only.
            try
            {
                axis.SetAttribute(name, candidate);
                object after = null;
                try
                {
                    after = parameter != null ? parameter.Value : axis.GetAttribute(name);
                }
                catch
                {
                }

                if (ValuesMatch(after, candidate) || ValuesMatch(after, text))
                {
                    return true;
                }

                error = "SetAttribute 后仍为 " + (after == null ? "<null>" : after.ToString());
            }
            catch (Exception ex)
            {
                error = Describe(ex);
            }

            if (parameter != null)
            {
                try
                {
                    parameter.Value = candidate;
                    object after = parameter.Value;
                    if (ValuesMatch(after, candidate) || ValuesMatch(after, text))
                    {
                        return true;
                    }

                    error = "Value 后仍为 " + (after == null ? "<null>" : after.ToString());
                }
                catch (Exception ex)
                {
                    error = Describe(ex);
                }
            }

            if (TrySetParameter(axis, name, candidate))
            {
                return true;
            }
        }

        if (string.IsNullOrEmpty(error))
        {
            error = parameter == null ? "找不到參數" : "無法寫入";
        }

        return false;
    }

    private static bool ValuesMatch(object actual, object expected)
    {
        if (actual == null || expected == null)
        {
            return actual == null && expected == null;
        }

        string left = FormatValue(actual);
        string right = expected is string
            ? (string)expected
            : FormatValue(expected);

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        double a;
        double b;
        return double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
               double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out b) &&
               Math.Abs(a - b) < 0.000001;
    }

    private static object CoerceValue(string text)
    {
        List<object> candidates = CoerceCandidates(text);
        return candidates.Count > 0 ? candidates[0] : text;
    }

    private static List<object> CoerceCandidates(string text)
    {
        List<object> values = new List<object>();
        if (string.Equals(text, "True", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "False", StringComparison.OrdinalIgnoreCase))
        {
            values.Add(bool.Parse(text));
            return values;
        }

        int integer;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
        {
            values.Add(integer);
        }

        long longInteger;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out longInteger))
        {
            values.Add(longInteger);
        }

        double number;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            values.Add(number);
            values.Add(Convert.ToSingle(number));
            try
            {
                values.Add(Convert.ToDecimal(number));
            }
            catch
            {
            }
        }

        values.Add(text);
        return values;
    }

    private static object ReadParameter(TechnologicalParameter parameter)
    {
        try
        {
            return parameter.Value;
        }
        catch
        {
            return "<unreadable>";
        }
    }

    private static string FormatValue(object value)
    {
        if (value is bool)
        {
            return ((bool)value) ? "True" : "False";
        }

        if (value is IFormattable)
        {
            return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
        }

        return value.ToString();
    }

    public static TechnologicalInstanceDB EnsureHighSpeedCounter(
        Project project,
        PlcSoftware plc,
        string name,
        string moduleNameContains)
    {
        TechnologicalInstanceDBComposition objects = plc.TechnologicalObjectGroup.TechnologicalObjects;
        TechnologicalInstanceDB existing = objects.Find(name);
        if (existing != null)
        {
            Console.WriteLine("  已有工藝對象：" + name);
            BindCounterModule(project, existing, moduleNameContains);
            return existing;
        }

        try
        {
            TechnologicalInstanceDB created = objects.Create(
                name,
                "High_Speed_Counter",
                new Version(4, 1));
            Console.WriteLine("  建立工藝對象：" + name + "（High_Speed_Counter）");
            BindCounterModule(project, created, moduleNameContains);
            return created;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  無法建立 " + name + "：" + Describe(ex));
            return null;
        }
    }

    private static void BindCounterModule(Project project, TechnologicalInstanceDB counter, string moduleNameContains)
    {
        DeviceItem module = FindDeviceItemByName(project, moduleNameContains);
        if (module == null)
        {
            Console.WriteLine("  找不到計數模組：" + moduleNameContains);
            return;
        }

        // Prefer the channel/submodule item when the parent is the rack device.
        DeviceItem channel = module;
        foreach (DeviceItem child in module.DeviceItems)
        {
            if (child.Name != null &&
                child.Name.IndexOf(moduleNameContains, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                channel = child;
                break;
            }
        }

        string[] names =
        {
            "Configuration.Module",
            "Module",
            "_Configuration.Module",
        };

        foreach (string name in names)
        {
            if (TrySetParameter(counter, name, channel) || TrySetParameter(counter, name, module))
            {
                Console.WriteLine("  綁定計數模組：" + channel.Name + " via " + name);
                TrySetParameter(counter, "Configuration.ChannelNo", 0);
                return;
            }
        }

        Console.WriteLine("  無法綁定計數模組：" + module.Name);
        DumpParameterWriteHints(counter, "Module");
    }

    // The TO refuses to compile when its channel settings disagree with the
    // technology module, so the module side has to be inspectable and writable too.
    public static void DumpModuleParameters(Project project, string moduleNameContains, string filter)
    {
        DeviceItem module = FindDeviceItemByName(project, moduleNameContains);
        if (module == null)
        {
            Console.WriteLine("找不到模組：" + moduleNameContains);
            return;
        }

        foreach (DeviceItem item in Flatten(module))
        {
            Console.WriteLine(new string('-', 40));
            Console.WriteLine("模組：" + item.Name);
            IEngineeringObject target = item;

            IList<EngineeringAttributeInfo> infos;
            try
            {
                infos = target.GetAttributeInfos();
            }
            catch (Exception ex)
            {
                Console.WriteLine("  無法列出屬性：" + Describe(ex));
                continue;
            }

            foreach (EngineeringAttributeInfo info in infos)
            {
                if (filter != null &&
                    info.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                object value;
                try
                {
                    value = target.GetAttribute(info.Name);
                }
                catch
                {
                    continue;
                }

                Console.WriteLine("  " + info.Name + " = " + value);
            }
        }
    }

    public static void SetModuleParameter(
        Project project,
        string moduleNameContains,
        string parameterName,
        string value)
    {
        DeviceItem module = FindDeviceItemByName(project, moduleNameContains);
        if (module == null)
        {
            Console.WriteLine("找不到模組：" + moduleNameContains);
            return;
        }

        bool written = false;
        foreach (DeviceItem item in Flatten(module))
        {
            IEngineeringObject target = item;
            object current;
            try
            {
                current = target.GetAttribute(parameterName);
            }
            catch
            {
                continue;
            }

            List<object> candidates = CoerceCandidates(value);
            if (current != null)
            {
                try
                {
                    candidates.Insert(0, Convert.ChangeType(value, current.GetType(),
                        System.Globalization.CultureInfo.InvariantCulture));
                }
                catch
                {
                }
            }

            foreach (object candidate in candidates)
            {
                try
                {
                    target.SetAttribute(parameterName, candidate);
                    Console.WriteLine("  " + item.Name + "：" + parameterName + " = " + candidate);
                    written = true;
                    break;
                }
                catch
                {
                }
            }
        }

        if (!written)
        {
            Console.WriteLine("  沒有任何模組接受 " + parameterName);
        }
    }

    private static IEnumerable<DeviceItem> Flatten(DeviceItem item)
    {
        yield return item;
        foreach (DeviceItem child in item.DeviceItems)
        {
            foreach (DeviceItem nested in Flatten(child))
            {
                yield return nested;
            }
        }
    }

    private static void DumpParameterWriteHints(TechnologicalInstanceDB axis, string filter)
    {
        foreach (TechnologicalParameter parameter in axis.Parameters)
        {
            if (parameter.Name == null ||
                parameter.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            object value = null;
            try
            {
                value = parameter.Value;
            }
            catch
            {
                value = "<unreadable>";
            }

            Console.WriteLine("    param " + parameter.Name + " = " + value +
                " / type=" + (value == null ? "?" : value.GetType().FullName));
        }
    }

    private static DeviceItem FindDeviceItemByName(Project project, string nameContains)
    {
        foreach (Device device in EnumerateDevices(project))
        {
            foreach (DeviceItem item in HardwareBuilder.AllItems(device))
            {
                if (item.Name != null &&
                    item.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return item;
                }
            }
        }

        return null;
    }

    private static IEnumerable<Device> EnumerateDevices(Project project)
    {
        foreach (Device device in project.Devices)
        {
            yield return device;
        }

        foreach (DeviceUserGroup group in project.DeviceGroups)
        {
            foreach (Device device in EnumerateGroupDevices(group))
            {
                yield return device;
            }
        }
    }

    private static IEnumerable<Device> EnumerateGroupDevices(DeviceUserGroup group)
    {
        foreach (Device device in group.Devices)
        {
            yield return device;
        }

        foreach (DeviceUserGroup child in group.Groups)
        {
            foreach (Device device in EnumerateGroupDevices(child))
            {
                yield return device;
            }
        }
    }

    private static bool IsTransferable(string name, object value)
    {
        if (string.IsNullOrEmpty(name) || value == null)
        {
            return false;
        }

        string text = value.ToString();
        if (text == "<unreadable>" || text.Length == 0)
        {
            return false;
        }

        if (name.Equals("Configuration.Module", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (name.StartsWith("Status", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ErrorBits", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ControlPanel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ActualPosition", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ActualVelocity", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Position", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Velocity", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (name.Equals("Actor.Interface.PTO", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".AREA", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".RID", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".OFFSET", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".DB_NUMBER", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOf("DigitalInputAddress", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("MinSwitchAddress", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("MaxSwitchAddress", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("AddressIn", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("AddressOut", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        return true;
    }

    private static string Describe(Exception ex)
    {
        return ex == null ? string.Empty : ex.GetBaseException().Message;
    }
}

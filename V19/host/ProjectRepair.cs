using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.Compiler;

// Fixes TEST_PROJECT issues without copying objects from a live reference project:
// patch variable-bound interpolation UDTs, rename HSC channels from program+tag
// cross-reference, and set OB30 cycle time from imported block metadata.
internal static class ProjectRepair
{
    private static readonly Regex MaxIntpArray =
        new Regex(@"Array\[0\.\.&quot;max_intp&quot;\]", RegexOptions.Compiled);

    private static readonly Regex LocalHsc =
        new Regex(@"Local~(?<name>HSC_[A-Za-z0-9_]+)", RegexOptions.Compiled);

    private static readonly Regex EncComponent =
        new Regex(@"<Component Name=""(?<name>Enc_[^""]+)""", RegexOptions.Compiled);

    private static readonly string[] InterpolationTypes =
    {
        "Interpolation_Array_adv",
        "Interpolation_Table_adv",
        "Interpolation_Pair",
    };

    private static readonly string[] InterpolationBlocks =
    {
        "Generate_Interpolation_Table_adv",
        "Interpolation_Controler_adv",
        "Interpolation_Lookup_adv",
        "Interpolation_Output_adv",
    };

    private static readonly Dictionary<string, string[]> TapeBlocksByPlc =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        {
            "KP_23019_Main",
            new[] { "Tape_White_Plate_Control", "Tape3_Plate_WH_DB" }
        },
        {
            "KP_23019_TP1",
            new[]
            {
                "Tape_Plate1_Control_red",
                "Tape_Plate1_Control_red_DB",
                "Tape_Plate2_Control_white",
                "Tape_Plate2_Control_white_DB",
            }
        },
        {
            "KP_23019_TP2",
            new[]
            {
                "Tape_Plate1_Control_red",
                "Tape_Plate1_Control_red_DB",
                "Tape_Plate2_Control_white",
                "Tape_Plate2_Control_white_DB",
            }
        },
    };

    public static void Run(Project project, List<PlcSoftware> plcSoftwares, string templateRoot)
    {
        string patchRoot = Path.Combine(templateRoot, "_patched");
        if (Directory.Exists(patchRoot))
        {
            Directory.Delete(patchRoot, true);
        }

        Directory.CreateDirectory(patchRoot);
        Console.WriteLine("修復工作目錄：" + patchRoot);

        PatchInterpolationArtifacts(templateRoot, patchRoot);
        ImportInterpolation(project, plcSoftwares, templateRoot, patchRoot);
        SyncHscNames(project, plcSoftwares, templateRoot);
        FixOb30Cycle(project, plcSoftwares, templateRoot);
        RemoveUnimportableBlocks(project, plcSoftwares);

        project.Save();
        Console.WriteLine("修復完成，專案已儲存。");
    }

    private static readonly string[] UnimportableBlockNames =
    {
        "Diagnostic error interrupt",
    };

    private static void RemoveUnimportableBlocks(Project project, List<PlcSoftware> plcSoftwares)
    {
        foreach (PlcSoftware plc in plcSoftwares)
        {
            foreach (string blockName in UnimportableBlockNames)
            {
                PlcBlock block = LadBlockTools.EnumerateBlocksPublic(plc.BlockGroup)
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, blockName, StringComparison.OrdinalIgnoreCase));

                if (block == null)
                {
                    continue;
                }

                block.Delete();
                Console.WriteLine("已刪除無法正確匯入的區塊：" + plc.Name + " / " + blockName);
            }
        }
    }

    private static void PatchInterpolationArtifacts(string templateRoot, string patchRoot)
    {
        Dictionary<string, int> maxIntpByPlc = LoadMaxIntpByPlc(templateRoot);

        foreach (string source in Directory.GetFiles(templateRoot, "*.xml", SearchOption.AllDirectories))
        {
            if (source.Contains("_patched"))
            {
                continue;
            }

            string text = File.ReadAllText(source, Encoding.UTF8);
            if (!text.Contains("max_intp"))
            {
                continue;
            }

            int maxIntp = ResolveMaxIntp(source, templateRoot, maxIntpByPlc);
            string patched = PatchInterpolationText(text, maxIntp);
            string relative = source.Substring(templateRoot.Length).TrimStart('\\', '/');
            string target = Path.Combine(patchRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllText(target, patched, new UTF8Encoding(false));
        }

        Console.WriteLine("已產生插值修補 XML。");
    }

    internal static string PatchInterpolationText(string text, int maxIntp)
    {
        // Openness rejects variable bounds; max_intp is a PLC constant in Default tag table.
        return text.Replace(
            "Array[0..&quot;max_intp&quot;]",
            "Array[0.." + maxIntp + "]");
    }

    private static Dictionary<string, int> LoadMaxIntpByPlc(string templateRoot)
    {
        Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in Directory.GetDirectories(templateRoot, "PLC_*"))
        {
            string plcName = Path.GetFileName(folder).Substring(4);
            int? parsed = ParseMaxIntp(Path.Combine(folder, "TagTables", "Default tag table.xml"));
            map[plcName] = parsed ?? 10;
        }

        return map;
    }

    private static int ResolveMaxIntp(
        string source,
        string templateRoot,
        Dictionary<string, int> maxIntpByPlc)
    {
        string relative = source.Substring(templateRoot.Length).TrimStart('\\', '/');
        string[] parts = relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].StartsWith("PLC_", StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }

        string plcName = parts[0].Substring(4);
        int value;
        return maxIntpByPlc.TryGetValue(plcName, out value) ? value : 10;
    }

    private static int? ParseMaxIntp(string tagTablePath)
    {
        if (!File.Exists(tagTablePath))
        {
            return null;
        }

        XDocument doc = XDocument.Load(tagTablePath);
        foreach (XElement constant in doc.Descendants().Where(
            element => element.Name.LocalName.EndsWith("PlcUserConstant", StringComparison.Ordinal) ||
                       element.Name.LocalName.EndsWith("PlcTag", StringComparison.Ordinal)))
        {
            XElement attributeList = constant.Element("AttributeList");
            if (attributeList == null)
            {
                continue;
            }

            XElement name = attributeList.Element("Name");
            XElement value = attributeList.Element("Value");
            if (name == null || value == null)
            {
                continue;
            }

            if (!string.Equals(name.Value, "max_intp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int parsed;
            return int.TryParse(value.Value.Trim(), out parsed) ? parsed : (int?)null;
        }

        return null;
    }

    private static void ImportInterpolation(
        Project project,
        List<PlcSoftware> plcSoftwares,
        string templateRoot,
        string patchRoot)
    {
        foreach (KeyValuePair<string, string[]> plcEntry in TapeBlocksByPlc)
        {
            PlcSoftware plc = plcSoftwares.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, plcEntry.Key, StringComparison.OrdinalIgnoreCase));

            if (plc == null)
            {
                continue;
            }

            string plcFolder = Path.Combine(patchRoot, "PLC_" + plcEntry.Key);
            Console.WriteLine("匯入插值修補：" + plcEntry.Key);

            foreach (string typeName in InterpolationTypes)
            {
                ImportTypeIfPresent(plc, Path.Combine(plcFolder, "Types", typeName + ".xml"));
            }

            foreach (string blockName in InterpolationBlocks.Concat(plcEntry.Value))
            {
                ImportBlockIfPresent(project, plc, templateRoot,
                    Path.Combine(plcFolder, "Blocks", blockName + ".xml"));
            }
        }
    }

    private static void ImportTypeIfPresent(PlcSoftware plc, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            plc.TypeGroup.Types.Import(new FileInfo(path), ImportOptions.Override);
            Console.WriteLine("  UDT " + Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex)
        {
            Console.WriteLine("  UDT 失敗 " + Path.GetFileName(path) + "：" + Describe(ex));
        }
    }

    private static void ImportBlockIfPresent(Project project, PlcSoftware plc, string templateRoot, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            string group = FindBlockGroup(path, templateRoot);
            EnsureBlockGroup(plc, group).Blocks.Import(new FileInfo(path), ImportOptions.Override);
            Console.WriteLine("  block " + Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex)
        {
            Console.WriteLine("  block 失敗 " + Path.GetFileName(path) + "：" + Describe(ex));
        }
    }

    private static string FindBlockGroup(string blockPath, string templateRoot)
    {
        string manifest = Path.Combine(templateRoot, "software-tree.txt");

        if (!File.Exists(manifest))
        {
            return string.Empty;
        }

        string blockName = Path.GetFileNameWithoutExtension(blockPath);
        string plcName = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(blockPath))).Substring(4);

        foreach (string line in File.ReadAllLines(manifest, Encoding.UTF8))
        {
            string[] cells = line.Split('\t');
            if (cells.Length >= 4 &&
                cells[0] == "block" &&
                string.Equals(cells[1], plcName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(cells[3], blockName, StringComparison.OrdinalIgnoreCase))
            {
                return cells[2];
            }
        }

        return string.Empty;
    }

    private static PlcBlockGroup EnsureBlockGroup(PlcSoftware plc, string path)
    {
        PlcBlockGroup current = plc.BlockGroup;
        if (string.IsNullOrEmpty(path))
        {
            return current;
        }

        foreach (string part in path.Split('/'))
        {
            PlcBlockUserGroup next = current.Groups.Find(part) ?? current.Groups.Create(part);
            current = next;
        }

        return current;
    }

    public static void SyncHscOnly(Project project, List<PlcSoftware> plcSoftwares, string templateRoot)
    {
        SyncHscNames(project, plcSoftwares, templateRoot);
    }

    private static void SyncHscNames(Project project, List<PlcSoftware> plcSoftwares, string templateRoot)
    {
        foreach (string plcFolder in Directory.GetDirectories(templateRoot, "PLC_*"))
        {
            string plcName = Path.GetFileName(plcFolder).Substring(4);
            Device device = FindDeviceForPlc(project, plcName);
            if (device == null)
            {
                Console.WriteLine("HSC 略過（找不到裝置）：" + plcName);
                continue;
            }

            Dictionary<int, string> targets = BuildHscRenameMap(plcFolder);
            if (targets.Count == 0)
            {
                continue;
            }

            Dictionary<int, string> fromHardware = LoadHscNamesFromHardwareXml(templateRoot, plcName);
            foreach (KeyValuePair<int, string> pair in fromHardware)
            {
                targets[pair.Key] = pair.Value;
            }

            Console.WriteLine("HSC 重新命名：" + plcName + "（" + targets.Count + " 通道）");
            foreach (DeviceItem item in HardwareBuilder.AllItems(device))
            {
                if (item.PositionNumber < 16 || item.PositionNumber > 21)
                {
                    continue;
                }

                string targetName;
                if (!targets.TryGetValue(item.PositionNumber, out targetName))
                {
                    continue;
                }

                if (string.Equals(item.Name, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    string oldName = item.Name;
                    item.SetAttribute("Name", targetName);
                    Console.WriteLine("  pos " + item.PositionNumber + "：" + oldName + " -> " + targetName);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  無法重新命名 pos " + item.PositionNumber + "：" + Describe(ex));
                }
            }
        }
    }

    private static Dictionary<int, string> LoadHscNamesFromHardwareXml(string templateRoot, string plcName)
    {
        Dictionary<int, string> map = new Dictionary<int, string>();
        string path = Path.Combine(templateRoot, "hardware.xml");
        if (!File.Exists(path))
        {
            return map;
        }

        XDocument doc = XDocument.Load(path);
        foreach (XElement item in doc.Descendants("Item"))
        {
            XAttribute container = item.Attribute("ContainerName");
            XAttribute position = item.Attribute("PositionNumber");
            XAttribute name = item.Attribute("Name");
            if (container == null || position == null || name == null)
            {
                continue;
            }

            if (!string.Equals(container.Value, plcName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int pos;
            if (!int.TryParse(position.Value, out pos) || pos < 16 || pos > 21)
            {
                continue;
            }

            map[pos] = name.Value;
        }

        return map;
    }

    private static Dictionary<int, string> BuildHscRenameMap(string plcFolder)
    {
        Dictionary<string, string> encToHsc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string blocksFolder = Path.Combine(plcFolder, "Blocks");
        if (Directory.Exists(blocksFolder))
        {
            foreach (string file in Directory.GetFiles(blocksFolder, "*.xml"))
            {
                PairEncAndHsc(File.ReadAllText(file, Encoding.UTF8), encToHsc);
            }
        }

        Dictionary<int, string> positionMap = new Dictionary<int, string>();
        string hardwareTags = Path.Combine(plcFolder, "TagTables", "Hardware.xml");
        if (!File.Exists(hardwareTags))
        {
            return positionMap;
        }

        XDocument doc = XDocument.Load(hardwareTags);
        foreach (XElement tag in doc.Descendants("SW.Tags.PlcTag"))
        {
            XElement attributeList = tag.Element("AttributeList");
            if (attributeList == null)
            {
                continue;
            }

            XElement address = attributeList.Element("LogicalAddress");
            XElement name = attributeList.Element("Name");
            if (address == null || name == null)
            {
                continue;
            }

            Match idMatch = Regex.Match(address.Value, @"%ID(\d+)");
            if (!idMatch.Success)
            {
                continue;
            }

            int byteAddress = int.Parse(idMatch.Groups[1].Value);
            if (byteAddress < 1000)
            {
                continue;
            }

            int position = 16 + (byteAddress - 1000) / 4;
            string hscName = InferHscName(name.Value);
            if (hscName == null)
            {
                encToHsc.TryGetValue(name.Value, out hscName);
            }

            if (hscName != null)
            {
                positionMap[position] = hscName;
            }
        }

        return positionMap;
    }

    private static string InferHscName(string encTagName)
    {
        if (!encTagName.StartsWith("Enc_", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string tail = encTagName.Substring(4);
        if (tail.Equals("Angle_Strander", StringComparison.OrdinalIgnoreCase))
        {
            return "HSC_Strander_angle";
        }

        if (tail.Equals("Angle_Strander_POS", StringComparison.OrdinalIgnoreCase))
        {
            return "HSC_Strander_angle_for_POS";
        }

        if (tail.Equals("Angle_1600_1B", StringComparison.OrdinalIgnoreCase))
        {
            return "HSC_1600_1B_Angle";
        }

        if (tail.Equals("Angle_Die", StringComparison.OrdinalIgnoreCase))
        {
            return "HSC_Die_Rotate_angle";
        }

        if (tail.Equals("Rotor_Inside", StringComparison.OrdinalIgnoreCase) ||
            tail.Equals("Rotor_inside", StringComparison.OrdinalIgnoreCase))
        {
            return "HSC_Rotor_inside";
        }

        if (tail.Equals("Rotor_SPD_Ref", StringComparison.OrdinalIgnoreCase))
        {
            return "HSC_Rotor_SPD_Ref";
        }

        Match twist = Regex.Match(tail, @"^630_(\d+)$");
        if (twist.Success)
        {
            return "HSC_630_" + twist.Groups[1].Value;
        }

        return "HSC_" + tail;
    }

    private static void PairEncAndHsc(string text, Dictionary<string, string> encToHsc)
    {
        string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        List<KeyValuePair<int, string>> hscs = new List<KeyValuePair<int, string>>();
        List<KeyValuePair<int, string>> encs = new List<KeyValuePair<int, string>>();

        for (int i = 0; i < lines.Length; i++)
        {
            Match hscMatch = LocalHsc.Match(lines[i]);
            if (hscMatch.Success)
            {
                hscs.Add(new KeyValuePair<int, string>(i, hscMatch.Groups["name"].Value));
            }

            Match encMatch = EncComponent.Match(lines[i]);
            if (encMatch.Success &&
                !encMatch.Groups["name"].Value.StartsWith("PNQ_", StringComparison.OrdinalIgnoreCase))
            {
                encs.Add(new KeyValuePair<int, string>(i, encMatch.Groups["name"].Value));
            }
        }

        foreach (KeyValuePair<int, string> hsc in hscs)
        {
            int bestDistance = int.MaxValue;
            string bestEnc = null;
            foreach (KeyValuePair<int, string> enc in encs)
            {
                int distance = Math.Abs(enc.Key - hsc.Key);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestEnc = enc.Value;
                }
            }

            if (bestEnc != null && bestDistance <= 20)
            {
                encToHsc[bestEnc] = hsc.Value;
            }
        }
    }

    private static void FixOb30Cycle(
        Project project,
        List<PlcSoftware> plcSoftwares,
        string templateRoot)
    {
        foreach (PlcSoftware plc in plcSoftwares)
        {
            string obPath = Path.Combine(templateRoot, "PLC_" + plc.Name, "Blocks", "Cyclic interrupt.xml");
            if (!File.Exists(obPath))
            {
                continue;
            }

            XDocument doc = XDocument.Load(obPath);
            XElement cycle = doc.Descendants("CyclicTime").FirstOrDefault();
            if (cycle == null)
            {
                continue;
            }

            int desired;
            if (!int.TryParse(cycle.Value.Trim(), out desired) || desired <= 0)
            {
                desired = 5;
            }

            PlcBlock ob = LadBlockTools.EnumerateBlocksPublic(plc.BlockGroup)
                .FirstOrDefault(block => string.Equals(block.Name, "Cyclic interrupt", StringComparison.OrdinalIgnoreCase));

            if (ob == null)
            {
                continue;
            }

            try
            {
                ob.SetAttribute("CyclicTime", desired);
                Console.WriteLine(plc.Name + " OB30 CyclicTime = " + desired);
            }
            catch (Exception ex)
            {
                Console.WriteLine(plc.Name + " OB30 設定失敗：" + Describe(ex));
            }
        }
    }

    private static Device FindDeviceForPlc(Project project, string plcName)
    {
        foreach (Device device in EnumerateDevices(project))
        {
            foreach (DeviceItem item in HardwareBuilder.AllItems(device))
            {
                SoftwareContainer container = item.GetService<SoftwareContainer>();
                PlcSoftware plc = container == null ? null : container.Software as PlcSoftware;
                if (plc != null &&
                    string.Equals(plc.Name, plcName, StringComparison.OrdinalIgnoreCase))
                {
                    return device;
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

        foreach (Device device in project.UngroupedDevicesGroup.Devices)
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

    private static string Describe(Exception ex)
    {
        return ex == null ? string.Empty : ex.GetBaseException().Message;
    }
}

// Inside PLC: Modbus library instance DBs and TO pulse binding.
internal static class InsidePlcRepair
{
    private static readonly string[] InsidePlcNames =
    {
        "KP_23019_Inside_PLC1",
        "KP_23019_Inside_PLC2",
    };

    private sealed class LibraryInstanceSpec
    {
        public string DbName;
        public string FbName;
    }

    private static readonly LibraryInstanceSpec[] ModbusLibraryInstances =
    {
        new LibraryInstanceSpec { DbName = "MB_SLAVE_DB", FbName = "MB_SLAVE" },
        new LibraryInstanceSpec { DbName = "MB_COMM_LOAD_MAIN_DB", FbName = "MB_COMM_LOAD" },
    };

    public static void Run(Project project, List<PlcSoftware> plcSoftwares, string templateRoot)
    {
        foreach (string plcName in InsidePlcNames)
        {
            PlcSoftware plc = plcSoftwares.FirstOrDefault(
                candidate => string.Equals(candidate.Name, plcName, StringComparison.OrdinalIgnoreCase));

            if (plc == null)
            {
                Console.WriteLine("略過（找不到 PLC）：" + plcName);
                continue;
            }

            Console.WriteLine(new string('-', 40));
            Console.WriteLine("Inside 修復：" + plcName);

            ImportHardwareTagsIfMissing(plc, templateRoot, plcName);
            EnsureModbusLibraryInstanceDbs(project, plc);
            RemoveImportedBlock(plc, "Diagnostic error interrupt");
            TechnologyBuilder.ConfigurePlc(plc, templateRoot);
        }

        project.Save();
        Console.WriteLine("Inside PLC 修復完成，專案已儲存。");
    }

    private static void ImportHardwareTagsIfMissing(PlcSoftware plc, string templateRoot, string plcName)
    {
        string hardwareTags = Path.Combine(templateRoot, "PLC_" + plcName, "TagTables", "Hardware.xml");
        if (!HasPulseTags(plc, plcName))
        {
            string fallback = Path.Combine(templateRoot, "PLC_KP_23019_Inside_PLC1", "TagTables", "Hardware.xml");
            if (File.Exists(fallback) &&
                !string.Equals(hardwareTags, fallback, StringComparison.OrdinalIgnoreCase))
            {
                hardwareTags = fallback;
            }
        }

        if (!File.Exists(hardwareTags))
        {
            Console.WriteLine("  略過 Hardware 標籤（找不到範本）");
            return;
        }

        if (HasPulseTags(plc, plcName))
        {
            Console.WriteLine("  Hardware 脈衝標籤已存在");
            return;
        }

        try
        {
            plc.TagTableGroup.TagTables.Import(new FileInfo(hardwareTags), ImportOptions.Override);
            Console.WriteLine("  已匯入 Hardware 標籤表（" + Path.GetFileName(hardwareTags) + "）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Hardware 標籤匯入失敗：" + Describe(ex));
        }
    }

    private static bool HasPulseTags(PlcSoftware plc, string plcName)
    {
        string suffix = plcName.IndexOf("Inside_PLC2", StringComparison.OrdinalIgnoreCase) >= 0
            ? "Axis_630_1_Pulse"
            : "Axis_630_1_Pulse";
        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            foreach (PlcTag tag in table.Tags)
            {
                if (tag.Name != null &&
                    string.Equals(tag.Name, suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
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

    private static void EnsureModbusLibraryInstanceDbs(Project project, PlcSoftware plc)
    {
        foreach (LibraryInstanceSpec spec in ModbusLibraryInstances)
        {
            MaterializeLibraryInstanceDb(project, plc, spec.DbName, spec.FbName);
        }
    }

    // TIA 慣例：從程式庫拖出 MB_SLAVE / MB_COMM_LOAD → 編譯產生 Instance DB → 刪除 FB，DB 留下。
    internal static void MaterializeLibraryInstanceDb(
        Project project,
        PlcSoftware plc,
        string dbName,
        string fbName)
    {
        PlcBlock existing = FindBlock(plc, dbName);
        if (existing != null && existing.Number > 0)
        {
            Console.WriteLine("  已有 " + dbName + "（DB" + existing.Number + "）");
            RemoveTransientLibraryFb(project, plc, fbName);
            return;
        }

        DeleteBlockByName(project, plc, dbName);

        int dbNumber = NextFreeDbNumber(plc);
        try
        {
            plc.BlockGroup.Blocks.CreateInstanceDB(dbName, false, dbNumber, fbName);
            project.Save();
            Console.WriteLine("  建立程式庫 Instance DB：" + dbName + " <- " + fbName + "（DB" + dbNumber + "）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  無法建立 " + dbName + "：" + Describe(ex));
            return;
        }

        CompilePlc(plc, dbName);
        RemoveTransientLibraryFb(project, plc, fbName);
        project.Save();

        PlcBlock created = FindBlock(plc, dbName);
        if (created == null)
        {
            Console.WriteLine("  警告：" + dbName + " 在編譯後消失");
        }
        else if (created.Number <= 0)
        {
            Console.WriteLine("  警告：" + dbName + " 仍為無效編號 DB" + created.Number);
        }
    }

    private static void DeleteBlockByName(Project project, PlcSoftware plc, string blockName)
    {
        PlcBlock block = FindBlock(plc, blockName);
        if (block == null)
        {
            return;
        }

        try
        {
            block.Delete();
            project.Save();
            Console.WriteLine("  刪除舊區塊 " + blockName);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  無法刪除 " + blockName + "：" + Describe(ex));
        }
    }

    private static void RemoveTransientLibraryFb(Project project, PlcSoftware plc, string fbName)
    {
        PlcBlock fb = LadBlockTools.EnumerateBlocksPublic(plc.BlockGroup)
            .FirstOrDefault(block =>
                string.Equals(block.Name, fbName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(block.GetType().Name, "FB", StringComparison.OrdinalIgnoreCase));

        if (fb == null)
        {
            return;
        }

        try
        {
            fb.Delete();
            project.Save();
            Console.WriteLine("  刪除暫時程式庫 FB：" + fbName + "（保留 Instance DB）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  無法刪除暫時 FB " + fbName + "：" + Describe(ex));
        }
    }

    private static void CompilePlc(PlcSoftware plc, string context)
    {
        ICompilable compilable = plc.GetService<ICompilable>();
        if (compilable == null)
        {
            return;
        }

        try
        {
            CompilerResult result = compilable.Compile();
            Console.WriteLine(
                "  編譯 " + context + "：" + result.State +
                "／錯誤 " + result.ErrorCount + "／警告 " + result.WarningCount);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  編譯失敗 " + context + "：" + Describe(ex));
        }
    }

    private static int NextFreeDbNumber(PlcSoftware plc)
    {
        HashSet<int> used = new HashSet<int>(
            LadBlockTools.EnumerateBlocksPublic(plc.BlockGroup)
                .Select(block => block.Number));

        for (int candidate = 350; candidate < 2000; candidate++)
        {
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return used.Max() + 1;
    }

    private static void RemoveImportedBlock(PlcSoftware plc, string blockName)
    {
        PlcBlock block = FindBlock(plc, blockName);
        if (block == null)
        {
            return;
        }

        block.Delete();
        Console.WriteLine("  已刪除不應匯入的區塊：" + blockName);
    }

    private static PlcBlock FindBlock(PlcSoftware plc, string name)
    {
        return LadBlockTools.EnumerateBlocksPublic(plc.BlockGroup)
            .FirstOrDefault(block => string.Equals(block.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static PlcBlockGroup FindOrCreateBlockGroup(PlcBlockGroup root, params string[] path)
    {
        PlcBlockGroup current = root;
        foreach (string segment in path)
        {
            PlcBlockUserGroup next = current.Groups.Find(segment) ?? current.Groups.Create(segment);
            current = next;
        }

        return current;
    }

    private static string Describe(Exception ex)
    {
        return ex == null ? string.Empty : ex.GetBaseException().Message;
    }
}

internal static class TfPlcRepair
{
    public static void Run(Project project, List<PlcSoftware> plcSoftwares, string templateRoot)
    {
        ProjectRepair.SyncHscOnly(project, plcSoftwares, templateRoot);

        PlcSoftware plc = plcSoftwares.FirstOrDefault(
            candidate => string.Equals(candidate.Name, "KP_23019_TF", StringComparison.OrdinalIgnoreCase));
        if (plc == null)
        {
            Console.WriteLine("略過（找不到 PLC）：KP_23019_TF");
            return;
        }

        Console.WriteLine("TF 修復：KP_23019_TF");
        InsidePlcRepair.MaterializeLibraryInstanceDb(project, plc, "MB_MASTER_MAIN_DB", "MB_MASTER");
        InsidePlcRepair.MaterializeLibraryInstanceDb(project, plc, "MB_COMM_LOAD_MAIN_DB", "MB_COMM_LOAD");
        InsidePlcRepair.MaterializeLibraryInstanceDb(project, plc, "CTRL_HSC_0_DB", "CTRL_HSC");
        InsidePlcRepair.MaterializeLibraryInstanceDb(project, plc, "CTRL_HSC_1_DB", "CTRL_HSC");
        project.Save();
        Console.WriteLine("TF 修復完成，專案已儲存。");
    }
}

internal static class MainPlcRepair
{
    public static void Run(Project project, List<PlcSoftware> plcSoftwares, string templateRoot)
    {
        PlcSoftware plc = plcSoftwares.FirstOrDefault(
            candidate => string.Equals(candidate.Name, "KP_23019_Main", StringComparison.OrdinalIgnoreCase));
        if (plc == null)
        {
            Console.WriteLine("略過（找不到 PLC）：KP_23019_Main");
            return;
        }

        Console.WriteLine("Main 修復：KP_23019_Main");
        TechnologyBuilder.EnsureHighSpeedCounter(project, plc, "HSC_Length", "TM Count");
        TryMaterialize(project, plc, "MB_COMM_LOAD_PLC_DB", "MB_COMM_LOAD", "Modbus_Comm_Load");
        TryMaterialize(project, plc, "MB_PLC_DB", "MB_MASTER", "Modbus_Master");
        TryMaterialize(project, plc, "MB_COMM_LOAD_Pico_DB", "MB_COMM_LOAD", "Modbus_Comm_Load");
        TryMaterialize(project, plc, "MB_Pico_DB", "MB_MASTER", "Modbus_Master");
        InsidePlcRepair.MaterializeLibraryInstanceDb(project, plc, "MB_SERVER_DB", "MB_SERVER");

        string techConfig = Path.Combine(
            Path.GetDirectoryName(templateRoot) ?? string.Empty,
            "..",
            "tech-config.txt");
        techConfig = Path.GetFullPath(techConfig);
        if (!File.Exists(techConfig))
        {
            techConfig = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "HmiExport",
                "tech-config.txt");
        }

        if (File.Exists(techConfig))
        {
            TechnologyBuilder.ApplyConfig(project, plcSoftwares, techConfig, "KP_23019_Main");
        }

        project.Save();
        Console.WriteLine("Main 修復完成，專案已儲存。");
    }

    private static void TryMaterialize(Project project, PlcSoftware plc, string dbName, params string[] fbNames)
    {
        foreach (string fbName in fbNames)
        {
            PlcBlock before = LadBlockTools.EnumerateBlocksPublic(plc.BlockGroup)
                .FirstOrDefault(block => string.Equals(block.Name, dbName, StringComparison.OrdinalIgnoreCase));
            if (before != null && before.Number > 0)
            {
                Console.WriteLine("  已有 " + dbName + "（DB" + before.Number + "）");
                return;
            }

            InsidePlcRepair.MaterializeLibraryInstanceDb(project, plc, dbName, fbName);
            PlcBlock after = LadBlockTools.EnumerateBlocksPublic(plc.BlockGroup)
                .FirstOrDefault(block => string.Equals(block.Name, dbName, StringComparison.OrdinalIgnoreCase));
            if (after != null && after.Number > 0)
            {
                return;
            }
        }
    }
}

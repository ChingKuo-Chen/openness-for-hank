using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;

// Copies one PLC station from another .ap21 into the attached project
// via a temporary global library, then sets IP and I-device to Main.
internal static class PlcCopier
{
    private const string PayoffChars = "\u7d66\u7dda"; // 給線

    public static int ListHeadless(string sourceAp21)
    {
        sourceAp21 = sourceAp21.Trim('"');
        if (!File.Exists(sourceAp21))
        {
            Console.WriteLine("找不到來源專案：" + sourceAp21);
            return 1;
        }

        Console.WriteLine("無介面開啟（只讀清單）：" + sourceAp21);
        using (TiaPortal portal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
        {
            Project project = portal.Projects.Open(new FileInfo(sourceAp21));
            try
            {
                List<PlcSoftware> plcs = LadBlockTools.FindAllPlcSoftwares(project);
                PrintDevices(project, plcs);
                LadBlockTools.DumpHardware(project);
            }
            finally
            {
                project.Close();
            }
        }

        return 0;
    }

    public static int ExportHeadless(string sourceAp21, string plcHint, string match, string destDir)
    {
        sourceAp21 = sourceAp21.Trim('"');
        if (!File.Exists(sourceAp21))
        {
            Console.WriteLine("找不到來源專案：" + sourceAp21);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(match))
        {
            match = "PreTwist";
        }

        if (string.IsNullOrWhiteSpace(destDir))
        {
            destDir = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "HmiExport",
                "Templates",
                Path.GetFileNameWithoutExtension(sourceAp21),
                "headless-" + SanitizeFile(match));
        }

        Directory.CreateDirectory(destDir);
        Console.WriteLine("無介面匯出（只讀）：" + sourceAp21);
        Console.WriteLine("比對：" + match);
        Console.WriteLine("輸出：" + destDir);

        using (TiaPortal portal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
        {
            Project project = portal.Projects.Open(new FileInfo(sourceAp21));
            try
            {
                List<PlcSoftware> plcs = LadBlockTools.FindAllPlcSoftwares(project);
                PrintDevices(project, plcs);

                PlcSoftware plc = FindPlcSoftware(plcs, plcHint);
                if (plc == null)
                {
                    Console.WriteLine("找不到 PLC。請用 --source-plc:<軟體名>");
                    return 1;
                }

                Console.WriteLine("PLC：" + plc.Name);
                int blocks = 0;
                foreach (Tuple<string, PlcBlock> item in WalkBlocks(plc.BlockGroup, ""))
                {
                    string path = item.Item1;
                    PlcBlock block = item.Item2;
                    bool hit = match == "*" ||
                        ContainsIgnoreCase(block.Name, match) ||
                        ContainsIgnoreCase(path, match);
                    if (hit)
                    {
                        string file = Path.Combine(destDir, SanitizeFile(block.Name) + ".xml");
                        try
                        {
                            if (File.Exists(file))
                            {
                                File.Delete(file);
                            }

                            block.Export(new FileInfo(file), ExportOptions.WithDefaults);
                            Console.WriteLine("BLOCK " + path + "/" + block.Name +
                                "  " + block.GetType().Name +
                                "  #" + block.Number +
                                "  -> " + file);
                            blocks++;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("BLOCK 失敗 " + path + "/" + block.Name + "：" + ex.Message);
                        }
                    }
                    else if (ContainsIgnoreCase(path, "Speed_Loop") ||
                        ContainsIgnoreCase(block.Name, "SoftGear"))
                    {
                        Console.WriteLine("  其他 " + path + "/" + block.Name +
                            "  " + block.GetType().Name);
                    }
                }

                int tags = 0;
                using (StreamWriter writer = new StreamWriter(
                    Path.Combine(destDir, "tags-" + SanitizeFile(match) + ".txt"), false))
                {
                    foreach (PlcTagTable table in WalkTagTables(plc.TagTableGroup))
                    {
                        foreach (PlcTag tag in table.Tags)
                        {
                            string comment = TryAttr(tag, "Comment");
                            if (!ContainsIgnoreCase(tag.Name, match) &&
                                !ContainsIgnoreCase(comment, match) &&
                                !ContainsIgnoreCase(table.Name, match))
                            {
                                continue;
                            }

                            string addr = tag.LogicalAddress ?? TryAttr(tag, "LogicalAddress") ?? "";
                            string line = table.Name + "\t" + tag.Name + "\t" +
                                tag.DataTypeName + "\t" + addr + "\t" + (comment ?? "");
                            Console.WriteLine("TAG  " + line);
                            writer.WriteLine(line);
                            tags++;
                        }
                    }
                }

                Console.WriteLine("匯出區塊 " + blocks + "、Tag " + tags);
            }
            finally
            {
                project.Close();
            }
        }

        return 0;
    }

    public static int DumpDiHeadless(string sourceAp21, string plcHint)
    {
        sourceAp21 = sourceAp21.Trim('"');
        if (!File.Exists(sourceAp21))
        {
            Console.WriteLine("找不到來源專案：" + sourceAp21);
            return 1;
        }

        Console.WriteLine("無介面 DI 設定：" + sourceAp21);
        using (TiaPortal portal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
        {
            Project project = portal.Projects.Open(new FileInfo(sourceAp21));
            try
            {
                List<PlcSoftware> plcs = LadBlockTools.FindAllPlcSoftwares(project);
                PrintDevices(project, plcs);
                PlcSoftware plc = FindPlcSoftware(plcs, plcHint);
                if (plc == null)
                {
                    Console.WriteLine("找不到 PLC。請用 --source-plc:<軟體名>");
                    return 1;
                }

                return HardwareSync.DumpCpuDi(project, plc.Name);
            }
            finally
            {
                project.Close();
            }
        }
    }

    public static int CopyInto(
        TiaPortal destPortal,
        Project dest,
        string sourceAp21,
        string sourceHint,
        string asCpuName,
        string groupPath,
        string ip,
        string controllerName)
    {
        sourceAp21 = sourceAp21.Trim('"');
        if (!File.Exists(sourceAp21))
        {
            Console.WriteLine("找不到來源專案：" + sourceAp21);
            return 1;
        }

        string libDir = Path.Combine(Path.GetTempPath(), "tia-pf-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libDir);
        string libPath = Path.Combine(libDir, "pfcopy.al21");

        Console.WriteLine("無介面開啟來源：" + sourceAp21);
        using (TiaPortal srcPortal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
        {
            Project src = srcPortal.Projects.Open(new FileInfo(sourceAp21));
            try
            {
                List<PlcSoftware> plcs = LadBlockTools.FindAllPlcSoftwares(src);
                PrintDevices(src, plcs);

                Device sourceDevice = FindSourceDevice(src, plcs, sourceHint);
                if (sourceDevice == null)
                {
                    Console.WriteLine("找不到要複製的 PLC。請用 --source-plc:<裝置名或軟體名>");
                    return 1;
                }

                Console.WriteLine("複製裝置：" + sourceDevice.Name);
                DirectoryInfo libFolder = new DirectoryInfo(libDir);
                UserGlobalLibrary srcLib = srcPortal.GlobalLibraries.Create<UserGlobalLibrary>(libFolder, "pfcopy");
                try
                {
                    MasterCopy created = srcLib.MasterCopyFolder.MasterCopies.Create((IMasterCopySource)sourceDevice);
                    Console.WriteLine("MasterCopy：" + created.Name +
                        " 數量=" + srcLib.MasterCopyFolder.MasterCopies.Count);
                    srcLib.Save();
                    Console.WriteLine("已存程式庫：" + (srcLib.Path == null ? libDir : srcLib.Path.FullName));
                    if (srcLib.Path != null)
                    {
                        libPath = srcLib.Path.FullName;
                    }
                }
                finally
                {
                    srcLib.Close();
                }
            }
            finally
            {
                src.Close();
            }
        }

        Console.WriteLine("在目標專案開啟程式庫並建立裝置…");
        UserGlobalLibrary destLib = destPortal.GlobalLibraries.Open(new FileInfo(libPath), OpenMode.ReadWrite);
        try
        {
            MasterCopy master = destLib.MasterCopyFolder.MasterCopies.FirstOrDefault();
            if (master == null)
            {
                Console.WriteLine("程式庫是空的。Count=" + destLib.MasterCopyFolder.MasterCopies.Count +
                    " Folders=" + destLib.MasterCopyFolder.Folders.Count);
                foreach (MasterCopyUserFolder folder in destLib.MasterCopyFolder.Folders)
                {
                    Console.WriteLine("  資料夾 " + folder.Name + " copies=" + folder.MasterCopies.Count);
                }

                return 1;
            }

            Console.WriteLine("從 MasterCopy 建立：" + master.Name);

            DeviceUserGroup group = EnsureGroup(dest, groupPath);
            Device made = group.Devices.CreateFrom(master);
            Console.WriteLine("已建立裝置：" + made.Name);

            RenameCopied(made, asCpuName);
            AssignAddressAndSubnet(dest, made, ip, "PN/IE_1");
            if (!string.IsNullOrEmpty(controllerName) &&
                !string.Equals(controllerName, "none", StringComparison.OrdinalIgnoreCase))
            {
                ConnectIDevice(dest, made, controllerName);
            }
            dest.Save();
            Console.WriteLine("已存檔。PF CPU 名稱應為：" + asCpuName + " / IP " + ip);
        }
        finally
        {
            destLib.Close();
            try
            {
                Directory.Delete(libDir, true);
            }
            catch
            {
            }
        }

        return 0;
    }

    private static void PrintDevices(Project project, List<PlcSoftware> plcs)
    {
        Console.WriteLine("來源裝置：");
        foreach (Device device in EnumerateDevices(project))
        {
            Console.WriteLine("  裝置：" + device.Name + " / " + device.TypeIdentifier);
            foreach (DeviceItem item in HardwareBuilder.AllItems(device))
            {
                SoftwareContainer container = item.GetService<SoftwareContainer>();
                PlcSoftware plc = container == null ? null : container.Software as PlcSoftware;
                if (plc != null)
                {
                    Console.WriteLine("    PLC：" + plc.Name);
                }

                HmiTarget hmi = container == null ? null : container.Software as HmiTarget;
                if (hmi != null)
                {
                    Console.WriteLine("    HMI：" + hmi.Name);
                }
            }
        }

        Console.WriteLine("PLC 軟體：" + string.Join("、", plcs.Select(p => p.Name).ToArray()));
    }

    private static Device FindSourceDevice(Project project, List<PlcSoftware> plcs, string hint)
    {
        List<Tuple<Device, string>> candidates = new List<Tuple<Device, string>>();
        foreach (Device device in EnumerateDevices(project))
        {
            string plcName = null;
            foreach (DeviceItem item in HardwareBuilder.AllItems(device))
            {
                SoftwareContainer container = item.GetService<SoftwareContainer>();
                PlcSoftware plc = container == null ? null : container.Software as PlcSoftware;
                if (plc != null)
                {
                    plcName = plc.Name;
                    break;
                }
            }

            string hmiName = null;
            foreach (DeviceItem item in HardwareBuilder.AllItems(device))
            {
                SoftwareContainer container = item.GetService<SoftwareContainer>();
                if (container == null || container.Software == null)
                {
                    continue;
                }

                HmiTarget hmi = container.Software as HmiTarget;
                if (hmi != null)
                {
                    hmiName = hmi.Name;
                    break;
                }
            }

            string label = plcName ?? hmiName ?? device.Name;
            candidates.Add(Tuple.Create(device, label));

            if (!string.IsNullOrEmpty(hint) &&
                (string.Equals(device.Name, hint, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(label, hint, StringComparison.OrdinalIgnoreCase)))
            {
                return device;
            }
        }

        List<Device> hits = candidates
            .Where(item => MatchesPayoff(item.Item1.Name) || MatchesPayoff(item.Item2))
            .Select(item => item.Item1)
            .ToList();

        if (hits.Count == 1)
        {
            return hits[0];
        }

        Console.WriteLine("候選（給線/PF/Payoff）：" + hits.Count);
        foreach (Tuple<Device, string> item in candidates)
        {
            Console.WriteLine("  " + item.Item1.Name + " / " + item.Item2);
        }

        return hits.Count == 1 ? hits[0] : null;
    }

    private static bool MatchesPayoff(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        string upper = name.ToUpperInvariant();
        return upper.IndexOf("PF", StringComparison.Ordinal) >= 0 ||
            upper.IndexOf("PAY", StringComparison.Ordinal) >= 0 ||
            name.IndexOf(PayoffChars, StringComparison.Ordinal) >= 0;
    }

    private static void RenameCopied(Device device, string newName)
    {
        bool hasPlc = false;
        bool hasHmi = false;
        DeviceItem cpuItem = null;
        DeviceItem hmiItem = null;

        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            SoftwareContainer container = item.GetService<SoftwareContainer>();
            if (container == null || container.Software == null)
            {
                continue;
            }

            if (container.Software is PlcSoftware)
            {
                hasPlc = true;
                cpuItem = item;
            }

            if (container.Software is HmiTarget)
            {
                hasHmi = true;
                hmiItem = item;
            }
        }

        if (hasPlc)
        {
            device.SetAttribute("Name", newName + "_station");
            Console.WriteLine("  裝置 -> " + newName + "_station");
            if (cpuItem != null)
            {
                cpuItem.SetAttribute("Name", newName);
                Console.WriteLine("  CPU " + cpuItem.Name + " -> " + newName);
            }

            return;
        }

        device.SetAttribute("Name", newName);
        Console.WriteLine("  裝置 -> " + newName);
        if (hasHmi && hmiItem != null)
        {
            try
            {
                hmiItem.SetAttribute("Name", newName);
                Console.WriteLine("  HMI 項 -> " + newName);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  HMI 項改名略過：" + ex.Message);
            }
        }
    }

    private static void AssignAddressAndSubnet(Project project, Device device, string ip, string subnetName)
    {
        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            NetworkInterface net = item.GetService<NetworkInterface>();
            if (net == null)
            {
                continue;
            }

            foreach (Node node in net.Nodes)
            {
                string current = TryAttr(node, "Address");
                if (string.IsNullOrEmpty(current) || !current.Contains("."))
                {
                    continue;
                }

                try
                {
                    node.SetAttribute("Address", ip);
                    Console.WriteLine("  IP " + node.Name + " -> " + ip);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  設 IP 失敗：" + ex.Message);
                }

                if (node.ConnectedSubnet != null)
                {
                    continue;
                }

                Subnet subnet = project.Subnets.FirstOrDefault(s =>
                    string.Equals(s.Name, subnetName, StringComparison.OrdinalIgnoreCase));
                try
                {
                    if (subnet == null)
                    {
                        node.CreateAndConnectToSubnet(subnetName);
                    }
                    else
                    {
                        node.ConnectToSubnet(subnet);
                    }

                    Console.WriteLine("  接子網路 " + subnetName);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  接子網路失敗：" + ex.Message);
                }
            }
        }
    }

    private static void ConnectIDevice(Project project, Device device, string controllerName)
    {
        Device controllerDevice = EnumerateDevices(project)
            .FirstOrDefault(d => string.Equals(d.Name, controllerName, StringComparison.OrdinalIgnoreCase));
        if (controllerDevice == null)
        {
            Console.WriteLine("找不到 IO 控制器裝置：" + controllerName);
            return;
        }

        IoController controller = HardwareBuilder.AllItems(controllerDevice)
            .Select(item => item.GetService<NetworkInterface>())
            .Where(net => net != null)
            .SelectMany(net => net.IoControllers.Cast<IoController>())
            .FirstOrDefault();

        List<NetworkInterface> interfaces = HardwareBuilder.AllItems(device)
            .Select(item => item.GetService<NetworkInterface>())
            .Where(net => net != null)
            .ToList();

        IoConnector connector = interfaces
            .SelectMany(net => net.IoConnectors.Cast<IoConnector>())
            .FirstOrDefault();

        if (connector == null)
        {
            foreach (NetworkInterface net in interfaces)
            {
                if (!EnableIoDevice(net, device.Name))
                {
                    continue;
                }

                connector = net.IoConnectors.Cast<IoConnector>().FirstOrDefault();
                if (connector != null)
                {
                    break;
                }
            }
        }

        if (controller == null || connector == null)
        {
            Console.WriteLine("無法接到 PROFINET IO（缺控制器或 I-device 連接器）。");
            return;
        }

        if (connector.ConnectedToIoSystem != null)
        {
            Console.WriteLine("  已接到 " + connector.ConnectedToIoSystem.Name);
            return;
        }

        IoSystem system = controller.IoSystem ?? controller.CreateIoSystem("PROFINET IO-System");
        connector.ConnectToIoSystem(system);
        Console.WriteLine("  I-device -> " + controllerName + "（" + system.Name + "）");
    }

    private static bool EnableIoDevice(NetworkInterface net, string deviceName)
    {
        try
        {
            object current = net.GetAttribute("InterfaceOperatingMode");
            if (current == null)
            {
                return false;
            }

            string text = current.ToString();
            if (text.IndexOf("IoDevice", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            object combined = Enum.Parse(current.GetType(), text + ", IoDevice", true);
            net.SetAttribute("InterfaceOperatingMode", combined);
            Console.WriteLine("  " + deviceName + "：介面切換為 " + combined);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  切換 I-device 失敗：" + ex.Message);
            return false;
        }
    }

    private static DeviceUserGroup EnsureGroup(Project project, string path)
    {
        DeviceUserGroup current = null;
        foreach (string segment in path.Split('/'))
        {
            DeviceUserGroupComposition composition = current == null ? project.DeviceGroups : current.Groups;
            DeviceUserGroup next = composition.Find(segment);
            if (next == null)
            {
                next = composition.Create(segment);
                Console.WriteLine("  建立群組：" + segment);
            }

            current = next;
        }

        return current;
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
            foreach (Device device in FromGroup(group))
            {
                yield return device;
            }
        }
    }

    private static IEnumerable<Device> FromGroup(DeviceUserGroup group)
    {
        foreach (Device device in group.Devices)
        {
            yield return device;
        }

        foreach (DeviceUserGroup child in group.Groups)
        {
            foreach (Device device in FromGroup(child))
            {
                yield return device;
            }
        }
    }

    private static PlcSoftware FindPlcSoftware(List<PlcSoftware> plcs, string hint)
    {
        if (plcs == null || plcs.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(hint))
        {
            PlcSoftware exact = plcs.FirstOrDefault(p =>
                string.Equals(p.Name, hint, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }

            List<PlcSoftware> hits = plcs
                .Where(p => ContainsIgnoreCase(p.Name, hint))
                .ToList();
            if (hits.Count == 1)
            {
                return hits[0];
            }
        }

        List<PlcSoftware> mains = plcs
            .Where(p => ContainsIgnoreCase(p.Name, "Main"))
            .ToList();
        return mains.Count == 1 ? mains[0] : (plcs.Count == 1 ? plcs[0] : null);
    }

    private static IEnumerable<Tuple<string, PlcBlock>> WalkBlocks(PlcBlockGroup group, string prefix)
    {
        foreach (PlcBlock block in group.Blocks)
        {
            yield return Tuple.Create(prefix, block);
        }

        foreach (PlcBlockUserGroup child in group.Groups)
        {
            string next = string.IsNullOrEmpty(prefix) ? child.Name : prefix + "/" + child.Name;
            foreach (Tuple<string, PlcBlock> item in WalkBlocks(child, next))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<PlcTagTable> WalkTagTables(PlcTagTableGroup group)
    {
        foreach (PlcTagTable table in group.TagTables)
        {
            yield return table;
        }

        foreach (PlcTagTableUserGroup child in group.Groups)
        {
            foreach (PlcTagTable table in WalkTagTables(child))
            {
                yield return table;
            }
        }
    }

    private static bool ContainsIgnoreCase(string text, string match)
    {
        return !string.IsNullOrEmpty(text) &&
            !string.IsNullOrEmpty(match) &&
            text.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string SanitizeFile(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "item";
        }

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }

    private static string TryAttr(IEngineeringObject target, string name)
    {
        try
        {
            object value = target.GetAttribute(name);
            return value == null ? null : value.ToString();
        }
        catch
        {
            return null;
        }
    }
}

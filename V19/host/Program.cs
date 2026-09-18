using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Siemens.Engineering.Cax;
using Siemens.Engineering;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Communication;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
using Siemens.Engineering.HW;
#if !TIA_V19
using Siemens.Engineering.HW.CommunicationConnections;
#endif
using Siemens.Engineering.HW.Extensions;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.HardwareCatalog;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
#if !TIA_V19
using HwConnection = Siemens.Engineering.HW.CommunicationConnections.Connection;
using HwHmiConnection = Siemens.Engineering.HW.CommunicationConnections.HmiConnection;
#endif
using HmiSoftConnection = Siemens.Engineering.Hmi.Communication.Connection;

internal static class Program
{
    private const string OpennessFolder =
        @"C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19";

    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveEngineeringAssembly;
        return Run(args);
    }

    private static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        TextWriter originalOutput = Console.Out;
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TiaOpennessCheck.log");
        StreamWriter logWriter = new StreamWriter(logPath, false, new UTF8Encoding(false));
        logWriter.AutoFlush = true;
        Console.SetOut(new TeeTextWriter(originalOutput, logWriter));

        int exitCode;
        try
        {
            Console.WriteLine("執行帳號：" + Environment.UserDomainName + "\\" + Environment.UserName);
            Console.WriteLine("測試時間：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            if (args.Any(arg => string.Equals(arg, "--annotate-practice", StringComparison.OrdinalIgnoreCase)))
            {
                PracticeAnnotator.Run(AppDomain.CurrentDomain.BaseDirectory);
                exitCode = 0;
            }
            else
            {
            string buildTarget = FindArgValue(args, "--build-test-project:");
            string ladSpecPath = FindArgValue(args, "--write-lad:");
            if (ladSpecPath != null)
            {
                string generated = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(ladSpecPath)),
                    Path.GetFileNameWithoutExtension(ladSpecPath) + ".generated.xml");

                File.WriteAllText(generated, LadWriter.Generate(ladSpecPath), new UTF8Encoding(false));
                Console.WriteLine("已產生 LAD XML：" + generated);
                exitCode = 0;
            }
            else if (args.Any(arg => string.Equals(arg, "--list-projects", StringComparison.OrdinalIgnoreCase)))
            {
                exitCode = ProjectSelector.ListOpenProjects();
            }
            else if (FindArgValue(args, "--headless-list:") != null)
            {
                exitCode = PlcCopier.ListHeadless(FindArgValue(args, "--headless-list:"));
            }
            else if (FindArgValue(args, "--headless-export:") != null)
            {
                exitCode = PlcCopier.ExportHeadless(
                    FindArgValue(args, "--headless-export:"),
                    FindArgValue(args, "--source-plc:"),
                    FindArgValue(args, "--match:") ?? "PreTwist",
                    FindArgValue(args, "--to:"));
            }
            else if (FindArgValue(args, "--headless-dump-di:") != null)
            {
                exitCode = PlcCopier.DumpDiHeadless(
                    FindArgValue(args, "--headless-dump-di:"),
                    FindArgValue(args, "--source-plc:"));
            }
            else if (buildTarget != null)
            {
                exitCode = ProjectBuilder.CreateEmptyProject(
                    buildTarget,
                    FindArgValue(args, "--name:") ?? "TEST_PROJECT");
            }
            else
            {
                bool createInputTag = args.Any(arg =>
                    string.Equals(arg, "--add-input-55-5", StringComparison.OrdinalIgnoreCase));
                bool createGogoInputTag = args.Any(arg =>
                    string.Equals(arg, "--add-input-60-1", StringComparison.OrdinalIgnoreCase));
                bool createGogoOutputTag = args.Any(arg =>
                    string.Equals(arg, "--add-output-100-0", StringComparison.OrdinalIgnoreCase));
                bool createDb123 = args.Any(arg =>
                    string.Equals(arg, "--create-db123", StringComparison.OrdinalIgnoreCase));
                bool createKtp1200Home = args.Any(arg =>
                    string.Equals(arg, "--create-ktp1200-home", StringComparison.OrdinalIgnoreCase));
                bool bindHmiDb123 = args.Any(arg =>
                    string.Equals(arg, "--bind-hmi-db123", StringComparison.OrdinalIgnoreCase));
                exitCode = DataBlockCreator.Run(
                    createInputTag,
                    createGogoInputTag,
                    createGogoOutputTag,
                    createDb123,
                    createKtp1200Home,
                    bindHmiDb123,
                    args);
            }
            }
        }
        catch (EngineeringSecurityException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n權限測試失敗：TIA Portal 拒絕 Openness 連線。");
            Console.ResetColor();
            Console.WriteLine(ex.Message);
            Console.WriteLine("請確認目前帳號位於 Siemens TIA Openness 群組，並已登出後重新登入。");
            exitCode = 3;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n測試失敗：" + ex.GetType().FullName);
            Console.ResetColor();
            Console.WriteLine(ex.Message);
            Console.WriteLine(ex.StackTrace);
            exitCode = 1;
        }

        if (!Console.IsInputRedirected)
        {
            Console.WriteLine("\n按任意鍵關閉視窗...");
            Console.ReadKey(true);
        }

        Console.SetOut(originalOutput);
        logWriter.Dispose();
        return exitCode;
    }

    private static string FindArgValue(string[] args, string prefix)
    {
        string match = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return match == null ? null : match.Substring(prefix.Length).Trim('"');
    }

    private static Assembly ResolveEngineeringAssembly(object sender, ResolveEventArgs args)
    {
        AssemblyName requestedAssembly = new AssemblyName(args.Name);

        if (!string.Equals(requestedAssembly.Name, "Siemens.Engineering", StringComparison.Ordinal)
            && !requestedAssembly.Name.StartsWith("Siemens.Engineering.", StringComparison.Ordinal))
        {
            return null;
        }

        string assemblyPath = Path.Combine(OpennessFolder, requestedAssembly.Name + ".dll");
        if (!File.Exists(assemblyPath))
        {
            return null;
        }

        return Assembly.LoadFrom(assemblyPath);
    }
}

// Rebuilds the hardware of a reference project one stage at a time, driven by
// the hardware.xml produced by --dump-hardware.  Stages are separate so each
// one can be checked in TIA before the next runs.
internal static class HardwareBuilder
{
    public static void Build(Project project, string planPath, string stage, string hmiType)
    {
        if (!File.Exists(planPath))
        {
            throw new FileNotFoundException("找不到硬體清單：" + planPath);
        }

        XDocument plan = XDocument.Load(planPath);
        Console.WriteLine("硬體清單：" + planPath);
        Console.WriteLine("目標專案：" + project.Name);
        Console.WriteLine("階段：" + stage);
        Console.WriteLine(new string('-', 46));

        switch (stage.ToLowerInvariant())
        {
            case "groups":
                BuildGroups(project, plan);
                break;
            case "plc":
                BuildStations(project, plan, false);
                break;
            case "gsd":
                BuildStations(project, plan, true);
                break;
            case "modules":
                PlugModules(project, plan);
                break;
            case "hmi":
                BuildHmi(project, plan, hmiType);
                break;
            case "network":
                BuildNetwork(project, plan);
                break;
            case "profinet":
                BuildProfinet(project, plan);
                break;
            default:
                throw new InvalidOperationException(
                    "未知階段：" + stage + "（可用：groups、plc、gsd、modules、hmi、network、profinet）");
        }

        project.Save();
        Console.WriteLine("專案已存檔。");
    }

    private static void BuildGroups(Project project, XDocument plan)
    {
        List<string> paths = plan.Root.Elements("Device")
            .Select(d => (string)d.Attribute("Group"))
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string path in paths)
        {
            EnsureGroup(project, path);
        }

        Console.WriteLine("群組完成，共 " + paths.Count + " 條路徑。");
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

    private static void BuildStations(Project project, XDocument plan, bool gsdOnly)
    {
        int created = 0;
        int skipped = 0;
        int failed = 0;

        foreach (XElement device in plan.Root.Elements("Device"))
        {
            string deviceName = (string)device.Attribute("Name");
            XElement head = FindHeadItem(device);

            if (head == null)
            {
                Console.WriteLine("  略過 " + deviceName + "：找不到可建立的主要模組型號。");
                skipped++;
                continue;
            }

            string headType = (string)head.Attribute("TypeIdentifier");
            bool isGsd = headType.StartsWith("GSD:", StringComparison.OrdinalIgnoreCase);
            if (isGsd != gsdOnly)
            {
                continue;
            }

            if (FindExistingDevice(project, deviceName) != null)
            {
                Console.WriteLine("  已存在，略過：" + deviceName);
                skipped++;
                continue;
            }

            string headName = (string)head.Attribute("Name");
            string groupPath = (string)device.Attribute("Group");

            try
            {
                DeviceComposition target = string.IsNullOrEmpty(groupPath)
                    ? project.Devices
                    : EnsureGroup(project, groupPath).Devices;

                Device made = target.CreateWithItem(headType, headName, deviceName);
                Console.WriteLine("  建立 " + made.Name + "（" + headName + " / " + headType + "）");
                created++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  失敗 " + deviceName + "：" + Describe(ex));
                failed++;
            }
        }

        Console.WriteLine("完成：新增 " + created + "、略過 " + skipped + "、失敗 " + failed);
    }

    private static void BuildNetwork(Project project, XDocument plan)
    {
        int addressed = 0;
        int connected = 0;
        int skipped = 0;

        foreach (XElement planDevice in plan.Root.Elements("Device"))
        {
            string deviceName = (string)planDevice.Attribute("Name");
            Device device = FindExistingDevice(project, deviceName);
            if (device == null)
            {
                continue;
            }

            List<XElement> plannedNodes = planDevice.Descendants("Node").ToList();
            List<NetworkInterface> interfaces = AllItems(device)
                .Select(item => item.GetService<NetworkInterface>())
                .Where(net => net != null)
                .ToList();

            List<Node> nodes = interfaces.SelectMany(net => net.Nodes.Cast<Node>()).ToList();

            if (nodes.Count != plannedNodes.Count)
            {
                Console.WriteLine(deviceName + "：節點數不符（清單 " + plannedNodes.Count +
                    "、實際 " + nodes.Count + "），略過。");
                skipped++;
                continue;
            }

            Console.WriteLine(deviceName + "：");

            for (int index = 0; index < nodes.Count; index++)
            {
                Node node = nodes[index];
                XElement planned = plannedNodes[index];
                string address = (string)planned.Attribute("Address");
                string subnetName = (string)planned.Attribute("Subnet");

                if (!string.IsNullOrEmpty(address) && address.Contains("."))
                {
                    try
                    {
                        node.SetAttribute("Address", address);
                        Console.WriteLine("    " + node.Name + " -> " + address);
                        addressed++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("    設定位址失敗 " + node.Name + "：" + Describe(ex));
                    }
                }

                if (string.IsNullOrEmpty(subnetName) || node.ConnectedSubnet != null)
                {
                    continue;
                }

                try
                {
                    Subnet subnet = project.Subnets.FirstOrDefault(s =>
                        string.Equals(s.Name, subnetName, StringComparison.OrdinalIgnoreCase));

                    if (subnet == null)
                    {
                        subnet = node.CreateAndConnectToSubnet(subnetName);
                        Console.WriteLine("    建立子網路 " + subnet.Name + " 並接上 " + node.Name);
                    }
                    else
                    {
                        node.ConnectToSubnet(subnet);
                        Console.WriteLine("    " + node.Name + " 接上 " + subnet.Name);
                    }

                    connected++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("    接子網路失敗 " + node.Name + "：" + Describe(ex));
                }
            }
        }

        Console.WriteLine("完成：設位址 " + addressed + "、接子網路 " + connected + "、略過 " + skipped);
    }

    // Assigning IO devices to their controller is what draws the PROFINET bus
    // and the "assigned to" labels in the network view.
    private static void BuildProfinet(Project project, XDocument plan)
    {
        int assigned = 0;
        int already = 0;
        int failed = 0;

        foreach (XElement planDevice in plan.Root.Elements("Device"))
        {
            XElement connectorPlan = planDevice.Descendants("IoConnector").FirstOrDefault();
            if (connectorPlan == null)
            {
                continue;
            }

            string deviceName = (string)planDevice.Attribute("Name");
            string controllerName = (string)connectorPlan.Attribute("Controller");

            Device device = FindExistingDevice(project, deviceName);
            Device controllerDevice = FindExistingDevice(project, controllerName);
            if (device == null || controllerDevice == null)
            {
                continue;
            }

            IoController controller = AllItems(controllerDevice)
                .Select(item => item.GetService<NetworkInterface>())
                .Where(net => net != null)
                .SelectMany(net => net.IoControllers.Cast<IoController>())
                .FirstOrDefault();

            List<NetworkInterface> interfaces = AllItems(device)
                .Select(item => item.GetService<NetworkInterface>())
                .Where(net => net != null)
                .ToList();

            IoConnector connector = interfaces
                .SelectMany(net => net.IoConnectors.Cast<IoConnector>())
                .FirstOrDefault();

            // An S7-1200 only exposes an IoConnector once its interface is put
            // into I-device mode, so enable that before giving up.
            if (connector == null)
            {
                foreach (NetworkInterface net in interfaces)
                {
                    if (!EnableIoDeviceMode(net, deviceName))
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
                Console.WriteLine("  " + deviceName + "：找不到 IO 控制器或連接器，略過。");
                failed++;
                continue;
            }

            if (connector.ConnectedToIoSystem != null)
            {
                already++;
                continue;
            }

            try
            {
                IoSystem system = controller.IoSystem
                    ?? controller.CreateIoSystem((string)connectorPlan.Attribute("IoSystem"));

                connector.ConnectToIoSystem(system);
                Console.WriteLine("  " + deviceName + " -> " + controllerName + "（" + system.Name + "）");
                assigned++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  " + deviceName + " 指派失敗：" + Describe(ex));
                failed++;
            }
        }

        Console.WriteLine("完成：指派 " + assigned + "、已指派 " + already + "、失敗 " + failed);
    }

    // InterfaceOperatingMode is a flags enum whose type is not referenced at
    // compile time, so parse the new value against the type already in place.
    private static bool EnableIoDeviceMode(NetworkInterface net, string deviceName)
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
            Console.WriteLine("  " + deviceName + "：切換 I-device 模式失敗：" + Describe(ex));
            return false;
        }
    }

    // Openness never reports an order number for HMI panels, so the panel type
    // cannot be read back from the reference project and has to be supplied.
    private static void BuildHmi(Project project, XDocument plan, string hmiType)
    {
        List<XElement> panels = plan.Root.Elements("Device")
            .Where(device => FindHeadItem(device) == null)
            .ToList();

        if (panels.Count == 0)
        {
            Console.WriteLine("清單裡沒有需要另外建立的 HMI。");
            return;
        }

        if (string.IsNullOrEmpty(hmiType))
        {
            Console.WriteLine("需要 --hmi-type: 參數，因為 Openness 讀不到面板的訂貨號。");
            Console.WriteLine("待建立的 HMI：" +
                string.Join("、", panels.Select(p => (string)p.Attribute("Name")).ToArray()));
            return;
        }

        foreach (XElement panel in panels)
        {
            string deviceName = (string)panel.Attribute("Name");
            if (FindExistingDevice(project, deviceName) != null)
            {
                Console.WriteLine("  已存在，略過：" + deviceName);
                continue;
            }

            XElement firstItem = panel.Elements("Item").FirstOrDefault();
            string itemName = firstItem == null ? deviceName : (string)firstItem.Attribute("Name");
            string groupPath = (string)panel.Attribute("Group");

            DeviceComposition target = string.IsNullOrEmpty(groupPath)
                ? project.Devices
                : EnsureGroup(project, groupPath).Devices;

            Device made = target.CreateWithItem(hmiType, itemName, deviceName);
            Console.WriteLine("  建立 " + made.Name + "（" + itemName + " / " + hmiType + "）");
        }
    }

    private static void PlugModules(Project project, XDocument plan)
    {
        int plugged = 0;
        int already = 0;
        int failed = 0;

        foreach (XElement planDevice in plan.Root.Elements("Device"))
        {
            string deviceName = (string)planDevice.Attribute("Name");
            Device device = FindExistingDevice(project, deviceName);
            if (device == null)
            {
                continue;
            }

            XElement head = FindHeadItem(planDevice);
            Console.WriteLine(deviceName + "：");

            foreach (XElement item in PluggableItems(planDevice, head))
            {
                string typeId = (string)item.Attribute("TypeIdentifier");
                string name = (string)item.Attribute("Name");
                int position = (int?)item.Attribute("PositionNumber") ?? 0;
                string containerType = (string)item.Attribute("ContainerType");

                HardwareObject target = ResolveContainer(device, containerType);
                if (target == null)
                {
                    Console.WriteLine("    找不到容器（" + containerType + "）：" + name);
                    failed++;
                    continue;
                }

                // A plugged module is reachable from the device even though its
                // Container points at the rack, so check by name across the
                // whole station rather than on the container's own item list.
                if (AllItems(device).Any(existing =>
                    string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    already++;
                    continue;
                }

                try
                {
                    if (!target.CanPlugNew(typeId, name, position))
                    {
                        Console.WriteLine("    " + target.Name + " slot " + position +
                            " 插不進去：" + name + " / " + typeId);
                        failed++;
                        continue;
                    }

                    target.PlugNew(typeId, name, position);
                    Console.WriteLine("    " + target.Name + " slot " + position + " <- " + name);
                    plugged++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("    失敗 slot " + position + "（" + name + "）：" + Describe(ex));
                    failed++;
                }
            }
        }

        Console.WriteLine("完成：插入 " + plugged + "、已存在 " + already + "、失敗 " + failed);
    }

    // Container names are localised, so match the recorded container by type:
    // this resolves rack, CPU and GSD rack with one rule.
    private static HardwareObject ResolveContainer(Device device, string containerType)
    {
        if (string.IsNullOrEmpty(containerType))
        {
            return null;
        }

        if (containerType.StartsWith("System:Device", StringComparison.OrdinalIgnoreCase))
        {
            return device;
        }

        return AllItems(device).FirstOrDefault(item =>
            string.Equals(item.TypeIdentifier, containerType, StringComparison.OrdinalIgnoreCase));
    }

    internal static IEnumerable<DeviceItem> AllItems(Device device)
    {
        foreach (DeviceItem item in device.DeviceItems)
        {
            yield return item;

            foreach (DeviceItem child in Descend(item))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<DeviceItem> Descend(DeviceItem item)
    {
        foreach (DeviceItem child in item.DeviceItems)
        {
            yield return child;

            foreach (DeviceItem grandChild in Descend(child))
            {
                yield return grandChild;
            }
        }
    }

    // Racks and the head module come with the station; everything else on the
    // top level of the plan is a card that has to be plugged explicitly.
    private static IEnumerable<XElement> PluggableItems(XElement planDevice, XElement head)
    {
        return planDevice.Elements("Item")
            .Where(item => item != head)
            .Where(item =>
            {
                string typeId = (string)item.Attribute("TypeIdentifier") ?? string.Empty;
                if (typeId.Length == 0)
                {
                    return false;
                }

                // No container means the item is built into its parent (for
                // example the ET 200SP bus adapter) and appears on its own.
                if (string.IsNullOrEmpty((string)item.Attribute("ContainerType")))
                {
                    return false;
                }

                if (typeId.StartsWith("System:Rack", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (typeId.IndexOf("/R/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }

                return typeId.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase)
                    || typeId.StartsWith("GSD:", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(item => (int?)item.Attribute("PositionNumber") ?? 0);
    }

    // The station is created from its head module: the CPU for a SIMATIC rack,
    // the DAP for a GSD device.  Racks are implicit and must not be created.
    private static XElement FindHeadItem(XElement device)
    {
        List<XElement> items = device.Elements("Item").ToList();

        XElement dap = items.FirstOrDefault(i =>
            ((string)i.Attribute("TypeIdentifier") ?? string.Empty)
                .IndexOf("/DAP/", StringComparison.OrdinalIgnoreCase) >= 0);
        if (dap != null)
        {
            return dap;
        }

        return items
            .Where(i => ((string)i.Attribute("TypeIdentifier") ?? string.Empty)
                .StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => (int?)i.Attribute("PositionNumber") ?? 0)
            .FirstOrDefault();
    }

    private static Device FindExistingDevice(Project project, string name)
    {
        return EnumerateAll(project).FirstOrDefault(d =>
            string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<Device> EnumerateAll(Project project)
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

    private static string Describe(Exception ex)
    {
        List<string> parts = new List<string>();
        for (Exception current = ex; current != null; current = current.InnerException)
        {
            parts.Add(current.Message);
        }

        return string.Join(" << ", parts.ToArray());
    }
}

internal static class ProjectBuilder
{
    public static int CreateEmptyProject(string targetDirectory, string name)
    {
        DirectoryInfo directory = new DirectoryInfo(targetDirectory);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException("找不到目標資料夾：" + directory.FullName);
        }

        string projectFolder = Path.Combine(directory.FullName, name);
        if (Directory.Exists(projectFolder))
        {
            throw new InvalidOperationException("專案資料夾已存在，請先刪除或改名：" + projectFolder);
        }

        Console.WriteLine("正在啟動新的 TIA Portal V21 實例（含使用者介面）...");
        TiaPortal portal = new TiaPortal(TiaPortalMode.WithUserInterface);

        Project project = portal.Projects.Create(directory, name);
        project.Save();

        Console.WriteLine("已建立專案：" + project.Path.FullName);
        Console.WriteLine("裝置數量：" + project.Devices.Count + "（新專案應為 0）");
        Console.WriteLine("TIA Portal 保持開啟，後續指令用 --project: 接上這個專案。");

        // Deliberately not disposing the portal: disposing would close TIA and
        // the follow-up steps attach to the running instance by project path.
        return 0;
    }
}

internal static class ProjectSelector
{
    public static int ListOpenProjects()
    {
        Console.WriteLine("TIA Portal V19 已開啟專案清單");
        Console.WriteLine(new string('=', 38));

        IList<TiaPortalProcess> processes = TiaPortal.GetProcesses();
        Console.WriteLine("找到的 TIA Portal 執行個體：" + processes.Count);

        foreach (TiaPortalProcess process in processes)
        {
            string projectPath = process.ProjectPath == null ? "<尚未開啟或讀不到>" : process.ProjectPath.FullName;
            Console.WriteLine("PID " + process.Id + " / " + projectPath);
        }

        return 0;
    }
}

internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter first;
    private readonly TextWriter second;

    public TeeTextWriter(TextWriter first, TextWriter second)
    {
        this.first = first;
        this.second = second;
    }

    public override Encoding Encoding
    {
        get { return first.Encoding; }
    }

    public override void Write(char value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void Write(string value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void WriteLine(string value)
    {
        first.WriteLine(value);
        second.WriteLine(value);
    }

    public override void Flush()
    {
        first.Flush();
        second.Flush();
    }
}

internal static class DataBlockCreator
{
    private const string DataBlockName = "test_db";
    private const string MemberDataType = "Int";
    private const int MemberCount = 10;
    private const string TargetProjectName = "Test V19 Standard";
    private const string TargetProjectPath = @"C:\Users\David\Desktop\Test V19 Standard\Test V19 Standard.ap19";

    // Overridden by --project:<完整 .ap19 路徑>。預設寫死是刻意的：避免在錯的專案上寫入。
    private static string _targetProjectName = TargetProjectName;
    private static string _targetProjectPath = TargetProjectPath;
    private const string InputTagTableName = "OpennessTags";
    private const string InputTagName = "Input_55_5";
    private const string InputTagAddress = "I55.5";
    private const string GogoTagTableName = "GOGO";
    private const string GogoTagName = "Input_60_1";
    private const string GogoTagAddress = "I60.1";
    private const string GogoOutputTagName = "Output_100_0";
    private const string GogoOutputTagAddress = "Q100.0";
    private const string RealDataBlockName = "DB123";
    private const int RealMemberCount = 10;

    public static int Run(
        bool createInputTag,
        bool createGogoInputTag,
        bool createGogoOutputTag,
        bool createDb123,
        bool createKtp1200Home,
        bool bindHmiDb123,
        string[] args)
    {
        Console.WriteLine("TIA Portal V19 Openness DB 建立工具");
        Console.WriteLine(new string('=', 38));

        ApplyProjectOverride(args);

        Console.WriteLine("正在尋找目標專案：" + _targetProjectName + " / " + _targetProjectPath);
        TiaPortalProcess targetProcess = EnsureTargetPortalProcess();
        Console.WriteLine("只連線這個 TIA 實例：PID " + targetProcess.Id);
        TiaPortal attachedPortal;
        try
        {
            attachedPortal = targetProcess.Attach();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("無法連到 " + _targetProjectName + " 這個 TIA 實例：" + ex.GetType().Name);
            Console.ResetColor();
            Console.WriteLine(ex.Message);
            Console.WriteLine("若 TIA 跳出 Openness 防火牆，請按 Yes to all。");
            throw;
        }

        using (TiaPortal portal = attachedPortal)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("連線成功：已選取目標專案。");
            Console.ResetColor();

            Project project = portal.Projects.First(IsTargetProject);
            Console.WriteLine("目標專案：" + project.Name + " / " + project.Path.FullName);

            // HMI-only 指令不要先掃全專案 PLC（26037 會卡很久／像當掉）。
            if (LadBlockTools.IsHmiOnlyCommand(args))
            {
                Console.WriteLine("HMI-only 指令：略過開場 PLC 掃描。");
                int? hmiExit = LadBlockTools.HandleProjectScope(
                    args,
                    portal,
                    project,
                    new List<PlcSoftware>());
                if (hmiExit.HasValue)
                {
                    return hmiExit.Value;
                }
            }

            Console.WriteLine("開始掃描 PLC 軟體…");
            List<PlcSoftware> plcSoftwares = FindPlcSoftwares(project);
            Console.WriteLine("頂層 PLC：" + plcSoftwares.Count);
            if (plcSoftwares.Count == 0)
            {
                // Devices filed under a device group are invisible to the
                // top-level scan, which is how every rebuilt project looks.
                Console.WriteLine("改掃裝置群組內 PLC…");
                plcSoftwares = LadBlockTools.FindAllPlcSoftwares(project);
            }

            Console.WriteLine("找到的 PLC 軟體數量：" + plcSoftwares.Count);

            // 唯讀的盤點／匯出指令不需要「剛好一個 PLC」，先處理。
            int? projectExitCode = LadBlockTools.HandleProjectScope(args, portal, project, plcSoftwares);
            if (projectExitCode.HasValue)
            {
                return projectExitCode.Value;
            }

            string wantedPlc = LadBlockTools.GetValue(args, "--plc:");
            if (wantedPlc != null)
            {
                plcSoftwares = plcSoftwares
                    .Where(candidate => string.Equals(candidate.Name, wantedPlc, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (plcSoftwares.Count == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("找不到指定的 PLC：" + wantedPlc);
                    Console.ResetColor();
                    return 5;
                }

                Console.WriteLine("指定 PLC：" + plcSoftwares[0].Name);
            }

            if (plcSoftwares.Count != 1)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("未找到唯一的 PLC，請用 --plc:<名稱> 指定。目前有：");
                foreach (PlcSoftware candidate in plcSoftwares)
                {
                    Console.WriteLine("  " + candidate.Name);
                }

                Console.ResetColor();
                return 5;
            }

            PlcSoftware plc = plcSoftwares[0];

            int? ladExitCode = LadBlockTools.Handle(args, project, plc);
            if (ladExitCode.HasValue)
            {
                return ladExitCode.Value;
            }

            if (createInputTag)
            {
                CreateInputTag(plc, project);
                return 0;
            }

            if (createGogoInputTag)
            {
                CreateGogoInputTag(plc, project);
                return 0;
            }

            if (createGogoOutputTag)
            {
                CreateOrAddGogoTag(plc, project, GogoOutputTagName, GogoOutputTagAddress);
                return 0;
            }

            if (createDb123)
            {
                CreateRealDataBlock(plc, project);
                return 0;
            }

            if (createKtp1200Home)
            {
                return HmiKtp1200HomeCreator.Run(portal, project, plc);
            }

            if (bindHmiDb123)
            {
                return HmiKtp1200HomeCreator.BindExisting(portal, project, plc);
            }

            string sourceFolder = AppDomain.CurrentDomain.BaseDirectory;
            string dataBlockSourcePath = Path.Combine(sourceFolder, "DB_ModbusData_v3.scl");
            string connectionBlockSourcePath = Path.Combine(sourceFolder, "DB_ModbusConnection_v3.scl");
            string modbusFunctionBlockSourcePath = Path.Combine(sourceFolder, "FB_ModbusRead40001_v3.scl");
            string obSourcePath = Path.Combine(sourceFolder, "Main_ModbusRead40001_v3.scl");

            if (plc.BlockGroup.Blocks.Find("FC_CompareOutput") == null)
            {
                throw new InvalidOperationException("找不到 FC_CompareOutput，請先建立比較功能後再匯入 Modbus 程式。");
            }

            DeletePreviousModbusObjects(plc);

            File.WriteAllText(dataBlockSourcePath, CreateModbusDataBlockSource(), new UTF8Encoding(false));
            File.WriteAllText(connectionBlockSourcePath, CreateModbusConnectionBlockSource(), new UTF8Encoding(false));
            File.WriteAllText(modbusFunctionBlockSourcePath, CreateModbusFunctionBlockSource(), new UTF8Encoding(false));
            File.WriteAllText(obSourcePath, CreateOb1Source(), new UTF8Encoding(false));

            Console.WriteLine("正在生成 DB_ModbusData...");
            plc.ExternalSourceGroup.ExternalSources
                .CreateFromFile(Path.GetFileName(dataBlockSourcePath), dataBlockSourcePath)
                .GenerateBlocksFromSource(GenerateBlockOption.None);

            Console.WriteLine("正在生成 DB_ModbusConnection...");
            plc.ExternalSourceGroup.ExternalSources
                .CreateFromFile(Path.GetFileName(connectionBlockSourcePath), connectionBlockSourcePath)
                .GenerateBlocksFromSource(GenerateBlockOption.None);

            Console.WriteLine("正在生成 FB_ModbusRead40001...");
            plc.ExternalSourceGroup.ExternalSources
                .CreateFromFile(Path.GetFileName(modbusFunctionBlockSourcePath), modbusFunctionBlockSourcePath)
                .GenerateBlocksFromSource(GenerateBlockOption.None);

            if (plc.BlockGroup.Blocks.Find("DB_ModbusRead40001") == null)
            {
                Console.WriteLine("正在建立 FB_ModbusRead40001 的 Instance DB...");
                plc.BlockGroup.Blocks.CreateInstanceDB("DB_ModbusRead40001", true, 1, "FB_ModbusRead40001");
            }

            Console.WriteLine("正在更新 OB1 呼叫...");
            plc.ExternalSourceGroup.ExternalSources
                .CreateFromFile(Path.GetFileName(obSourcePath), obSourcePath)
                .GenerateBlocksFromSource(GenerateBlockOption.None);

            project.Save();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("已建立 Modbus TCP 讀取器，並在 OB1 呼叫它；專案已儲存。");
            Console.ResetColor();
        }

        Console.WriteLine("Openness 連線已安全中斷；TIA Portal 不會被關閉。");
        return 0;
    }

    private static void CreateInputTag(PlcSoftware plc, Project project)
    {
        PlcTagTable tagTable = plc.TagTableGroup.TagTables.Find(InputTagTableName);
        if (tagTable == null)
        {
            tagTable = plc.TagTableGroup.TagTables.Create(InputTagTableName);
            Console.WriteLine("已建立 PLC Tag table：" + InputTagTableName);
        }

        PlcTag existingTag = tagTable.Tags.Find(InputTagName);
        if (existingTag != null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Tag 已存在，未修改：" + InputTagName);
            Console.ResetColor();
            return;
        }

        tagTable.Tags.Create(InputTagName, "Bool", InputTagAddress);
        project.Save();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("已建立 Tag：" + InputTagName + " / Bool / " + InputTagAddress);
        Console.WriteLine("專案已儲存。");
        Console.ResetColor();
    }

    private static void CreateGogoInputTag(PlcSoftware plc, Project project)
    {
        PlcTagTable tagTable = plc.TagTableGroup.TagTables.Find(GogoTagTableName);
        if (tagTable == null)
        {
            tagTable = plc.TagTableGroup.TagTables.Create(GogoTagTableName);
            Console.WriteLine("已建立 PLC Tag table：" + GogoTagTableName);
        }

        PlcTag existingTag = tagTable.Tags.Find(GogoTagName);
        if (existingTag != null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Tag 已存在，未修改：" + GogoTagName);
            Console.ResetColor();
            return;
        }

        tagTable.Tags.Create(GogoTagName, "Bool", GogoTagAddress);
        project.Save();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("已建立 Tag：" + GogoTagName + " / Bool / " + GogoTagAddress);
        Console.WriteLine("專案已儲存。");
        Console.ResetColor();
    }

    private static void CreateOrAddGogoTag(PlcSoftware plc, Project project, string tagName, string tagAddress)
    {
        PlcTagTable tagTable = plc.TagTableGroup.TagTables.Find(GogoTagTableName);
        if (tagTable == null)
        {
            tagTable = plc.TagTableGroup.TagTables.Create(GogoTagTableName);
            Console.WriteLine("已建立 PLC Tag table：" + GogoTagTableName);
        }

        PlcTag existingTag = tagTable.Tags.Find(tagName);
        if (existingTag != null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Tag 已存在，未修改：" + tagName);
            Console.ResetColor();
            return;
        }

        tagTable.Tags.Create(tagName, "Bool", tagAddress);
        project.Save();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("已建立 Tag：" + tagName + " / Bool / " + tagAddress);
        Console.WriteLine("專案已儲存。");
        Console.ResetColor();
    }

    private static void CreateRealDataBlock(PlcSoftware plc, Project project)
    {
        string sourceName = RealDataBlockName + ".scl";
        string sourcePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, sourceName);
        File.WriteAllText(sourcePath, CreateRealDataBlockSource(), new UTF8Encoding(false));

        PlcExternalSource existingSource = plc.ExternalSourceGroup.ExternalSources.Find(sourceName);
        if (existingSource != null)
        {
            existingSource.Delete();
        }

        Console.WriteLine("正在生成全域 DB：" + RealDataBlockName);
        plc.ExternalSourceGroup.ExternalSources
            .CreateFromFile(sourceName, sourcePath)
            .GenerateBlocksFromSource(GenerateBlockOption.None);

        DataBlock dataBlock = plc.BlockGroup.Blocks.Find(RealDataBlockName) as DataBlock;
        if (dataBlock == null)
        {
            throw new InvalidOperationException("匯入完成後仍找不到全域 DB：" + RealDataBlockName);
        }

        project.Save();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("已建立全域 DB：" + RealDataBlockName + "，內含 Real1 到 Real10。");
        Console.WriteLine("專案已儲存。");
        Console.ResetColor();
    }

    private static string CreateRealDataBlockSource()
    {
        StringBuilder members = new StringBuilder();
        for (int index = 1; index <= RealMemberCount; index++)
        {
            members.AppendLine("    Real" + index + " : Real;");
        }

        return
            "DATA_BLOCK \"" + RealDataBlockName + "\"" + Environment.NewLine +
            "{ S7_Optimized_Access := 'TRUE' }" + Environment.NewLine +
            "VERSION : 0.1" + Environment.NewLine +
            "VAR" + Environment.NewLine +
            members +
            "END_VAR" + Environment.NewLine +
            "BEGIN" + Environment.NewLine +
            "END_DATA_BLOCK" + Environment.NewLine;
    }

    private static List<PlcSoftware> FindPlcSoftwares(Project project)
    {
        List<PlcSoftware> plcSoftwares = new List<PlcSoftware>();

        foreach (Device device in project.Devices)
        {
            foreach (DeviceItem item in device.DeviceItems)
            {
                AddPlcSoftware(item, plcSoftwares);
            }
        }

        return plcSoftwares;
    }

    private static void DeletePreviousModbusObjects(PlcSoftware plc)
    {
        string[] blockNames =
        {
            "DB_ModbusRead40001",
            "FB_ModbusRead40001",
            "DB_ModbusData",
            "DB_ModbusConnection"
        };

        foreach (string blockName in blockNames)
        {
            PlcBlock block = plc.BlockGroup.Blocks.Find(blockName);
            if (block != null)
            {
                block.Delete();
                Console.WriteLine("已刪除舊 Modbus block：" + blockName);
            }
        }

        string[] sourceNames =
        {
            "DB_ModbusData.scl",
            "FB_ModbusRead40001.scl",
            "Main_ModbusRead40001.scl",
            "DB_ModbusData_v2.scl",
            "FB_ModbusRead40001_v2.scl",
            "Main_ModbusRead40001_v2.scl",
            "DB_ModbusData_v3.scl",
            "DB_ModbusConnection_v3.scl",
            "FB_ModbusRead40001_v3.scl",
            "Main_ModbusRead40001_v3.scl"
        };

        foreach (string sourceName in sourceNames)
        {
            PlcExternalSource source = plc.ExternalSourceGroup.ExternalSources.Find(sourceName);
            if (source != null)
            {
                source.Delete();
                Console.WriteLine("已刪除舊 Modbus source：" + sourceName);
            }
        }
    }

    private const string PortalExe =
        @"C:\Program Files\Siemens\Automation\Portal V19\Bin\Siemens.Automation.Portal.exe";

    private static TiaPortalProcess FindTargetPortalProcess()
    {
        foreach (TiaPortalProcess process in TiaPortal.GetProcesses())
        {
            if (IsTargetProjectPath(process.ProjectPath))
            {
                return process;
            }
        }

        return null;
    }

    private static TiaPortalProcess EnsureTargetPortalProcess()
    {
        TiaPortalProcess found = FindTargetPortalProcess();
        if (found != null)
        {
            return found;
        }

        if (string.IsNullOrEmpty(_targetProjectPath) || !File.Exists(_targetProjectPath))
        {
            throw new FileNotFoundException("找不到專案：" + _targetProjectPath);
        }

        if (!File.Exists(PortalExe))
        {
            throw new FileNotFoundException("找不到 TIA Portal：" + PortalExe);
        }

        Console.WriteLine("TIA 沒開這個專案，正在啟動：");
        Console.WriteLine("  " + PortalExe);
        Console.WriteLine("  " + _targetProjectPath);
        ProcessStartInfo info = new ProcessStartInfo();
        info.FileName = PortalExe;
        info.Arguments = "\"" + _targetProjectPath + "\"";
        info.UseShellExecute = true;
        Process.Start(info);

        DateTime deadline = DateTime.UtcNow.AddMinutes(10);
        int waited = 0;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5000);
            waited += 5;
            found = FindTargetPortalProcess();
            if (found != null)
            {
                Console.WriteLine("專案已開啟。PID " + found.Id);
                return found;
            }

            IList<TiaPortalProcess> all = TiaPortal.GetProcesses();
            Console.WriteLine("等待 TIA 載入專案… " + waited + "s／實例 " + all.Count);
            foreach (TiaPortalProcess process in all)
            {
                string path = process.ProjectPath == null ? "<尚未開啟>" : process.ProjectPath.FullName;
                Console.WriteLine("  PID " + process.Id + " / " + path);
            }
        }

        throw new TimeoutException("等了 10 分鐘還沒看到專案：" + _targetProjectPath);
    }

    private static bool IsTargetProject(Project project)
    {
        return string.Equals(project.Name, _targetProjectName, StringComparison.Ordinal) &&
            IsTargetProjectPath(project.Path);
    }

    private static bool IsTargetProjectPath(FileInfo projectPath)
    {
        return projectPath != null &&
            string.Equals(projectPath.FullName, _targetProjectPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyProjectOverride(string[] args)
    {
        string requested = LadBlockTools.GetValue(args, "--project:");
        if (requested == null)
        {
            return;
        }

        requested = requested.Trim('"');
        if (!requested.EndsWith(".ap19", StringComparison.OrdinalIgnoreCase) &&
            !requested.EndsWith(".ap21", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("--project: 要給完整的 .ap19 檔案路徑，不是資料夾：" + requested);
        }

        _targetProjectPath = requested;
        _targetProjectName = Path.GetFileNameWithoutExtension(requested);

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("已改用指定專案：" + _targetProjectName);
        Console.WriteLine("  " + _targetProjectPath);
        Console.ResetColor();
    }

    private static void PrintCreationInfos(string objectName, IEngineeringComposition composition)
    {
        Console.WriteLine(objectName + " 可用的建立類型與參數：");

        foreach (EngineeringCreationInfo creationInfo in composition.GetCreationInfos())
        {
            Console.WriteLine("  類型：" + V19Api.TypeName(creationInfo));

            foreach (EngineeringCreationParameterInfo parameterInfo in creationInfo.ParameterInfos)
            {
                Console.WriteLine("    參數：" + parameterInfo.Name);

                foreach (PropertyInfo property in parameterInfo.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    object value;

                    try
                    {
                        value = property.GetValue(parameterInfo, null);
                    }
                    catch (Exception ex)
                    {
                        value = "<讀取失敗：" + ex.GetType().Name + ">";
                    }

                    Console.WriteLine("      " + property.Name + "：" + FormatValue(value));
                }
            }
        }
    }

    private static string FormatValue(object value)
    {
        if (value == null)
        {
            return "<null>";
        }

        if (value is string)
        {
            return (string)value;
        }

        System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
        if (enumerable == null)
        {
            return value.ToString();
        }

        List<string> items = new List<string>();
        foreach (object item in enumerable)
        {
            items.Add(item == null ? "<null>" : item.ToString());
        }

        return string.Join(", ", items);
    }

    private static string CreateDataBlockXml()
    {
        StringBuilder members = new StringBuilder();
        for (int index = 1; index <= MemberCount; index++)
        {
            members.AppendLine("            <Member Name=\"Tag" + index + "\" Datatype=\"Int\" />");
        }

        return
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine +
            "<Document>" + Environment.NewLine +
            "  <SW.Blocks.GlobalDB ID=\"0\">" + Environment.NewLine +
            "    <AttributeList>" + Environment.NewLine +
            "      <AutoNumber>true</AutoNumber>" + Environment.NewLine +
            "      <HeaderAuthor />" + Environment.NewLine +
            "      <HeaderFamily />" + Environment.NewLine +
            "      <HeaderName />" + Environment.NewLine +
            "      <HeaderVersion>0.1</HeaderVersion>" + Environment.NewLine +
            "      <Interface>" + Environment.NewLine +
            "        <Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v2\">" + Environment.NewLine +
            "          <Section Name=\"Static\">" + Environment.NewLine +
            members +
            "          </Section>" + Environment.NewLine +
            "        </Sections>" + Environment.NewLine +
            "      </Interface>" + Environment.NewLine +
            "      <IsOnlyStoredInLoadMemory>false</IsOnlyStoredInLoadMemory>" + Environment.NewLine +
            "      <IsRetainMemResEnabled>false</IsRetainMemResEnabled>" + Environment.NewLine +
            "      <IsWriteProtectedInAS>false</IsWriteProtectedInAS>" + Environment.NewLine +
            "      <MemoryLayout>Optimized</MemoryLayout>" + Environment.NewLine +
            "      <MemoryReserve>100</MemoryReserve>" + Environment.NewLine +
            "      <Name>" + DataBlockName + "</Name>" + Environment.NewLine +
            "      <Namespace />" + Environment.NewLine +
            "      <Number>1</Number>" + Environment.NewLine +
            "      <ProgrammingLanguage>DB</ProgrammingLanguage>" + Environment.NewLine +
            "    </AttributeList>" + Environment.NewLine +
            "  </SW.Blocks.GlobalDB>" + Environment.NewLine +
            "</Document>";
    }

    private static string CreateModbusDataBlockSource()
    {
        return
            "DATA_BLOCK \"DB_ModbusData\"" + Environment.NewLine +
            "{ S7_Optimized_Access := 'FALSE' }" + Environment.NewLine +
            "VERSION : 0.1" + Environment.NewLine +
            "VAR" + Environment.NewLine +
            "    Reg40001 : Word;" + Environment.NewLine +
            "END_VAR" + Environment.NewLine +
            "BEGIN" + Environment.NewLine +
            "END_DATA_BLOCK" + Environment.NewLine;
    }

    private static string CreateModbusConnectionBlockSource()
    {
        return
            "DATA_BLOCK \"DB_ModbusConnection\"" + Environment.NewLine +
            "{ S7_Optimized_Access := 'FALSE' }" + Environment.NewLine +
            "VERSION : 0.1" + Environment.NewLine +
            "VAR" + Environment.NewLine +
            "    Connection : TCON_IP_v4 := (" + Environment.NewLine +
            "        InterfaceId := 64," + Environment.NewLine +
            "        ID := 1," + Environment.NewLine +
            "        ConnectionType := B#16#0B," + Environment.NewLine +
            "        ActiveEstablished := TRUE," + Environment.NewLine +
            "        RemoteAddress := (ADDR := [192, 168, 40, 1])," + Environment.NewLine +
            "        RemotePort := 502," + Environment.NewLine +
            "        LocalPort := 0);" + Environment.NewLine +
            "END_VAR" + Environment.NewLine +
            "BEGIN" + Environment.NewLine +
            "END_DATA_BLOCK" + Environment.NewLine;
    }

    private static string CreateModbusFunctionBlockSource()
    {
        return
            "FUNCTION_BLOCK \"FB_ModbusRead40001\"" + Environment.NewLine +
            "{ S7_Optimized_Access := 'TRUE' }" + Environment.NewLine +
            "VERSION : 0.1" + Environment.NewLine +
            "VAR_OUTPUT" + Environment.NewLine +
            "    Done : Bool;" + Environment.NewLine +
            "    Busy : Bool;" + Environment.NewLine +
            "    Error : Bool;" + Environment.NewLine +
            "    Status : Word;" + Environment.NewLine +
            "END_VAR" + Environment.NewLine +
            "VAR" + Environment.NewLine +
            "    PollTimer : TON;" + Environment.NewLine +
            "    Client : MB_CLIENT;" + Environment.NewLine +
            "END_VAR" + Environment.NewLine +
            "BEGIN" + Environment.NewLine +
            "    #PollTimer(IN := NOT #PollTimer.Q, PT := T#1s);" + Environment.NewLine +
            "    #Client(" + Environment.NewLine +
            "        REQ := #PollTimer.Q," + Environment.NewLine +
            "        DISCONNECT := FALSE," + Environment.NewLine +
            "        MB_MODE := 0," + Environment.NewLine +
            "        MB_DATA_ADDR := 40001," + Environment.NewLine +
            "        MB_DATA_LEN := 1," + Environment.NewLine +
            "        MB_DATA_PTR := \"DB_ModbusData\".Reg40001," + Environment.NewLine +
            "        CONNECT := \"DB_ModbusConnection\".Connection," + Environment.NewLine +
            "        DONE => #Done," + Environment.NewLine +
            "        BUSY => #Busy," + Environment.NewLine +
            "        ERROR => #Error," + Environment.NewLine +
            "        STATUS => #Status);" + Environment.NewLine +
            "END_FUNCTION_BLOCK" + Environment.NewLine;
    }

    private static string CreateOb1Source()
    {
        return
            "ORGANIZATION_BLOCK \"Main\"" + Environment.NewLine +
            "BEGIN" + Environment.NewLine +
            "    \"FC_CompareOutput\"();" + Environment.NewLine +
            "    \"DB_ModbusRead40001\"();" + Environment.NewLine +
            "END_ORGANIZATION_BLOCK" + Environment.NewLine;
    }

    private static void PrintCreateMethods(string objectName, object composition)
    {
        Console.WriteLine(objectName + " 可用的建立方法：");

        foreach (MethodInfo method in composition.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(candidate => candidate.Name.StartsWith("Create", StringComparison.Ordinal)))
        {
            string parameters = string.Join(", ", method.GetParameters()
                .Select(parameter => parameter.ParameterType.Name + " " + parameter.Name));

            Console.WriteLine("  " + method.ReturnType.Name + " " + method.Name + "(" + parameters + ")");
        }
    }

    private static void AddPlcSoftware(DeviceItem item, ICollection<PlcSoftware> plcSoftwares)
    {
        SoftwareContainer softwareContainer = item.GetService<SoftwareContainer>();
        PlcSoftware plcSoftware = softwareContainer == null ? null : softwareContainer.Software as PlcSoftware;

        if (plcSoftware != null)
        {
            plcSoftwares.Add(plcSoftware);
        }

        foreach (DeviceItem child in item.DeviceItems)
        {
            AddPlcSoftware(child, plcSoftwares);
        }
    }
}

internal static class HmiKtp1200HomeCreator
{
    private const string HmiDeviceName = "KTP1200";
    private const string HomeScreenName = "HOME PAGE";
    private const string DataBlockName = "DB123";
    private const int RealMemberCount = 10;
    private const string HmiAddress = "192.168.40.3";
    private const string SubnetMask = "255.255.255.0";
    private const string PreferredArticle = "6AV2123-2MB03";

    public static int Run(TiaPortal portal, Project project, PlcSoftware plc)
    {
        Console.WriteLine("TIA Portal V21 Openness KTP1200 HOME PAGE 工具");
        Console.WriteLine(new string('=', 38));

        if (plc.BlockGroup.Blocks.Find(DataBlockName) == null)
        {
            throw new InvalidOperationException("找不到全域 DB：" + DataBlockName + "。請先用 --create-db123 建立。");
        }

        Device plcDevice;
        DeviceItem plcCpuItem;
        FindPlcHardware(project, plc, out plcDevice, out plcCpuItem);
        Console.WriteLine("PLC 裝置：" + plcDevice.Name + " / CPU：" + plcCpuItem.Name);

        Device hmiDevice = FindExistingHmi(project);
        if (hmiDevice == null)
        {
            hmiDevice = CreateKtp1200(portal, project);
        }
        else
        {
            Console.WriteLine("已找到 HMI 裝置，重用：" + hmiDevice.Name + " / " + hmiDevice.TypeIdentifier);
        }

        HmiTarget hmi = FindHmiTarget(hmiDevice);
        if (hmi == null)
        {
            throw new InvalidOperationException("KTP1200 已建立，但找不到 HmiTarget 軟體。");
        }

        EthernetEndpoint plcEthernet = FindEthernetEndpoint(plcDevice);
        EthernetEndpoint hmiEthernet = FindEthernetEndpoint(hmiDevice);
        if (plcEthernet == null || hmiEthernet == null)
        {
            throw new InvalidOperationException("找不到 PLC 或 HMI 的 Ethernet 介面。");
        }

        Console.WriteLine("PLC 介面：" + plcEthernet.Item.Name + " / Node=" + plcEthernet.Node.Name);
        Console.WriteLine("HMI 介面：" + hmiEthernet.Item.Name + " / Node=" + hmiEthernet.Node.Name);
        DumpAttributeInfos("PLC Node", plcEthernet.Node);

        ConnectToSameSubnet(plcEthernet.Node, hmiEthernet.Node);
        string plcAddress = GetAttributeString(plcEthernet.Node, "Address") ?? "192.168.0.1";
        string hmiAddress = ChooseHmiAddress(plcAddress);
        string mask = GetAttributeString(plcEthernet.Node, "SubnetMask") ?? SubnetMask;
        TrySetNodeAddress(hmiEthernet.Node, hmiAddress, mask);
        Console.WriteLine("PLC IP " + plcAddress + " / HMI IP " + hmiAddress);
        project.Save();
        Console.WriteLine("已先儲存 KTP1200 與網路設定。");

        string connectionName = EnsureHmiConnection(hmiDevice, hmi, hmiEthernet, plcDevice, plcCpuItem, plcEthernet);
        Console.WriteLine("HMI 連線名稱：" + (connectionName ?? "<稍後由 Tag 自動建立>"));

        EnsureDb123StandardAccess(plc, project);
        ImportDb123Tags(hmi, connectionName);
        if (string.IsNullOrEmpty(connectionName) && hmi.Connections.Any())
        {
            connectionName = hmi.Connections.First().Name;
            Console.WriteLine("Tag 匯入後出現 HMI 連線：" + connectionName);
        }

        ConfigureHomePage(hmi);
        TrySetStartScreen(project, hmiDevice, hmi);

        project.Save();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("已寫入 test openess：KTP1200 Basic PN、HOME PAGE、HMI Tag Real1-Real10。");
        Console.WriteLine("專案已儲存。");
        Console.ResetColor();
        Console.WriteLine("若 HOME PAGE 仍是空的，請把 Real1-Real10 拖到畫面上。");
        Console.ResetColor();
        Console.WriteLine("Openness 連線已安全中斷；TIA Portal 不會被關閉。");
        return 0;
    }

    public static int BindExisting(TiaPortal portal, Project project, PlcSoftware plc)
    {
        Console.WriteLine("TIA Portal V21 綁定 HMI Tag 到 DB123");
        Console.WriteLine(new string('=', 38));

        DataBlock dataBlock = plc.BlockGroup.Blocks.Find(DataBlockName) as DataBlock;
        if (dataBlock == null)
        {
            throw new InvalidOperationException("找不到全域 DB：" + DataBlockName + "。");
        }

        int dbNumber = 1;
        try
        {
            object number = dataBlock.GetAttribute("Number");
            if (number != null)
            {
                dbNumber = Convert.ToInt32(number, CultureInfo.InvariantCulture);
            }
        }
        catch
        {
        }

        Console.WriteLine("DB123 編號=" + dbNumber);

        Device hmiDevice = FindExistingHmi(project);
        if (hmiDevice == null)
        {
            throw new InvalidOperationException("找不到 KTP1200。");
        }

        HmiTarget hmi = FindHmiTarget(hmiDevice);
        if (hmi == null)
        {
            throw new InvalidOperationException("找不到 HmiTarget。");
        }

        if (!hmi.Connections.Any())
        {
            throw new InvalidOperationException("HMI 連線還不存在。請先在 TIA 連線編輯器建立 Connection_1。");
        }

        string connectionName = hmi.Connections.First().Name;
        Console.WriteLine("使用現有 HMI 連線：" + connectionName);
        foreach (HmiSoftConnection connection in hmi.Connections)
        {
            DumpAttributeInfos("HMI 連線 " + connection.Name, connection);
            DumpAllAttributes("HMI 連線 " + connection.Name + " 值", connection);
        }

        TagTable table = hmi.TagFolder.DefaultTagTable;
        Tag sample = table.Tags.Find("Real1");
        if (sample != null)
        {
            DumpAttributeInfos("HMI Tag Real1", sample);
            DumpAllAttributes("HMI Tag Real1 值", sample);
        }

        for (int index = 1; index <= RealMemberCount; index++)
        {
            Tag tag = table.Tags.Find("Real" + index);
            if (tag == null)
            {
                Console.WriteLine("找不到 HMI Tag：Real" + index);
                continue;
            }

            int offset = (index - 1) * 4;
            string logical = "%DB" + dbNumber + ".DBD" + offset;
            TrySetAttribute(tag, "Length", 4);
            TrySetAttribute(tag, "DataType", "Real");
            TrySetAttribute(tag, "HmiDataType", "Real");
            TrySetAttribute(tag, "AddressAccessMode", "Absolute");
            TrySetAttribute(tag, "LogicalAddress", logical);
            TrySetAttribute(tag, "Address", logical);
            TrySetAttribute(tag, "Connection", connectionName);
        }

        if (sample != null)
        {
            DumpAllAttributes("HMI Tag Real1 設定後", sample);
        }

        ICompilable compilable = plc.GetService<ICompilable>();
        if (compilable != null)
        {
            Console.WriteLine("正在編譯 PLC，讓 HMI 找得到 DB123 成員...");
            CompilerResult result = compilable.Compile();
            Console.WriteLine("編譯狀態：" + result.State);
        }

        project.Save();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("PLC 已編譯並儲存。");
        Console.ResetColor();
        return 0;
    }

    private static Device CreateKtp1200(TiaPortal portal, Project project)
    {
        CatalogEntry selected = SelectKtp1200CatalogEntry(portal);
        Console.WriteLine("硬體目錄：" + selected.TypeName);
        Console.WriteLine("訂貨號：" + selected.ArticleNumber + " / " + selected.Version);
        Console.WriteLine("TypeIdentifier：" + selected.TypeIdentifier);

        Device device = project.Devices.CreateWithItem(selected.TypeIdentifier, HmiDeviceName, HmiDeviceName);
        Console.WriteLine("已建立 HMI 裝置：" + device.Name);
        return device;
    }

    private static CatalogEntry SelectKtp1200CatalogEntry(TiaPortal portal)
    {
        IList<CatalogEntry> entries = portal.HardwareCatalog.Find("KTP1200");
        if (entries == null || entries.Count == 0)
        {
            throw new InvalidOperationException("硬體目錄找不到 KTP1200。");
        }

        List<CatalogEntry> ranked = entries
            .Where(entry =>
                (entry.TypeName != null && entry.TypeName.IndexOf("KTP1200", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (entry.ArticleNumber != null && entry.ArticleNumber.IndexOf("6AV2123", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (entry.TypeIdentifier != null && entry.TypeIdentifier.IndexOf("6AV2", StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderByDescending(IsPreferredBasicPn)
            .ThenByDescending(entry => entry.Version ?? string.Empty)
            .ToList();

        foreach (CatalogEntry entry in ranked.Take(8))
        {
            Console.WriteLine("目錄候選：" + entry.TypeName + " / " + entry.ArticleNumber + " / " + entry.Version);
        }

        if (ranked.Count == 0)
        {
            throw new InvalidOperationException("硬體目錄有結果，但沒有可用的 KTP1200 項目。");
        }

        return ranked[0];
    }

    private static bool IsPreferredBasicPn(CatalogEntry entry)
    {
        string haystack = ((entry.TypeName ?? string.Empty) + " " +
            (entry.ArticleNumber ?? string.Empty) + " " +
            (entry.TypeIdentifier ?? string.Empty)).Replace(" ", string.Empty);

        bool isUnified = haystack.IndexOf("Unified", StringComparison.OrdinalIgnoreCase) >= 0;
        bool isDp = haystack.IndexOf("2MA03", StringComparison.OrdinalIgnoreCase) >= 0;
        bool isPn = haystack.IndexOf(PreferredArticle.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase) >= 0
            || haystack.IndexOf("2MB03", StringComparison.OrdinalIgnoreCase) >= 0
            || haystack.IndexOf("BasicPN", StringComparison.OrdinalIgnoreCase) >= 0;

        return !isUnified && !isDp && isPn;
    }

    private static Device FindExistingHmi(Project project)
    {
        foreach (Device device in project.Devices)
        {
            if (string.Equals(device.Name, HmiDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }

            string typeId = device.TypeIdentifier ?? string.Empty;
            if (typeId.IndexOf("KTP1200", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeId.Replace(" ", string.Empty).IndexOf("6AV2123-2MB03", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return device;
            }

            if (FindHmiTarget(device) != null &&
                ((device.Name ?? string.Empty).IndexOf("KTP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (device.Name ?? string.Empty).IndexOf("HMI", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return device;
            }
        }

        return null;
    }

    private static HmiTarget FindHmiTarget(Device device)
    {
        foreach (DeviceItem item in WalkDeviceItems(device))
        {
            SoftwareContainer container = item.GetService<SoftwareContainer>();
            HmiTarget hmi = container == null ? null : container.Software as HmiTarget;
            if (hmi != null)
            {
                return hmi;
            }
        }

        return null;
    }

    private static void FindPlcHardware(Project project, PlcSoftware plc, out Device plcDevice, out DeviceItem plcCpuItem)
    {
        foreach (Device device in project.Devices)
        {
            Console.WriteLine("檢查裝置：" + device.Name + " / " + device.TypeIdentifier);
            foreach (DeviceItem item in WalkDeviceItems(device))
            {
                SoftwareContainer container = item.GetService<SoftwareContainer>();
                PlcSoftware found = container == null ? null : container.Software as PlcSoftware;
                if (found != null)
                {
                    Console.WriteLine("  PLC 軟體：" + found.Name + " @ " + item.Name);
                    if (string.Equals(found.Name, plc.Name, StringComparison.Ordinal))
                    {
                        plcDevice = device;
                        plcCpuItem = item;
                        return;
                    }
                }
            }
        }

        throw new InvalidOperationException("找不到 PLC 對應的硬體裝置。");
    }

    private static EthernetEndpoint FindEthernetEndpoint(Device device)
    {
        foreach (DeviceItem item in WalkDeviceItems(device))
        {
            NetworkInterface network = item.GetService<NetworkInterface>();
            if (network == null || network.InterfaceType != NetType.Ethernet || !network.Nodes.Any())
            {
                continue;
            }

            return new EthernetEndpoint
            {
                Item = item,
                Network = network,
                Node = network.Nodes.First()
            };
        }

        return null;
    }

    private static void ConnectToSameSubnet(Node plcNode, Node hmiNode)
    {
        Subnet subnet = plcNode.ConnectedSubnet ?? hmiNode.ConnectedSubnet;
        if (subnet == null)
        {
            subnet = plcNode.CreateAndConnectToSubnet("PN_IE_Openness");
            Console.WriteLine("已建立子網：" + subnet.Name);
        }
        else
        {
            Console.WriteLine("使用現有子網：" + subnet.Name);
        }

        if (plcNode.ConnectedSubnet == null)
        {
            plcNode.ConnectToSubnet(subnet);
            Console.WriteLine("PLC 已連到子網。");
        }

        if (hmiNode.ConnectedSubnet == null)
        {
            hmiNode.ConnectToSubnet(subnet);
            Console.WriteLine("HMI 已連到子網。");
        }
    }

    private static void TrySetNodeAddress(Node node, string address, string subnetMask)
    {
        TrySetAttribute(node, "Address", address);
        TrySetAttribute(node, "SubnetMask", subnetMask);
        TrySetAttribute(node, "UseRouter", false);
        Console.WriteLine("HMI 嘗試設定 IP：" + address);
        DumpAttributeInfos("HMI Node", node);
    }

    private static string EnsureHmiConnection(
        Device hmiDevice,
        HmiTarget hmi,
        EthernetEndpoint hmiEthernet,
        Device plcDevice,
        DeviceItem plcCpuItem,
        EthernetEndpoint plcEthernet)
    {
        foreach (HmiSoftConnection existingSoft in hmi.Connections)
        {
            Console.WriteLine("已有 HMI 軟體連線：" + existingSoft.Name);
            return existingSoft.Name;
        }

#if TIA_V19
        Console.WriteLine("V19：略過 HW CommunicationConnections（此 API 是 V21）。改匯入軟體連線。");
        string plcIp = GetAttributeString(plcEthernet.Node, "Address") ?? "192.168.0.1";
        string hmiIp = GetAttributeString(hmiEthernet.Node, "Address") ?? "192.168.0.2";
        TryImportSoftwareConnection(hmi, plcDevice, plcCpuItem, hmiIp, plcIp);
        foreach (HmiSoftConnection afterImport in hmi.Connections)
        {
            Console.WriteLine("已有 HMI 軟體連線：" + afterImport.Name);
            return afterImport.Name;
        }

        return null;
#else
        CommunicationManagement management = FindCommunicationManagement(hmiDevice);
        if (management == null)
        {
            throw new InvalidOperationException("HMI 裝置找不到 CommunicationManagement 服務。");
        }

        foreach (HwConnection existing in management.Connections)
        {
            HwHmiConnection existingHmi = existing as HwHmiConnection;
            if (existingHmi != null && !string.IsNullOrEmpty(existingHmi.LocalConnectionName))
            {
                Console.WriteLine("已有硬體 HMI 連線：" + existingHmi.LocalConnectionName);
                return existingHmi.LocalConnectionName;
            }
        }

        Console.WriteLine("嘗試 Create<HmiConnection>...");
        try
        {
            HwHmiConnection created = management.Connections.Create<HwHmiConnection>(
                hmiEthernet.Node,
                plcCpuItem,
                plcEthernet.Node);
            string createdName = ReadConnectionName(created, hmi);
            if (!string.IsNullOrEmpty(createdName))
            {
                Console.WriteLine("已建立 HMI 連線：" + createdName);
                return createdName;
            }
        }
        catch (NonRecoverableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Create<HmiConnection> 失敗：" + DescribeException(ex));
        }

        return null;
#endif
    }

    private static void TryImportSoftwareConnection(
        HmiTarget hmi,
        Device plcDevice,
        DeviceItem plcCpuItem,
        string hmiAddress,
        string plcAddress)
    {
        string path = Path.Combine(GetExportFolder(), "HmiConnection.import.xml");
        File.WriteAllText(
            path,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine +
            "<Document>" + Environment.NewLine +
            "  <Engineering version=\"V19\" />" + Environment.NewLine +
            "  <Hmi.Communication.Connection ID=\"0\">" + Environment.NewLine +
            "    <AttributeList>" + Environment.NewLine +
            "      <Driver>SMART_S7_1200_OMS</Driver>" + Environment.NewLine +
            "      <InterfaceType>ETHERNET</InterfaceType>" + Environment.NewLine +
            "      <Name>HMI_Connection_1</Name>" + Environment.NewLine +
            "      <Online>true</Online>" + Environment.NewLine +
            "      <PhysicId>S7_ETHERNET_IP4</PhysicId>" + Environment.NewLine +
            "      <ProtocolId>S7_ETHERNET_IP4</ProtocolId>" + Environment.NewLine +
            "    </AttributeList>" + Environment.NewLine +
            "    <ObjectList>" + Environment.NewLine +
            "      <Hmi.Communication.NameValuePair ID=\"1\" CompositionName=\"PhysicValues\">" + Environment.NewLine +
            "        <AttributeList><Name>LocAddress</Name><Value>" + hmiAddress + "</Value></AttributeList>" + Environment.NewLine +
            "      </Hmi.Communication.NameValuePair>" + Environment.NewLine +
            "      <Hmi.Communication.NameValuePair ID=\"2\" CompositionName=\"ProtocolValues\">" + Environment.NewLine +
            "        <AttributeList><Name>RemStAddress</Name><Value>" + plcAddress + "</Value></AttributeList>" + Environment.NewLine +
            "      </Hmi.Communication.NameValuePair>" + Environment.NewLine +
            "    </ObjectList>" + Environment.NewLine +
            "  </Hmi.Communication.Connection>" + Environment.NewLine +
            "</Document>",
            new UTF8Encoding(false));

        try
        {
            hmi.Connections.Import(new FileInfo(path), ImportOptions.Override);
            Console.WriteLine("已匯入軟體 HMI 連線 HMI_Connection_1。");
        }
        catch (Exception ex)
        {
            Console.WriteLine("軟體連線匯入失敗：" + DescribeException(ex));
            return;
        }

        HmiSoftConnection connection = hmi.Connections.Find("HMI_Connection_1") ?? hmi.Connections.FirstOrDefault();
        if (connection == null)
        {
            return;
        }

        DumpAttributeInfos("HMI 軟體連線", connection);
        DumpAllAttributes("HMI 軟體連線 ReadWrite", connection);
        TrySetAttribute(connection, "CommunicationDriver", "SIMATIC S7 1200");
        TrySetAttribute(connection, "Partner", plcCpuItem.Name);
        TrySetAttribute(connection, "Station", plcDevice.Name);
        TrySetAttribute(
            connection,
            "InitialAddress",
            "CommunicationInterface = Industrial Ethernet;HostAddress = 192.168.0.1;PlcRack = 0;PlcSlot = 1;");
    }

#if !TIA_V19
    private static CommunicationManagement FindCommunicationManagement(Device device)
    {
        foreach (DeviceItem item in WalkDeviceItems(device))
        {
            CommunicationManagement management = item.GetService<CommunicationManagement>();
            if (management != null)
            {
                Console.WriteLine("CommunicationManagement 位於：" + item.Name);
                return management;
            }
        }

        return null;
    }
#endif

    private static void ImportDb123Tags(HmiTarget hmi, string connectionName)
    {
        TagTable table = hmi.TagFolder.DefaultTagTable;
        Console.WriteLine("HMI Tag table：" + table.Name);
        EnsureSimpleHmiTags(table);

        string exportFolder = GetExportFolder();
        string templatePath = Path.Combine(exportFolder, "Real1.tag.xml");
        Tag templateTag = table.Tags.Find("Real1");
        if (templateTag == null)
        {
            throw new InvalidOperationException("找不到可用的 HMI Tag 模板 Real1。");
        }

        if (File.Exists(templatePath))
        {
            File.Delete(templatePath);
        }

        templateTag.Export(new FileInfo(templatePath), ExportOptions.WithDefaults);
        Console.WriteLine("已匯出 Real1 模板：" + templatePath);

        if (string.IsNullOrEmpty(connectionName) && hmi.Connections.Any())
        {
            connectionName = hmi.Connections.First().Name;
        }

        if (string.IsNullOrEmpty(connectionName))
        {
            Console.WriteLine("尚無 HMI 連線，先保留現有 HMI Tag，繼續做 HOME PAGE。");
            return;
        }

        try
        {
            ImportAddressBoundTagsFromTemplate(table, templatePath, connectionName, "%DB1.DBD{0}");
            Console.WriteLine("已 Import Absolute HMI Tag Real1-Real10，連線 " + connectionName);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Absolute Tag Import 失敗：" + DescribeException(ex));
        }
    }

    private static void EnsureDb123StandardAccess(PlcSoftware plc, Project project)
    {
        DataBlock dataBlock = plc.BlockGroup.Blocks.Find(DataBlockName) as DataBlock;
        if (dataBlock == null)
        {
            throw new InvalidOperationException("找不到全域 DB：" + DataBlockName);
        }

        DumpAttributeInfos("DB123", dataBlock);
        TrySetAttribute(dataBlock, "OptimizedAccess", false);
        TrySetAttribute(dataBlock, "OptimizedBlockAccess", false);
        TrySetAttribute(dataBlock, "MemoryLayout", "Standard");

        string sourceName = DataBlockName + "_standard.scl";
        string sourcePath = Path.Combine(GetExportFolder(), sourceName);
        File.WriteAllText(
            sourcePath,
            "DATA_BLOCK \"" + DataBlockName + "\"" + Environment.NewLine +
            "{ S7_Optimized_Access := 'FALSE' }" + Environment.NewLine +
            "VERSION : 0.1" + Environment.NewLine +
            "VAR" + Environment.NewLine +
            "    Real1 : Real;" + Environment.NewLine +
            "    Real2 : Real;" + Environment.NewLine +
            "    Real3 : Real;" + Environment.NewLine +
            "    Real4 : Real;" + Environment.NewLine +
            "    Real5 : Real;" + Environment.NewLine +
            "    Real6 : Real;" + Environment.NewLine +
            "    Real7 : Real;" + Environment.NewLine +
            "    Real8 : Real;" + Environment.NewLine +
            "    Real9 : Real;" + Environment.NewLine +
            "    Real10 : Real;" + Environment.NewLine +
            "END_VAR" + Environment.NewLine +
            "BEGIN" + Environment.NewLine +
            "END_DATA_BLOCK" + Environment.NewLine,
            new UTF8Encoding(false));

        PlcExternalSource existingSource = plc.ExternalSourceGroup.ExternalSources.Find(sourceName);
        if (existingSource != null)
        {
            existingSource.Delete();
        }

        Console.WriteLine("把 DB123 改成非最佳化，才能用絕對位址綁 HMI。");
        plc.ExternalSourceGroup.ExternalSources
            .CreateFromFile(sourceName, sourcePath)
            .GenerateBlocksFromSource(GenerateBlockOption.None);
        project.Save();
        Console.WriteLine("DB123 已改為標準存取並儲存。");
    }

    private static void ImportAddressBoundTagsFromTemplate(
        TagTable table,
        string templatePath,
        string connectionName,
        string addressFormat)
    {
        for (int index = 1; index <= RealMemberCount; index++)
        {
            string tagName = "Real" + index;
            int offset = (index - 1) * 4;
            XDocument document = XDocument.Load(templatePath);
            XElement tagElement = FindElement(document, "Hmi.Tag.Tag");
            SetOrCreateAttribute(tagElement, "Name", tagName);
            SetOrCreateAttribute(tagElement, "Length", "4");
            SetOrCreateAttribute(tagElement, "AddressAccessMode", "Absolute");
            SetOrCreateAttribute(tagElement, "LogicalAddress", string.Format(addressFormat, offset));
            RemoveAttribute(tagElement, "Coding");

            XElement linkList = tagElement.Elements().FirstOrDefault(element => element.Name.LocalName == "LinkList");
            if (linkList == null)
            {
                linkList = new XElement("LinkList");
                tagElement.Add(linkList);
            }

            SetOpenLink(linkList, "DataType", "Real");
            SetOpenLink(linkList, "HmiDataType", "Real");
            if (!string.IsNullOrEmpty(connectionName))
            {
                SetOpenLink(linkList, "Connection", connectionName);
            }

            XElement controllerTag = linkList.Elements().FirstOrDefault(element => element.Name.LocalName == "ControllerTag");
            if (controllerTag != null)
            {
                controllerTag.Remove();
            }

            string importPath = Path.Combine(GetExportFolder(), tagName + ".addr.xml");
            document.Save(importPath);
            table.Tags.Import(new FileInfo(importPath), ImportOptions.Override);
        }
    }

    private static void EnsureSimpleHmiTags(TagTable table)
    {
        string exportFolder = GetExportFolder();
        string tablePath = Path.Combine(exportFolder, "DefaultTagTable.xml");
        if (File.Exists(tablePath))
        {
            File.Delete(tablePath);
        }

        table.Export(new FileInfo(tablePath), ExportOptions.WithDefaults);
        XDocument document = XDocument.Load(tablePath);
        XElement tableElement = FindElement(document, "Hmi.Tag.TagTable");
        XElement objectList = GetOrCreateObjectList(tableElement);
        int nextId = GetMaxId(document) + 1;
        bool added = false;
        for (int index = 1; index <= RealMemberCount; index++)
        {
            string tagName = "Real" + index;
            if (table.Tags.Find(tagName) != null)
            {
                continue;
            }

            objectList.Add(new XElement(
                "Hmi.Tag.Tag",
                new XAttribute("ID", nextId++.ToString()),
                new XAttribute("CompositionName", "Tags"),
                new XElement("AttributeList", new XElement("Name", tagName))));
            added = true;
        }

        if (!added)
        {
            Console.WriteLine("HMI Tag Real1 到 Real10 已存在。");
            return;
        }

        string importPath = Path.Combine(exportFolder, "DefaultTagTable.import.xml");
        document.Save(importPath);
        TagSystemFolder folder = (TagSystemFolder)table.Parent;
        folder.TagTables.Import(new FileInfo(importPath), ImportOptions.Override);
        Console.WriteLine("已建立內部 HMI Tag：Real1 到 Real10。");
    }

    private static void ImportBoundTagsFromTemplate(
        TagTable table,
        string templatePath,
        string connectionName,
        IEnumerable<string> linkNames,
        string plcTagFormat)
    {
        XDocument template = XDocument.Load(templatePath);
        for (int index = 1; index <= RealMemberCount; index++)
        {
            string tagName = "Real" + index;
            XDocument document = XDocument.Load(templatePath);
            XElement tagElement = FindElement(document, "Hmi.Tag.Tag");
            SetOrCreateAttribute(tagElement, "Name", tagName);
            SetOrCreateAttribute(tagElement, "Length", "4");
            SetOrCreateAttribute(tagElement, "LogicalAddress", string.Empty);
            RemoveAttribute(tagElement, "Coding");

            XElement linkList = tagElement.Elements().FirstOrDefault(element => element.Name.LocalName == "LinkList");
            if (linkList == null)
            {
                linkList = new XElement("LinkList");
                tagElement.Add(linkList);
            }

            SetOpenLink(linkList, "DataType", "Real");
            SetOpenLink(linkList, "HmiDataType", "Real");
            foreach (string linkName in linkNames)
            {
                if (linkName == "Connection")
                {
                    if (!string.IsNullOrEmpty(connectionName))
                    {
                        SetOpenLink(linkList, "Connection", connectionName);
                    }

                    continue;
                }

                SetOpenLink(linkList, linkName, string.Format(plcTagFormat, tagName));
            }

            string importPath = Path.Combine(GetExportFolder(), tagName + ".import.xml");
            document.Save(importPath);
            table.Tags.Import(new FileInfo(importPath), ImportOptions.Override);
        }
    }

    private static void SetOpenLink(XElement linkList, string name, string value)
    {
        XElement existing = linkList.Elements().FirstOrDefault(element => element.Name.LocalName == name);
        if (existing != null)
        {
            existing.Remove();
        }

        linkList.Add(new XElement(
            name,
            new XAttribute("TargetID", "@OpenLink"),
            new XElement("Name", value)));
    }

    private static void ReexportBoundTag(Tag tag)
    {
        if (tag == null)
        {
            return;
        }

        string path = Path.Combine(GetExportFolder(), "Real1.bound.xml");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        tag.Export(new FileInfo(path), ExportOptions.WithDefaults);
        Console.WriteLine("已回匯 Real1 以確認綁定：" + path);
    }

    private static void BindImportedTagsToPlc(TagTable table, string connectionName)
    {
        for (int index = 1; index <= RealMemberCount; index++)
        {
            Tag tag = table.Tags.Find("Real" + index);
            if (tag == null)
            {
                continue;
            }

            if (index == 1)
            {
                DumpAttributeInfos("HMI Tag Real1", tag);
                DumpAllAttributes("HMI Tag Real1 ReadWrite", tag);
                DumpCompositionInfos("HMI Tag Real1", tag);
                DumpInvocationInfos("HMI Tag Real1", tag);
                string tagExport = Path.Combine(GetExportFolder(), "Real1.tag.xml");
                if (File.Exists(tagExport))
                {
                    File.Delete(tagExport);
                }

                tag.Export(new FileInfo(tagExport), ExportOptions.WithDefaults);
                Console.WriteLine("已匯出 Real1：" + tagExport);
            }

            TrySetAttribute(tag, "DataType", "Real");
            TrySetAttribute(tag, "PlcTag", DataBlockName + ".Real" + index);
            if (!string.IsNullOrEmpty(connectionName))
            {
                TrySetAttribute(tag, "Connection", connectionName);
            }
        }
    }

    private static void TryCreateTagsViaComposition(TagTable table)
    {
        IEngineeringComposition composition = table.Tags as IEngineeringComposition;
        if (composition == null)
        {
            return;
        }

        IList<EngineeringCreationInfo> infos = composition.GetCreationInfos();
        if (infos == null || infos.Count == 0)
        {
            return;
        }

        Console.WriteLine("HMI Tags.Create 可用，嘗試直接建立。");
        foreach (EngineeringCreationInfo info in infos)
        {
            Console.WriteLine("  類型：" + V19Api.TypeName(info));
        }
    }

    private static XElement CreateHmiTagElement(
        int id,
        string name,
        string connectionName,
        string plcTag,
        IEnumerable<string> attributeNames)
    {
        XElement attributes = new XElement("AttributeList");
        foreach (string attributeName in attributeNames)
        {
            if (attributeName == "Name")
            {
                attributes.Add(new XElement("Name", name));
            }
            else if (attributeName == "DataType")
            {
                attributes.Add(new XElement("DataType", "Real"));
            }
            else if (attributeName == "PlcTag")
            {
                attributes.Add(new XElement("PlcTag", plcTag));
            }
            else if (attributeName == "Connection" && !string.IsNullOrEmpty(connectionName))
            {
                attributes.Add(new XElement("Connection", connectionName));
            }
        }

        return new XElement(
            "Hmi.Tag.Tag",
            new XAttribute("ID", id.ToString()),
            new XAttribute("CompositionName", "Tags"),
            attributes);
    }

    private static void ConfigureHomePage(HmiTarget hmi)
    {
        Console.WriteLine("畫面資料夾：" + hmi.ScreenFolder.Name + " / Screens=" + hmi.ScreenFolder.Screens.Count);
        foreach (Screen existing in WalkScreens(hmi.ScreenFolder))
        {
            Console.WriteLine("  既有畫面：" + existing.Name);
        }

        ScreenComposition screens = hmi.ScreenFolder.Screens;
        Screen home = screens.Find(HomeScreenName);
        Screen source = home ?? screens.FirstOrDefault();
        if (source == null)
        {
            ImportHomePageFromScratch(hmi);
            home = hmi.ScreenFolder.Screens.Find(HomeScreenName);
            source = home;
        }

        if (home != null)
        {
            DumpCompositionInfos("HOME PAGE", home);
            DumpInvocationInfos("HOME PAGE", home);
        }

        if (source == null)
        {
            return;
        }

        string exportFolder = GetExportFolder();
        string exportPath = Path.Combine(exportFolder, "HomePage.source.xml");
        if (File.Exists(exportPath))
        {
            File.Delete(exportPath);
        }

        source.Export(new FileInfo(exportPath), ExportOptions.WithDefaults);
        Console.WriteLine("已匯出畫面：" + source.Name + " -> " + exportPath);

        if (FindElement(XDocument.Load(exportPath), "Hmi.Screen.Screen") == null)
        {
            throw new InvalidOperationException("匯出的畫面 XML 格式不符合預期。");
        }

        XDocument attempt = XDocument.Load(exportPath);
        XElement screenElement = FindElement(attempt, "Hmi.Screen.Screen");
        SetOrCreateAttribute(screenElement, "Name", HomeScreenName);
        XElement objectList = GetOrCreateObjectList(screenElement);
        RemoveGeneratedHomeItems(objectList);

        int nextId = GetMaxId(attempt) + 1;
        XElement layer = new XElement(
            "Hmi.Screen.Layer",
            new XAttribute("ID", nextId++.ToString()),
            new XAttribute("CompositionName", "Layers"),
            new XElement(
                "AttributeList",
                new XElement("Index", "0"),
                new XElement("Name", "Layer_0")),
            new XElement("ObjectList"));
        objectList.Add(layer);
        XElement layerItems = layer.Element("ObjectList");

        nextId = AddTextField(layerItems, nextId, "Title_Home", 40, 16, 600, 40, "DB123 HOME PAGE", "ScreenItems");
        for (int index = 1; index <= RealMemberCount; index++)
        {
            int top = 40 + (index * 56);
            string tagName = "Real" + index;
            nextId = AddTextField(layerItems, nextId, "Label_" + tagName, 40, top, 180, 36, tagName, "ScreenItems");
            nextId = AddIoField(layerItems, nextId, "IOField_" + tagName, 260, top, 200, 36, tagName, "ScreenItems");
        }

        string importPath = Path.Combine(exportFolder, "HomePage.import.xml");
        attempt.Save(importPath);
        try
        {
            screens.Import(new FileInfo(importPath), ImportOptions.Override);
            Console.WriteLine("已把 DB123 欄位放進 HOME PAGE。");
        }
        catch (NonRecoverableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine("畫面 Layer/物件匯入失敗：" + DescribeException(ex));
        }

        if (home == null && !string.Equals(source.Name, HomeScreenName, StringComparison.Ordinal))
        {
            Screen imported = screens.Find(HomeScreenName);
            if (imported != null && screens.Find(source.Name) != null &&
                !string.Equals(source.Name, HomeScreenName, StringComparison.Ordinal))
            {
                Console.WriteLine("保留原畫面：" + source.Name + "；啟動畫面將改為 HOME PAGE。");
            }
        }
    }

    private static int AddTextField(XElement objectList, int id, string name, int left, int top, int width, int height, string text, string compositionName)
    {
        int textId = id + 1;
        int itemId = id + 2;
        objectList.Add(new XElement(
            "Hmi.Screen.TextField",
            new XAttribute("ID", id.ToString()),
            new XAttribute("CompositionName", compositionName),
            new XElement(
                "AttributeList",
                new XElement("Height", height.ToString()),
                new XElement("Left", left.ToString()),
                new XElement("Name", name),
                new XElement("ObjectName", name),
                new XElement("Top", top.ToString()),
                new XElement("Width", width.ToString())),
            new XElement(
                "ObjectList",
                new XElement(
                    "MultilingualText",
                    new XAttribute("ID", textId.ToString()),
                    new XAttribute("CompositionName", "Text"),
                    new XElement(
                        "ObjectList",
                        new XElement(
                            "MultilingualTextItem",
                            new XAttribute("ID", itemId.ToString()),
                            new XAttribute("CompositionName", "Items"),
                            new XElement(
                                "AttributeList",
                                new XElement("Culture", "en-US"),
                                new XElement("Text", text))))))));
        return id + 3;
    }

    private static int AddIoField(XElement objectList, int id, string name, int left, int top, int width, int height, string tagName, string compositionName)
    {
        objectList.Add(new XElement(
            "Hmi.Screen.IOField",
            new XAttribute("ID", id.ToString()),
            new XAttribute("CompositionName", compositionName),
            new XElement(
                "AttributeList",
                new XElement("FieldType", "Output"),
                new XElement("Height", height.ToString()),
                new XElement("Left", left.ToString()),
                new XElement("Name", name),
                new XElement("ObjectName", name),
                new XElement("Tag", tagName),
                new XElement("Top", top.ToString()),
                new XElement("Width", width.ToString()))));
        return id + 1;
    }

    public static int SetStartScreenOnly(Project project)
    {
        Console.WriteLine("設定 HMI start screen：" + HomeScreenName);
        Device hmiDevice = FindExistingHmi(project);
        if (hmiDevice == null)
        {
            throw new InvalidOperationException("找不到 HMI 裝置。");
        }

        HmiTarget hmi = FindHmiTarget(hmiDevice);
        if (hmi == null)
        {
            throw new InvalidOperationException("找不到 HmiTarget。");
        }

        TrySetStartScreen(project, hmiDevice, hmi);
        project.Save();
        Console.WriteLine("已存 start screen 設定。");
        return 0;
    }

    public static int RunLabHmiGap(TiaPortal portal, Project project)
    {
        Console.WriteLine("=== V19 lab HMI remaining tests ===");
        Device hmiDevice = FindExistingHmi(project);
        if (hmiDevice == null)
        {
            throw new InvalidOperationException("找不到 HMI 裝置。");
        }

        HmiTarget hmi = FindHmiTarget(hmiDevice);
        if (hmi == null)
        {
            throw new InvalidOperationException("找不到 HmiTarget。");
        }

        Console.WriteLine("HMI：" + hmiDevice.Name + " / " + hmi.Name);
        DumpHmiGapCompositions(hmi);
        TryProbeRecipeAndDiscrete(hmi);

        Screen home = hmi.ScreenFolder.Screens.Find(HomeScreenName);
        if (home == null)
        {
            Console.WriteLine("找不到畫面：" + HomeScreenName);
        }
        else
        {
            DumpCompositionInfos("HOME PAGE", home);
            string livePath = Path.Combine(GetExportFolder(), "HOME_PAGE.live.xml");
            if (File.Exists(livePath))
            {
                File.Delete(livePath);
            }

            home.Export(new FileInfo(livePath), ExportOptions.WithDefaults);
            Console.WriteLine("已匯出空白/現況 HOME PAGE：" + livePath);
            TryImportTextFieldWithoutLayer(hmi, livePath);
        }

        TrySoftkeyGlobalRoundtrip(hmi);
        TryFixTagAcquisitionCycles(hmi);
        try
        {
            project.Save();
            Console.WriteLine("已存（畫面／Softkey 測試後）。");
            return StationSync.CompileAllHmi(portal, project);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL：存檔或編譯 HMI（專案可能已 disposed）：" + DescribeException(ex));
            return 1;
        }
    }

    private static void DumpHmiGapCompositions(HmiTarget hmi)
    {
        DumpCompositionInfos("HmiTarget", hmi);
        Console.WriteLine("HmiTarget composition 名稱含 Recipe／Alarm／Discrete：");
        bool found = false;
        try
        {
            IEngineeringObject engineering = hmi;
            foreach (EngineeringCompositionInfo info in engineering.GetCompositionInfos())
            {
                string name = info.Name ?? "";
                if (name.IndexOf("Recipe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Alarm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Discrete", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = true;
                    Console.WriteLine("  HIT " + name + " / " + V19Api.TypeName(info));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  掃 composition 失敗：" + DescribeException(ex));
        }

        if (!found)
        {
            Console.WriteLine("  （沒有）");
        }
    }

    private static void TryProbeRecipeAndDiscrete(HmiTarget hmi)
    {
        string[] names =
        {
            "Recipes", "RecipeFolder", "RecipeView", "DiscreteAlarms", "Alarms",
            "HmiAlarms", "AnalogAlarms", "AlarmClasses"
        };
        foreach (string name in names)
        {
            try
            {
                object value = hmi.GetAttribute(name);
                Console.WriteLine("GetAttribute(" + name + ")=" + (value == null ? "(null)" : value.GetType().Name));
            }
            catch (Exception ex)
            {
                Console.WriteLine("GetAttribute(" + name + ") 不支援：" + ex.Message);
            }
        }
    }

    private static void TryImportTextFieldWithoutLayer(HmiTarget hmi, string livePath)
    {
        Console.WriteLine("--- 畫面物件：在匯出檔 ObjectList 加 TextField（不加 Layer）---");
        XDocument document = XDocument.Load(livePath);
        XElement screenElement = FindElement(document, "Hmi.Screen.Screen");
        XElement objectList = GetOrCreateObjectList(screenElement);
        bool hasLayer = objectList.Elements().Any(e =>
            (e.Name.LocalName ?? "").IndexOf("Layer", StringComparison.OrdinalIgnoreCase) >= 0);
        Console.WriteLine("現況 XML 有 Layer：" + hasLayer);
        int nextId = GetMaxId(document) + 1;
        AddTextField(objectList, nextId, "Title_Home_Retry", 40, 16, 600, 40, "DB123 HOME PAGE", "ScreenItems");
        string importPath = Path.Combine(GetExportFolder(), "HOME_PAGE.nolayer.import.xml");
        document.Save(importPath);
        try
        {
            hmi.ScreenFolder.Screens.Import(new FileInfo(importPath), ImportOptions.Override);
            Console.WriteLine("PASS：無 Layer 的 TextField Import。");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL：無 Layer TextField Import：" + DescribeException(ex));
        }
    }

    private static void TrySoftkeyGlobalRoundtrip(HmiTarget hmi)
    {
        Console.WriteLine("--- Softkey：ScreenGlobalElements roundtrip + stub ---");
        string exportPath = Path.Combine(GetExportFolder(), "ScreenGlobalElements.live.xml");
        if (File.Exists(exportPath))
        {
            File.Delete(exportPath);
        }

        try
        {
            hmi.ScreenGlobalElements.Export(new FileInfo(exportPath), ExportOptions.WithDefaults);
            Console.WriteLine("已匯出 ScreenGlobalElements：" + exportPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL：ScreenGlobalElements Export：" + DescribeException(ex));
            return;
        }

        try
        {
            hmi.ImportScreenGlobalElements(new FileInfo(exportPath), ImportOptions.Override);
            Console.WriteLine("PASS：ImportScreenGlobalElements roundtrip。");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL：ImportScreenGlobalElements roundtrip：" + DescribeException(ex));
            return;
        }

        Console.WriteLine("SKIP：不在 ScreenGlobalElements 硬塞 SoftKey（GlobalAssignment=true 曾 NonRecoverable、Portal disposed）。V19 空畫面沒有既有 ActivateScreen 可改。");
    }

    private static void TryFixTagAcquisitionCycles(HmiTarget hmi)
    {
        Console.WriteLine("--- HMI Tag acquisition cycle ---");
        string cycleName = null;
        try
        {
            object cycles = ((IEngineeringObject)hmi).GetComposition("Cycles");
            System.Collections.IEnumerable enumerable = cycles as System.Collections.IEnumerable;
            if (enumerable != null)
            {
                foreach (object item in enumerable)
                {
                    IEngineeringObject obj = item as IEngineeringObject;
                    string name = obj == null ? null : Convert.ToString(obj.GetAttribute("Name"));
                    Console.WriteLine("Cycle：" + name);
                    if (cycleName == null && !string.IsNullOrEmpty(name))
                    {
                        cycleName = name;
                    }

                    if (name != null &&
                        (name.IndexOf("1 s", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         name.IndexOf("1s", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        cycleName = name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("列出 Cycles 失敗：" + DescribeException(ex));
        }

        if (string.IsNullOrEmpty(cycleName))
        {
            Console.WriteLine("SKIP：沒有可用 Cycle 名稱。");
            return;
        }

        Console.WriteLine("將 Real1-10 綁 Cycle：" + cycleName);
        TagTable table = hmi.TagFolder.DefaultTagTable;
        Tag template = table.Tags.Find("Real1");
        if (template == null)
        {
            Console.WriteLine("SKIP：找不到 Real1。");
            return;
        }

        string templatePath = Path.Combine(GetExportFolder(), "Real1.cycle.xml");
        if (File.Exists(templatePath))
        {
            File.Delete(templatePath);
        }

        template.Export(new FileInfo(templatePath), ExportOptions.WithDefaults);
        try
        {
            for (int index = 1; index <= RealMemberCount; index++)
            {
                string tagName = "Real" + index;
                int offset = (index - 1) * 4;
                XDocument document = XDocument.Load(templatePath);
                XElement tagElement = FindElement(document, "Hmi.Tag.Tag");
                SetOrCreateAttribute(tagElement, "Name", tagName);
                SetOrCreateAttribute(tagElement, "Length", "4");
                SetOrCreateAttribute(tagElement, "AddressAccessMode", "Absolute");
                SetOrCreateAttribute(tagElement, "LogicalAddress", "%DB1.DBD" + offset);
                XElement linkList = tagElement.Elements().FirstOrDefault(element => element.Name.LocalName == "LinkList");
                if (linkList == null)
                {
                    linkList = new XElement("LinkList");
                    tagElement.Add(linkList);
                }

                SetOpenLink(linkList, "DataType", "Real");
                SetOpenLink(linkList, "HmiDataType", "Real");
                SetOpenLink(linkList, "Connection", "HMI_Connection_1");
                SetOpenLink(linkList, "Cycle", cycleName);
                XElement controllerTag = linkList.Elements().FirstOrDefault(element => element.Name.LocalName == "ControllerTag");
                if (controllerTag != null)
                {
                    controllerTag.Remove();
                }

                string importPath = Path.Combine(GetExportFolder(), tagName + ".cycle.import.xml");
                document.Save(importPath);
                table.Tags.Import(new FileInfo(importPath), ImportOptions.Override);
            }

            Console.WriteLine("PASS：Real1-10 已 Import Cycle=" + cycleName);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL：Tag Cycle Import：" + DescribeException(ex));
        }
    }

    private static void TrySetStartScreen(HmiTarget hmi)
    {
        TrySetStartScreen(null, null, hmi);
    }

    private static void TrySetStartScreen(Project project, Device hmiDevice, HmiTarget hmi)
    {
        DumpAttributeInfos("HmiTarget", hmi);
        DumpCompositionInfos("HmiTarget", hmi);
        DumpInvocationInfos("HmiTarget", hmi);
        DumpAttributeInfos("ScreenOverview", hmi.ScreenOverview);
        DumpInvocationInfos("ScreenOverview", hmi.ScreenOverview);
        DumpCycles(hmi);
        DumpHmiServices(hmi);

        Screen home = hmi.ScreenFolder.Screens.Find(HomeScreenName);
        if (home != null)
        {
            DumpAttributeInfos("HOME PAGE", home);
            DumpInvocationInfos("HOME PAGE", home);
            TrySetAttribute(home, "IsStartScreen", true);
            TrySetAttribute(home, "StartScreen", true);
        }

        string[] names = { "StartScreen", "StartupScreen", "InitialScreen", "Start screen", "StartScreenName", "Startbild" };
        foreach (string name in names)
        {
            TrySetAttribute(hmi, name, HomeScreenName);
            TrySetAttribute(hmi.ScreenOverview, name, HomeScreenName);
        }

        if (hmiDevice != null)
        {
            foreach (DeviceItem item in WalkDeviceItems(hmiDevice))
            {
                DumpAttributeInfos("HMI 裝置項 " + item.Name, item);
                DumpInvocationInfos("HMI 裝置項 " + item.Name, item);
                DumpHmiServices(item);
                foreach (string name in names)
                {
                    TrySetAttribute(item, name, HomeScreenName);
                }
            }

            TryCaxExportHmi(project, hmiDevice);
        }

        TryPatchScreenGlobalStartScreen(hmi);
        TryPatchScreenOverviewStartScreen(hmi);
    }

    private static void DumpCycles(HmiTarget hmi)
    {
        try
        {
            object cycles = ((IEngineeringObject)hmi).GetComposition("Cycles");
            System.Collections.IEnumerable enumerable = cycles as System.Collections.IEnumerable;
            if (enumerable == null)
            {
                Console.WriteLine("Cycles = <null>");
                return;
            }

            int count = 0;
            foreach (object item in enumerable)
            {
                count++;
                IEngineeringObject obj = item as IEngineeringObject;
                if (obj == null)
                {
                    Console.WriteLine("Cycle " + item);
                    continue;
                }

                DumpAttributeInfos("Cycle", obj);
                DumpInvocationInfos("Cycle", obj);
            }

            Console.WriteLine("Cycles count=" + count);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Cycles dump 失敗：" + DescribeException(ex));
        }
    }

    private static void DumpHmiServices(IEngineeringObject target)
    {
        IEngineeringServiceProvider provider = target as IEngineeringServiceProvider;
        if (provider == null)
        {
            return;
        }

        try
        {
            foreach (EngineeringServiceInfo info in provider.GetServiceInfos())
            {
                string typeName = info.Type == null ? "?" : info.Type.FullName;
                Console.WriteLine("service " + target.GetType().Name + " / " + typeName);
                if (info.Type == null)
                {
                    continue;
                }

                try
                {
                    object service = provider.GetService(info.Type);
                    IEngineeringObject eng = service as IEngineeringObject;
                    if (eng != null)
                    {
                        DumpAttributeInfos("service " + typeName, eng);
                        DumpInvocationInfos("service " + typeName, eng);
                    }
                    else
                    {
                        Console.WriteLine("  GetService = " + (service == null ? "<null>" : service.GetType().FullName));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  GetService 失敗：" + DescribeException(ex));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("GetServiceInfos 失敗：" + DescribeException(ex));
        }
    }

    private static void TryCaxExportHmi(Project project, Device hmiDevice)
    {
        if (project == null || hmiDevice == null)
        {
            return;
        }

        try
        {
            CaxProvider cax = project.GetService<CaxProvider>();
            if (cax == null)
            {
                Console.WriteLine("CaxProvider = <null>");
                return;
            }

            string path = Path.Combine(GetExportFolder(), SanitizeFile(hmiDevice.Name) + ".aml");
            Console.WriteLine("CAx 匯出 " + hmiDevice.Name + " → " + path);
            TransferResult result = cax.Export(hmiDevice, new FileInfo(path));
            Console.WriteLine("CAx = " + (result == null ? "<null>" :
                ("err=" + result.ErrorCount + " warn=" + result.WarningCount + " " + result.State)));
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path);
                foreach (string needle in new[] { "StartScreen", "Start screen", "Startbild", "HOME PAGE" })
                {
                    int at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine("CAx contains '" + needle + "': " + (at >= 0));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("CAx 失敗：" + DescribeException(ex));
        }
    }

    private static string SanitizeFile(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }

    private static void TryPatchScreenGlobalStartScreen(HmiTarget hmi)
    {
        string path = Path.Combine(GetExportFolder(), "ScreenGlobalElements.xml");
        FileInfo file = new FileInfo(path);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            hmi.ScreenGlobalElements.Export(file, ExportOptions.WithDefaults);
            Console.WriteLine("已匯出 ScreenGlobalElements：" + path);
        }
        catch (Exception ex)
        {
            Console.WriteLine("ScreenGlobalElements Export 失敗：" + DescribeException(ex));
            return;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine("讀 ScreenGlobalElements 失敗：" + ex.Message);
            return;
        }

        bool changed = false;
        foreach (XElement attrList in doc.Descendants().Where(e => e.Name.LocalName == "AttributeList"))
        {
            foreach (string name in new[] { "StartScreen", "StartupScreen", "InitialScreen", "StartScreenName" })
            {
                XElement node = attrList.Elements().FirstOrDefault(e => e.Name.LocalName == name);
                if (node == null)
                {
                    continue;
                }

                if (!string.Equals(node.Value, HomeScreenName, StringComparison.Ordinal))
                {
                    Console.WriteLine("ScreenGlobalElements " + name + "：" + node.Value + " → " + HomeScreenName);
                    node.Value = HomeScreenName;
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            Console.WriteLine("ScreenGlobalElements 沒有既有 StartScreen 欄位，不硬塞（V19 Basic 不支援）。");
            return;
        }
        else
        {
            doc.Save(path);
        }

        try
        {
            hmi.ImportScreenGlobalElements(file, ImportOptions.Override);
            Console.WriteLine("已 ImportScreenGlobalElements。");
        }
        catch (Exception ex)
        {
            Console.WriteLine("ImportScreenGlobalElements 失敗：" + DescribeException(ex));
        }
    }

    private static void TryPatchScreenOverviewStartScreen(HmiTarget hmi)
    {
        string path = Path.Combine(GetExportFolder(), "ScreenOverview.xml");
        FileInfo file = new FileInfo(path);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            hmi.ScreenOverview.Export(file, ExportOptions.WithDefaults);
            Console.WriteLine("已匯出 ScreenOverview：" + path);
        }
        catch (Exception ex)
        {
            Console.WriteLine("ScreenOverview Export 失敗：" + DescribeException(ex));
            return;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine("讀 ScreenOverview 失敗：" + ex.Message);
            return;
        }

        foreach (XElement attr in doc.Descendants().Where(e => e.Name.LocalName == "AttributeList").Elements())
        {
            Console.WriteLine("ScreenOverview attr " + attr.Name.LocalName + "=" + attr.Value);
        }

        bool changed = false;
        XElement rootAttr = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
        if (rootAttr != null)
        {
            XElement node = rootAttr.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "StartScreen" ||
                e.Name.LocalName == "StartupScreen" ||
                e.Name.LocalName == "InitialScreen");
            if (node != null)
            {
                Console.WriteLine("ScreenOverview " + node.Name.LocalName + "：" + node.Value + " → " + HomeScreenName);
                node.Value = HomeScreenName;
                changed = true;
            }
            else
            {
                rootAttr.Add(new XElement("StartScreen", HomeScreenName));
                Console.WriteLine("ScreenOverview 補上 StartScreen=" + HomeScreenName);
                changed = true;
            }
        }

        if (changed)
        {
            doc.Save(path);
        }

        try
        {
            hmi.ImportScreenOverview(file, ImportOptions.Override);
            Console.WriteLine("已 ImportScreenOverview。");
        }
        catch (Exception ex)
        {
            Console.WriteLine("ImportScreenOverview 失敗：" + DescribeException(ex));
        }
    }

    private static void RemoveExistingRealTags(XElement objectList)
    {
        List<XElement> doomed = objectList.Elements()
            .Where(element =>
                element.Name.LocalName == "Hmi.Tag.Tag" &&
                IsGeneratedRealName(GetAttributeValue(element, "Name")))
            .ToList();

        foreach (XElement element in doomed)
        {
            element.Remove();
        }
    }

    private static void RemoveGeneratedHomeItems(XElement objectList)
    {
        List<XElement> doomed = objectList.Elements()
            .Where(element =>
            {
                string name = GetAttributeValue(element, "Name") ?? GetAttributeValue(element, "ObjectName") ?? string.Empty;
                return name == "Title_Home" ||
                    name.StartsWith("Label_Real", StringComparison.Ordinal) ||
                    name.StartsWith("IOField_Real", StringComparison.Ordinal);
            })
            .ToList();

        foreach (XElement element in doomed)
        {
            element.Remove();
        }
    }

    private static bool IsGeneratedRealName(string name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith("Real", StringComparison.Ordinal))
        {
            return false;
        }

        int number;
        return int.TryParse(name.Substring(4), out number) && number >= 1 && number <= RealMemberCount;
    }

    private static IEnumerable<DeviceItem> WalkDeviceItems(Device device)
    {
        foreach (DeviceItem item in device.DeviceItems)
        {
            foreach (DeviceItem child in WalkDeviceItems(item))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<DeviceItem> WalkDeviceItems(DeviceItem item)
    {
        yield return item;
        foreach (DeviceItem child in item.DeviceItems)
        {
            foreach (DeviceItem nested in WalkDeviceItems(child))
            {
                yield return nested;
            }
        }
    }

    private static string GetExportFolder()
    {
        string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HmiExport");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static XElement FindElement(XDocument document, string localName)
    {
        return document.Descendants().FirstOrDefault(element => element.Name.LocalName == localName);
    }

    private static XElement GetOrCreateObjectList(XElement parent)
    {
        XElement objectList = parent.Elements().FirstOrDefault(element => element.Name.LocalName == "ObjectList");
        if (objectList == null)
        {
            objectList = new XElement("ObjectList");
            parent.Add(objectList);
        }

        return objectList;
    }

    private static void RemoveAttribute(XElement parent, string name)
    {
        XElement attributeList = parent.Elements().FirstOrDefault(element => element.Name.LocalName == "AttributeList");
        if (attributeList == null)
        {
            return;
        }

        XElement attribute = attributeList.Elements().FirstOrDefault(element => element.Name.LocalName == name);
        if (attribute != null)
        {
            attribute.Remove();
        }
    }

    private static void SetOrCreateAttribute(XElement parent, string name, string value)
    {
        XElement attributeList = parent.Elements().FirstOrDefault(element => element.Name.LocalName == "AttributeList");
        if (attributeList == null)
        {
            attributeList = new XElement("AttributeList");
            parent.AddFirst(attributeList);
        }

        XElement attribute = attributeList.Elements().FirstOrDefault(element => element.Name.LocalName == name);
        if (attribute == null)
        {
            attributeList.Add(new XElement(name, value));
        }
        else
        {
            attribute.Value = value;
        }
    }

    private static string GetAttributeValue(XElement parent, string name)
    {
        XElement attributeList = parent.Elements().FirstOrDefault(element => element.Name.LocalName == "AttributeList");
        if (attributeList == null)
        {
            return null;
        }

        XElement attribute = attributeList.Elements().FirstOrDefault(element => element.Name.LocalName == name);
        return attribute == null ? null : attribute.Value;
    }

    private static int GetMaxId(XDocument document)
    {
        int max = 0;
        foreach (XElement element in document.Descendants())
        {
            XAttribute id = element.Attribute("ID");
            int value;
            if (id != null && int.TryParse(id.Value, out value) && value > max)
            {
                max = value;
            }
        }

        return max;
    }

    private static void TrySetAttribute(IEngineeringObject target, string name, object value)
    {
        try
        {
            target.SetAttribute(name, value);
            Console.WriteLine("已設定 " + name + " = " + value);
        }
        catch (Exception ex)
        {
            Console.WriteLine("略過屬性 " + name + "：" + ex.GetType().Name);
        }
    }

    private static void DumpAttributeInfos(string title, IEngineeringObject target)
    {
        if (target == null)
        {
            return;
        }

        Console.WriteLine(title + " 屬性：");
        try
        {
            foreach (EngineeringAttributeInfo info in target.GetAttributeInfos())
            {
                object value = null;
                try
                {
                    value = target.GetAttribute(info.Name);
                }
                catch
                {
                    value = "<讀取失敗>";
                }

                Console.WriteLine("  " + info.Name + " = " + (value == null ? "<null>" : value.ToString()));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  無法列出屬性：" + ex.Message);
        }
    }

    private static void DumpAllAttributes(string title, IEngineeringObject target)
    {
        Console.WriteLine(title + "：");
        try
        {
            foreach (KeyValuePair<string, object> pair in V19Api.ReadWriteAttributes(target))
            {
                Console.WriteLine("  " + pair.Key + " = " + (pair.Value == null ? "<null>" : pair.Value.ToString()));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  無法列出：" + ex.Message);
        }
    }

    private static void DumpCompositionInfos(string title, IEngineeringObject target)
    {
        Console.WriteLine(title + " compositions：");
        try
        {
            foreach (EngineeringCompositionInfo info in target.GetCompositionInfos())
            {
                Console.WriteLine("  " + info.Name + " / " + V19Api.TypeName(info));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  無法列出：" + ex.Message);
        }
    }

    private static void DumpInvocationInfos(string title, IEngineeringObject target)
    {
        Console.WriteLine(title + " invocations：");
        try
        {
            foreach (EngineeringInvocationInfo info in target.GetInvocationInfos())
            {
                Console.WriteLine("  " + info.Name);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  無法列出：" + ex.Message);
        }
    }

    private static IEnumerable<Screen> WalkScreens(ScreenFolder folder)
    {
        foreach (Screen screen in folder.Screens)
        {
            yield return screen;
        }

        foreach (ScreenUserFolder child in folder.Folders)
        {
            foreach (Screen screen in WalkScreens(child))
            {
                yield return screen;
            }
        }
    }

    private static void ImportHomePageFromScratch(HmiTarget hmi)
    {
        string exportFolder = GetExportFolder();
        string emptyPath = Path.Combine(exportFolder, "HomePage.empty.xml");
        File.WriteAllText(
            emptyPath,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine +
            "<Document>" + Environment.NewLine +
            "  <Engineering version=\"V19\" />" + Environment.NewLine +
            "  <Hmi.Screen.Screen ID=\"0\">" + Environment.NewLine +
            "    <AttributeList>" + Environment.NewLine +
            "      <Name>" + HomeScreenName + "</Name>" + Environment.NewLine +
            "    </AttributeList>" + Environment.NewLine +
            "  </Hmi.Screen.Screen>" + Environment.NewLine +
            "</Document>",
            new UTF8Encoding(false));

        Console.WriteLine("先匯入空白 HOME PAGE：" + emptyPath);
        hmi.ScreenFolder.Screens.Import(new FileInfo(emptyPath), ImportOptions.Override);

        Screen home = hmi.ScreenFolder.Screens.Find(HomeScreenName);
        if (home == null)
        {
            throw new InvalidOperationException("空白 HOME PAGE 匯入後仍找不到畫面。");
        }

        string exported = Path.Combine(exportFolder, "HomePage.source.xml");
        if (File.Exists(exported))
        {
            File.Delete(exported);
        }

        home.Export(new FileInfo(exported), ExportOptions.WithDefaults);
        Console.WriteLine("已匯出空白 HOME PAGE：" + exported);
        FillHomePageFromExport(hmi, exported);
    }

    private static void FillHomePageFromExport(HmiTarget hmi, string exportedPath)
    {
        XDocument document = XDocument.Load(exportedPath);
        XElement screenElement = FindElement(document, "Hmi.Screen.Screen");
        SetOrCreateAttribute(screenElement, "Name", HomeScreenName);
        XElement objectList = GetOrCreateObjectList(screenElement);
        RemoveGeneratedHomeItems(objectList);

        int nextId = GetMaxId(document) + 1;
        nextId = AddTextField(objectList, nextId, "Title_Home", 40, 16, 600, 40, "DB123 HOME PAGE", "ScreenItems");
        for (int index = 1; index <= RealMemberCount; index++)
        {
            int top = 40 + (index * 56);
            string tagName = "Real" + index;
            nextId = AddTextField(objectList, nextId, "Label_" + tagName, 40, top, 180, 36, tagName, "ScreenItems");
            nextId = AddIoField(objectList, nextId, "IOField_" + tagName, 260, top, 200, 36, tagName, "ScreenItems");
        }

        string importPath = Path.Combine(GetExportFolder(), "HomePage.import.xml");
        document.Save(importPath);
        try
        {
            hmi.ScreenFolder.Screens.Import(new FileInfo(importPath), ImportOptions.Override);
            Console.WriteLine("已把 DB123 欄位放進 HOME PAGE。");
        }
        catch (Exception ex)
        {
            Console.WriteLine("畫面物件匯入失敗，先保留空白 HOME PAGE：" + DescribeException(ex));
        }
    }

    private static string CreateHomePageXml()
    {
        StringBuilder items = new StringBuilder();
        int id = 2;
        items.Append(CreateScratchTextFieldXml(ref id, "Title_Home", 40, 16, 600, 40, "DB123 HOME PAGE"));
        for (int index = 1; index <= RealMemberCount; index++)
        {
            int top = 40 + (index * 56);
            string tagName = "Real" + index;
            items.Append(CreateScratchTextFieldXml(ref id, "Label_" + tagName, 40, top, 180, 36, tagName));
            items.Append(CreateScratchIoFieldXml(ref id, "IOField_" + tagName, 260, top, 200, 36, tagName));
        }

        return
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine +
            "<Document>" + Environment.NewLine +
            "  <Engineering version=\"V19\" />" + Environment.NewLine +
            "  <Hmi.Screen.Screen ID=\"0\">" + Environment.NewLine +
            "    <AttributeList>" + Environment.NewLine +
            "      <Name>" + HomeScreenName + "</Name>" + Environment.NewLine +
            "    </AttributeList>" + Environment.NewLine +
            "    <ObjectList>" + Environment.NewLine +
            items +
            "    </ObjectList>" + Environment.NewLine +
            "  </Hmi.Screen.Screen>" + Environment.NewLine +
            "</Document>";
    }

    private static string CreateScratchTextFieldXml(ref int id, string name, int left, int top, int width, int height, string text)
    {
        int fieldId = id++;
        int textId = id++;
        int itemId = id++;
        return
            "      <Hmi.Screen.TextField ID=\"" + fieldId + "\" CompositionName=\"ScreenItems\">" + Environment.NewLine +
            "        <AttributeList>" + Environment.NewLine +
            "          <Height>" + height + "</Height>" + Environment.NewLine +
            "          <Left>" + left + "</Left>" + Environment.NewLine +
            "          <Name>" + name + "</Name>" + Environment.NewLine +
            "          <Top>" + top + "</Top>" + Environment.NewLine +
            "          <Width>" + width + "</Width>" + Environment.NewLine +
            "        </AttributeList>" + Environment.NewLine +
            "        <ObjectList>" + Environment.NewLine +
            "          <MultilingualText ID=\"" + textId + "\" CompositionName=\"Text\">" + Environment.NewLine +
            "            <ObjectList>" + Environment.NewLine +
            "              <MultilingualTextItem ID=\"" + itemId + "\" CompositionName=\"Items\">" + Environment.NewLine +
            "                <AttributeList>" + Environment.NewLine +
            "                  <Culture>zh-CN</Culture>" + Environment.NewLine +
            "                  <Text>" + text + "</Text>" + Environment.NewLine +
            "                </AttributeList>" + Environment.NewLine +
            "              </MultilingualTextItem>" + Environment.NewLine +
            "            </ObjectList>" + Environment.NewLine +
            "          </MultilingualText>" + Environment.NewLine +
            "        </ObjectList>" + Environment.NewLine +
            "      </Hmi.Screen.TextField>" + Environment.NewLine;
    }

    private static string CreateScratchIoFieldXml(ref int id, string name, int left, int top, int width, int height, string tagName)
    {
        int fieldId = id++;
        return
            "      <Hmi.Screen.IOField ID=\"" + fieldId + "\" CompositionName=\"ScreenItems\">" + Environment.NewLine +
            "        <AttributeList>" + Environment.NewLine +
            "          <Height>" + height + "</Height>" + Environment.NewLine +
            "          <Left>" + left + "</Left>" + Environment.NewLine +
            "          <Name>" + name + "</Name>" + Environment.NewLine +
            "          <Tag>" + tagName + "</Tag>" + Environment.NewLine +
            "          <Top>" + top + "</Top>" + Environment.NewLine +
            "          <Width>" + width + "</Width>" + Environment.NewLine +
            "        </AttributeList>" + Environment.NewLine +
            "      </Hmi.Screen.IOField>" + Environment.NewLine;
    }

    private static void DumpCreationInfos(string title, IEngineeringComposition composition)
    {
        Console.WriteLine(title + " GetCreationInfos：");
        try
        {
            foreach (EngineeringCreationInfo info in composition.GetCreationInfos())
            {
                Console.WriteLine("  類型：" + V19Api.TypeName(info));
                foreach (EngineeringCreationParameterInfo parameter in info.ParameterInfos)
                {
                    Console.WriteLine("    參數：" + parameter.Name + " mandatory=" + parameter.IsMandatory);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  無法列出：" + ex.Message);
        }
    }

    private static string ReadConnectionName(IEngineeringObject created, HmiTarget hmi)
    {
#if !TIA_V19
        HwHmiConnection hmiConnection = created as HwHmiConnection;
        if (hmiConnection != null && !string.IsNullOrEmpty(hmiConnection.LocalConnectionName))
        {
            Console.WriteLine("  Valid=" + hmiConnection.IsValid + " / Type=" + hmiConnection.ConnectionType);
            return hmiConnection.LocalConnectionName;
        }

        HwConnection connection = created as HwConnection;
        if (connection != null)
        {
            Console.WriteLine("  Valid=" + connection.IsValid + " / Type=" + connection.ConnectionType);
        }
#endif

        if (hmi.Connections.Any())
        {
            return hmi.Connections.First().Name;
        }

        try
        {
            object name = created.GetAttribute("LocalConnectionName");
            if (name != null && !string.IsNullOrEmpty(name.ToString()))
            {
                return name.ToString();
            }

            name = created.GetAttribute("Name");
            if (name != null && !string.IsNullOrEmpty(name.ToString()))
            {
                return name.ToString();
            }
        }
        catch
        {
        }

        return null;
    }

    private static DeviceItem FindHmiDeviceItem(HmiTarget hmi)
    {
        IEngineeringObject current = hmi;
        while (current != null)
        {
            DeviceItem item = current as DeviceItem;
            if (item != null)
            {
                return item;
            }

            current = current.Parent;
        }

        return null;
    }

    private static void AddUnique(ICollection<DeviceItem> items, DeviceItem item)
    {
        if (item != null && !items.Contains(item))
        {
            items.Add(item);
        }
    }

    private static string ChooseHmiAddress(string plcAddress)
    {
        string[] parts = (plcAddress ?? string.Empty).Split('.');
        int last;
        if (parts.Length == 4 && int.TryParse(parts[3], out last))
        {
            int hmiLast = last == 2 ? 3 : 2;
            return parts[0] + "." + parts[1] + "." + parts[2] + "." + hmiLast;
        }

        return HmiAddress;
    }

    private static string GetAttributeString(IEngineeringObject target, string name)
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

    private static string DescribeException(Exception ex)
    {
        if (ex == null)
        {
            return "<null>";
        }

        StringBuilder text = new StringBuilder(ex.GetType().Name + "：" + ex.Message);
        EngineeringException engineering = ex as EngineeringException;
        if (engineering != null)
        {
            if (!string.IsNullOrEmpty(engineering.MessageData.Text))
            {
                text.Append(" | ").Append(engineering.MessageData.Text);
            }

            if (!string.IsNullOrEmpty(engineering.MessageData.DetailText))
            {
                text.Append(" | ").Append(engineering.MessageData.DetailText);
            }

            if (engineering.DetailMessageData != null)
            {
                foreach (ExceptionMessageData detail in engineering.DetailMessageData)
                {
                    text.Append(" | ").Append(detail.Text);
                    if (!string.IsNullOrEmpty(detail.DetailText))
                    {
                        text.Append(" / ").Append(detail.DetailText);
                    }
                }
            }
        }

        Exception inner = ex.InnerException;
        while (inner != null)
        {
            text.Append(" | ").Append(inner.GetType().Name).Append("：").Append(inner.Message);
            inner = inner.InnerException;
        }

        return text.ToString();
    }

    private sealed class EthernetEndpoint
    {
        public DeviceItem Item { get; set; }
        public NetworkInterface Network { get; set; }
        public Node Node { get; set; }
    }
}

internal static class LadBlockTools
{
    public static bool IsHmiOnlyCommand(string[] args)
    {
        return HasFlag(args, "--repair-tf-hmi") ||
               HasFlag(args, "--clear-tf-recipe") ||
               HasFlag(args, "--cleanup-tf-recipe-junk") ||
               HasFlag(args, "--compile-tf-hmi") ||
               HasFlag(args, "--compile-hmi") ||
               HasFlag(args, "--export-tf-hmi") ||
               HasFlag(args, "--fix-tf-hmi") ||
               HasFlag(args, "--import-tf-screens") ||
               HasFlag(args, "--fix-tf-names") ||
               HasFlag(args, "--rewire-hmi-plc") ||
               HasFlag(args, "--set-start-screen") ||
               HasFlag(args, "--lab-hmi-gap");
    }

    public static int? HandleProjectScope(string[] args, TiaPortal portal, Project project, List<PlcSoftware> plcSoftwares)
    {
        // HMI 修補／編譯不依賴開場全掃；需要 PLC 時各指令自己 FindPlc。
        if (HasFlag(args, "--repair-tf-hmi"))
        {
            Console.WriteLine("進入 --repair-tf-hmi");
            return StationSync.RepairTfHmi(portal, project);
        }

        if (HasFlag(args, "--clear-tf-recipe"))
        {
            Console.WriteLine("進入 --clear-tf-recipe（TF 不需要 Recipe：清 P4 + 刪 Recipe）");
            return StationSync.ClearTfRecipeAndP4(portal, project);
        }

        if (HasFlag(args, "--cleanup-tf-recipe-junk"))
        {
            Console.WriteLine("進入 --cleanup-tf-recipe-junk（刪 24137 Internal 廢物，恢復 bobbin_limit Absolute）");
            return StationSync.CleanupTfRecipeJunkTags(portal, project);
        }

        if (HasFlag(args, "--compile-tf-hmi"))
        {
            return StationSync.CompileTfHmi(portal, project);
        }

        if (HasFlag(args, "--compile-hmi"))
        {
            return StationSync.CompileAllHmi(portal, project);
        }

        if (HasFlag(args, "--set-start-screen"))
        {
            return HmiKtp1200HomeCreator.SetStartScreenOnly(project);
        }

        if (HasFlag(args, "--lab-hmi-gap"))
        {
            Console.WriteLine("進入 --lab-hmi-gap");
            return HmiKtp1200HomeCreator.RunLabHmiGap(portal, project);
        }

        if (HasFlag(args, "--export-tf-hmi") || HasFlag(args, "--fix-tf-hmi"))
        {
            return StationSync.ExportFixTfHmi(portal, project, HasFlag(args, "--fix-tf-hmi"));
        }

        if (HasFlag(args, "--import-tf-screens"))
        {
            return StationSync.ImportTfHmiScreens(portal, project);
        }

        if (HasFlag(args, "--fix-tf-names"))
        {
            return StationSync.FixTfHmiNames(portal, project);
        }

        if (HasFlag(args, "--rewire-hmi-plc"))
        {
            return StationSync.RewireHmiPlcTags(portal, project);
        }

        // 舊的 FindPlcSoftwares 只掃頂層裝置，裝置放在群組裡時會回傳空清單。
        if (plcSoftwares.Count == 0)
        {
            Console.WriteLine("HandleProjectScope：補掃全部 PLC…");
            plcSoftwares = FindAllPlcSoftwares(project);
            Console.WriteLine("HandleProjectScope：PLC=" + plcSoftwares.Count);
        }

        if (HasFlag(args, "--online-status"))
        {
            ShowOnlineStatus(
                project,
                ResolvePlc(plcSoftwares, GetValue(args, "--plc:")));
            return 0;
        }

        if (HasFlag(args, "--go-offline"))
        {
            string wantedOffline = GetValue(args, "--plc:");
            if (string.IsNullOrEmpty(wantedOffline) || wantedOffline == "*")
            {
                foreach (PlcSoftware candidate in plcSoftwares)
                {
                    SetOnlineState(project, candidate, goOnline: false);
                }
            }
            else
            {
                SetOnlineState(
                    project,
                    ResolvePlc(plcSoftwares, wantedOffline),
                    goOnline: false);
            }

            return 0;
        }

        if (HasFlag(args, "--save-project"))
        {
            project.Save();
            Console.WriteLine("專案已存檔：" + project.Name);
            return 0;
        }

        if (HasFlag(args, "--go-online"))
        {
            SetOnlineState(
                project,
                ResolvePlc(plcSoftwares, GetValue(args, "--plc:")),
                goOnline: true);
            return 0;
        }

        if (HasFlag(args, "--download-plc"))
        {
            DownloadPlc(
                project,
                ResolvePlc(plcSoftwares, GetValue(args, "--plc:")),
                GetValue(args, "--pc-interface:"));
            return 0;
        }

        string changeCpu = GetValue(args, "--change-cpu:");
        if (changeCpu != null)
        {
            string typeId = changeCpu;
            if (!typeId.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase))
            {
                typeId = "OrderNumber:" + typeId;
            }

            return ChangePlcCpu(
                project,
                plcSoftwares,
                GetValue(args, "--plc:"),
                typeId);
        }

        string renamePlc = GetValue(args, "--rename-plc:");
        if (renamePlc != null)
        {
            int eq = renamePlc.IndexOf('=');
            if (eq <= 0 || eq >= renamePlc.Length - 1)
            {
                Console.WriteLine("用法：--rename-plc:舊名稱=新名稱  例：--rename-plc:PLC_1=PLC_TCP");
                return 1;
            }

            return RenamePlc(
                project,
                plcSoftwares,
                renamePlc.Substring(0, eq),
                renamePlc.Substring(eq + 1));
        }

        string copyFrom = GetValue(args, "--copy-plc-from:");
        if (copyFrom != null)
        {
            return PlcCopier.CopyInto(
                portal,
                project,
                copyFrom,
                GetValue(args, "--source-plc:"),
                GetValue(args, "--as:") ?? "25017_PF_PLC",
                GetValue(args, "--group:") ?? "1.PLC&HMI/5.Payoff",
                GetValue(args, "--ip:") ?? "192.168.0.140",
                GetValue(args, "--pn-controller:") ?? "S7-1200 station_1");
        }

        if (HasFlag(args, "--sync-drawing-hw"))
        {
            return HardwareSync.Apply(portal, project);
        }

        if (HasFlag(args, "--continue-drawing-hw"))
        {
            return HardwareSync.ContinueDrawingHw(portal, project);
        }

        if (HasFlag(args, "--move-energy"))
        {
            return HardwareSync.MoveEnergyMeter800(portal, project);
        }

        if (HasFlag(args, "--rewire-energy-tags"))
        {
            return StationSync.RewireEnergyTags800(project);
        }

        if (HasFlag(args, "--station-20-24"))
        {
            return StationSync.Apply(portal, project);
        }

        if (HasFlag(args, "--rename-18b-rest"))
        {
            return StationSync.RenameRest(portal, project);
        }

        if (HasFlag(args, "--rename-plc-18b"))
        {
            return StationSync.RenamePlcProgram18BTo20B(project);
        }

        if (HasFlag(args, "--hide-18b"))
        {
            return StationSync.HideVisible18B(portal, project);
        }

        if (HasFlag(args, "--fix-power-loss"))
        {
            return StationSync.FixPowerLossRamp(portal, project);
        }

        if (HasFlag(args, "--rename-hmi-18b"))
        {
            return StationSync.RenameHmi18BOnly(project);
        }

        if (HasFlag(args, "--fix-20-24-compile"))
        {
            return StationSync.FixCompile(portal, project);
        }

        if (HasFlag(args, "--finish-20-24"))
        {
            return StationSync.FinishUserRequest(portal, project);
        }

        if (HasFlag(args, "--fix-ob30"))
        {
            return StationSync.FixOb30(portal, project);
        }

        if (HasFlag(args, "--align-main-tf"))
        {
            return HardwareSync.AlignMainTf(portal, project);
        }

        if (HasFlag(args, "--align-tf-c-sm"))
        {
            return HardwareSync.AlignTfCTableSm(portal, project);
        }

        if (HasFlag(args, "--align-inside-c-sm"))
        {
            return HardwareSync.AlignInsideCTableSm(portal, project);
        }

        if (HasFlag(args, "--add-24b-zigbee"))
        {
            return StationSync.Add24BZigbee(portal, project);
        }

        if (HasFlag(args, "--add-24b-watchdog"))
        {
            return StationSync.Add24BInsideWatchdog(portal, project);
        }

        if (HasFlag(args, "--wire-section4"))
        {
            return StationSync.WireSection4(portal, project);
        }

        if (HasFlag(args, "--add-24b-cyclic"))
        {
            return StationSync.Add24BToCyclic(portal, project);
        }

        if (HasFlag(args, "--mirror-pos-valve2"))
        {
            return StationSync.MirrorPosValve2(portal, project);
        }

        if (HasFlag(args, "--wire-tower-lamps"))
        {
            return StationSync.WireTowerLamps(portal, project);
        }

        if (HasFlag(args, "--wire-loader-stop-air"))
        {
            return StationSync.WireLoaderStopAndAir(portal, project);
        }

        if (HasFlag(args, "--wire-pretwist"))
        {
            return StationSync.WirePreTwist(portal, project);
        }

        if (HasFlag(args, "--unwire-6b-pretwist"))
        {
            return StationSync.Unwire6BPreTwist(portal, project);
        }

        if (HasFlag(args, "--wire-pretwist-softgear"))
        {
            return StationSync.WirePreTwistSoftGear(portal, project);
        }

        if (HasFlag(args, "--wire-c-extra-events"))
        {
            return StationSync.WireCExtraEvents(portal, project);
        }

        if (HasFlag(args, "--wire-payoff-takeup-run"))
        {
            return StationSync.WirePayoffTakeupRun(portal, project);
        }

        if (HasFlag(args, "--sync-inside-io"))
        {
            return StationSync.SyncInsideIo(portal, project);
        }

        if (HasFlag(args, "--fix-inside-sections"))
        {
            return StationSync.FixInsideSections(portal, project);
        }

        if (HasFlag(args, "--wire-inside-s4"))
        {
            return StationSync.WireInsideS4(portal, project);
        }

        if (HasFlag(args, "--wire-home-angle"))
        {
            return StationSync.WireHomeAngle(portal, project);
        }

        if (HasFlag(args, "--wire-home-hw-interrupt"))
        {
            return StationSync.WireHomeHwInterrupt(portal, project);
        }

        if (HasFlag(args, "--wire-home-6b"))
        {
            return StationSync.WireHome6B(portal, project);
        }

        if (HasFlag(args, "--wire-main-tcp"))
        {
            return StationSync.WireMainTcp(portal, project);
        }

        if (HasFlag(args, "--wire-inside-rtu"))
        {
            return StationSync.WireInsideRtu(portal, project);
        }

        if (HasFlag(args, "--strip-inside-old-rs485"))
        {
            return StationSync.StripInsideOldRs485(portal, project);
        }

        if (HasFlag(args, "--strip-inside-pico"))
        {
            return StationSync.StripInsidePico(portal, project);
        }

        if (HasFlag(args, "--remap-inside-section-id"))
        {
            return StationSync.RemapInsideSectionId(portal, project);
        }

        if (HasFlag(args, "--strip-inside-zigbee"))
        {
            return StationSync.StripInsideZigbee(portal, project);
        }

        if (HasFlag(args, "--remap-inside-unload-snr"))
        {
            return StationSync.RemapInsideUnloadSnr(portal, project);
        }

        if (HasFlag(args, "--fix-inside-leftover"))
        {
            return StationSync.FixInsideLeftover(portal, project);
        }

        if (HasFlag(args, "--map-inside-safety-bar"))
        {
            return StationSync.MapInsideSafetyBar(portal, project);
        }

        if (HasFlag(args, "--wire-inside-lock"))
        {
            return StationSync.WireInsideLock(portal, project);
        }

        if (HasFlag(args, "--strip-inside-line-io"))
        {
            return StationSync.StripInsideLineIo(portal, project);
        }

        if (HasFlag(args, "--strip-inside-encoder"))
        {
            return StationSync.StripInsideEncoder(portal, project);
        }

        if (HasFlag(args, "--strip-inside-run-bits"))
        {
            return StationSync.StripInsideRunBits(portal, project);
        }

        if (HasFlag(args, "--strip-inside-s4-3face"))
        {
            return StationSync.StripInsideThreeFaceS4(portal, project);
        }

        if (HasFlag(args, "--fix-inside-audit"))
        {
            return StationSync.FixInsideAudit(portal, project);
        }

        if (HasFlag(args, "--remap-main-zigbee"))
        {
            return StationSync.RemapMainZigbee(portal, project);
        }

        if (HasFlag(args, "--strip-main-zigbee-drop"))
        {
            return StationSync.StripMainZigbeeDrop(portal, project);
        }

        if (HasFlag(args, "--strip-main-zigbee-left"))
        {
            return StationSync.StripMainZigbeeLeft(portal, project);
        }

        if (HasFlag(args, "--strip-hmi-zigbee"))
        {
            return StationSync.StripHmiZigbee(portal, project);
        }

        if (HasFlag(args, "--repair-tf-hmi"))
        {
            return StationSync.RepairTfHmi(portal, project);
        }

        if (HasFlag(args, "--clear-tf-recipe"))
        {
            return StationSync.ClearTfRecipeAndP4(portal, project);
        }

        if (HasFlag(args, "--cleanup-tf-recipe-junk"))
        {
            return StationSync.CleanupTfRecipeJunkTags(portal, project);
        }

        if (HasFlag(args, "--compile-tf-hmi"))
        {
            return StationSync.CompileTfHmi(portal, project);
        }

        if (HasFlag(args, "--compile-hmi"))
        {
            return StationSync.CompileAllHmi(portal, project);
        }

        if (HasFlag(args, "--rewire-hmi-plc"))
        {
            return StationSync.RewireHmiPlcTags(portal, project);
        }

        if (HasFlag(args, "--export-tf-hmi") || HasFlag(args, "--fix-tf-hmi"))
        {
            return StationSync.ExportFixTfHmi(portal, project, HasFlag(args, "--fix-tf-hmi"));
        }

        if (HasFlag(args, "--import-tf-screens"))
        {
            return StationSync.ImportTfHmiScreens(portal, project);
        }

        if (HasFlag(args, "--fix-tf-names"))
        {
            return StationSync.FixTfHmiNames(portal, project);
        }

        if (HasFlag(args, "--organize-block-folders"))
        {
            return StationSync.OrganizeBlockFolders(portal, project);
        }

        if (HasFlag(args, "--wire-rotor-sync"))
        {
            int plc = StationSync.WireRotorSync(portal, project);
            if (plc != 0)
            {
                return plc;
            }

            return Hmi24BEvents.WirePitch24B(portal, project);
        }

        if (HasFlag(args, "--wire-hmi-pitch-24b"))
        {
            return Hmi24BEvents.WirePitch24B(portal, project);
        }

        if (HasFlag(args, "--set-home-filter"))
        {
            bool ok = HardwareSync.TrySetHomeInputFilter(project);
            if (ok)
            {
                project.Save();
                Console.WriteLine("已存（歸零近接輸入濾波）。");
            }

            return ok ? 0 : 2;
        }

        if (HasFlag(args, "--complete-tf"))
        {
            return StationSync.CompleteTf(portal, project);
        }

        if (HasFlag(args, "--wire-tf-tension"))
        {
            return StationSync.WireTfTension(portal, project);
        }

        if (HasFlag(args, "--extend-20b-events"))
        {
            return StationSync.Extend20BEvents(portal, project);
        }

        if (HasFlag(args, "--extend-24b-events"))
        {
            return StationSync.Extend24BEvents(portal, project);
        }

        if (HasFlag(args, "--show-24b-hmi"))
        {
            return Hmi24BEvents.ShowOnOpHmi(portal, project);
        }

        if (HasFlag(args, "--ensure-aerr-lw") || GetValue(args, "--ensure-aerr-lw:") != null)
        {
            int extra = 15;
            string raw = GetValue(args, "--ensure-aerr-lw:");
            if (raw != null)
            {
                int parsed;
                if (int.TryParse(raw, out parsed))
                {
                    extra = parsed;
                }
            }

            return Hmi24BEvents.EnsureOpLwUpTo(portal, project, extra);
        }

        if (HasFlag(args, "--probe-hmi-alarms"))
        {
            return Hmi24BEvents.ProbeAlarms(portal, project);
        }

        if (HasFlag(args, "--update-rio-im"))
        {
            return HardwareSync.UpdateRioImAu02(portal, project);
        }

        if (HasFlag(args, "--probe-drive"))
        {
            return HardwareSync.ProbeDrive(
                portal,
                project,
                GetValue(args, "--drive:") ?? "25017_24B_Rotor_Drive");
        }

        if (HasFlag(args, "--dump-main-di"))
        {
            return HardwareSync.DumpCpuDi(project, GetValue(args, "--plc:") ?? "25017_Main_PLC");
        }

        if (HasFlag(args, "--layout-network"))
        {
            return HardwareSync.LayoutNetwork(project);
        }

        string showNetwork = GetValue(args, "--show-network:");
        if (showNetwork != null)
        {
            return HardwareSync.ShowInNetwork(project, showNetwork);
        }

        if (HasFlag(args, "--fix-drawing-hw"))
        {
            return HardwareSync.FixIo(portal, project);
        }

        if (HasFlag(args, "--probe-drawing-hw"))
        {
            return HardwareSync.ProbeIo(portal, project);
        }

        if (HasFlag(args, "--move-pf-pn"))
        {
            return HardwareSync.MovePfPn(portal, project);
        }

        if (HasFlag(args, "--probe-pf-pn"))
        {
            return HardwareSync.ProbePfPn(portal, project);
        }

        if (HasFlag(args, "--overview-addresses"))
        {
            return HardwareSync.OverviewAddresses(
                portal,
                project,
                GetValue(args, "--plc:") ?? "25017_Main_PLC");
        }

        if (HasFlag(args, "--copy-op2-hmi"))
        {
            return HardwareSync.CopyOp2Hmi(portal, project);
        }

        if (HasFlag(args, "--align-drawing-hmi"))
        {
            return HardwareSync.AlignDrawingHmi(portal, project);
        }

        if (HasFlag(args, "--list-devices"))
        {
            ListDevices(project, plcSoftwares);
            return 0;
        }

        if (HasFlag(args, "--unused-io"))
        {
            DumpUnusedIo(
                plcSoftwares,
                GetValue(args, "--only-plc:") ?? GetValue(args, "--plc:"),
                GetValue(args, "--to:"));
            return 0;
        }

        if (HasFlag(args, "--export-tags"))
        {
            string only = GetValue(args, "--only-plc:") ?? GetValue(args, "--plc:");
            string root = Path.Combine(GetExportFolder(), "Templates", Sanitize(project.Name));
            ExportStats stats = new ExportStats { Root = root };
            foreach (PlcSoftware plc in plcSoftwares)
            {
                if (only != null && !string.Equals(plc.Name, only, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string folder = Path.Combine(root, "PLC_" + Sanitize(plc.Name));
                Console.WriteLine("匯出變數表：" + plc.Name);
                ExportPlcTagTables(plc.TagTableGroup, folder, stats);
            }

            Console.WriteLine("變數表匯出完成：成功 " + stats.Ok + "、失敗 " + stats.Failed);
            return 0;
        }

        if (HasFlag(args, "--compile-all"))
        {
            CompileAll(project, plcSoftwares);
            return 0;
        }

        if (HasFlag(args, "--dump-hardware"))
        {
            DumpHardware(project);
            return 0;
        }

        string hardwarePlan = GetValue(args, "--build-hardware:");
        if (hardwarePlan != null)
        {
            HardwareBuilder.Build(
                project,
                hardwarePlan,
                GetValue(args, "--stage:") ?? "groups",
                GetValue(args, "--hmi-type:"));
            return 0;
        }

        if (HasFlag(args, "--save"))
        {
            project.Save();
            Console.WriteLine("專案已儲存：" + project.Name);
            return 0;
        }

        string renameText = GetValue(args, "--rename-text:");
        if (renameText != null)
        {
            int split = renameText.IndexOf('=');
            if (split <= 0 || split >= renameText.Length - 1)
            {
                Console.WriteLine("用法：--rename-text:舊字串=新字串  例：--rename-text:24137=25017");
                return 1;
            }

            RenameProjectText(project, renameText.Substring(0, split), renameText.Substring(split + 1));
            return 0;
        }

        string treeRoot = GetValue(args, "--block-tree:");
        if (treeRoot != null)
        {
            DumpSoftwareTree(plcSoftwares, treeRoot);
            return 0;
        }

        string importRoot = GetValue(args, "--import-all:");
        if (importRoot != null)
        {
            ImportAll(project, plcSoftwares, importRoot, GetValue(args, "--only-plc:"));
            return 0;
        }

        if (HasFlag(args, "--clear-devices"))
        {
            ClearDevices(project);
            return 0;
        }

        string verifyPlan = GetValue(args, "--verify-catalog:");
        if (verifyPlan != null)
        {
            VerifyAgainstCatalog(project, verifyPlan);
            return 0;
        }

        string catalogFilter = GetValue(args, "--catalog:");
        if (catalogFilter != null)
        {
            SearchCatalog(project, catalogFilter);
            return 0;
        }

        string plugTarget = GetValue(args, "--plug-locations:");
        if (plugTarget != null)
        {
            ShowPlugLocations(project, plugTarget);
            return 0;
        }

        string ifaceTarget = GetValue(args, "--interface-attrs:");
        if (ifaceTarget != null)
        {
            DumpInterfaceAttributes(project, ifaceTarget);
            return 0;
        }

        string probeName = GetValue(args, "--probe-device:");
        if (probeName != null)
        {
            ProbeDevice(project, probeName);
            return 0;
        }

        string screenName = GetValue(args, "--export-screen:");
        if (screenName != null)
        {
            ExportScreen(project, screenName);
            return 0;
        }

        string screenImportPath = GetValue(args, "--import-screen:");
        if (screenImportPath != null)
        {
            ImportScreen(project, screenImportPath);
            return 0;
        }

        string renameScreen = GetValue(args, "--rename-screen:");
        if (renameScreen != null)
        {
            return RenameHmiScreen(project, renameScreen);
        }

        if (HasFlag(args, "--export-all"))
        {
            ExportAll(project, plcSoftwares);
            return 0;
        }

        if (HasFlag(args, "--export-types"))
        {
            ExportTypesOnly(project, plcSoftwares, GetValue(args, "--to:") ??
                Path.Combine(GetExportFolder(), "Templates", Sanitize(project.Name)));
            return 0;
        }

        if (HasFlag(args, "--configure-technology"))
        {
            string templateRoot = GetValue(args, "--from:") ??
                Path.Combine(GetExportFolder(), "Templates", "23019KP_B5_TCP_V21");
            TechnologyBuilder.Configure(project, plcSoftwares, templateRoot);
            return 0;
        }

        if (HasFlag(args, "--dump-tech-config"))
        {
            string path = GetValue(args, "--to:") ??
                Path.Combine(GetExportFolder(), "tech-config.txt");
            TechnologyBuilder.DumpConfig(plcSoftwares, path, GetValue(args, "--plc:"));
            return 0;
        }

        // Block exports carry network titles as multilingual text; importing one
        // fails unless the project already knows that culture.
        string addLanguage = GetValue(args, "--add-language:");
        if (addLanguage != null)
        {
            LanguageSettings settings = project.LanguageSettings;
            Console.WriteLine("目前語言：" + string.Join(", ",
                settings.ActiveLanguages.Select(item => item.Culture.Name).ToArray()));

            Language language = settings.Languages.Find(
                new System.Globalization.CultureInfo(addLanguage));
            if (language == null)
            {
                Console.WriteLine("目錄裡沒有這個語言：" + addLanguage);
                return 1;
            }

            settings.ActiveLanguages.Add(language);
            project.Save();
            Console.WriteLine("已加入語言：" + addLanguage);
            return 0;
        }

        string addPlc = GetValue(args, "--add-plc:");
        if (addPlc != null)
        {
            string type = GetValue(args, "--type:") ?? "OrderNumber:6ES7 510-1DJ01-0AB0/V2.9";
            try
            {
                Device made = project.Devices.CreateWithItem(type, addPlc, addPlc);
                Console.WriteLine("已建立 PLC：" + made.Name + "（" + type + "）");
                project.Save();
            }
            catch (Exception ex)
            {
                Console.WriteLine("建立失敗：" + ex.Message);
                return 1;
            }

            return 0;
        }

        string listTags = GetValue(args, "--list-tags:");
        if (listTags != null)
        {
            TechnologyBuilder.ListTags(
                ResolvePlc(plcSoftwares, GetValue(args, "--plc:")),
                listTags == "*" ? null : listTags);
            return 0;
        }

        if (HasFlag(args, "--list-system-constants"))
        {
            ListSystemConstants(ResolvePlc(plcSoftwares, GetValue(args, "--plc:")));
            return 0;
        }

        string materializeLibraryDb = GetValue(args, "--materialize-library-db:");
        if (materializeLibraryDb != null)
        {
            string[] cells = materializeLibraryDb.Split(new[] { '=' }, 2);
            if (cells.Length != 2 || string.IsNullOrWhiteSpace(cells[0]) || string.IsNullOrWhiteSpace(cells[1]))
            {
                throw new ArgumentException("--materialize-library-db: 格式為 <DB名稱>=<程式庫FB名稱>。");
            }

            InsidePlcRepair.MaterializeLibraryInstanceDb(
                project,
                ResolvePlc(plcSoftwares, GetValue(args, "--plc:")),
                cells[0].Trim(),
                cells[1].Trim());
            return 0;
        }

        string createInstanceDb = GetValue(args, "--create-instance-db:");
        if (createInstanceDb != null)
        {
            string[] cells = createInstanceDb.Split(new[] { '=' }, 2);
            if (cells.Length != 2 || string.IsNullOrWhiteSpace(cells[0]) || string.IsNullOrWhiteSpace(cells[1]))
            {
                throw new ArgumentException("--create-instance-db: 格式為 <DB名稱>=<FB名稱>。");
            }

            CreateInstanceDb(
                project,
                ResolvePlc(plcSoftwares, GetValue(args, "--plc:")),
                cells[0].Trim(),
                cells[1].Trim());
            return 0;
        }

        string renameTag = GetValue(args, "--rename-tag:");
        if (renameTag != null)
        {
            string[] cells = renameTag.Split(new[] { '=' }, 2);
            if (TechnologyBuilder.RenameTag(
                    ResolvePlc(plcSoftwares, GetValue(args, "--plc:")),
                    cells[0].Trim(),
                    cells[1].Trim()))
            {
                project.Save();
            }

            return 0;
        }

        string deleteTag = GetValue(args, "--delete-tag:");
        if (deleteTag != null)
        {
            if (TechnologyBuilder.DeleteTag(
                    ResolvePlc(plcSoftwares, GetValue(args, "--plc:")),
                    deleteTag))
            {
                project.Save();
            }

            return 0;
        }

        string dumpModule = GetValue(args, "--dump-module-params:");
        if (dumpModule != null)
        {
            TechnologyBuilder.DumpModuleParameters(project, dumpModule, GetValue(args, "--filter:"));
            return 0;
        }

        string setModule = GetValue(args, "--set-module-param:");
        if (setModule != null)
        {
            string[] cells = setModule.Split(new[] { '=' }, 2);
            TechnologyBuilder.SetModuleParameter(
                project,
                GetValue(args, "--module:"),
                cells[0].Trim(),
                cells.Length > 1 ? cells[1].Trim() : string.Empty);
            project.Save();
            return 0;
        }

        string applyTechXml = GetValue(args, "--apply-tech-xml:");
        if (applyTechXml != null)
        {
            if (!Path.IsPathRooted(applyTechXml))
            {
                applyTechXml = Path.Combine(GetExportFolder(), applyTechXml);
            }

            TechXmlPatcher.Apply(
                project,
                plcSoftwares,
                applyTechXml,
                GetValue(args, "--plc:"),
                Path.Combine(GetExportFolder(), "TechXml"));
            return 0;
        }

        string recreateAxes = GetValue(args, "--recreate-axes:");
        if (recreateAxes != null)
        {
            if (!Path.IsPathRooted(recreateAxes))
            {
                recreateAxes = Path.Combine(GetExportFolder(), recreateAxes);
            }

            TechnologyBuilder.RecreateAxes(project, plcSoftwares, recreateAxes, GetValue(args, "--plc:"));
            return 0;
        }

        string applyTechConfig = GetValue(args, "--apply-tech-config:");
        if (applyTechConfig != null)
        {
            if (!Path.IsPathRooted(applyTechConfig))
            {
                applyTechConfig = Path.Combine(GetExportFolder(), applyTechConfig);
            }

            TechnologyBuilder.ApplyConfig(project, plcSoftwares, applyTechConfig, GetValue(args, "--plc:"));
            return 0;
        }

        string techParamName = GetValue(args, "--tech-params:");
        if (techParamName != null)
        {
            PlcSoftware plc = ResolvePlc(plcSoftwares, GetValue(args, "--plc:"));
            TechnologyBuilder.DumpParameters(plc, techParamName);
            return 0;
        }

        string exportTechName = GetValue(args, "--export-technology:");
        if (exportTechName != null)
        {
            PlcSoftware plc = ResolvePlc(plcSoftwares, GetValue(args, "--plc:"));
            string path = GetValue(args, "--to:") ??
                Path.Combine(GetExportFolder(), exportTechName + ".technology.xml");
            TechnologyBuilder.ExportTechnology(plc, exportTechName, path);
            return 0;
        }

        if (HasFlag(args, "--sync-hsc"))
        {
            string templateRoot = GetValue(args, "--from:") ??
                Path.Combine(GetExportFolder(), "Templates", "23019KP_B5_TCP_V21");
            ProjectRepair.SyncHscOnly(project, plcSoftwares, templateRoot);
            project.Save();
            return 0;
        }

        if (HasFlag(args, "--dump-technology"))
        {
            string techPath = GetValue(args, "--to:") ??
                Path.Combine(GetExportFolder(), "Templates", Sanitize(project.Name), "technology-manifest.txt");
            TechnologyBuilder.Dump(plcSoftwares, techPath);
            return 0;
        }

        if (HasFlag(args, "--build-technology"))
        {
            string templateRoot = GetValue(args, "--from:") ??
                Path.Combine(GetExportFolder(), "Templates", Sanitize(project.Name));
            TechnologyBuilder.Build(project, plcSoftwares, templateRoot);
            return 0;
        }

        if (HasFlag(args, "--repair-inside"))
        {
            string templateRoot = GetValue(args, "--from:") ??
                Path.Combine(GetExportFolder(), "Templates", "23019KP_B5_TCP_V21");
            InsidePlcRepair.Run(project, plcSoftwares, templateRoot);
            return 0;
        }

        if (HasFlag(args, "--repair-tf"))
        {
            string templateRoot = GetValue(args, "--from:") ??
                Path.Combine(GetExportFolder(), "Templates", "23019KP_B5_TCP_V21");
            TfPlcRepair.Run(project, plcSoftwares, templateRoot);
            return 0;
        }

        if (HasFlag(args, "--repair-main"))
        {
            string templateRoot = GetValue(args, "--from:") ??
                Path.Combine(GetExportFolder(), "Templates", "23019KP_B5_TCP_V21");
            MainPlcRepair.Run(project, plcSoftwares, templateRoot);
            return 0;
        }

        if (HasFlag(args, "--repair-project"))
        {
            string repairRoot = GetValue(args, "--from:") ??
                Path.Combine(GetExportFolder(), "Templates", Sanitize(project.Name));
            ProjectRepair.Run(project, plcSoftwares, repairRoot);
            return 0;
        }

        if (HasFlag(args, "--ensure-languages"))
        {
            EnsureProjectLanguages(project, "en-US", "zh-CN", "zh-TW", "id-ID");
            project.Save();
            Console.WriteLine("專案語言已更新並儲存。");
            return 0;
        }

        string connectionImportPath = GetValue(args, "--import-connection:");
        if (connectionImportPath != null)
        {
            ImportConnection(project, connectionImportPath);
            return 0;
        }

        string connectionDeleteName = GetValue(args, "--delete-connection:");
        if (connectionDeleteName != null)
        {
            DeleteConnection(project, connectionDeleteName);
            return 0;
        }

        return null;
    }

    private static void ImportConnection(Project project, string path)
    {
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(GetExportFolder(), path);
        }

        HmiTarget hmi = FindAnyHmiTarget(project);
        if (hmi == null)
        {
            throw new InvalidOperationException("專案裡找不到 HMI 裝置。");
        }

        Console.WriteLine("匯入前的連線：" + string.Join("、", hmi.Connections.Select(c => c.Name).ToArray()));
        Console.WriteLine("正在匯入連線 XML：" + path);
        hmi.Connections.Import(new FileInfo(path), ImportOptions.Override);

        Console.WriteLine("匯入後的連線：");
        foreach (HmiSoftConnection connection in hmi.Connections)
        {
            Console.WriteLine("  " + connection.Name +
                "／Driver=" + ReadAttribute(connection, "Driver") +
                "／Partner=" + ReadAttribute(connection, "PartnerIdForRuntime"));
        }

        project.Save();
        Console.WriteLine("專案已存檔。");
    }

    private static void DeleteConnection(Project project, string connectionName)
    {
        HmiTarget hmi = FindAnyHmiTarget(project);
        if (hmi == null)
        {
            throw new InvalidOperationException("專案裡找不到 HMI 裝置。");
        }

        HmiSoftConnection target = hmi.Connections
            .FirstOrDefault(c => string.Equals(c.Name, connectionName, StringComparison.OrdinalIgnoreCase));

        if (target == null)
        {
            Console.WriteLine("找不到連線：" + connectionName);
            return;
        }

        target.Delete();
        project.Save();
        Console.WriteLine("已刪除連線：" + connectionName);
    }

    private static string ReadAttribute(IEngineeringObject item, string name)
    {
        try
        {
            object value = item.GetAttribute(name);
            return value == null ? "<null>" : value.ToString();
        }
        catch
        {
            return "<讀不到>";
        }
    }

    public static int? Handle(string[] args, Project project, PlcSoftware plc)
    {
        string xrefNames = GetValue(args, "--xref:");
        if (xrefNames != null || HasFlag(args, "--xref"))
        {
            DumpCrossReferences(plc, xrefNames, GetValue(args, "--to:"));
            return 0;
        }

        if (HasFlag(args, "--list-blocks"))
        {
            ListBlocks(plc);
            return 0;
        }

        string exportName = GetValue(args, "--export-block:");
        if (exportName != null)
        {
            ExportBlock(plc, exportName, GetValue(args, "--to:"));
            return 0;
        }

        string importPath = GetValue(args, "--import-block:");
        if (importPath != null)
        {
            ImportBlock(plc, project, importPath);
            return 0;
        }

        string importType = GetValue(args, "--import-type:");
        if (importType != null)
        {
            ImportPlcType(plc, project, importType);
            return 0;
        }

        string sclDir = GetValue(args, "--import-scl-dir:");
        if (sclDir != null)
        {
            ImportSclDirectory(plc, project, sclDir);
            return 0;
        }

        string sclPath = GetValue(args, "--import-scl:");
        if (sclPath != null)
        {
            ImportSclSource(plc, project, sclPath, saveProject: true);
            return 0;
        }

        string setBlockNumber = GetValue(args, "--set-block-number:");
        if (setBlockNumber != null)
        {
            int eq = setBlockNumber.IndexOf('=');
            if (eq <= 0 || eq >= setBlockNumber.Length - 1)
            {
                Console.WriteLine("用法：--set-block-number:區塊名=編號  例：--set-block-number:for_dsp_plc_zone1=19");
                return 1;
            }

            return SetBlockNumber(
                plc,
                project,
                setBlockNumber.Substring(0, eq),
                setBlockNumber.Substring(eq + 1));
        }

        string deleteName = GetValue(args, "--delete-block:");
        if (deleteName != null)
        {
            DeleteBlock(plc, project, deleteName);
            return 0;
        }


        if (HasFlag(args, "--compile"))
        {
            Compile(plc);
            return 0;
        }

        return null;
    }

    private static readonly string[] DefaultOp1XrefTags =
    {
        "I_PB_Start", "I_PB_Stop", "I_PB_Reset", "I_PB_Jog", "I_PB_Bell",
        "I_PB_Takeup_Start", "I_PB_Takeup_Stop", "I_PB_EStop",
        "Q_Lamp_Run", "Q_Lamp_Stop", "Q_Lamp_Alarm",
        "Q_Lamp_Takeup_Run", "Q_Lamp_Takeup_Stop", "Q_Bell"
    };

    private static bool IsIoTableName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return string.Equals(name, "Hardware", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("RIO_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIoAddress(string address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return false;
        }

        string a = address.Trim().ToUpperInvariant();
        return a.StartsWith("%I", StringComparison.Ordinal) || a.StartsWith("%Q", StringComparison.Ordinal);
    }

    private static bool IsProgramTypeName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return false;
        }

        return typeName.IndexOf("LAD", StringComparison.OrdinalIgnoreCase) >= 0
            || typeName.IndexOf("SCL", StringComparison.OrdinalIgnoreCase) >= 0
            || typeName.IndexOf("Function block", StringComparison.OrdinalIgnoreCase) >= 0
            || typeName.IndexOf("Organization block", StringComparison.OrdinalIgnoreCase) >= 0
            || typeName.IndexOf("Data block", StringComparison.OrdinalIgnoreCase) >= 0
            || typeName.IndexOf("Function", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsHmiTypeName(string typeName, string path)
    {
        if (!string.IsNullOrEmpty(typeName)
            && (typeName.IndexOf("HMI", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("Screen", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("WinCC", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return true;
        }

        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string norm = path.Replace('/', '\\');
        return norm.IndexOf("\\HMI", StringComparison.OrdinalIgnoreCase) >= 0
            || norm.IndexOf("\\Screens", StringComparison.OrdinalIgnoreCase) >= 0
            || norm.IndexOf("WinCC", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void DumpUnusedIo(List<PlcSoftware> plcSoftwares, string onlyPlc, string destination)
    {
        List<string> lines = new List<string>();
        lines.Add("有 IO Tag、程式沒用到（TIA 交叉引用）");
        lines.Add("表：Hardware / RIO_*。Events / PN / Default 不在這份。");
        lines.Add("產生：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        lines.Add("");

        foreach (PlcSoftware plc in plcSoftwares)
        {
            if (onlyPlc != null && !string.Equals(plc.Name, onlyPlc, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Console.WriteLine(new string('=', 60));
            Console.WriteLine("PLC：" + plc.Name);
            lines.Add("## " + plc.Name);
            int unused = 0;
            int hmiOnly = 0;
            int used = 0;

            foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
            {
                if (!IsIoTableName(table.Name))
                {
                    continue;
                }

                List<PlcTag> tags = new List<PlcTag>();
                foreach (PlcTag tag in table.Tags)
                {
                    if (IsIoAddress(ReadAttribute(tag, "LogicalAddress")))
                    {
                        tags.Add(tag);
                    }
                }

                tags.Sort((a, b) => string.Compare(
                    ReadAttribute(a, "LogicalAddress"),
                    ReadAttribute(b, "LogicalAddress"),
                    StringComparison.OrdinalIgnoreCase));

                foreach (PlcTag tag in tags)
                {
                    string addr = ReadAttribute(tag, "LogicalAddress");
                    CrossReferenceService service = tag.GetService<CrossReferenceService>();
                    List<string> programHits = new List<string>();
                    List<string> hmiHits = new List<string>();
                    if (service != null)
                    {
                        CollectIoHits(service.GetCrossReferences(CrossReferenceFilter.AllObjects).Sources, programHits, hmiHits);
                    }

                    if (programHits.Count > 0)
                    {
                        used++;
                        continue;
                    }

                    if (hmiHits.Count > 0)
                    {
                        hmiOnly++;
                        string row = addr.PadRight(10) + " " + tag.Name.PadRight(36) + "  " + table.Name + "  僅 HMI";
                        Console.WriteLine("  " + row);
                        lines.Add("- " + addr + "  `" + tag.Name + "`  " + table.Name + "  僅 HMI：" + string.Join("；", hmiHits));
                        continue;
                    }

                    unused++;
                    string unusedRow = addr.PadRight(10) + " " + tag.Name.PadRight(36) + "  " + table.Name;
                    Console.WriteLine("  " + unusedRow);
                    lines.Add("- " + addr + "  `" + tag.Name + "`  " + table.Name);
                }
            }

            lines.Add("");
            lines.Add("程式有用 " + used + "、僅 HMI " + hmiOnly + "、程式沒用 " + unused);
            lines.Add("");
            Console.WriteLine("程式有用 " + used + "、僅 HMI " + hmiOnly + "、程式沒用 " + unused);
        }

        string path = destination ?? Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Practice",
            "io-merge",
            "unused-io.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        Console.WriteLine(new string('-', 60));
        Console.WriteLine("已寫入：" + path);
    }

    private static void CollectIoHits(SourceObjectComposition sources, List<string> programHits, List<string> hmiHits)
    {
        if (sources == null)
        {
            return;
        }

        foreach (SourceObject source in sources)
        {
            CollectIoHits(source.Children, programHits, hmiHits);
            foreach (ReferenceObject reference in source.References)
            {
                foreach (Location location in reference.Locations)
                {
                    string typeName = NullText(reference.TypeName);
                    string path = NullText(reference.Path);
                    string where = NullText(reference.Name);
                    if (!string.IsNullOrEmpty(NullText(location.ReferenceLocation)))
                    {
                        where = where + " " + location.ReferenceLocation;
                    }

                    if (IsHmiTypeName(typeName, path))
                    {
                        hmiHits.Add(where);
                    }
                    else if (IsProgramTypeName(typeName))
                    {
                        programHits.Add(where);
                    }
                    else if (!string.IsNullOrEmpty(where))
                    {
                        programHits.Add(where + " [" + typeName + "]");
                    }
                }
            }
        }
    }

    private static void DumpCrossReferences(PlcSoftware plc, string names, string destination)
    {
        string[] wanted = string.IsNullOrWhiteSpace(names)
            ? DefaultOp1XrefTags
            : names.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .ToArray();

        List<string> lines = new List<string>();
        lines.Add("PLC " + plc.Name);
        lines.Add("交叉引用（等同 TIA 選變數後看「交叉引用」）");
        lines.Add("");

        foreach (string name in wanted)
        {
            PlcTag tag = FindPlcTag(plc, name);
            Console.WriteLine(new string('-', 60));
            Console.WriteLine("Tag：" + name);
            lines.Add("## " + name);

            if (tag == null)
            {
                Console.WriteLine("  找不到這個 Tag");
                lines.Add("找不到這個 Tag");
                lines.Add("");
                continue;
            }

            string address = ReadAttribute(tag, "LogicalAddress");
            Console.WriteLine("  位址：" + address);
            lines.Add("位址 " + address);

            CrossReferenceService service = tag.GetService<CrossReferenceService>();
            if (service == null)
            {
                Console.WriteLine("  這個物件沒有 CrossReferenceService（TIA GUI 也要先選到變數才會有資料）");
                lines.Add("沒有 CrossReferenceService");
                lines.Add("");
                continue;
            }

            CrossReferenceResult result = service.GetCrossReferences(CrossReferenceFilter.AllObjects);
            int hits = DumpSourceTree(result.Sources, lines, "  ");

            if (hits == 0)
            {
                Console.WriteLine("  沒有交叉引用（程式／HMI 沒人用，或區塊還未編譯）");
                lines.Add("沒有交叉引用");
            }

            lines.Add("");
        }

        string path = destination ?? Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Practice",
            "io-merge",
            "op1-xref.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        Console.WriteLine(new string('-', 60));
        Console.WriteLine("已寫入：" + path);
    }

    private static PlcTag FindPlcTag(PlcSoftware plc, string name)
    {
        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            PlcTag tag = table.Tags.Find(name);
            if (tag != null)
            {
                return tag;
            }
        }

        return null;
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

    private static int DumpSourceTree(SourceObjectComposition sources, List<string> lines, string indent)
    {
        int hits = 0;
        if (sources == null)
        {
            return 0;
        }

        foreach (SourceObject source in sources)
        {
            hits += DumpSourceTree(source.Children, lines, indent);
            foreach (ReferenceObject reference in source.References)
            {
                foreach (Location location in reference.Locations)
                {
                    hits++;
                    string row = string.Join("\t",
                        location.Access,
                        NullText(reference.Name),
                        NullText(reference.TypeName),
                        NullText(location.Name),
                        NullText(location.TypeName),
                        NullText(location.ReferenceLocation),
                        NullText(location.Address),
                        NullText(location.ReferencedAsName),
                        NullText(reference.Path));
                    Console.WriteLine(indent + row);
                    lines.Add(row);
                }
            }
        }

        return hits;
    }

    private static string NullText(object value)
    {
        return value == null ? "" : value.ToString();
    }

    private static void ListBlocks(PlcSoftware plc)
    {
        Console.WriteLine("PLC 程式區塊清單");
        Console.WriteLine(new string('-', 38));

        foreach (PlcBlock block in EnumerateBlocks(plc.BlockGroup))
        {
            Console.WriteLine(
                block.GetType().Name.PadRight(20) +
                block.Name.PadRight(28) +
                "編號=" + block.Number.ToString().PadRight(6) +
                "語言=" + block.ProgrammingLanguage);
        }

        Console.WriteLine("系統區塊（Program resources）");
        Console.WriteLine(new string('-', 38));
        PlcBlockSystemGroup sysRoot = plc.BlockGroup as PlcBlockSystemGroup;
        if (sysRoot == null)
        {
            Console.WriteLine("（這個 PLC 沒有 SystemBlockGroups）");
            return;
        }

        foreach (PlcSystemBlockGroup group in sysRoot.SystemBlockGroups)
        {
            ListSystemBlocks(group, group.Name);
        }
    }

    private static void ListSystemBlocks(PlcSystemBlockGroup group, string prefix)
    {
        foreach (PlcBlock block in group.Blocks)
        {
            Console.WriteLine(
                (prefix + "/").PadRight(22) +
                block.GetType().Name.PadRight(16) +
                block.Name.PadRight(28) +
                "編號=" + block.Number);
        }

        foreach (PlcSystemBlockGroup child in group.Groups)
        {
            ListSystemBlocks(child, prefix + "/" + child.Name);
        }
    }

    private static void ExportBlock(PlcSoftware plc, string blockName, string destination)
    {
        PlcBlock block = EnumerateBlocks(plc.BlockGroup)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, blockName, StringComparison.OrdinalIgnoreCase));

        if (block == null)
        {
            throw new InvalidOperationException("找不到區塊：" + blockName);
        }

        string path = destination ?? Path.Combine(GetExportFolder(), block.Name + ".block.xml");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        block.Export(new FileInfo(path), ExportOptions.WithDefaults);
        Console.WriteLine("已匯出區塊：" + block.Name + " -> " + path);
    }

    private static void ListSystemConstants(PlcSoftware plc)
    {
        int count = 0;
        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            foreach (PlcSystemConstant constant in table.SystemConstants)
            {
                Console.WriteLine(
                    table.Name + " / " + constant.Name +
                    "  " + constant.DataTypeName +
                    "  " + constant.Value);
                count++;
            }
        }

        Console.WriteLine("System constants：" + count);
    }

    private static void ShowOnlineStatus(Project project, PlcSoftware plc)
    {
        DeviceItem cpu = FindPlcCpuItem(project, plc);
        OnlineProvider provider = cpu.GetService<OnlineProvider>();
        if (provider == null)
        {
            throw new InvalidOperationException("這個 PLC CPU 不支援 OnlineProvider：" + plc.Name);
        }

        ConnectionConfiguration configuration = provider.Configuration;
        Console.WriteLine("PLC：" + plc.Name + "／CPU：" + cpu.Name);
        Console.WriteLine("Online state：" + provider.State);
        Console.WriteLine("Connection configured：" +
            (configuration != null && configuration.IsConfigured));

        if (configuration == null)
        {
            return;
        }

        foreach (ConfigurationMode mode in configuration.Modes)
        {
            Console.WriteLine("Mode：" + mode.Name);
            foreach (ConfigurationPcInterface pc in mode.PcInterfaces)
            {
                Console.WriteLine("  PC interface：" + pc.Name + " #" + pc.Number);
                foreach (ConfigurationAddress address in pc.Addresses)
                {
                    Console.WriteLine("    PC address：" + address.Name + " / " + address.Address);
                }

                foreach (ConfigurationTargetInterface target in pc.TargetInterfaces)
                {
                    Console.WriteLine("    Target interface：" + target.Name);
                    foreach (ConfigurationAddress address in target.Addresses)
                    {
                        Console.WriteLine("      Target address：" + address.Name + " / " + address.Address);
                    }
                }
            }
        }
    }

    private static void SetOnlineState(Project project, PlcSoftware plc, bool goOnline)
    {
        DeviceItem cpu = FindPlcCpuItem(project, plc);
        OnlineProvider provider = cpu.GetService<OnlineProvider>();
        if (provider == null)
        {
            throw new InvalidOperationException("這個 PLC CPU 不支援 OnlineProvider：" + plc.Name);
        }

        Console.WriteLine("PLC：" + plc.Name + "／CPU：" + cpu.Name +
            "／目前狀態：" + provider.State);
        if (goOnline)
        {
            if (provider.Configuration == null || !provider.Configuration.IsConfigured)
            {
                throw new InvalidOperationException("PLC 尚未設定可用的 Online connection：" + plc.Name);
            }

            Console.WriteLine("正在讓指定 PLC Online...");
            Console.WriteLine("Online 結果：" + provider.GoOnline());
        }
        else
        {
            Console.WriteLine("正在讓指定 PLC Offline...");
            provider.GoOffline();
            Console.WriteLine("Online 結果：" + provider.State);
        }
    }

    private static void DownloadPlc(Project project, PlcSoftware plc, string pcInterfaceName)
    {
        if (string.IsNullOrWhiteSpace(pcInterfaceName))
        {
            throw new ArgumentException(
                "--download-plc 必須同時給 --pc-interface:<目前已驗證的 PC 網卡名稱>。");
        }

        DeviceItem cpu = FindPlcCpuItem(project, plc);
        OnlineProvider online = cpu.GetService<OnlineProvider>();
        if (online == null)
        {
            throw new InvalidOperationException("這個 PLC CPU 不支援 OnlineProvider：" + plc.Name);
        }

        if (online.State != OnlineState.Offline)
        {
            throw new InvalidOperationException(
                "下載前 PLC 必須是 Offline，目前為：" + online.State);
        }

        DownloadProvider provider = cpu.GetService<DownloadProvider>();
        if (provider == null || provider.Configuration == null)
        {
            throw new InvalidOperationException("這個 PLC CPU 不支援 DownloadProvider：" + plc.Name);
        }

        ConnectionConfiguration configuration = provider.Configuration;
        ConfigurationMode mode = configuration.Modes.Find("PN/IE");
        if (mode == null)
        {
            throw new InvalidOperationException("找不到 PN/IE download mode。");
        }

        ConfigurationPcInterface pc = mode.PcInterfaces.Find(pcInterfaceName, 1);
        if (pc == null)
        {
            throw new InvalidOperationException("找不到指定 PC 網卡：" + pcInterfaceName);
        }

        if (pc.TargetInterfaces.Count != 1)
        {
            throw new InvalidOperationException(
                "指定網卡的 PLC target interface 數量不是唯一：" + pc.TargetInterfaces.Count);
        }

        ConfigurationTargetInterface target = pc.TargetInterfaces.FirstOrDefault();
        if (target == null)
        {
            throw new InvalidOperationException("找不到 PLC target interface。");
        }

        Console.WriteLine("下載目標：PLC " + plc.Name + "／CPU " + cpu.Name);
        Console.WriteLine("PC 網卡：" + pc.Name + " #" + pc.Number +
            "／Target：" + target.Name);

        DownloadConfigurationDelegate configure = ConfigureDownload;
        DownloadResult result = provider.Download(
            target,
            configure,
            configure,
            DownloadOptions.Hardware | DownloadOptions.Software);

        Console.WriteLine("下載結果：" + result.State +
            "／錯誤 " + result.ErrorCount +
            "／警告 " + result.WarningCount);
        foreach (DownloadResultMessage message in result.Messages)
        {
            Console.WriteLine(
                "  " + message.State +
                "／錯誤 " + message.ErrorCount +
                "／警告 " + message.WarningCount +
                "：" + message.Message);
        }

        if (result.State == DownloadResultState.Error || result.ErrorCount != 0)
        {
            throw new InvalidOperationException("PLC download 未成功。");
        }
    }

    private static void ConfigureDownload(DownloadConfiguration configuration)
    {
        Console.WriteLine("Download configuration：" + configuration.GetType().Name +
            "：" + configuration.Message);

        StopModules stopModules = configuration as StopModules;
        if (stopModules != null)
        {
            stopModules.CurrentSelection = StopModulesSelections.StopAll;
            return;
        }

        AllBlocksDownload allBlocks = configuration as AllBlocksDownload;
        if (allBlocks != null)
        {
            allBlocks.CurrentSelection = AllBlocksDownloadSelections.DownloadAllBlocks;
            return;
        }

        ConsistentBlocksDownload consistentBlocks = configuration as ConsistentBlocksDownload;
        if (consistentBlocks != null)
        {
            consistentBlocks.CurrentSelection = ConsistentBlocksDownloadSelections.ConsistentDownload;
            return;
        }

        ActiveTestCanPreventDownload activeTest = configuration as ActiveTestCanPreventDownload;
        if (activeTest != null)
        {
            activeTest.CurrentSelection = ActiveTestCanPreventDownloadSelections.AcceptAll;
            return;
        }

        AlarmTextLibrariesDownload alarmTexts = configuration as AlarmTextLibrariesDownload;
        if (alarmTexts != null)
        {
            alarmTexts.CurrentSelection = AlarmTextLibrariesDownloadSelections.ConsistentDownload;
            return;
        }

        ExpandDownload expand = configuration as ExpandDownload;
        if (expand != null)
        {
            expand.CurrentSelection = ExpandDownloadSelections.Download;
            return;
        }

        UserManagementDownload userManagement = configuration as UserManagementDownload;
        if (userManagement != null)
        {
            userManagement.CurrentSelection =
                UserManagementPreDownloadSelections.KeepOnlineUserManagementData;
            return;
        }

        if (configuration is SelectiveDeleteDownload)
        {
            throw new InvalidOperationException(
                "下載要求刪除線上資料，已安全停止；請先確認要刪除的資料。");
        }

        if (configuration is DownloadPasswordConfiguration)
        {
            throw new InvalidOperationException(
                "PLC download 需要密碼，已安全停止；不要把密碼傳給工具。");
        }

        DownloadCheckConfiguration check = configuration as DownloadCheckConfiguration;
        if (check != null)
        {
            check.Checked = true;
        }
    }

    private static DeviceItem FindPlcCpuItem(Project project, PlcSoftware plc)
    {
        Device device;
        DeviceItem cpu;
        if (!TryFindPlcHardware(project, plc, out device, out cpu))
        {
            throw new InvalidOperationException("找不到 PLC 對應的 CPU：" + plc.Name);
        }

        return cpu;
    }

    private static bool TryFindPlcHardware(
        Project project,
        PlcSoftware plc,
        out Device device,
        out DeviceItem cpu)
    {
        foreach (Device candidateDevice in EnumerateDevices(project))
        {
            foreach (DeviceItem item in HardwareBuilder.AllItems(candidateDevice))
            {
                SoftwareContainer container = item.GetService<SoftwareContainer>();
                PlcSoftware candidate = container == null ? null : container.Software as PlcSoftware;
                if (candidate != null &&
                    string.Equals(candidate.Name, plc.Name, StringComparison.OrdinalIgnoreCase))
                {
                    device = candidateDevice;
                    cpu = item;
                    return true;
                }
            }
        }

        device = null;
        cpu = null;
        return false;
    }

    private static int ChangePlcCpu(
        Project project,
        List<PlcSoftware> plcSoftwares,
        string plcName,
        string typeIdentifier)
    {
        PlcSoftware plc = ResolvePlc(plcSoftwares, plcName);
        Device device;
        DeviceItem cpu;
        if (!TryFindPlcHardware(project, plc, out device, out cpu))
        {
            Console.WriteLine("找不到 PLC 對應的 CPU：" + plc.Name);
            return 1;
        }

        foreach (PlcSoftware candidate in plcSoftwares)
        {
            Device otherDevice;
            DeviceItem otherCpu;
            if (!TryFindPlcHardware(project, candidate, out otherDevice, out otherCpu))
            {
                continue;
            }

            OnlineProvider provider = otherCpu.GetService<OnlineProvider>();
            if (provider == null || provider.State == OnlineState.Offline)
            {
                continue;
            }

            Console.WriteLine("換型號前先讓 " + candidate.Name + " Offline，目前：" + provider.State);
            provider.GoOffline();
        }

        Console.WriteLine("CPU：" + cpu.Name + " / " + cpu.TypeIdentifier);
        if (string.Equals(cpu.TypeIdentifier, typeIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("已經是這個型號，不需更換。");
            return 0;
        }

        cpu.ChangeType(typeIdentifier);
        Console.WriteLine("已換型號：" + cpu.Name + " -> " + cpu.TypeIdentifier);
        project.Save();
        return 0;
    }

    private static int RenamePlc(
        Project project,
        List<PlcSoftware> plcSoftwares,
        string oldName,
        string newName)
    {
        oldName = (oldName ?? string.Empty).Trim();
        newName = (newName ?? string.Empty).Trim();
        if (oldName.Length == 0 || newName.Length == 0)
        {
            Console.WriteLine("用法：--rename-plc:舊名稱=新名稱");
            return 1;
        }

        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            Console.WriteLine("新舊名稱相同，不需改名：" + oldName);
            return 0;
        }

        PlcSoftware conflict = plcSoftwares.FirstOrDefault(
            candidate => string.Equals(candidate.Name, newName, StringComparison.OrdinalIgnoreCase));
        if (conflict != null)
        {
            Console.WriteLine("已有同名 PLC：" + conflict.Name);
            return 1;
        }

        PlcSoftware plc = ResolvePlc(plcSoftwares, oldName);
        Device device;
        DeviceItem cpu;
        if (!TryFindPlcHardware(project, plc, out device, out cpu))
        {
            Console.WriteLine("找不到 PLC 對應的 CPU：" + oldName);
            return 1;
        }

        foreach (PlcSoftware candidate in plcSoftwares)
        {
            DeviceItem otherCpu;
            Device otherDevice;
            if (!TryFindPlcHardware(project, candidate, out otherDevice, out otherCpu))
            {
                continue;
            }

            OnlineProvider provider = otherCpu.GetService<OnlineProvider>();
            if (provider == null || provider.State == OnlineState.Offline)
            {
                continue;
            }

            Console.WriteLine("改名前先讓 " + candidate.Name + " Offline，目前：" + provider.State);
            provider.GoOffline();
            Console.WriteLine("  " + candidate.Name + "：" + provider.State);
        }

        string oldCpu = cpu.Name;
        string oldDevice = device == null ? null : device.Name;
        cpu.SetAttribute("Name", newName);
        Console.WriteLine("CPU：" + oldCpu + " -> " + cpu.Name);

        if (device != null &&
            string.Equals(device.Name, oldName, StringComparison.OrdinalIgnoreCase))
        {
            device.SetAttribute("Name", newName);
            Console.WriteLine("裝置：" + oldDevice + " -> " + device.Name);
        }
        else if (device != null)
        {
            Console.WriteLine("裝置維持：" + device.Name);
        }

        project.Save();
        Console.WriteLine("已改名並存檔：" + oldName + " -> " + newName);
        return 0;
    }

    private static void CreateInstanceDb(
        Project project,
        PlcSoftware plc,
        string dbName,
        string fbName)
    {
        PlcBlock existing = EnumerateBlocks(plc.BlockGroup)
            .FirstOrDefault(block => string.Equals(block.Name, dbName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            Console.WriteLine("已有 Instance DB：" + dbName + "（DB" + existing.Number + "）");
            return;
        }

        HashSet<int> usedNumbers = new HashSet<int>(
            EnumerateBlocks(plc.BlockGroup).Select(block => block.Number));
        int dbNumber = Enumerable.Range(350, 1650)
            .FirstOrDefault(number => !usedNumbers.Contains(number));
        if (dbNumber == 0)
        {
            throw new InvalidOperationException("找不到可用的 Instance DB 編號。");
        }

        plc.BlockGroup.Blocks.CreateInstanceDB(dbName, false, dbNumber, fbName);
        project.Save();
        Console.WriteLine("已建立 Instance DB：" + dbName + " <- " + fbName + "（DB" + dbNumber + "）");
    }

    private sealed class ExportStats
    {
        public string Root;
        public int Ok;
        public int Failed;
        public readonly List<string> Lines = new List<string>();
    }

    private static void ListDevices(Project project, List<PlcSoftware> plcSoftwares)
    {
        Console.WriteLine("專案裝置清單：" + project.Name);
        Console.WriteLine(new string('-', 38));

        foreach (Device device in EnumerateDevices(project))
        {
            Console.WriteLine("裝置：" + device.Name + " / " + device.TypeIdentifier);

            foreach (DeviceItem item in WalkItems(device.DeviceItems))
            {
                SoftwareContainer container = item.GetService<SoftwareContainer>();
                if (container == null)
                {
                    continue;
                }

                PlcSoftware plcSoftware = container.Software as PlcSoftware;
                if (plcSoftware != null)
                {
                    Console.WriteLine("    PLC 軟體：" + plcSoftware.Name +
                        "／區塊 " + EnumerateBlocks(plcSoftware.BlockGroup).Count() + " 個");
                }

                HmiTarget hmiTarget = container.Software as HmiTarget;
                if (hmiTarget != null)
                {
                    Console.WriteLine("    HMI 軟體：" + hmiTarget.Name +
                        "／畫面 " + hmiTarget.ScreenFolder.Screens.Count + " 個" +
                        "／連線 " + hmiTarget.Connections.Count() + " 條");
                }
            }
        }

        Console.WriteLine(new string('-', 38));
        Console.WriteLine("PLC 軟體總數：" + plcSoftwares.Count);
    }

    private static void ExportAll(Project project, List<PlcSoftware> plcSoftwares)
    {
        ExportStats stats = new ExportStats();
        stats.Root = Path.Combine(GetExportFolder(), "Templates", Sanitize(project.Name));
        Directory.CreateDirectory(stats.Root);

        Console.WriteLine("範本庫輸出位置：" + stats.Root);
        Console.WriteLine(new string('-', 38));

        stats.Lines.Add("# " + project.Name + " 範本庫");
        stats.Lines.Add("");
        stats.Lines.Add("由 `TiaOpennessCheck.exe --export-all` 產生於 " +
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "。");
        stats.Lines.Add("");
        stats.Lines.Add("來源專案：`" + project.Path.FullName + "`");
        stats.Lines.Add("");
        stats.Lines.Add("這些 XML 是 Openness 匯出的真實結構，要建新物件時照抄改名，不要憑記憶猜屬性名稱。");

        foreach (Device device in EnumerateDevices(project))
        {
            foreach (DeviceItem item in WalkItems(device.DeviceItems))
            {
                SoftwareContainer container = item.GetService<SoftwareContainer>();
                if (container == null)
                {
                    continue;
                }

                PlcSoftware plcSoftware = container.Software as PlcSoftware;
                if (plcSoftware != null)
                {
                    ExportPlcSoftware(plcSoftware, device, stats);
                }

                HmiTarget hmiTarget = container.Software as HmiTarget;
                if (hmiTarget != null)
                {
                    ExportHmiTarget(hmiTarget, device, stats);
                }
            }
        }

        stats.Lines.Add("");
        stats.Lines.Add("---");
        stats.Lines.Add("");
        stats.Lines.Add("成功 " + stats.Ok + " 項，失敗 " + stats.Failed + " 項。");

        string indexPath = Path.Combine(stats.Root, "INDEX.md");
        File.WriteAllLines(indexPath, stats.Lines.ToArray(), new UTF8Encoding(false));

        Console.WriteLine(new string('-', 38));
        Console.WriteLine("完成：成功 " + stats.Ok + " 項，失敗 " + stats.Failed + " 項。");
        Console.WriteLine("索引：" + indexPath);
    }

    // The first export pass only walked TypeGroup.Types at the root, so nested
    // library UDTs such as Switch_Type were missing from the template library.
    private static void ExportTypesOnly(Project project, List<PlcSoftware> plcSoftwares, string root)
    {
        Directory.CreateDirectory(root);
        ExportStats stats = new ExportStats { Root = root };
        int ok = 0;
        int failed = 0;

        foreach (PlcSoftware plc in plcSoftwares)
        {
            string folder = Path.Combine(root, "PLC_" + Sanitize(plc.Name));
            Console.WriteLine("PLC：" + plc.Name);

            foreach (PlcType type in EnumerateTypes(plc.TypeGroup))
            {
                string path = Path.Combine(folder, "Types", Sanitize(type.Name) + ".xml");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    type.Export(new FileInfo(path), ExportOptions.WithDefaults);
                    Console.WriteLine("  " + type.Name);
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  失敗 " + type.Name + "：" + Flatten(ex));
                    failed++;
                }
            }
        }

        Console.WriteLine("UDT 匯出完成：成功 " + ok + "、失敗 " + failed);
    }

    private static void ExportPlcSoftware(PlcSoftware plc, Device device, ExportStats stats)
    {
        Console.WriteLine("PLC：" + plc.Name + "（" + device.Name + "）");
        string folder = Path.Combine(stats.Root, "PLC_" + Sanitize(plc.Name));

        stats.Lines.Add("");
        stats.Lines.Add("## PLC：" + plc.Name + "（裝置 " + device.Name + " / " + device.TypeIdentifier + "）");
        stats.Lines.Add("");

        foreach (PlcBlock block in EnumerateBlocks(plc.BlockGroup))
        {
            TryExportItem(
                block,
                Path.Combine(folder, "Blocks", Sanitize(block.Name) + ".xml"),
                block.Name + "（" + block.GetType().Name + " / " + block.ProgrammingLanguage + "）",
                stats);
        }

        foreach (PlcType type in EnumerateTypes(plc.TypeGroup))
        {
            TryExportItem(
                type,
                Path.Combine(folder, "Types", Sanitize(type.Name) + ".xml"),
                type.Name + "（PLC 資料型別）",
                stats);
        }

        ExportPlcTagTables(plc.TagTableGroup, folder, stats);
    }

    private static void ExportPlcTagTables(PlcTagTableGroup group, string folder, ExportStats stats)
    {
        foreach (PlcTagTable table in group.TagTables)
        {
            TryExportItem(
                table,
                Path.Combine(folder, "TagTables", Sanitize(table.Name) + ".xml"),
                table.Name + "（PLC 變數表）",
                stats);
        }

        foreach (PlcTagTableUserGroup child in group.Groups)
        {
            ExportPlcTagTables(child, folder, stats);
        }
    }

    private static void ExportHmiTarget(HmiTarget hmi, Device device, ExportStats stats)
    {
        Console.WriteLine("HMI：" + hmi.Name + "（" + device.Name + "）");
        string folder = Path.Combine(stats.Root, "HMI_" + Sanitize(hmi.Name));

        stats.Lines.Add("");
        stats.Lines.Add("## HMI：" + hmi.Name + "（裝置 " + device.Name + " / " + device.TypeIdentifier + "）");
        stats.Lines.Add("");

        foreach (Screen screen in hmi.ScreenFolder.Screens)
        {
            TryExportItem(
                screen,
                Path.Combine(folder, "Screens", Sanitize(screen.Name) + ".xml"),
                screen.Name + "（畫面）",
                stats);
        }

        foreach (TagTable table in hmi.TagFolder.TagTables)
        {
            TryExportItem(
                table,
                Path.Combine(folder, "TagTables", Sanitize(table.Name) + ".xml"),
                table.Name + "（HMI 變數表）",
                stats);
        }

        foreach (HmiSoftConnection connection in hmi.Connections)
        {
            TryExportItem(
                connection,
                Path.Combine(folder, "Connections", Sanitize(connection.Name) + ".xml"),
                connection.Name + "（HMI 連線）",
                stats);
        }
    }

    private static void TryExportItem(object item, string path, string label, ExportStats stats)
    {
        try
        {
            MethodInfo exportMethod = item.GetType().GetMethod(
                "Export",
                new[] { typeof(FileInfo), typeof(ExportOptions) });

            if (exportMethod == null)
            {
                stats.Failed++;
                stats.Lines.Add("- 略過 " + label + "：這個型別沒有 Export(FileInfo, ExportOptions)");
                Console.WriteLine("  略過：" + label + "（沒有 Export）");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            exportMethod.Invoke(item, new object[] { new FileInfo(path), ExportOptions.WithDefaults });

            stats.Ok++;
            stats.Lines.Add("- " + label + " → `" + path.Substring(stats.Root.Length).TrimStart('\\') + "`");
            Console.WriteLine("  已匯出：" + label);
        }
        catch (Exception ex)
        {
            string detail = Flatten(ex);
            stats.Failed++;
            stats.Lines.Add("- 失敗 " + label + "：" + detail);
            Console.WriteLine("  匯出失敗：" + label + " — " + detail);
        }
    }

    private static string Flatten(Exception ex)
    {
        StringBuilder text = new StringBuilder();
        Exception current = ex;

        while (current != null)
        {
            if (text.Length > 0)
            {
                text.Append(" ← ");
            }

            text.Append(current.GetType().Name).Append("：").Append(current.Message);
            current = current.InnerException;
        }

        return text.ToString();
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "unnamed";
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }

    private static void RenameProjectText(Project project, string oldText, string newText)
    {
        if (string.IsNullOrEmpty(oldText) || string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            throw new ArgumentException("rename-text 需要 old=new 且兩者不同");
        }

        int count = 0;

        foreach (Device device in EnumerateDevices(project).ToList())
        {
            count += TryRenameEngineeringName(device, "裝置", oldText, newText);

            foreach (DeviceItem item in WalkItems(device.DeviceItems).ToList())
            {
                count += TryRenameEngineeringName(item, "硬體項", oldText, newText);

                SoftwareContainer container = item.GetService<SoftwareContainer>();
                HmiTarget hmi = container == null ? null : container.Software as HmiTarget;
                if (hmi != null)
                {
                    count += RenameHmiTagTablesViaImport(hmi, oldText, newText);
                    count += RenameHmiObjects(hmi, oldText, newText);
                }
            }
        }

        project.Save();
        Console.WriteLine("改名完成：" + count + " 項。");
    }

    private static int TryRenameEngineeringName(IEngineeringObject target, string label, string oldText, string newText)
    {
        try
        {
            object current = target.GetAttribute("Name");
            string name = current == null ? null : current.ToString();
            if (name == null || name.IndexOf(oldText, StringComparison.Ordinal) < 0)
            {
                return 0;
            }

            string updated = name.Replace(oldText, newText);
            target.SetAttribute("Name", updated);
            Console.WriteLine(label + "：" + name + " -> " + updated);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(label + " 改名失敗：" + Flatten(ex));
            return 0;
        }
    }

    private static int RenameHmiTagTablesViaImport(HmiTarget hmi, string oldText, string newText)
    {
        string staging = Path.Combine(GetExportFolder(), "_rename_staging", Sanitize(hmi.Name));
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }

        Directory.CreateDirectory(staging);
        int files = 0;

        foreach (TagTable table in hmi.TagFolder.TagTables)
        {
            string path = Path.Combine(staging, Sanitize(table.Name) + ".xml");
            table.Export(new FileInfo(path), ExportOptions.WithDefaults);
            string xml = File.ReadAllText(path, Encoding.UTF8);
            if (xml.IndexOf(oldText, StringComparison.Ordinal) < 0)
            {
                continue;
            }

            File.WriteAllText(path, xml.Replace(oldText, newText), Encoding.UTF8);
            TagSystemFolder folder = (TagSystemFolder)table.Parent;
            folder.TagTables.Import(new FileInfo(path), ImportOptions.Override);
            Console.WriteLine("  HMI Tag 表：" + table.Name + "（已替換 " + oldText + "）");
            files++;
        }

        return files;
    }

    private static int RenameHmiObjects(HmiTarget hmi, string oldText, string newText)
    {
        int count = 0;

        foreach (HmiSoftConnection conn in hmi.Connections)
        {
            foreach (string attr in new[] { "Partner", "Station", "CommunicationPartner" })
            {
                try
                {
                    object value = conn.GetAttribute(attr);
                    string text = value == null ? null : value.ToString();
                    if (text == null || text.IndexOf(oldText, StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    conn.SetAttribute(attr, text.Replace(oldText, newText));
                    Console.WriteLine("  HMI 連線屬性 " + conn.Name + " / " + attr);
                    count++;
                }
                catch
                {
                }
            }
        }

        return count;
    }

    private static void ClearDevices(Project project)
    {
        int devices = 0;
        foreach (Device device in EnumerateDevices(project).ToList())
        {
            Console.WriteLine("刪除裝置：" + device.Name);
            device.Delete();
            devices++;
        }

        int groups = 0;
        foreach (DeviceUserGroup group in project.DeviceGroups.ToList())
        {
            Console.WriteLine("刪除群組：" + group.Name);
            group.Delete();
            groups++;
        }

        project.Save();
        Console.WriteLine("已清空：裝置 " + devices + " 台、群組 " + groups + " 個。");
        Console.WriteLine("目前裝置數：" + EnumerateDevices(project).Count());
    }

    // Confirms every article the rebuild uses really exists in this
    // installation's hardware catalog, so nothing is silently carried over as
    // a string that only happened to work in the source project.
    private static void VerifyAgainstCatalog(Project project, string planPath)
    {
        TiaPortal portal = GetPortalOf(project);
        XDocument plan = XDocument.Load(planPath);

        List<string> ordered = plan.Descendants("Item")
            .Select(item => (string)item.Attribute("TypeIdentifier"))
            .Where(type => !string.IsNullOrEmpty(type)
                && type.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine("要驗證的 SIMATIC 件號：" + ordered.Count + " 種");
        int matched = 0;
        int missing = 0;

        foreach (string typeIdentifier in ordered)
        {
            string article = typeIdentifier.Substring("OrderNumber:".Length);
            int slash = article.IndexOf('/');
            if (slash > 0)
            {
                article = article.Substring(0, slash);
            }

            IList<CatalogEntry> hits = portal.HardwareCatalog.Find(article);
            CatalogEntry exact = hits.FirstOrDefault(entry =>
                string.Equals(entry.TypeIdentifier, typeIdentifier, StringComparison.OrdinalIgnoreCase));

            if (exact != null)
            {
                Console.WriteLine("  OK   " + typeIdentifier + "  <- 目錄：" + exact.CatalogPath);
                matched++;
            }
            else
            {
                Console.WriteLine("  缺   " + typeIdentifier);
                foreach (CatalogEntry hit in hits)
                {
                    Console.WriteLine("        目錄有：" + hit.TypeIdentifier);
                }

                missing++;
            }
        }

        Console.WriteLine("驗證結果：目錄命中 " + matched + "、找不到 " + missing);
    }

    private static void SearchCatalog(Project project, string filter)
    {
        TiaPortal portal = GetPortalOf(project);
        IList<CatalogEntry> entries = portal.HardwareCatalog.Find(filter);

        Console.WriteLine("目錄搜尋「" + filter + "」，命中 " + entries.Count + " 筆：");
        foreach (CatalogEntry entry in entries)
        {
            Console.WriteLine("  件號 " + entry.ArticleNumber + " / 版本 " + entry.Version);
            Console.WriteLine("      TypeIdentifier = " + entry.TypeIdentifier);
            Console.WriteLine("      " + entry.TypeName + "｜" + entry.CatalogPath);
        }
    }

    // Dumping the same interface from the reference and the new project shows
    // exactly which attribute switches an S7-1200 into I-device mode.
    private static void DumpInterfaceAttributes(Project project, string deviceName)
    {
        Device device = EnumerateDevices(project)
            .FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));

        if (device == null)
        {
            Console.WriteLine("找不到裝置：" + deviceName);
            return;
        }

        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            NetworkInterface net = item.GetService<NetworkInterface>();
            if (net == null)
            {
                continue;
            }

            Console.WriteLine("介面：" + item.Name);
            Console.WriteLine("  IoControllers=" + net.IoControllers.Count +
                " IoConnectors=" + net.IoConnectors.Count +
                " Nodes=" + net.Nodes.Count);

            IEngineeringObject target = net;
            foreach (EngineeringAttributeInfo info in target.GetAttributeInfos())
            {
                object value;
                try
                {
                    value = target.GetAttribute(info.Name);
                }
                catch (Exception ex)
                {
                    value = "<" + ex.GetBaseException().Message + ">";
                }

                Console.WriteLine("    " + info.Name + " = " + (value ?? "<null>"));
            }
        }
    }

    private static void ShowPlugLocations(Project project, string deviceName)
    {
        Device device = EnumerateDevices(project)
            .FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));

        if (device == null)
        {
            Console.WriteLine("可用裝置：" + string.Join("、", EnumerateDevices(project).Select(d => d.Name).ToArray()));
            throw new InvalidOperationException("找不到裝置：" + deviceName);
        }

        ReportPlugLocations(device.Name + "（Device）", device);

        foreach (DeviceItem item in device.DeviceItems)
        {
            ReportPlugLocations("  " + item.Name + "（pos " + item.PositionNumber + "）", item);
        }
    }

    private static void ReportPlugLocations(string label, HardwareObject target)
    {
        IList<PlugLocation> locations;
        try
        {
            locations = target.GetPlugLocations();
        }
        catch (Exception ex)
        {
            Console.WriteLine(label + " -> 取不到插槽：" + Flatten(ex));
            return;
        }

        Console.WriteLine(label + " -> 可用插槽 " + locations.Count + " 個");
        foreach (PlugLocation location in locations)
        {
            Console.WriteLine("      slot " + location.PositionNumber + "：" + location.Label);
        }
    }

    private static TiaPortal GetPortalOf(Project project)
    {
        IEngineeringObject current = project.Parent;
        while (current != null)
        {
            TiaPortal portal = current as TiaPortal;
            if (portal != null)
            {
                return portal;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("無法從專案取得 TiaPortal 實例。");
    }

    private static void ProbeDevice(Project project, string deviceName)
    {
        Device device = EnumerateDevices(project)
            .FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));

        if (device == null)
        {
            Console.WriteLine("可用裝置：" + string.Join("、", EnumerateDevices(project).Select(d => d.Name).ToArray()));
            throw new InvalidOperationException("找不到裝置：" + deviceName);
        }

        Console.WriteLine("裝置：" + device.Name);
        DumpEveryAttribute(device, "  ");

        foreach (DeviceItem item in WalkItems(device.DeviceItems))
        {
            Console.WriteLine("項目：" + item.Name + "（pos=" + item.PositionNumber + "）");
            DumpEveryAttribute(item, "  ");

            SoftwareContainer container = item.GetService<SoftwareContainer>();
            if (container != null && container.Software != null)
            {
                Console.WriteLine("  [軟體] " + container.Software.GetType().Name);
                DumpEveryAttribute(container.Software, "    ");
            }
        }
    }

    private static void DumpEveryAttribute(IEngineeringObject target, string indent)
    {
        IList<EngineeringAttributeInfo> infos;
        try
        {
            infos = target.GetAttributeInfos();
        }
        catch (Exception ex)
        {
            Console.WriteLine(indent + "無法列出屬性：" + ex.GetType().Name);
            return;
        }

        foreach (EngineeringAttributeInfo info in infos)
        {
            string value = TryReadAttribute(target, info.Name);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            Console.WriteLine(indent + info.Name + " = " + value);
        }
    }

    // 重建硬體需要的是每個 DeviceItem 的 TypeIdentifier（訂貨號＋韌體版本）與插槽位置，
    // Device.TypeIdentifier（System:Device.S71200）資訊不足以重建。
    public static void DumpHardware(Project project)
    {
        string path = Path.Combine(GetExportFolder(), "Templates",
            Sanitize(project.Name), "hardware.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        XElement root = new XElement("Hardware",
            new XAttribute("Project", project.Name));

        foreach (KeyValuePair<string, Device> entry in EnumerateDevicesWithGroup(project))
        {
            Device device = entry.Value;
            XElement deviceElement = new XElement("Device",
                new XAttribute("Name", device.Name ?? string.Empty),
                new XAttribute("Group", entry.Key),
                new XAttribute("TypeIdentifier", device.TypeIdentifier ?? string.Empty));

            foreach (DeviceItem item in device.DeviceItems)
            {
                deviceElement.Add(DescribeItem(item));
            }

            root.Add(deviceElement);
            Console.WriteLine("裝置：" + device.Name + " / " + device.TypeIdentifier);
        }

        root.Add(DescribeSubnets(project));

        new XDocument(root).Save(path);
        Console.WriteLine("已寫出硬體清單：" + path);
    }

    private static XElement DescribeItem(DeviceItem item)
    {
        return DescribeItem(item, new HashSet<DeviceItem>());
    }

    private static XElement DescribeItem(DeviceItem item, HashSet<DeviceItem> visited)
    {
        if (!visited.Add(item))
        {
            return new XElement("ItemRef",
                new XAttribute("Name", item.Name ?? string.Empty));
        }

        XElement element = new XElement("Item",
            new XAttribute("Name", item.Name ?? string.Empty),
            new XAttribute("TypeIdentifier", item.TypeIdentifier ?? string.Empty),
            new XAttribute("PositionNumber", item.PositionNumber));

        // Slot numbers are only unique per container: an S7-1200 communication
        // board and a signal module can both sit at position 3 because one is
        // plugged into the CPU and the other into the rack.  Record the
        // container by type, since its name is localised (Rack_0 / 机架_0).
        HardwareObject container = item.Container;
        if (container != null)
        {
            element.SetAttributeValue("ContainerType", container.TypeIdentifier ?? string.Empty);
            element.SetAttributeValue("ContainerName", container.Name ?? string.Empty);
        }

        string[] interesting = { "OrderNumber", "FirmwareVersion", "Classification", "DeviceItemType" };
        foreach (string name in interesting)
        {
            string value = TryReadAttribute(item, name);
            if (value != null)
            {
                element.SetAttributeValue(name, value);
            }
        }

        foreach (Address address in item.Addresses)
        {
            element.Add(new XElement("Address",
                new XAttribute("StartAddress", address.StartAddress),
                new XAttribute("Length", address.Length),
                new XAttribute("IoType", address.IoType.ToString())));
        }

        NetworkInterface network = item.GetService<NetworkInterface>();
        if (network != null)
        {
            foreach (Node node in network.Nodes)
            {
                element.Add(new XElement("Node",
                    new XAttribute("Name", node.Name ?? string.Empty),
                    new XAttribute("Address", TryReadAttribute(node, "Address") ?? string.Empty),
                    new XAttribute("Subnet", node.ConnectedSubnet == null
                        ? string.Empty
                        : node.ConnectedSubnet.Name)));
            }

            // IO device 端記錄自己掛在哪個 IO system 上，這是重播 PROFINET 拓撲的關鍵。
            foreach (IoConnector connector in network.IoConnectors)
            {
                // GetIoController() throws on unassigned GSD connectors, so walk
                // up from the IO system instead.
                IoSystem system = connector.ConnectedToIoSystem;
                element.Add(new XElement("IoConnector",
                    new XAttribute("IoSystem", system == null ? string.Empty : system.Name),
                    new XAttribute("Controller", system == null
                        ? string.Empty
                        : DescribeController(system.Parent as IoController))));
            }

            foreach (IoController controller in network.IoControllers)
            {
                element.Add(new XElement("IoController",
                    new XAttribute("IoSystem", controller.IoSystem == null
                        ? string.Empty
                        : controller.IoSystem.Name)));
            }

            foreach (TransferArea area in network.TransferAreas)
            {
                XElement areaElement = new XElement("TransferArea",
                    new XAttribute("Name", area.Name ?? string.Empty),
                    new XAttribute("Type", area.Type.ToString()),
                    new XAttribute("Direction", area.Direction.ToString()));
                foreach (Address address in area.LocalAddresses)
                {
                    areaElement.Add(new XElement("Local",
                        new XAttribute("StartAddress", address.StartAddress),
                        new XAttribute("Length", address.Length),
                        new XAttribute("IoType", address.IoType.ToString())));
                }

                foreach (Address address in area.PartnerAddresses)
                {
                    areaElement.Add(new XElement("Partner",
                        new XAttribute("StartAddress", address.StartAddress),
                        new XAttribute("Length", address.Length),
                        new XAttribute("IoType", address.IoType.ToString())));
                }

                element.Add(areaElement);
            }
        }

        foreach (DeviceItem child in item.DeviceItems)
        {
            element.Add(DescribeItem(child, visited));
        }

        foreach (DeviceItem extra in item.Items)
        {
            if (!visited.Contains(extra))
            {
                element.Add(DescribeItem(extra, visited));
            }
        }

        return element;
    }

    private static string DescribeController(IoController controller)
    {
        if (controller == null)
        {
            return string.Empty;
        }

        NetworkInterface owner = controller.Parent as NetworkInterface;
        Device device = owner == null ? null : OwningDevice(owner.OwnedBy);
        return device == null ? "?" : device.Name;
    }

    private static Device OwningDevice(DeviceItem item)
    {
        IEngineeringObject current = item;
        while (current != null)
        {
            Device device = current as Device;
            if (device != null)
            {
                return device;
            }

            current = current.Parent;
        }

        return null;
    }

    private static XElement DescribeSubnets(Project project)
    {
        XElement subnets = new XElement("Subnets");

        foreach (Subnet subnet in project.Subnets)
        {
            subnets.Add(new XElement("Subnet",
                new XAttribute("Name", subnet.Name ?? string.Empty),
                new XAttribute("TypeIdentifier", subnet.TypeIdentifier ?? string.Empty),
                new XAttribute("NodeCount", subnet.Nodes.Count)));
        }

        return subnets;
    }

    private static string TryReadAttribute(IEngineeringObject item, string name)
    {
        try
        {
            object value = item.GetAttribute(name);
            return value == null ? null : value.ToString();
        }
        catch
        {
            return null;
        }
    }

    public static List<PlcSoftware> FindAllPlcSoftwares(Project project)
    {
        List<PlcSoftware> found = new List<PlcSoftware>();

        foreach (Device device in EnumerateDevices(project))
        {
            foreach (DeviceItem item in WalkItems(device.DeviceItems))
            {
                SoftwareContainer container = item.GetService<SoftwareContainer>();
                PlcSoftware plc = container == null ? null : container.Software as PlcSoftware;
                if (plc != null)
                {
                    found.Add(plc);
                }
            }
        }

        return found;
    }

    private static void CompileAll(Project project, List<PlcSoftware> plcSoftwares)
    {
        Console.WriteLine("要編譯的 PLC 數量：" + plcSoftwares.Count);
        Console.WriteLine(new string('-', 38));

        int totalErrors = 0;
        int totalWarnings = 0;

        foreach (PlcSoftware plc in plcSoftwares)
        {
            ICompilable compilable = plc.GetService<ICompilable>();
            if (compilable == null)
            {
                Console.WriteLine(plc.Name + "：不支援編譯服務，略過。");
                continue;
            }

            Console.WriteLine("正在編譯 " + plc.Name + " ...");
            try
            {
                CompilerResult result = compilable.Compile();
                totalErrors += result.ErrorCount;
                totalWarnings += result.WarningCount;
                Console.WriteLine("  " + plc.Name + "：" + result.State +
                    "／錯誤 " + result.ErrorCount + "／警告 " + result.WarningCount);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  " + plc.Name + " 編譯失敗：" + Flatten(ex));
            }
        }

        project.Save();
        Console.WriteLine(new string('-', 38));
        Console.WriteLine("全部編譯完成：錯誤 " + totalErrors + "／警告 " + totalWarnings + "；專案已存檔。");
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

    // The reference project files its devices under 1.PLC&HMI / 2.Remote_IO /
    // 3.Drive, so the rebuild has to recreate those folders too.
    private static IEnumerable<KeyValuePair<string, Device>> EnumerateDevicesWithGroup(Project project)
    {
        foreach (Device device in project.Devices)
        {
            yield return new KeyValuePair<string, Device>(string.Empty, device);
        }

        foreach (Device device in project.UngroupedDevicesGroup.Devices)
        {
            yield return new KeyValuePair<string, Device>(string.Empty, device);
        }

        foreach (DeviceUserGroup group in project.DeviceGroups)
        {
            foreach (KeyValuePair<string, Device> entry in EnumerateGroupDevicesWithPath(group, group.Name))
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<KeyValuePair<string, Device>> EnumerateGroupDevicesWithPath(
        DeviceUserGroup group, string path)
    {
        foreach (Device device in group.Devices)
        {
            yield return new KeyValuePair<string, Device>(path, device);
        }

        foreach (DeviceUserGroup child in group.Groups)
        {
            foreach (KeyValuePair<string, Device> entry in EnumerateGroupDevicesWithPath(child, path + "/" + child.Name))
            {
                yield return entry;
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

    private static void ExportScreen(Project project, string screenName)
    {
        HmiTarget hmi = FindAnyHmiTarget(project);
        if (hmi == null)
        {
            throw new InvalidOperationException("專案裡找不到 HMI 裝置。");
        }

        Screen screen = hmi.ScreenFolder.Screens
            .FirstOrDefault(candidate => string.Equals(candidate.Name, screenName, StringComparison.OrdinalIgnoreCase));

        if (screen == null)
        {
            Console.WriteLine("可用畫面：" + string.Join("、", hmi.ScreenFolder.Screens.Select(s => s.Name).ToArray()));
            throw new InvalidOperationException("找不到畫面：" + screenName);
        }

        string path = Path.Combine(GetExportFolder(), screen.Name.Replace(" ", "_") + ".screen.xml");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        screen.Export(new FileInfo(path), ExportOptions.WithDefaults);
        Console.WriteLine("已匯出畫面：" + screen.Name + " -> " + path);
    }

    private static void ImportScreen(Project project, string path)
    {
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(GetExportFolder(), path);
        }

        HmiTarget hmi = FindAnyHmiTarget(project);
        if (hmi == null)
        {
            throw new InvalidOperationException("專案裡找不到 HMI 裝置。");
        }

        Console.WriteLine("正在匯入畫面 XML：" + path);
        hmi.ScreenFolder.Screens.Import(new FileInfo(path), ImportOptions.Override);
        Console.WriteLine("畫面匯入完成。");
        project.Save();
        Console.WriteLine("專案已存檔。");
    }

    private static IEnumerable<Screen> WalkScreens(ScreenFolder folder)
    {
        foreach (Screen screen in folder.Screens)
        {
            yield return screen;
        }

        foreach (ScreenUserFolder child in folder.Folders)
        {
            foreach (Screen screen in WalkScreens(child))
            {
                yield return screen;
            }
        }
    }

    private static int RenameHmiScreen(Project project, string spec)
    {
        int eq = spec.IndexOf('=');
        if (eq <= 0 || eq == spec.Length - 1)
        {
            Console.WriteLine("用法：--rename-screen:舊畫面名=新畫面名");
            return 1;
        }

        string oldName = spec.Substring(0, eq);
        string newName = spec.Substring(eq + 1);
        string staging = Path.Combine(GetExportFolder(), "_screen_rename");
        Directory.CreateDirectory(staging);
        int renamed = 0;
        int refs = 0;

        foreach (Device device in EnumerateDevices(project))
        {
            foreach (DeviceItem item in WalkItems(device.DeviceItems))
            {
                SoftwareContainer container = item.GetService<SoftwareContainer>();
                HmiTarget hmi = container == null ? null : container.Software as HmiTarget;
                if (hmi == null)
                {
                    continue;
                }

                ScreenFolder folder = hmi.ScreenFolder;
                Dictionary<string, string> patched = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (Screen screen in WalkScreens(hmi.ScreenFolder).ToList())
                {
                    string liveName = screen.Name;
                    string path = Path.Combine(staging, Sanitize(device.Name) + "_" + Sanitize(liveName) + ".xml");
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    try
                    {
                        screen.Export(new FileInfo(path), ExportOptions.WithDefaults);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  畫面匯出失敗 " + liveName + "：" + ex.Message);
                        continue;
                    }

                    string xml = File.ReadAllText(path, Encoding.UTF8);
                    if (xml.IndexOf(oldName, StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    File.WriteAllText(path, xml.Replace(oldName, newName), Encoding.UTF8);
                    patched[liveName] = path;
                    if (screen.Parent is ScreenFolder)
                    {
                        folder = (ScreenFolder)screen.Parent;
                    }
                }

                if (patched.Count == 0)
                {
                    continue;
                }

                Screen oldScreen = WalkScreens(hmi.ScreenFolder)
                    .FirstOrDefault(s => string.Equals(s.Name, oldName, StringComparison.Ordinal));
                if (oldScreen != null)
                {
                    ScreenFolder oldFolder = oldScreen.Parent as ScreenFolder ?? folder;
                    oldScreen.Delete();
                    folder = oldFolder;
                    Console.WriteLine(device.Name + " 已刪 " + oldName);
                    project.Save();
                }

                string newPath;
                if (patched.TryGetValue(oldName, out newPath))
                {
                    try
                    {
                        folder.Screens.Import(new FileInfo(newPath), ImportOptions.None);
                        Console.WriteLine("  新建畫面：" + device.Name + " / " + newName);
                        renamed++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  新建畫面失敗 " + newName + "：" + ex.Message);
                    }
                }

                foreach (KeyValuePair<string, string> pair in patched)
                {
                    if (string.Equals(pair.Key, oldName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        folder.Screens.Import(new FileInfo(pair.Value), ImportOptions.Override);
                        Console.WriteLine("  畫面引用：" + device.Name + " / " + pair.Key);
                        refs++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  畫面匯入失敗 " + pair.Key + "：" + ex.Message);
                    }
                }

                project.Save();
                Console.WriteLine(device.Name + " 已存。");
            }
        }

        project.Save();
        Console.WriteLine("畫面改名：" + renamed + " 個，引用 " + refs + " 處。已存。");
        return renamed == 0 ? 1 : 0;
    }

    private static HmiTarget FindAnyHmiTarget(Project project)
    {
        foreach (Device device in EnumerateDevices(project))
        {
            foreach (DeviceItem item in WalkItems(device.DeviceItems))
            {
                SoftwareContainer container = item.GetService<SoftwareContainer>();
                HmiTarget hmi = container == null ? null : container.Software as HmiTarget;
                if (hmi != null)
                {
                    return hmi;
                }
            }
        }

        return null;
    }

    private static IEnumerable<DeviceItem> WalkItems(DeviceItemComposition items)
    {
        foreach (DeviceItem item in items)
        {
            yield return item;
            foreach (DeviceItem child in WalkItems(item.DeviceItems))
            {
                yield return child;
            }
        }
    }

    private static void ImportBlock(PlcSoftware plc, Project project, string path)
    {
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(GetExportFolder(), path);
        }

        string blockName = ReadExportedBlockName(path);
        PlcBlockGroup target = FindGroupWithBlock(plc.BlockGroup, blockName) ?? plc.BlockGroup;
        Console.WriteLine("正在匯入區塊 XML：" + path);
        if (!string.IsNullOrEmpty(blockName))
        {
            Console.WriteLine("目標區塊：" + blockName + "／資料夾 " + DescribeBlockGroup(plc.BlockGroup, target));
        }

        IList<PlcBlock> imported = target.Blocks.Import(new FileInfo(path), ImportOptions.Override);
        foreach (PlcBlock block in imported)
        {
            Console.WriteLine("已匯入：" + block.Name + " / 語言=" + block.ProgrammingLanguage);
        }

        project.Save();
        Console.WriteLine("專案已儲存。");
    }

    private static void ImportPlcType(PlcSoftware plc, Project project, string path)
    {
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(GetExportFolder(), path);
        }

        XDocument document = XDocument.Load(path);
        XElement nameEl = document.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Name" &&
            e.Parent != null &&
            e.Parent.Name.LocalName == "AttributeList");
        string typeName = nameEl == null ? null : nameEl.Value;
        PlcType existing = typeName == null
            ? null
            : EnumerateTypes(plc.TypeGroup).FirstOrDefault(t =>
                string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
        PlcTypeGroup target = existing == null
            ? plc.TypeGroup
            : (existing.Parent as PlcTypeGroup) ?? plc.TypeGroup;
        Console.WriteLine("正在匯入 UDT：" + path + " → " + (typeName ?? "?"));
        target.Types.Import(new FileInfo(path), ImportOptions.Override);
        project.Save();
        Console.WriteLine("專案已儲存。");
    }

    private static string ReadExportedBlockName(string path)
    {
        XDocument document = XDocument.Load(path);
        XElement block = document.Root == null
            ? null
            : document.Root.Elements().FirstOrDefault(element =>
                element.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
        if (block == null)
        {
            return null;
        }

        XElement attributeList = block.Elements().FirstOrDefault(element =>
            element.Name.LocalName == "AttributeList");
        XElement name = attributeList == null
            ? null
            : attributeList.Elements().FirstOrDefault(element => element.Name.LocalName == "Name");
        return name == null ? null : name.Value;
    }

    private static PlcBlockGroup FindGroupWithBlock(PlcBlockGroup group, string blockName)
    {
        if (string.IsNullOrEmpty(blockName))
        {
            return null;
        }

        if (group.Blocks.Find(blockName) != null)
        {
            return group;
        }

        foreach (PlcBlockUserGroup child in group.Groups)
        {
            PlcBlockGroup found = FindGroupWithBlock(child, blockName);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static string DescribeBlockGroup(PlcBlockGroup root, PlcBlockGroup target)
    {
        if (object.ReferenceEquals(root, target))
        {
            return "(根目錄)";
        }

        string path = FindBlockGroupPath(root, target, string.Empty);
        return string.IsNullOrEmpty(path) ? target.Name : path;
    }

    private static string FindBlockGroupPath(PlcBlockGroup group, PlcBlockGroup target, string prefix)
    {
        if (object.ReferenceEquals(group, target))
        {
            return prefix.Length == 0 ? group.Name : prefix;
        }

        foreach (PlcBlockUserGroup child in group.Groups)
        {
            string next = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
            string found = FindBlockGroupPath(child, target, next);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static void ImportSclDirectory(PlcSoftware plc, Project project, string dir)
    {
        if (!Path.IsPathRooted(dir))
        {
            dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dir);
        }

        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException("找不到 SCL 目錄：" + dir);
        }

        string[] files = Directory.GetFiles(dir, "*.scl")
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        int ok = 0;
        int failed = 0;
        foreach (string file in files)
        {
            try
            {
                ImportSclSource(plc, project, file, saveProject: false);
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("SCL 匯入失敗：" + Path.GetFileName(file));
                Console.WriteLine(ex.GetType().Name + ": " + ex.Message);
            }
        }

        project.Save();
        Console.WriteLine("SCL 目錄匯入完成：成功 " + ok + " / 失敗 " + failed + " / 共 " + files.Length);
    }

    private static void ImportSclSource(PlcSoftware plc, Project project, string path, bool saveProject)
    {
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        }

        string sourceName = Path.GetFileName(path);
        PlcExternalSource existingSource = plc.ExternalSourceGroup.ExternalSources.Find(sourceName);
        if (existingSource != null)
        {
            existingSource.Delete();
        }

        string sourceText = File.ReadAllText(path);
        System.Text.RegularExpressions.Match nameMatch =
            System.Text.RegularExpressions.Regex.Match(
                sourceText,
                @"(?:FUNCTION(?:_BLOCK)?|DATA_BLOCK)\s+""([^""]+)""",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (nameMatch.Success)
        {
            PlcBlock existingBlock = EnumerateBlocks(plc.BlockGroup)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, nameMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            if (existingBlock != null)
            {
                Console.WriteLine("刪除既有區塊再匯入 SCL：" + existingBlock.Name);
                existingBlock.Delete();
            }
        }

        Console.WriteLine("正在從 SCL 產生區塊：" + path);
        GenerateBlockOption option = GenerateBlockOption.None;
        foreach (string candidate in new[] { "Overwrite", "None" })
        {
            try
            {
                option = (GenerateBlockOption)Enum.Parse(typeof(GenerateBlockOption), candidate);
                break;
            }
            catch (ArgumentException)
            {
            }
        }

        plc.ExternalSourceGroup.ExternalSources
            .CreateFromFile(sourceName, path)
            .GenerateBlocksFromSource(option);

        if (saveProject)
        {
            project.Save();
            Console.WriteLine("已產生區塊並儲存專案。");
        }
        else
        {
            Console.WriteLine("已產生區塊：" + sourceName);
        }
    }

    private static int SetBlockNumber(PlcSoftware plc, Project project, string blockName, string numberText)
    {
        int number;
        if (!int.TryParse(numberText.Trim(), out number) || number <= 0)
        {
            Console.WriteLine("區塊編號必須是正整數：" + numberText);
            return 1;
        }

        PlcBlock block = EnumerateBlocks(plc.BlockGroup)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, blockName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (block == null)
        {
            Console.WriteLine("找不到區塊：" + blockName);
            return 1;
        }

        try
        {
            block.SetAttribute("AutoNumber", false);
        }
        catch (Exception ex)
        {
            Console.WriteLine("AutoNumber 略過：" + ex.Message);
        }

        block.SetAttribute("Number", number);
        project.Save();
        Console.WriteLine("已設定編號：" + block.Name + " = " + block.Number);
        return 0;
    }

    private static void DeleteBlock(PlcSoftware plc, Project project, string blockName)
    {
        PlcBlock block = EnumerateBlocks(plc.BlockGroup)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, blockName, StringComparison.OrdinalIgnoreCase));

        if (block == null)
        {
            Console.WriteLine("找不到區塊，未刪除：" + blockName);
            return;
        }

        block.Delete();
        project.Save();
        Console.WriteLine("已刪除區塊：" + blockName);
    }

    private static void Compile(PlcSoftware plc)
    {
        ICompilable compilable = plc.GetService<ICompilable>();
        if (compilable == null)
        {
            Console.WriteLine("這個 PLC 不支援編譯服務。");
            return;
        }

        Console.WriteLine("正在編譯 PLC...");
        CompilerResult result = compilable.Compile();
        Console.WriteLine("編譯狀態：" + result.State + "／錯誤 " + result.ErrorCount + "／警告 " + result.WarningCount);
        PrintCompilerMessages(result.Messages, 1);
    }

    private static void PrintCompilerMessages(CompilerResultMessageComposition messages, int depth)
    {
        if (messages == null)
        {
            return;
        }

        foreach (CompilerResultMessage message in messages)
        {
            Console.WriteLine(new string(' ', depth * 2) + message.State + " " + message.Path + "：" + message.Description);
            PrintCompilerMessages(message.Messages, depth + 1);
        }
    }

    // The flat export loses the folder each item lived in, so record the tree
    // separately and replay it when importing into the rebuilt project.
    private static void DumpSoftwareTree(List<PlcSoftware> plcSoftwares, string root)
    {
        List<string> lines = new List<string>();

        foreach (PlcSoftware plc in plcSoftwares)
        {
            WalkBlockTree(plc.Name, string.Empty, plc.BlockGroup, lines);
            WalkTypeTree(plc.Name, string.Empty, plc.TypeGroup, lines);
            WalkTagTableTree(plc.Name, string.Empty, plc.TagTableGroup, lines);
        }

        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "software-tree.txt");
        File.WriteAllLines(path, lines, Encoding.UTF8);

        Console.WriteLine("已寫出結構清單：" + path);
        Console.WriteLine("項目數：" + lines.Count);
    }

    private static void WalkBlockTree(string plc, string prefix, PlcBlockGroup group, List<string> lines)
    {
        foreach (PlcBlock block in group.Blocks)
        {
            lines.Add("block\t" + plc + "\t" + prefix + "\t" + block.Name);
        }

        foreach (PlcBlockUserGroup child in group.Groups)
        {
            WalkBlockTree(plc, Join(prefix, child.Name), child, lines);
        }
    }

    private static void WalkTypeTree(string plc, string prefix, PlcTypeGroup group, List<string> lines)
    {
        foreach (PlcType type in group.Types)
        {
            lines.Add("type\t" + plc + "\t" + prefix + "\t" + type.Name);
        }

        foreach (PlcTypeUserGroup child in group.Groups)
        {
            WalkTypeTree(plc, Join(prefix, child.Name), child, lines);
        }
    }

    private static void WalkTagTableTree(string plc, string prefix, PlcTagTableGroup group, List<string> lines)
    {
        foreach (PlcTagTable table in group.TagTables)
        {
            lines.Add("tagtable\t" + plc + "\t" + prefix + "\t" + table.Name);
        }

        foreach (PlcTagTableUserGroup child in group.Groups)
        {
            WalkTagTableTree(plc, Join(prefix, child.Name), child, lines);
        }
    }

    private static string Join(string prefix, string name)
    {
        return prefix.Length == 0 ? name : prefix + "/" + name;
    }

    private sealed class ImportItem
    {
        public string Kind;
        public string Plc;
        public string Group;
        public string Name;
        public string File;
        public string LastError;
    }

    // System OB82 cannot be reliably recreated from exported XML; skip on batch import.
    private static readonly HashSet<string> SkipBlockImportNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Diagnostic error interrupt",
    };

    private static void ImportAll(Project project, List<PlcSoftware> plcSoftwares, string root, string onlyPlc)
    {
        if (!Directory.Exists(root))
        {
            Console.WriteLine("找不到範本庫資料夾：" + root);
            return;
        }

        Dictionary<string, string> groupOf = LoadTreeManifest(root);
        EnsureProjectLanguages(project, "en-US", "zh-TW", "id-ID");

        int totalOk = 0;
        int totalFailed = 0;

        foreach (string folder in Directory.GetDirectories(root, "PLC_*"))
        {
            string plcName = Path.GetFileName(folder).Substring(4);
            if (onlyPlc != null && !string.Equals(plcName, onlyPlc, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            PlcSoftware plc = plcSoftwares
                .FirstOrDefault(candidate => string.Equals(candidate.Name, plcName, StringComparison.OrdinalIgnoreCase));

            if (plc == null)
            {
                Console.WriteLine("略過（專案裡沒有這個 PLC）：" + plcName);
                continue;
            }

            Console.WriteLine(new string('=', 60));
            Console.WriteLine("PLC：" + plcName);

            totalOk += RunPhase(plc, "type", Path.Combine(folder, "Types"), groupOf, ref totalFailed);
            totalOk += RunPhase(plc, "tagtable", Path.Combine(folder, "TagTables"), groupOf, ref totalFailed);
            totalOk += RunPhase(plc, "block", Path.Combine(folder, "Blocks"), groupOf, ref totalFailed);

            project.Save();
            Console.WriteLine("已儲存專案（" + plcName + " 完成）。");
        }

        Console.WriteLine(new string('=', 60));
        Console.WriteLine("匯入總計：成功 " + totalOk + "、失敗 " + totalFailed);
    }

    private static void EnsureProjectLanguages(Project project, params string[] cultures)
    {
        LanguageComposition available = project.LanguageSettings.Languages;
        LanguageAssociation active = project.LanguageSettings.ActiveLanguages;

        foreach (string cultureName in cultures)
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
            Language language = available.Find(culture);
            if (language == null)
            {
                Console.WriteLine("專案語言表沒有：" + cultureName);
                continue;
            }

            if (active.Find(culture) != null)
            {
                continue;
            }

            active.Add(language);
            Console.WriteLine("已啟用專案語言：" + cultureName);
        }
    }

    private static Dictionary<string, string> LoadTreeManifest(string root)
    {
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(root, "software-tree.txt");
        if (!File.Exists(path))
        {
            Console.WriteLine("沒有 software-tree.txt，全部匯入到根目錄。");
            return map;
        }

        foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
        {
            string[] cells = line.Split('\t');
            if (cells.Length < 4)
            {
                continue;
            }

            map[cells[0] + "|" + cells[1] + "|" + cells[3]] = cells[2];
        }

        Console.WriteLine("結構清單載入：" + map.Count + " 筆");
        return map;
    }

    // Dependencies between blocks are not declared anywhere, so instead of
    // computing an order, retry the failures until a pass stops making progress.
    private static int RunPhase(
        PlcSoftware plc,
        string kind,
        string folder,
        Dictionary<string, string> groupOf,
        ref int totalFailed)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        List<ImportItem> pending = Directory.GetFiles(folder, "*.xml")
            .Select(file => new ImportItem
            {
                Kind = kind,
                Plc = plc.Name,
                Name = Path.GetFileNameWithoutExtension(file),
                File = file,
            })
            .Where(item => kind != "block" || !SkipBlockImportNames.Contains(item.Name))
            .ToList();

        foreach (ImportItem item in pending)
        {
            string key = kind + "|" + plc.Name + "|" + item.Name;
            item.Group = groupOf.ContainsKey(key) ? groupOf[key] : string.Empty;
        }

        Console.WriteLine("  " + kind + "：待匯入 " + pending.Count);

        int ok = 0;
        int round = 0;

        while (pending.Count > 0)
        {
            round++;
            List<ImportItem> failed = new List<ImportItem>();

            foreach (ImportItem item in pending)
            {
                try
                {
                    ImportOne(plc, item);
                    ok++;
                }
                catch (Exception ex)
                {
                    item.LastError = Flatten(ex);
                    failed.Add(item);
                }
            }

            Console.WriteLine("    第 " + round + " 輪：成功 " + (pending.Count - failed.Count) +
                "、待重試 " + failed.Count);

            if (failed.Count == pending.Count)
            {
                foreach (ImportItem item in failed)
                {
                    Console.WriteLine("    失敗 " + item.Name + "：" + item.LastError);
                }

                totalFailed += failed.Count;
                break;
            }

            pending = failed;
        }

        return ok;
    }

    private static void ImportOne(PlcSoftware plc, ImportItem item)
    {
        FileInfo file = new FileInfo(item.File);

        if (item.Kind == "type")
        {
            EnsureTypeGroup(plc, item.Group).Types.Import(file, ImportOptions.Override);
            return;
        }

        if (item.Kind == "tagtable")
        {
            EnsureTagTableGroup(plc, item.Group).TagTables.Import(file, ImportOptions.Override);
            return;
        }

        EnsureBlockGroup(plc, item.Group).Blocks.Import(file, ImportOptions.Override);
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

    private static PlcTypeGroup EnsureTypeGroup(PlcSoftware plc, string path)
    {
        PlcTypeGroup current = plc.TypeGroup;
        if (string.IsNullOrEmpty(path))
        {
            return current;
        }

        foreach (string part in path.Split('/'))
        {
            PlcTypeUserGroup next = current.Groups.Find(part) ?? current.Groups.Create(part);
            current = next;
        }

        return current;
    }

    private static PlcTagTableGroup EnsureTagTableGroup(PlcSoftware plc, string path)
    {
        PlcTagTableGroup current = plc.TagTableGroup;
        if (string.IsNullOrEmpty(path))
        {
            return current;
        }

        foreach (string part in path.Split('/'))
        {
            PlcTagTableUserGroup next = current.Groups.Find(part) ?? current.Groups.Create(part);
            current = next;
        }

        return current;
    }

    private static IEnumerable<PlcType> EnumerateTypes(PlcTypeGroup group)
    {
        foreach (PlcType type in group.Types)
        {
            yield return type;
        }

        foreach (PlcTypeUserGroup childGroup in group.Groups)
        {
            foreach (PlcType type in EnumerateTypes(childGroup))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<PlcBlock> EnumerateBlocks(PlcBlockGroup group)
    {
        foreach (PlcBlock block in group.Blocks)
        {
            yield return block;
        }

        foreach (PlcBlockUserGroup childGroup in group.Groups)
        {
            foreach (PlcBlock block in EnumerateBlocks(childGroup))
            {
                yield return block;
            }
        }
    }

    internal static IEnumerable<PlcBlock> EnumerateBlocksPublic(PlcBlockGroup group)
    {
        return EnumerateBlocks(group);
    }

    private static string GetExportFolder()
    {
        string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HmiExport");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static bool HasFlag(string[] args, string flag)
    {
        return args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));
    }

    public static string GetValue(string[] args, string prefix)
    {
        string match = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return match == null ? null : match.Substring(prefix.Length);
    }

    private static PlcSoftware ResolvePlc(List<PlcSoftware> plcSoftwares, string wantedPlc)
    {
        if (string.IsNullOrEmpty(wantedPlc))
        {
            if (plcSoftwares.Count == 1)
            {
                return plcSoftwares[0];
            }

            throw new InvalidOperationException(
                "請用 --plc:<名稱> 指定 PLC。目前有：" +
                string.Join(", ", plcSoftwares.Select(plc => plc.Name)));
        }

        PlcSoftware match = plcSoftwares.FirstOrDefault(
            candidate => string.Equals(candidate.Name, wantedPlc, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            throw new InvalidOperationException("找不到 PLC：" + wantedPlc);
        }

        return match;
    }
}

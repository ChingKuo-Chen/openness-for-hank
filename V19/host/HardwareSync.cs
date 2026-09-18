using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.HardwareCatalog;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Communication;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
#if !TIA_V19
using Siemens.Engineering.HW.CommunicationConnections;
#endif
#if !TIA_V19
using HwConnection = Siemens.Engineering.HW.CommunicationConnections.Connection;
using HwHmiConnection = Siemens.Engineering.HW.CommunicationConnections.HmiConnection;
#endif
using HmiSoftConnection = Siemens.Engineering.Hmi.Communication.Connection;

// Align 26037_Cusor PLC/RIO racks with ST-25017-TDY circuit drawing.
// HMI is left alone (changing the panel type would drop screens).
internal static class HardwareSync
{
    public static int UpdateRioImAu02(TiaPortal portal, Project project)
    {
        Console.WriteLine("RIO IM：實物組合包 AA02 → 機架 AU02（盒上 FW V6.1）。");
        string typeId = null;
        IList<CatalogEntry> hits = portal.HardwareCatalog.Find("6ES7 155-6AU02-0BN0");
        if (hits != null)
        {
            CatalogEntry match = hits.FirstOrDefault(e =>
                string.Equals(e.ArticleNumber, "6ES7 155-6AU02-0BN0", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Version, "V6.1", StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                typeId = match.TypeIdentifier;
            }
        }

        if (typeId == null)
        {
            Console.WriteLine("目錄沒有 6ES7 155-6AU02-0BN0 / V6.1。");
            return 1;
        }

        Console.WriteLine("目錄 -> " + typeId);
        string[] heads =
        {
            "25017_6B_RIO",
            "25017_12B_RIO",
            "25017_20B_RIO",
            "25017_24B_RIO"
        };

        foreach (string name in heads)
        {
            ChangeItemType(project, name, "6ES7 155-6AU01-0BN0", typeId);
        }

        project.Save();
        Console.WriteLine("已存。");
        foreach (string name in heads)
        {
            Device device = FindDeviceByItem(project, name);
            DeviceItem item = device == null
                ? null
                : HardwareBuilder.AllItems(device)
                    .FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine("  " + name + " = " + (item == null ? "找不到" : item.TypeIdentifier));
        }

        return 0;
    }

    public static int LayoutNetwork(Project project)
    {
        Console.WriteLine("Network 排版：左上 Main PLC，同類一列。");
        try
        {
            project.ShowHwEditor(View.Network);
        }
        catch (Exception ex)
        {
            Console.WriteLine("開 Network 檢視：" + Flatten(ex));
        }

        Device mainPlc = FindDeviceByItem(project, "25017_Main_PLC");
        if (mainPlc != null)
        {
            try
            {
                mainPlc.ShowInEditor(View.Network);
                Console.WriteLine("已對準 Main PLC。");
            }
            catch (Exception ex)
            {
                Console.WriteLine("ShowInEditor Main：" + Flatten(ex));
            }
        }

        List<LayoutSlot> slots = new List<LayoutSlot>();
        foreach (Device device in EnumerateDevices(project))
        {
            string label = NetworkLabel(device);
            slots.Add(new LayoutSlot
            {
                Device = device,
                Label = label,
                Row = NetworkRow(label, device),
                Order = NetworkOrder(label)
            });
        }

        slots.Sort(CompareLayout);
        DumpGraphicAttrs(slots.Count == 0 ? null : slots[0].Device);

        int originX = 40;
        int originY = 40;
        int stepX = 420;
        int stepY = 300;
        int moved = 0;
        int failed = 0;
        int lastRow = -1;
        int col = 0;
        foreach (LayoutSlot slot in slots)
        {
            if (slot.Row != lastRow)
            {
                lastRow = slot.Row;
                col = 0;
            }

            int x = originX + col * stepX;
            int y = originY + slot.Row * stepY;
            Console.WriteLine("  列" + slot.Row + " 欄" + col + " (" + x + "," + y + ") " +
                slot.Device.Name + " / " + slot.Label);
            if (TrySetGraphicPosition(slot.Device, x, y))
            {
                moved++;
            }
            else
            {
                failed++;
                Console.WriteLine("    設座標失敗");
            }

            col++;
        }

        project.Save();
        Console.WriteLine("已存。搬 " + moved + "／失敗 " + failed + "／共 " + slots.Count);
        try
        {
            project.ShowHwEditor(View.Network);
        }
        catch
        {
        }

        return failed == 0 && moved > 0 ? 0 : 1;
    }

    public static int ShowInNetwork(Project project, string name)
    {
        try
        {
            project.ShowHwEditor(View.Network);
        }
        catch (Exception ex)
        {
            Console.WriteLine("開 Network 檢視：" + Flatten(ex));
        }

        Device device = FindDeviceByItem(project, name);
        if (device == null)
        {
            Console.WriteLine("找不到 " + name);
            return 1;
        }

        try
        {
            device.ShowInEditor(View.Network);
            Console.WriteLine("已對準 " + name + " / " + device.Name);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ShowInEditor " + name + "：" + Flatten(ex));
            return 1;
        }
    }

    private sealed class LayoutSlot
    {
        public Device Device;
        public string Label;
        public int Row;
        public int Order;
    }

    private static int CompareLayout(LayoutSlot a, LayoutSlot b)
    {
        int row = a.Row.CompareTo(b.Row);
        if (row != 0)
        {
            return row;
        }

        int order = a.Order.CompareTo(b.Order);
        if (order != 0)
        {
            return order;
        }

        return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
    }

    private static string NetworkLabel(Device device)
    {
        DeviceItem cpu = FindCpu(device);
        if (cpu != null && !string.IsNullOrEmpty(cpu.Name))
        {
            return cpu.Name;
        }

        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            if (item.Name != null && item.Name.StartsWith("25017_", StringComparison.OrdinalIgnoreCase))
            {
                return item.Name;
            }
        }

        return device.Name ?? string.Empty;
    }

    private static int NetworkRow(string label, Device device)
    {
        string text = ((label ?? string.Empty) + " " + (device.Name ?? string.Empty) + " " +
            (device.TypeIdentifier ?? string.Empty)).ToUpperInvariant();
        if (text.IndexOf("MAIN_PLC", StringComparison.Ordinal) >= 0 ||
            text.IndexOf("MAIN_HMI", StringComparison.Ordinal) >= 0 ||
            text.IndexOf("OP2_HMI", StringComparison.Ordinal) >= 0)
        {
            return 0;
        }

        if (text.IndexOf("TF_PLC", StringComparison.Ordinal) >= 0 ||
            text.IndexOf("TF_HMI", StringComparison.Ordinal) >= 0 ||
            text.IndexOf("PF_PLC", StringComparison.Ordinal) >= 0)
        {
            return 1;
        }

        if (text.IndexOf("INSIDE", StringComparison.Ordinal) >= 0)
        {
            return 2;
        }

        if (text.IndexOf("RIO", StringComparison.Ordinal) >= 0 ||
            text.IndexOf("ET200", StringComparison.Ordinal) >= 0 ||
            text.IndexOf("ET 200", StringComparison.Ordinal) >= 0)
        {
            return 3;
        }

        if (text.IndexOf("DRIVE", StringComparison.Ordinal) >= 0 ||
            text.IndexOf("UNIDRIVE", StringComparison.Ordinal) >= 0 ||
            text.IndexOf("GSD:", StringComparison.Ordinal) >= 0)
        {
            return 4;
        }

        return 5;
    }

    private static int NetworkOrder(string label)
    {
        string text = (label ?? string.Empty).ToUpperInvariant();
        if (text.IndexOf("MAIN_PLC", StringComparison.Ordinal) >= 0)
        {
            return 0;
        }

        if (text.IndexOf("MAIN_HMI", StringComparison.Ordinal) >= 0)
        {
            return 1;
        }

        if (text.IndexOf("OP2_HMI", StringComparison.Ordinal) >= 0)
        {
            return 2;
        }

        if (text.IndexOf("TF_PLC", StringComparison.Ordinal) >= 0)
        {
            return 10;
        }

        if (text.IndexOf("TF_HMI", StringComparison.Ordinal) >= 0)
        {
            return 11;
        }

        if (text.IndexOf("PF_PLC", StringComparison.Ordinal) >= 0)
        {
            return 12;
        }

        if (text.IndexOf("6B", StringComparison.Ordinal) >= 0)
        {
            return 20;
        }

        if (text.IndexOf("12B", StringComparison.Ordinal) >= 0)
        {
            return 21;
        }

        if (text.IndexOf("20B", StringComparison.Ordinal) >= 0)
        {
            return 22;
        }

        if (text.IndexOf("24B", StringComparison.Ordinal) >= 0)
        {
            return 23;
        }

        if (text.IndexOf("CAP", StringComparison.Ordinal) >= 0)
        {
            return 24;
        }

        if (text.IndexOf("TF_DRIVE", StringComparison.Ordinal) >= 0)
        {
            return 25;
        }

        return 90;
    }

    private static void DumpGraphicAttrs(IEngineeringObject target)
    {
        if (target == null)
        {
            return;
        }

        Console.WriteLine("圖形屬性探測：" + target.GetType().Name);
        try
        {
            foreach (EngineeringAttributeInfo info in target.GetAttributeInfos())
            {
                string name = info.Name ?? string.Empty;
                if (name.IndexOf("Pos", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("Loc", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("X", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("Y", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("Graphic", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                object value = null;
                try
                {
                    value = target.GetAttribute(info.Name);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  " + info.Name + " 讀失敗：" + Flatten(ex));
                    continue;
                }

                Console.WriteLine("  " + info.Name + " = " +
                    (value == null ? "<null>" : value.GetType().FullName + " " + value));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  GetAttributeInfos：" + Flatten(ex));
        }
    }

    private static bool TrySetGraphicPosition(IEngineeringObject target, int x, int y)
    {
        if (TrySetXyObject(target, x, y))
        {
            return true;
        }

        foreach (string name in new[] { "PositionX", "X" })
        {
            if (TrySetNumber(target, name, x) &&
                (TrySetNumber(target, "PositionY", y) || TrySetNumber(target, "Y", y)))
            {
                return true;
            }
        }

        Device device = target as Device;
        if (device != null)
        {
            foreach (DeviceItem item in device.DeviceItems)
            {
                if (TrySetXyObject(item, x, y))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TrySetXyObject(IEngineeringObject target, int x, int y)
    {
        IList<EngineeringAttributeInfo> infos;
        try
        {
            infos = target.GetAttributeInfos();
        }
        catch
        {
            return false;
        }

        foreach (EngineeringAttributeInfo info in infos)
        {
            object current;
            try
            {
                current = target.GetAttribute(info.Name);
            }
            catch
            {
                continue;
            }

            if (current == null)
            {
                continue;
            }

            Type type = current.GetType();
            PropertyInfo px = type.GetProperty("X");
            PropertyInfo py = type.GetProperty("Y");
            if (px == null || py == null)
            {
                continue;
            }

            try
            {
                object next = Activator.CreateInstance(type, new object[]
                {
                    Convert.ChangeType(x, px.PropertyType),
                    Convert.ChangeType(y, py.PropertyType)
                });
                target.SetAttribute(info.Name, next);
                Console.WriteLine("    " + info.Name + " -> " + next);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    " + info.Name + " 設失敗：" + Flatten(ex));
            }
        }

        return false;
    }

    private static bool TrySetNumber(IEngineeringObject target, string name, int value)
    {
        try
        {
            target.SetAttribute(name, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int Apply(TiaPortal portal, Project project)
    {
        Console.WriteLine("依電路圖對硬體（ST-25017-TDY）。HMI 不改。");
        Console.WriteLine("專案：" + project.Path.FullName);

        string cpu1212 = CatalogType(portal, "6ES7 212-1AE40-0XB0");
        string sm1223Dc = CatalogType(portal, "6ES7 223-1BL32-0XB0");
        string cb1241 = CatalogType(portal, "6ES7 241-1CH30-1XB0");
        string cm1241 = CatalogType(portal, "6ES7 241-1CH32-0XB0");
        string sm1238 = CatalogType(portal, "6ES7 238-5XA32-0XB0");

        if (cpu1212 == null || sm1223Dc == null || cb1241 == null ||
            cm1241 == null || sm1238 == null)
        {
            Console.WriteLine("目錄缺件，停。上面缺的那幾個 TypeIdentifier 要先有。");
            return 1;
        }

        // 實物已改 6ES7 155-6AA02-0BN0 組合包；機架 IM 用 AU02。
        // 舊圖 AA01 / AU01 不要再當準則。換 IM 用 --update-rio-im。
        Console.WriteLine("RIO IM：實物 AA02 是組合包；機架用 AU02-0BN0（IM 155-6 PN ST）。");

        SyncMain(project, cb1241, cm1241, sm1223Dc, sm1238);
        project.Save();
        Console.WriteLine("已存（Main）。");

        SyncTfCpu(project, cpu1212);
        project.Save();
        Console.WriteLine("已存（TF CPU）。");

        foreach (string cpu in new[]
        {
            "25017_6B_Inside_PLC",
            "25017_12B_Inside_PLC",
            "25017_18B_Inside_PLC"
        })
        {
            SyncInsideSm(project, cpu, sm1223Dc);
        }

        project.Save();
        Console.WriteLine("已存（Inside SM）。");

        CopyInsideAs24B(project);
        CopyRioAs24B(project);
        project.Save();
        Console.WriteLine("已存（24B 複製）。");

        Rename18Bto20B(project);
        project.Save();
        Console.WriteLine("已存（18B→20B 名稱）。");

        CopyDriveAs24B(project);
        Align24BDriveTo(project, 220);
        project.Save();
        Console.WriteLine("已存（24B Drive）。");

        Console.WriteLine("電路圖硬體同步結束。接著 --dump-hardware 對一次。");
        return 0;
    }

    // 只對 Main 機架 / TF CPU，不拷 24B、不改站名。
    public static int AlignMainTf(TiaPortal portal, Project project)
    {
        Console.WriteLine("照圖對 Main 機架與 TF CPU（不拷裝置）。");
        string cpu1212 = CatalogType(portal, "6ES7 212-1AE40-0XB0");
        string sm1223Dc = CatalogType(portal, "6ES7 223-1BL32-0XB0");
        string cb1241 = CatalogType(portal, "6ES7 241-1CH30-1XB0");
        string cm1241 = CatalogType(portal, "6ES7 241-1CH32-0XB0");
        string sm1238 = CatalogType(portal, "6ES7 238-5XA32-0XB0");
        if (cpu1212 == null || sm1223Dc == null || cb1241 == null ||
            cm1241 == null || sm1238 == null)
        {
            Console.WriteLine("目錄缺件，停。");
            return 1;
        }

        SyncMain(project, cb1241, cm1241, sm1223Dc, sm1238);
        project.Save();
        Console.WriteLine("已存（Main 機架）。");

        SyncTfCpu(project, cpu1212);
        project.Save();
        Console.WriteLine("已存（TF CPU）。");
        DumpMainTfSummary(project);
        return 0;
    }

    // C 表 TF-260 表頭：slot2 = 6ES7 223-1BL32-0XB0（DI16/DQ16 DC → I4/Q4）。
    // 圖面／現活是 6ES7 221-1BH32-0XB0（只有 DI），所以 Q4 燈沒硬體。
    public static int AlignTfCTableSm(TiaPortal portal, Project project)
    {
        Console.WriteLine("TF 對 C 表模組：221-1BH32（只 DI）→ 223-1BL32（DI/DQ，Q4）。");
        string sm1223Dc = CatalogType(portal, "6ES7 223-1BL32-0XB0");
        if (sm1223Dc == null)
        {
            return 1;
        }

        Device tf = FindDeviceByItem(project, "25017_TF_PLC");
        if (tf == null)
        {
            Console.WriteLine("找不到 TF。");
            return 1;
        }

        if (FindByOrder(tf, "6ES7 223-1BL32-0XB0") != null)
        {
            Console.WriteLine("TF 已有 SM1223 DC/DC。");
            SetModuleStart(project, "25017_TF_PLC", "6ES7 223-1BL32-0XB0", 4);
            project.Save();
            Console.WriteLine("已存。");
            return 0;
        }

        DeviceItem sm1221 = FindByOrder(tf, "6ES7 221-1BH32-0XB0");
        if (sm1221 == null)
        {
            DeviceItem rack = FindRack(tf);
            if (rack == null)
            {
                Console.WriteLine("TF 找不到機架。");
                return 1;
            }

            PlugIfMissing(rack, sm1223Dc, "DI 16/DQ 16x24VDC_TF", 3);
        }
        else
        {
            Console.WriteLine("換 " + sm1221.Name + " " + sm1221.TypeIdentifier +
                " -> " + sm1223Dc);
            try
            {
                sm1221.ChangeType(sm1223Dc);
                Console.WriteLine("  ChangeType 完成。");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  ChangeType 失敗，改刪再插：" + Flatten(ex));
                int pos = sm1221.PositionNumber;
                DeviceItem rack = FindRack(tf);
                sm1221.Delete();
                if (rack != null)
                {
                    PlugIfMissing(rack, sm1223Dc, "DI 16/DQ 16x24VDC_TF", pos);
                }
            }
        }

        if (FindByOrder(tf, "6ES7 223-1BL32-0XB0") == null)
        {
            Console.WriteLine("換完還是沒有 223-1BL32。");
            return 1;
        }

        SetModuleStart(project, "25017_TF_PLC", "6ES7 223-1BL32-0XB0", 4);
        project.Save();
        Console.WriteLine("已存（TF SM）。");
        DumpMainTfSummary(project);
        return 0;
    }

    // C 表 Rotor inside：CPU 214-1AG40 + SM 223-1BH32（DI16/DQ16）。
    // 舊 --sync-drawing-hw 曾誤換成 BL32；四台 Inside 改回 BH32。
    public static int AlignInsideCTableSm(TiaPortal portal, Project project)
    {
        Console.WriteLine("Inside 對 C 表模組：223-1BL32 → 223-1BH32（四台）。");
        string smBh32 = CatalogType(portal, "6ES7 223-1BH32-0XB0");
        if (smBh32 == null)
        {
            Console.WriteLine("目錄沒有 6ES7 223-1BH32-0XB0。");
            return 1;
        }

        string[] plcs =
        {
            "25017_6B_Inside_PLC",
            "25017_12B_Inside_PLC",
            "25017_20B_Inside_PLC",
            "25017_24B_Inside_PLC"
        };

        int failed = 0;
        foreach (string plcName in plcs)
        {
            Device device = FindDeviceByItem(project, plcName);
            if (device == null)
            {
                Console.WriteLine(plcName + "：找不到裝置。");
                failed++;
                continue;
            }

            if (FindByOrder(device, "6ES7 223-1BH32-0XB0") != null)
            {
                Console.WriteLine(plcName + "：已是 223-1BH32。");
                continue;
            }

            DeviceItem sm = FindByOrder(device, "6ES7 223-1BL32-0XB0");
            if (sm == null)
            {
                Console.WriteLine(plcName + "：找不到 223-1BL32，也沒有 BH32。");
                failed++;
                continue;
            }

            Console.WriteLine(plcName + " " + sm.Name + " " + sm.TypeIdentifier + " -> " + smBh32);
            try
            {
                sm.ChangeType(smBh32);
                Console.WriteLine("  ChangeType 完成。");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  ChangeType 失敗，改刪再插：" + Flatten(ex));
                int pos = sm.PositionNumber;
                string name = sm.Name;
                DeviceItem rack = FindRack(device);
                sm.Delete();
                if (rack == null)
                {
                    Console.WriteLine("  找不到機架。");
                    failed++;
                    continue;
                }

                PlugIfMissing(rack, smBh32, name, pos);
            }

            if (FindByOrder(device, "6ES7 223-1BH32-0XB0") == null)
            {
                Console.WriteLine(plcName + "：換完還是沒有 BH32。");
                failed++;
                continue;
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + " SM）。");
        }

        return failed == 0 ? 0 : 1;
    }

    private static void DumpMainTfSummary(Project project)
    {
        foreach (string cpuName in new[] { "25017_Main_PLC", "25017_TF_PLC" })
        {
            Device device = FindDeviceByItem(project, cpuName);
            if (device == null)
            {
                Console.WriteLine(cpuName + "：找不到。");
                continue;
            }

            Console.WriteLine("=== " + cpuName + " / " + device.Name + " ===");
            foreach (DeviceItem item in HardwareBuilder.AllItems(device))
            {
                string order = TryAttr(item, "OrderNumber");
                if (string.IsNullOrEmpty(order) &&
                    (item.TypeIdentifier == null ||
                     item.TypeIdentifier.IndexOf("OrderNumber:", StringComparison.Ordinal) < 0))
                {
                    continue;
                }

                Console.WriteLine("  slot " + item.PositionNumber + "  " + item.Name +
                    "  " + (string.IsNullOrEmpty(item.TypeIdentifier) ? order : item.TypeIdentifier));
            }
        }
    }

    public static int DumpCpuDi(Project project, string cpuName)
    {
        Device device = FindDeviceByItem(project, cpuName);
        if (device == null)
        {
            Console.WriteLine("找不到 " + cpuName);
            return 1;
        }

        DeviceItem cpu = null;
        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            if (string.Equals(item.Name, cpuName, StringComparison.OrdinalIgnoreCase))
            {
                cpu = item;
                break;
            }
        }

        if (cpu == null)
        {
            Console.WriteLine("找不到 CPU 項目：" + cpuName);
            return 1;
        }

        DeviceItem di = null;
        foreach (DeviceItem child in cpu.DeviceItems)
        {
            Console.WriteLine("CPU 子項：" + child.Name + " pos=" + child.PositionNumber);
            if (child.Name != null &&
                child.Name.IndexOf("DI 14", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                di = child;
            }
        }

        if (di == null)
        {
            Console.WriteLine("找不到 DI 14。");
            return 1;
        }

        Console.WriteLine("=== CPU " + cpu.Name + " attrs ===");
        DumpAttrs(cpu);
        try
        {
            foreach (KeyValuePair<string, object> pair in V19Api.ReadWriteAttributes(cpu))
            {
                string key = pair.Key ?? "";
                if (key.IndexOf("Int", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    key.IndexOf("Chan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    key.IndexOf("Edge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    key.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    key.IndexOf("Event", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Console.WriteLine("  RW " + key + " = " +
                        (pair.Value == null ? "<null>" : pair.Value.ToString()));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  CPU RW：" + Flatten(ex));
        }

        IEngineeringObject cpuObj = cpu;
        try
        {
            foreach (EngineeringCompositionInfo info in cpuObj.GetCompositionInfos())
            {
                Console.WriteLine("  CPU composition " + info.Name + " / " +
                    V19Api.TypeName(info));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  CPU compositions：" + Flatten(ex));
        }

        HwIdentifierController hwIds = cpu.GetService<HwIdentifierController>();
        if (hwIds != null)
        {
            Console.WriteLine("=== CPU HwIdentifiers " + hwIds.RegisteredHwIdentifiers.Count + " ===");
            foreach (HwIdentifier id in hwIds.RegisteredHwIdentifiers)
            {
                Console.WriteLine("  HW " + id);
                DumpAttrs(id);
            }
        }

        DumpChannels("CPU", cpu);
        DumpChannels("DI", di);

        Console.WriteLine("=== DI " + di.Name + " ===");
        DumpAttrs(di);
        DumpKnownServices(di);
        DumpInvokes(di);
        DumpServiceInfos(di);
        GsdDeviceItem gsd = di.GetService<GsdDeviceItem>();
        if (gsd != null)
        {
            Console.WriteLine("  --- GsdDeviceItem ---");
            DumpAttrs(gsd);
            DumpInvokes(gsd);
        }

        string[] guesses =
        {
            "HardwareInterrupt",
            "HardwareInterruptActive",
            "HardwareInterruptRisingEdgeActive",
            "HardwareInterruptFallingEdgeActive",
            "Channel.8.HardwareInterrupt",
            "Channel.9.HardwareInterrupt",
            "Channel.10.HardwareInterrupt",
            "Channel.11.HardwareInterrupt",
            "IoChannel_8.HardwareInterrupt",
            "IoChannel_9.HardwareInterrupt",
        };
        foreach (string name in guesses)
        {
            TryGetNamed(di, name);
            if (gsd != null)
            {
                TryGetNamed(gsd, name);
            }
        }

        foreach (Address address in di.Addresses)
        {
            Console.WriteLine("  --- Address " + address.IoType + " " +
                address.StartAddress + "+" + address.Length + " ---");
            DumpAttrs(address);
            DumpInvokes(address);
            foreach (string name in guesses)
            {
                TryGetNamed(address, name);
            }
        }

        foreach (DeviceItem child in di.DeviceItems)
        {
            Console.WriteLine("  DI 子項：" + child.Name);
            DumpAttrs(child);
        }

        return 0;
    }

    public static bool TryEnableHomeRisingInterrupts(Project project)
    {
        Console.WriteLine("Main 板載 DI I1.1/I1.2/I1.3 開上升沿硬體中斷。");
        Device device = FindDeviceByItem(project, "25017_Main_PLC");
        if (device == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC 裝置。");
            return false;
        }

        DeviceItem di = null;
        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            if (item.Name != null &&
                item.Name.IndexOf("DI 14", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                di = item;
                break;
            }
        }

        if (di == null)
        {
            Console.WriteLine("找不到 DI 14。");
            return false;
        }

        int[] channels = { 8, 9, 10, 11 };
        string[] stations = { "6B I1.0", "12B I1.1", "20B I1.2", "24B I1.3" };
        bool allOk = true;
        for (int i = 0; i < channels.Length; i++)
        {
            if (!TryEnableHomeRisingInterrupt(project, channels[i], stations[i]))
            {
                allOk = false;
            }
        }

        return allOk;
    }

    public static bool TryEnableHomeRisingInterrupt(Project project, int channelNumber, string label)
    {
        Device device = FindDeviceByItem(project, "25017_Main_PLC");
        if (device == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC 裝置。");
            return false;
        }

        DeviceItem di = null;
        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            if (item.Name != null &&
                item.Name.IndexOf("DI 14", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                di = item;
                break;
            }
        }

        if (di == null)
        {
            Console.WriteLine("找不到 DI 14。");
            return false;
        }

        Channel channel = null;
        foreach (Channel candidate in di.Channels)
        {
            if (candidate.IoType == ChannelIoType.Input &&
                candidate.Number == channelNumber)
            {
                channel = candidate;
                break;
            }
        }

        if (channel == null)
        {
            Console.WriteLine("  沒有通道 " + channelNumber + "（" + label + "）");
            return false;
        }

        return TrySetChannelRisingInterrupt(channel, label);
    }

    public static void DumpMainHsc(Project project)
    {
        Device device = FindDeviceByItem(project, "25017_Main_PLC");
        if (device == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC 裝置。");
            return;
        }

        Console.WriteLine("=== Main HSC ===");
        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            string name = item.Name ?? string.Empty;
            if (name.IndexOf("HSC", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("Enc", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("Een_", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            string addr = "";
            foreach (Address address in item.Addresses)
            {
                addr += " " + address.IoType + " " + address.StartAddress + "+" + address.Length;
            }

            Console.WriteLine("  " + name + addr);
            string[] interesting =
            {
                "Name",
                "AuthorName",
                "OperatingMode",
                "Enabled",
                "Enable",
                "HscType",
                "Type",
                "Input1",
                "Input2",
                "ClockInput",
                "DirectionInput",
            };
            foreach (string attr in interesting)
            {
                try
                {
                    object value = item.GetAttribute(attr);
                    Console.WriteLine("    " + attr + " = " +
                        (value == null ? "<null>" : value.ToString()));
                }
                catch
                {
                }
            }
        }
    }

    public static bool RenameMainHsc6(Project project)
    {
        Device device = FindDeviceByItem(project, "25017_Main_PLC");
        if (device == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC 裝置。");
            return false;
        }

        DeviceItem hsc = null;
        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            if (string.Equals(item.Name, "Enc_rotor_6B", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  已是 Enc_rotor_6B");
                return true;
            }

            if (string.Equals(item.Name, "HSC_6", StringComparison.OrdinalIgnoreCase))
            {
                hsc = item;
            }
        }

        if (hsc == null)
        {
            Console.WriteLine("Main 找不到 HSC_6。");
            return false;
        }

        try
        {
            hsc.SetAttribute("Name", "Enc_rotor_6B");
            Console.WriteLine("  硬體項：HSC_6 -> " + hsc.Name);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  HSC_6 改名失敗：" + Flatten(ex));
            return false;
        }
    }

    public static bool TrySetHomeInputFilter(Project project)
    {
        Console.WriteLine("歸零近接 I1.0–I1.3 輸入濾波改 6.4 ms（近接抖動）。I0.0–I0.3 計尺／1B 不動。");
        Device device = FindDeviceByItem(project, "25017_Main_PLC");
        if (device == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC 裝置。");
            return false;
        }

        DeviceItem cpu = null;
        DeviceItem di = null;
        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            if (string.Equals(item.Name, "25017_Main_PLC", StringComparison.OrdinalIgnoreCase))
            {
                cpu = item;
            }

            if (item.Name != null &&
                item.Name.IndexOf("DI 14", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                di = item;
            }
        }

        if (di == null)
        {
            Console.WriteLine("找不到 DI 14。");
            return false;
        }

        string[] names =
        {
            "InputDelay",
            "InputDelay2",
            "InputDelayGroup2",
            "InputDelay_2",
            "InputDelayCH8",
            "InputDelayCH9",
            "InputFilter",
            "InputFilter2",
        };

        bool wrote = false;
        foreach (DeviceItem target in new[] { di, cpu })
        {
            if (target == null)
            {
                continue;
            }

            foreach (string name in names)
            {
                try
                {
                    object current = target.GetAttribute(name);
                    Console.WriteLine("  GET " + target.Name + "." + name + " = " +
                        (current == null ? "<null>" : current.ToString()));
                    target.SetAttribute(name, InputDelay.Value6Dot4ms);
                    Console.WriteLine("  SET " + target.Name + "." + name + " = 6.4 ms");
                    wrote = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  " + target.Name + "." + name + "：" + Flatten(ex));
                }
            }
        }

        Channel home = null;
        foreach (Channel channel in di.Channels)
        {
            if (channel.IoType == ChannelIoType.Input && channel.Number == 9)
            {
                home = channel;
                break;
            }
        }

        if (home != null)
        {
            foreach (string name in new[] { "InputDelay", "InputFilter" })
            {
                try
                {
                    object current = home.GetAttribute(name);
                    Console.WriteLine("  GET ch9." + name + " = " +
                        (current == null ? "<null>" : current.ToString()));
                    home.SetAttribute(name, InputDelay.Value6Dot4ms);
                    Console.WriteLine("  SET ch9." + name + " = 6.4 ms");
                    wrote = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  ch9." + name + "：" + Flatten(ex));
                }
            }
        }

        return wrote;
    }

    private static bool TrySetChannelRisingInterrupt(Channel channel, string label)
    {
        string[] names =
        {
            "HardwareInterruptRisingEdgeActive",
            "HardwareInterrupt",
            "HardwareInterruptActive",
            "InputDelay",
            "PulseCatch",
            "PulseCatchActive",
            "Invert",
            "RisingEdge",
            "RisingEdgeActive",
            "EnableRisingEdge",
            "Event",
            "EventName",
        };

        bool wrote = false;
        foreach (string name in names)
        {
            try
            {
                object current = channel.GetAttribute(name);
                Console.WriteLine("  " + label + " GET " + name + " = " +
                    (current == null ? "<null>" : current.ToString()));
            }
            catch (Exception ex)
            {
                Console.WriteLine("  " + label + " GET " + name + "：" + Flatten(ex));
            }
        }

        try
        {
            channel.SetAttribute(
                "HardwareInterruptRisingEdgeActive",
                HardwareInterruptRisingEdgeActive.Active);
            Console.WriteLine("  " + label + " SET HardwareInterruptRisingEdgeActive=Active");
            wrote = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  " + label + " SET RisingEdge：" + Flatten(ex));
        }

        try
        {
            channel.SetAttribute("HardwareInterrupt", HardwareInterrupt.Active);
            Console.WriteLine("  " + label + " SET HardwareInterrupt=Active");
            wrote = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  " + label + " SET HardwareInterrupt：" + Flatten(ex));
        }

        return wrote;
    }

    private static void DumpChannels(string label, DeviceItem item)
    {
        Console.WriteLine("=== " + label + " Channels " + item.Channels.Count + " ===");
        foreach (Channel channel in item.Channels)
        {
            Console.WriteLine("  " + label + " ch Type=" + channel.Type +
                " Io=" + channel.IoType +
                " No=" + channel.Number);
            DumpAttrs(channel);
            foreach (string name in new[]
            {
                "HardwareInterrupt",
                "HardwareInterruptActive",
                "HardwareInterruptRisingEdgeActive",
                "HardwareInterruptFallingEdgeActive",
            })
            {
                TryGetNamed(channel, name);
            }
        }
    }

    private static void DumpServiceInfos(DeviceItem item)
    {
        IEngineeringServiceProvider provider = item as IEngineeringServiceProvider;
        if (provider == null)
        {
            Console.WriteLine("  serviceInfos：不是 IEngineeringServiceProvider");
            return;
        }

        try
        {
            foreach (EngineeringServiceInfo info in provider.GetServiceInfos())
            {
                Console.WriteLine("  serviceInfo " +
                    V19Api.TypeName(info));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  serviceInfos：" + Flatten(ex));
        }
    }

    private static void TryGetNamed(IEngineeringObject target, string name)
    {
        try
        {
            object value = target.GetAttribute(name);
            Console.WriteLine("  GET " + target.GetType().Name + "." + name + " = " +
                (value == null ? "<null>" : value.ToString()));
        }
        catch (Exception ex)
        {
            Console.WriteLine("  GET " + target.GetType().Name + "." + name + "：" + Flatten(ex));
        }
    }

    public static int ProbeDrive(TiaPortal portal, Project project, string itemName)
    {
        Console.WriteLine("查 Drive 改址屬性：" + itemName);
        Device device = FindDeviceByItem(project, itemName);
        if (device == null)
        {
            Console.WriteLine("找不到 " + itemName);
            return 1;
        }

        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            bool hasAddr = false;
            foreach (Address address in item.Addresses)
            {
                if (address.StartAddress >= 0)
                {
                    hasAddr = true;
                    break;
                }
            }

            if (!hasAddr)
            {
                continue;
            }

            Console.WriteLine("=== " + item.Name + " pos=" + item.PositionNumber + " ===");
            DumpAttrs(item);
            DumpKnownServices(item);
            DumpInvokes(item);
            GsdDeviceItem gsd = item.GetService<GsdDeviceItem>();
            if (gsd != null)
            {
                Console.WriteLine("  --- GsdDeviceItem ---");
                DumpAttrs(gsd);
                DumpInvokes(gsd);
            }

            foreach (Address address in item.Addresses)
            {
                if (address.StartAddress < 0)
                {
                    continue;
                }

                Console.WriteLine("  --- Address " + address.IoType + " " +
                    address.StartAddress + "+" + address.Length + " ---");
                DumpAttrs(address);
                DumpInvokes(address);
                foreach (AddressController controller in address.AddressControllers)
                {
                    Console.WriteLine("  --- AddressController ---");
                    DumpAttrs(controller);
                    DumpInvokes(controller);
                }
            }
        }

        return 0;
    }

    public static int ContinueDrawingHw(TiaPortal portal, Project project)
    {
        Console.WriteLine("繼續建：18B→20B 硬體名 + 24B Rotor Drive。");
        Console.WriteLine("專案：" + project.Path.FullName);

        Rename18Bto20B(project);
        project.Save();
        Console.WriteLine("已存（18B→20B 名稱）。");

        CopyDriveAs24B(project);
        Align24BDriveTo(project, 220);
        project.Save();
        Console.WriteLine("已存（24B Drive）。");
        return 0;
    }

    public static int Align24BDrive220(TiaPortal portal, Project project)
    {
        Console.WriteLine("24B Rotor Drive → I220/Q220（Energy Meter 不動）。");
        Align24BDriveTo(project, 220);
        project.Save();
        Console.WriteLine("已存。");
        OverviewAddresses(portal, project, "25017_Main_PLC");
        return 0;
    }

    private static void Align24BDriveTo(Project project, int iBase)
    {
        int park = iBase + 380;
        int finalDelta = iBase - 160;
        int parkDelta = park - 160;
        AlignDriveFromTemplate(project, "25017_20B_Rotor_Drive", "25017_24B_Rotor_Drive", parkDelta);
        AlignDriveFromTemplate(project, "25017_20B_Rotor_Drive", "25017_24B_Rotor_Drive", finalDelta);
    }

    public static int MoveEnergyMeter800(TiaPortal portal, Project project)
    {
        Console.WriteLine("Energy Meter → I800 / Q800（24B Drive 不動）。");
        MoveEnergyMeterTo(project, 800, 800);
        project.Save();
        Console.WriteLine("已存。");
        OverviewAddresses(portal, project, "25017_Main_PLC");
        return 0;
    }

    private static void MoveEnergyMeterTo(Project project, int iBase, int qBase)
    {
        Device device = FindDeviceByItem(project, "25017_Main_PLC");
        if (device == null)
        {
            Console.WriteLine("找不到 Main PLC。");
            return;
        }

        List<DeviceItem> items = new List<DeviceItem>();
        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            string name = item.Name ?? string.Empty;
            if (string.Equals(name, "SM 1238_Energy", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Energy Meter_", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(item);
            }
        }

        if (items.Count == 0)
        {
            Console.WriteLine("找不到 SM 1238_Energy。");
            return;
        }

        List<Address> inputs = new List<Address>();
        List<Address> outputs = new List<Address>();
        Dictionary<Address, string> names = new Dictionary<Address, string>();
        foreach (DeviceItem item in items)
        {
            foreach (Address address in item.Addresses)
            {
                if (address.StartAddress < 0)
                {
                    continue;
                }

                names[address] = item.Name;
                string io = address.IoType.ToString();
                if (io.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    inputs.Add(address);
                }
                else if (io.IndexOf("Output", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    outputs.Add(address);
                }
            }
        }

        inputs.Sort((a, b) => EnergyPackOrder(names[a]).CompareTo(EnergyPackOrder(names[b])));
        outputs.Sort((a, b) => EnergyPackOrder(names[a]).CompareTo(EnergyPackOrder(names[b])));

        AssignPackedStarts(inputs, iBase, names);
        AssignPackedStarts(outputs, qBase, names);
    }

    private static int EnergyPackOrder(string name)
    {
        if (name != null && name.StartsWith("Energy Meter_", StringComparison.OrdinalIgnoreCase))
        {
            int n;
            if (int.TryParse(name.Substring("Energy Meter_".Length), out n))
            {
                return n;
            }

            return 50;
        }

        return 0;
    }

    private static void AssignPackedStarts(
        List<Address> addresses,
        int finalBase,
        Dictionary<Address, string> names)
    {
        int next = finalBase;
        foreach (Address address in addresses)
        {
            int bytes = AddressBytes(address);
            if (OverlapsHsc(next, bytes) || next + bytes > 1024)
            {
                Console.WriteLine("  " + names[address] + " 從 " + next +
                    " 會超出過程映像／HSC，略過。");
                continue;
            }

            SetAddressStart(address, names[address], next);
            next += bytes;
        }
    }

    private static bool OverlapsHsc(int start, int bytes)
    {
        int end = start + bytes - 1;
        return start <= 1023 && end >= 1000;
    }

    private static int AddressBytes(Address address)
    {
        int bits = address.Length;
        if (bits <= 0)
        {
            return 1;
        }

        return (bits + 7) / 8;
    }

    private static void SetAddressStart(Address address, string name, int start)
    {
        int old = address.StartAddress;
        if (old == start)
        {
            Console.WriteLine("  " + name + " " + address.IoType + " 已是 " + start);
            return;
        }

        try
        {
            address.SetAttribute("StartAddress", start);
            Console.WriteLine("  " + name + " " + address.IoType + " " + old + " -> " + start);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  改址失敗 " + name + " " + address.IoType +
                " " + old + "：" + Flatten(ex));
        }
    }

    private static void MoveExactStart(
        Project project,
        string cpuName,
        string itemName,
        string ioType,
        int from,
        int to)
    {
        Device device = FindDeviceByItem(project, cpuName);
        if (device == null)
        {
            Console.WriteLine(cpuName + "：找不到。");
            return;
        }

        DeviceItem item = HardwareBuilder.AllItems(device)
            .FirstOrDefault(i => string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            Console.WriteLine(cpuName + " 找不到 " + itemName);
            return;
        }

        foreach (Address address in item.Addresses)
        {
            if (address.StartAddress != from)
            {
                continue;
            }

            if (address.IoType.ToString().IndexOf(ioType, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            try
            {
                address.SetAttribute("StartAddress", to);
                Console.WriteLine("  " + itemName + " " + address.IoType +
                    " " + from + " -> " + to);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  改 " + itemName + " " + from + " 失敗：" + Flatten(ex));
            }
        }
    }

    public static int FixIo(TiaPortal portal, Project project)
    {
        Console.WriteLine("補電路圖硬體的位址／PN。");
        SetModuleStart(project, "25017_6B_Inside_PLC", "6ES7 223-1BL32-0XB0", 2);
        SetModuleStart(project, "25017_12B_Inside_PLC", "6ES7 223-1BL32-0XB0", 2);
        SetModuleStart(project, "25017_20B_Inside_PLC", "6ES7 223-1BL32-0XB0", 2);
        SetModuleStart(project, "25017_24B_Inside_PLC", "6ES7 223-1BL32-0XB0", 2);
        ConnectRioToMain(project, "25017_24B_RIO", "192.168.0.23");
        // Energy Meter_1=I274 長度 32 byte，Meter2/3 接到 I359。
        // 24B ZigBee 只能放 I360。I500 是 PF I-device，電表不能搬去 500。
        SetRioSlot(project, "25017_24B_RIO", 4, 54);
        SetRioSlot(project, "25017_24B_RIO", 3, 52);
        SetRioSlot(project, "25017_24B_RIO", 2, 50);
        SetRioSlot(project, "25017_24B_RIO", 6, 52);
        SetRioSlot(project, "25017_24B_RIO", 5, 50);
        SetRioSlot(project, "25017_24B_RIO", 7, 80);
        SetRioSlot(project, "25017_24B_RIO", 1, 360);
        AlignMainSm3(project);

        project.Save();
        Console.WriteLine("已存（位址／PN）。");
        return 0;
    }

    public static int ProbeIo(TiaPortal portal, Project project)
    {
        Console.WriteLine("查位址／ET200SP 屬性。");
        DumpLowAddresses(project);
        Device rio = FindDeviceByItem(project, "25017_24B_RIO");
        if (rio == null)
        {
            Console.WriteLine("找不到 24B RIO。");
            return 1;
        }

        DeviceItem di = HardwareBuilder.AllItems(rio)
            .FirstOrDefault(i => string.Equals(i.Name, "DI 16x24VDC ST_1", StringComparison.OrdinalIgnoreCase));
        if (di == null)
        {
            Console.WriteLine("找不到 24B DI1。");
            return 1;
        }

        Console.WriteLine("=== DeviceItem " + di.Name + " 屬性 ===");
        DumpAttrs(di);
        DumpKnownServices(di);
        DumpInvokes(di);
        foreach (DeviceItem child in di.DeviceItems)
        {
            Console.WriteLine("=== child " + child.Name + " 屬性 ===");
            DumpAttrs(child);
            DumpKnownServices(child);
            DumpInvokes(child);
            foreach (Address address in child.Addresses)
            {
                Console.WriteLine("=== Address " + address.IoType + " start=" +
                    address.StartAddress + " len=" + address.Length + " ===");
                DumpAttrs(address);
                DumpInvokes(address);
                foreach (AddressController controller in address.AddressControllers)
                {
                    Console.WriteLine("  AddressController owner=" +
                        (controller.OwnedBy == null ? "?" : controller.OwnedBy.Name));
                    DumpAttrs(controller);
                }
            }
        }

        DumpTransferAreas(project);
        DumpControllerAddresses(project);
        DumpRegistered(project, "25017_Main_PLC");
        DumpChannels(project, "25017_Main_PLC");
        return 0;
    }

    public static int MovePfPn(TiaPortal portal, Project project)
    {
        Console.WriteLine("PF I-device Transfer areas（PF PLC → X1 → I-device communication）。");
        Console.WriteLine("圖面／GUI：From_Main_PLC 控制器 Q6–10；To_Main_PLC 控制器 I8–12。改到 Q500 / I500。");
        ProbePfIdevice(project);
        MoveIdevicePartner(project, "25017_PF_PLC", 500);
        MoveIdevicePartner(project, "25017_Main_PLC", 500);
        AlignMainSm3(project);
        project.Save();
        Console.WriteLine("已存。接著 Overview Main。");
        OverviewAddresses(portal, project, "25017_Main_PLC");
        return 0;
    }

    public static int ProbePfPn(TiaPortal portal, Project project)
    {
        ProbePfIdevice(project);
        DumpTransferAreaTypes();
        ExportPfCax(project);
        return 0;
    }

    public static int OverviewAddresses(TiaPortal portal, Project project, string cpuName)
    {
        Console.WriteLine("Overview of addresses：" + cpuName);
        Device cpuDevice = FindDeviceByItem(project, cpuName);
        if (cpuDevice == null)
        {
            Console.WriteLine("找不到 " + cpuName);
            return 1;
        }

        HashSet<Device> devices = new HashSet<Device>();
        devices.Add(cpuDevice);
        foreach (DeviceItem item in ReachableItems(cpuDevice))
        {
            NetworkInterface net = item.GetService<NetworkInterface>();
            if (net == null)
            {
                continue;
            }

            foreach (IoController controller in net.IoControllers)
            {
                if (controller.IoSystem == null)
                {
                    continue;
                }

                foreach (IoConnector connector in controller.IoSystem.ConnectedIoDevices)
                {
                    Device remote = DeviceOf(connector);
                    if (remote != null)
                    {
                        devices.Add(remote);
                    }
                }
            }
        }

        List<string> rows = new List<string>();
        foreach (Device device in devices)
        {
            string deviceLabel = device.Name;
            foreach (DeviceItem item in ReachableItems(device))
            {
                foreach (Address address in item.Addresses)
                {
                    if (address.StartAddress < 0)
                    {
                        continue;
                    }

                    rows.Add(FormatOverviewRow(address, item.Name, deviceLabel));
                }

                NetworkInterface net = item.GetService<NetworkInterface>();
                if (net == null)
                {
                    continue;
                }

                Console.WriteLine("  net " + deviceLabel + "/" + item.Name +
                    " TransferAreas=" + net.TransferAreas.Count +
                    " Items=" + item.Items.Count);
                foreach (TransferArea area in net.TransferAreas)
                {
                    foreach (Address address in area.PartnerAddresses)
                    {
                        if (address.StartAddress < 0)
                        {
                            continue;
                        }

                        rows.Add(FormatOverviewRow(
                            address, area.Name + " partner", deviceLabel));
                    }

                    foreach (Address address in area.LocalAddresses)
                    {
                        if (address.StartAddress < 0)
                        {
                            continue;
                        }

                        rows.Add(FormatOverviewRow(
                            address, area.Name + " local", deviceLabel));
                    }
                }
            }
        }

        rows.Sort();
        Console.WriteLine("Type  from-to    bytes  module  [device]");
        foreach (string row in rows)
        {
            Console.WriteLine(row);
        }

        Console.WriteLine("共 " + rows.Count + " 筆。");
        return 0;
    }

    public static int CopyOp2Hmi(TiaPortal portal, Project project)
    {
        Console.WriteLine("OP2 HMI：畫面與連線 Tag 跟 OP1 相同，只改 IP。不換面板型號。");
        Console.WriteLine("專案：" + project.Path.FullName);

        Device op1 = FindDeviceByItem(project, "25017_Main_HMI");
        if (op1 == null)
        {
            Console.WriteLine("找不到 25017_Main_HMI。");
            return 1;
        }

        Device op2 = FindDeviceByItem(project, "25017_OP2_HMI");
        if (op2 == null)
        {
            Device made = CopyDevice(project, op1);
            if (made == null)
            {
                return 1;
            }

            RenameHmiDevice(made, "25017_OP2_HMI");
            SetNodeIp(made, "X1", "192.168.0.3");
            SetNodeIp(made, "X3", "192.168.1.3");
            ConnectNodeToSubnet(project, made, "X1", "PN/IE_1");
            op2 = made;
        }
        else
        {
            Console.WriteLine("25017_OP2_HMI 已存在，檢查連線。");
        }

        Device main = FindDeviceByItem(project, "25017_Main_PLC");
        Console.WriteLine("=== OP1 ===");
        DescribeHmiSoftware(op1);
        Console.WriteLine("=== OP2 ===");
        DescribeHmiSoftware(op2);
        EnsureHmiPlcConnection(op2, main);
        Console.WriteLine("=== OP2 確認 ===");
        DescribeHmiSoftware(op2);
        project.Save();
        LadBlockTools.DumpHardware(project);
        Console.WriteLine("已存。OP1 25017_Main_HMI = 192.168.0.2；OP2 25017_OP2_HMI = 192.168.0.3。");
        return 0;
    }

    public static int AlignDrawingHmi(TiaPortal portal, Project project)
    {
        Console.WriteLine("依圖換 HMI。圖：Main 2×KTP1200 Basic；TF 1×KTP400 Basic。");
        Console.WriteLine("專案：" + project.Path.FullName);

        string ktp1200 = CatalogHmiType(portal, "6AV2 123-2MB03-0AX0", "KTP1200");
        string ktp400 = CatalogHmiType(portal, "6AV2 123-2DB03-0AX0", "KTP400");
        if (ktp1200 == null || ktp400 == null)
        {
            return 1;
        }

        ChangeHmiPanel(project, "25017_Main_HMI", ktp1200, "192.168.0.2");
        project.Save();
        Console.WriteLine("已存 OP1。");

        ChangeHmiPanel(project, "25017_OP2_HMI", ktp1200, "192.168.0.3");
        project.Save();
        Console.WriteLine("已存 OP2。");

        ChangeHmiPanel(project, "25017_TF_HMI", ktp400, "192.168.0.151");
        project.Save();
        Console.WriteLine("已存 TF HMI。");

        LadBlockTools.DumpHardware(project);
        return 0;
    }

    private static string FormatOverviewRow(Address address, string module, string device)
    {
        int bits = address.Length;
        int bytes = bits % 8 == 0 && bits > 0 ? bits / 8 : bits;
        int from = address.StartAddress;
        int to = from + bytes - 1;
        string io = address.IoType.ToString();
        if (io.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            io = "I";
        }
        else if (io.IndexOf("Output", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            io = "Q";
        }

        return string.Format(
            "  {0,-4} {1,4}-{2,-4} {3,4}B  {4}  [{5}]",
            io, from, to, bytes, module, device);
    }

    private static IEnumerable<DeviceItem> ReachableItems(Device device)
    {
        HashSet<DeviceItem> seen = new HashSet<DeviceItem>();
        foreach (DeviceItem item in device.DeviceItems)
        {
            foreach (DeviceItem extra in WalkReachable(item, seen))
            {
                yield return extra;
            }
        }
    }

    private static IEnumerable<DeviceItem> WalkReachable(DeviceItem item, HashSet<DeviceItem> seen)
    {
        if (!seen.Add(item))
        {
            yield break;
        }

        yield return item;
        foreach (DeviceItem child in item.DeviceItems)
        {
            foreach (DeviceItem extra in WalkReachable(child, seen))
            {
                yield return extra;
            }
        }

        foreach (DeviceItem extra in item.Items)
        {
            foreach (DeviceItem nested in WalkReachable(extra, seen))
            {
                yield return nested;
            }
        }
    }

    private static Device DeviceOf(IEngineeringObject target)
    {
        IEngineeringObject current = target;
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

    private static void ProbePfIdevice(Project project)
    {
        Console.WriteLine("=== PF I-device 深挖 ===");
        DumpPnInterface(project, "25017_PF_PLC");
        Console.WriteLine("=== TF I-device（對照） ===");
        DumpPnInterface(project, "25017_TF_PLC");
        Console.WriteLine("=== Main IO system 底下的 I-device ===");
        DumpMainConnectedIo(project);
        DumpMainBytes(project, 0, 20);
        TryCreatePfTransferAreas(project, false);
    }

    private static void TryCreatePfTransferAreas(Project project, bool keep)
    {
        Device device = FindDeviceByItem(project, "25017_PF_PLC");
        if (device == null)
        {
            return;
        }

        Console.WriteLine("=== TryCreate CD TransferAreas（keep=" + keep + "） ===");
        foreach (DeviceItem item in ReachableItems(device))
        {
            NetworkInterface net = item.GetService<NetworkInterface>();
            if (net == null || net.IoConnectors.Count == 0)
            {
                continue;
            }

            Console.WriteLine("  net " + item.Name + " mode=" + net.InterfaceOperatingMode +
                " areas=" + net.TransferAreas.Count);
            TryOneArea(net, "From_Main_PLC", keep);
            TryOneArea(net, "To_Main_PLC", keep);
            Console.WriteLine("  after Create count=" + net.TransferAreas.Count);
            foreach (TransferArea area in net.TransferAreas)
            {
                DumpArea(area, "    ");
            }
        }
    }

    private static void TryOneArea(NetworkInterface net, string name, bool keep)
    {
        TransferArea area = null;
        try
        {
            area = net.TransferAreas.Create(name, TransferAreaType.CD);
            Console.WriteLine("  Create(" + name + ", CD) OK");
            DumpArea(area, "    ");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Create(" + name + ", CD)：" + Flatten(ex));
            return;
        }

        if (keep || area == null)
        {
            return;
        }

        bool occupied = false;
        foreach (Address address in area.PartnerAddresses)
        {
            if (address.StartAddress == 6 || address.StartAddress == 8)
            {
                occupied = true;
            }
        }

        if (occupied)
        {
            Console.WriteLine("  " + name + " 就是 GUI 那筆（partner 6/8），留下。");
            return;
        }

        try
        {
            area.Delete();
            Console.WriteLine("  " + name + " 是新開的空區，已刪，避免跟 GUI 重複。");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  刪 " + name + "：" + Flatten(ex));
        }
    }

    private static void DumpTransferAreaTypes()
    {
        Console.WriteLine("=== TransferAreaType ===");
        foreach (TransferAreaType type in Enum.GetValues(typeof(TransferAreaType)))
        {
            Console.WriteLine("  " + type + " = " + (int)type);
        }

        Console.WriteLine("=== CaxImportOptions ===");
        foreach (CaxImportOptions option in Enum.GetValues(typeof(CaxImportOptions)))
        {
            Console.WriteLine("  " + option + " = " + (int)option);
        }
    }

    private static void ExportPfCax(Project project)
    {
        Device device = FindDeviceByItem(project, "25017_PF_PLC");
        if (device == null)
        {
            Console.WriteLine("CAx：找不到 PF。");
            return;
        }

        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HmiExport", "Templates", "26037_Cusor");
        Directory.CreateDirectory(dir);
        ExportCaxDevice(project, device, Path.Combine(dir, "cax-pf.aml"));

        Device main = FindDeviceByItem(project, "25017_Main_PLC");
        if (main != null)
        {
            ExportCaxDevice(project, main, Path.Combine(dir, "cax-main.aml"));
        }

        DeviceItem cpu = FindCpu(device);
        if (cpu == null)
        {
            return;
        }

        try
        {
#if TIA_V19
            Console.WriteLine("V19：略過 GsdExportProvider（V21 API）。");
#else
            GsdExportProvider gsd = cpu.GetService<GsdExportProvider>();
            Console.WriteLine("GsdExportProvider = " +
                (gsd == null ? "<null>" : gsd.GetType().Name));
            if (gsd != null)
            {
                string gsdPath = Path.Combine(dir, "gsd-pf.xml");
                gsd.Export(new FileInfo(gsdPath), "25017_PF_PLC", "I-device GSD probe");
                Console.WriteLine("GSD 匯出：" + gsdPath);
            }
#endif
        }
        catch (Exception ex)
        {
            Console.WriteLine("GSD 匯出失敗：" + Flatten(ex));
        }
    }

    private static void ExportCaxDevice(Project project, Device device, string aml)
    {
        Console.WriteLine("CAx 匯出：" + aml);
        try
        {
            CaxProvider cax = project.GetService<CaxProvider>();
            if (cax == null)
            {
                Console.WriteLine("CaxProvider = <null>");
                return;
            }

            TransferResult result = cax.Export(device, new FileInfo(aml));
            Console.WriteLine("CAx " + device.Name + " = " +
                (result == null ? "<null>" : ("err=" + result.ErrorCount +
                    " warn=" + result.WarningCount + " " + result.State)));
        }
        catch (Exception ex)
        {
            Console.WriteLine("CAx 匯出失敗：" + Flatten(ex));
        }
    }

    private static void DumpCaxResult(TransferResult result, string indent)
    {
        try
        {
            foreach (EngineeringAttributeInfo info in result.GetAttributeInfos())
            {
                object value = result.GetAttribute(info.Name);
                Console.WriteLine(indent + info.Name + " = " +
                    (value == null ? "<null>" : value.ToString()));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(indent + "attrs：" + Flatten(ex));
        }
    }

    private static void ProbeTransferFind(NetworkInterface net)
    {
        TransferAreaComposition areas = net.TransferAreas;
        Console.WriteLine("  TransferAreas.Count=" + areas.Count);
        foreach (System.Reflection.MethodInfo method in areas.GetType()
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (method.Name.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0 ||
                method.Name.IndexOf("Add", StringComparison.OrdinalIgnoreCase) >= 0 ||
                method.Name == "Find")
            {
                string args = string.Join(",", method.GetParameters()
                    .Select(p => p.ParameterType.Name + " " + p.Name).ToArray());
                Console.WriteLine("  areas." + method.Name + "(" + args + ")");
            }
        }
        for (int index = 0; index < 8; index++)
        {
            try
            {
                TransferArea area = areas.Find(index);
                if (area == null)
                {
                    Console.WriteLine("  Find(" + index + ")=<null>");
                    continue;
                }

                Console.WriteLine("  Find(" + index + ")=" + area.Name +
                    " type=" + area.Type);
                DumpArea(area, "    ");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Find(" + index + ")：" + Flatten(ex));
                break;
            }
        }

        int enumIndex = 0;
        foreach (TransferArea area in areas)
        {
            Console.WriteLine("  enum[" + enumIndex + "] " + area.Name);
            DumpArea(area, "    ");
            enumIndex++;
        }

        try
        {
            IoConnector connector = net.IoConnectors.Cast<IoConnector>().FirstOrDefault();
            if (connector != null)
            {
                System.Reflection.MethodInfo method = connector.GetType().GetMethod("GetIoController");
                if (method != null)
                {
                    object invoked = method.Invoke(connector, null);
                    Console.WriteLine("  GetIoController -> " +
                        (invoked == null ? "<null>" : invoked.GetType().FullName));
                    IoController io = invoked as IoController;
                    if (io != null)
                    {
                        Console.WriteLine("  IoController.Addresses=" + io.Addresses.Count);
                        foreach (Address address in io.Addresses)
                        {
                            Console.WriteLine("    ctrl " + address.IoType + " " +
                                address.StartAddress + "+" + address.Length);
                        }

                        DumpCompositions(io, "    io.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  GetIoController：" + Flatten(ex));
        }
    }

    private static void DumpMainBytes(Project project, int from, int to)
    {
        Device main = FindDeviceByItem(project, "25017_Main_PLC");
        if (main == null)
        {
            return;
        }

        Console.WriteLine("=== Main 位址 " + from + "–" + to + " ===");
        foreach (DeviceItem item in ReachableItems(main))
        {
            foreach (Address address in item.Addresses)
            {
                if (address.StartAddress < from || address.StartAddress > to)
                {
                    continue;
                }

                Console.WriteLine("  " + item.Name + " " + address.IoType + " " +
                    address.StartAddress + "+" + address.Length);
                foreach (AddressController controller in address.AddressControllers)
                {
                    Console.WriteLine("    controller " +
                        (controller.OwnedBy == null ? "?" : controller.OwnedBy.Name));
                    foreach (Address registered in controller.RegisteredAddresses)
                    {
                        if (registered.StartAddress < 0)
                        {
                            continue;
                        }

                        Console.WriteLine("      reg " + registered.IoType + " " +
                            registered.StartAddress + "+" + registered.Length);
                    }
                }
            }
        }
    }

    private static void DumpPnInterface(Project project, string cpuName)
    {
        Device device = FindDeviceByItem(project, cpuName);
        if (device == null)
        {
            Console.WriteLine(cpuName + " 找不到。");
            return;
        }

        foreach (DeviceItem item in ReachableItems(device))
        {
            NetworkInterface net = item.GetService<NetworkInterface>();
            if (net == null)
            {
                continue;
            }

            Console.WriteLine("--- " + cpuName + " / " + item.Name + " ---");
            Console.WriteLine("  mode=" + net.InterfaceOperatingMode);
            Console.WriteLine("  TransferAreas=" + net.TransferAreas.Count);
            ProbeTransferFind(net);
            Console.WriteLine("  IoConnectors=" + net.IoConnectors.Count);
            Console.WriteLine("  IoControllers=" + net.IoControllers.Count);
            Console.WriteLine("  DeviceItems=" + item.DeviceItems.Count +
                " Items=" + item.Items.Count);
            DumpCompositions(net, "  net.");
            DumpAttrs(net);
            DumpInvokes(net);

            foreach (TransferArea area in net.TransferAreas)
            {
                DumpArea(area, "  ");
            }

            try
            {
                object extraAreas = ((IEngineeringObject)net).GetComposition("TransferAreas");
                Console.WriteLine("  GetComposition TransferAreas = " +
                    (extraAreas == null ? "<null>" : extraAreas.GetType().FullName));
            }
            catch (Exception ex)
            {
                Console.WriteLine("  GetComposition TransferAreas：" + Flatten(ex));
            }

            foreach (IoConnector connector in net.IoConnectors)
            {
                Console.WriteLine("  IoConnector -> " +
                    (connector.ConnectedToIoSystem == null
                        ? "(空)"
                        : connector.ConnectedToIoSystem.Name));
                DumpCompositions(connector, "    conn.");
                DumpAttrs(connector);
                DumpInvokes(connector);
            }

            foreach (DeviceItem child in item.DeviceItems)
            {
                Console.WriteLine("  child " + child.Name + " type=" + child.TypeIdentifier);
                DumpItemAddresses(child, "    ");
            }

            foreach (DeviceItem extra in item.Items)
            {
                Console.WriteLine("  extra " + extra.Name + " type=" + extra.TypeIdentifier);
                DumpItemAddresses(extra, "    ");
                DumpCompositions(extra, "    extra.");
                DumpAttrs(extra);
            }
        }
    }

    private static void DumpMainConnectedIo(Project project)
    {
        Device main = FindDeviceByItem(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 Main。");
            return;
        }

        foreach (DeviceItem item in ReachableItems(main))
        {
            NetworkInterface net = item.GetService<NetworkInterface>();
            if (net == null)
            {
                continue;
            }

            foreach (IoController controller in net.IoControllers)
            {
                if (controller.IoSystem == null)
                {
                    continue;
                }

                Console.WriteLine("Main IoSystem " + controller.IoSystem.Name +
                    " devices=" + controller.IoSystem.ConnectedIoDevices.Count);
                foreach (IoConnector connector in controller.IoSystem.ConnectedIoDevices)
                {
                    Device remote = DeviceOf(connector);
                    Console.WriteLine("  connected " +
                        (remote == null ? "?" : remote.Name) +
                        " connector parent=" +
                        (connector.Parent == null ? "?" : connector.Parent.GetType().Name));
                    DumpCompositions(connector, "    ");
                    DumpAttrs(connector);
                    IEngineeringObject owned = connector.Parent;
                    DeviceItem ownerItem = owned as DeviceItem;
                    if (ownerItem != null)
                    {
                        Console.WriteLine("    ownerItem " + ownerItem.Name +
                            " Items=" + ownerItem.Items.Count);
                        foreach (DeviceItem extra in ownerItem.Items)
                        {
                            Console.WriteLine("      extra " + extra.Name);
                            DumpItemAddresses(extra, "        ");
                        }

                        foreach (DeviceItem child in ownerItem.DeviceItems)
                        {
                            Console.WriteLine("      child " + child.Name);
                            DumpItemAddresses(child, "        ");
                        }
                    }
                }
            }
        }
    }

    private static void DumpItemAddresses(DeviceItem item, string indent)
    {
        foreach (Address address in item.Addresses)
        {
            Console.WriteLine(indent + address.IoType + " " +
                address.StartAddress + "+" + address.Length);
        }
    }

    private static void DumpCompositions(IEngineeringObject target, string indent)
    {
        try
        {
            foreach (EngineeringCompositionInfo info in target.GetCompositionInfos())
            {
                Console.WriteLine(indent + "composition " + info.Name);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(indent + "compositions：" + Flatten(ex));
        }
    }

    private static void DumpArea(TransferArea area, string indent)
    {
        Console.WriteLine(indent + "area " + area.Name +
            " type=" + area.Type +
            " dir=" + area.Direction +
            " L->P=" + area.LocalToPartnerLength +
            " P->L=" + area.PartnerToLocalLength);
        foreach (Address address in area.LocalAddresses)
        {
            Console.WriteLine(indent + "  local " + address.IoType + " " +
                address.StartAddress + "+" + address.Length);
        }

        foreach (Address address in area.PartnerAddresses)
        {
            Console.WriteLine(indent + "  partner " + address.IoType + " " +
                address.StartAddress + "+" + address.Length);
        }

        DumpAttrs(area);
    }

    private static void MoveIdevicePartner(Project project, string cpuName, int start)
    {
        Device device = FindDeviceByItem(project, cpuName);
        if (device == null)
        {
            Console.WriteLine(cpuName + "：找不到。");
            return;
        }

        int moved = 0;
        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            NetworkInterface net = item.GetService<NetworkInterface>();
            if (net == null)
            {
                continue;
            }

            TransferAreaComposition areas = net.TransferAreas;
            if (areas.Count == 0)
            {
                try
                {
                    areas = ((IEngineeringObject)net).GetComposition("TransferAreas") as TransferAreaComposition;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  GetComposition TransferAreas：" + Flatten(ex));
                }
            }

            if (areas == null)
            {
                continue;
            }

            foreach (TransferArea area in areas)
            {
                foreach (Address address in area.PartnerAddresses)
                {
                    if (address.StartAddress < 0)
                    {
                        continue;
                    }

                    int current = address.StartAddress;
                    if (current == start)
                    {
                        Console.WriteLine("  " + area.Name + " partner " +
                            address.IoType + " 已是 " + start);
                        continue;
                    }

                    try
                    {
                        address.SetAttribute("StartAddress", start);
                        Console.WriteLine("  " + area.Name + " partner " +
                            address.IoType + " " + current + " -> " + start);
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  改 " + area.Name + " partner 失敗：" + Flatten(ex));
                    }
                }
            }
        }

        foreach (DeviceItem item in ReachableItems(device))
        {
            if (item.Name == null)
            {
                continue;
            }

            bool pn = item.Name.IndexOf("From_Main", StringComparison.OrdinalIgnoreCase) >= 0 ||
                item.Name.IndexOf("To_Main", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!pn)
            {
                continue;
            }

            Console.WriteLine("  item " + item.Name + " pos=" + item.PositionNumber);
            foreach (Address address in item.Addresses)
            {
                if (address.StartAddress < 0)
                {
                    continue;
                }

                int current = address.StartAddress;
                Console.WriteLine("    " + address.IoType + " " + current +
                    "+" + address.Length);
                if (current == start)
                {
                    continue;
                }

                try
                {
                    address.SetAttribute("StartAddress", start);
                    Console.WriteLine("    -> " + start);
                    moved++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("    改失敗：" + Flatten(ex));
                }
            }
        }

        Console.WriteLine("PF partner 改了 " + moved + " 筆。");
    }

    private static void DumpLowAddresses(Project project)
    {
        Console.WriteLine("=== 位址 0–90 / 250–280 ===");
        foreach (Device device in EnumerateDevices(project))
        {
            foreach (DeviceItem item in HardwareBuilder.AllItems(device))
            {
                foreach (Address address in item.Addresses)
                {
                    int start = address.StartAddress;
                    if (start < 0)
                    {
                        continue;
                    }

                    bool low = start <= 90;
                    bool zig = start >= 250 && start <= 280;
                    if (!low && !zig)
                    {
                        continue;
                    }

                    Console.WriteLine("  " + device.Name + " / " + item.Name +
                        " " + address.IoType + " " + start + "+" + address.Length);
                }
            }
        }
    }

    private static void DumpAttrs(IEngineeringObject target)
    {
        IList<EngineeringAttributeInfo> infos;
        try
        {
            infos = target.GetAttributeInfos();
        }
        catch (Exception ex)
        {
            Console.WriteLine("  無法列屬性：" + Flatten(ex));
            return;
        }

        foreach (EngineeringAttributeInfo info in infos)
        {
            object value;
            try
            {
                value = target.GetAttribute(info.Name);
            }
            catch (Exception ex)
            {
                value = "<" + Flatten(ex) + ">";
            }

            string text = value == null ? "<null>" : value.ToString();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            Console.WriteLine("  " + info.Name + " = " + text +
                (info.AccessMode.ToString().IndexOf("Write", StringComparison.OrdinalIgnoreCase) >= 0
                    ? " [W]"
                    : ""));
        }
    }

    private static void DumpKnownServices(DeviceItem item)
    {
        TryService<AddressController>(item, "AddressController");
        TryService<GsdDeviceItem>(item, "GsdDeviceItem");
        TryService<NetworkInterface>(item, "NetworkInterface");
    }

    private static void TryService<T>(DeviceItem item, string label) where T : class, IEngineeringService
    {
        try
        {
            T service = item.GetService<T>();
            Console.WriteLine("  service " + label + " = " + (service == null ? "<null>" : service.GetType().Name));
        }
        catch (Exception ex)
        {
            Console.WriteLine("  service " + label + "：" + Flatten(ex));
        }
    }

    private static void DumpInvokes(IEngineeringObject target)
    {
        try
        {
            foreach (EngineeringInvocationInfo info in target.GetInvocationInfos())
            {
                Console.WriteLine("  invoke " + info.Name);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  invokes：" + Flatten(ex));
        }
    }

    private static void DumpRegistered(Project project, string cpuName)
    {
        Console.WriteLine("=== " + cpuName + " RegisteredAddresses ===");
        Device device = FindDeviceByItem(project, cpuName);
        if (device == null)
        {
            return;
        }

        HashSet<string> seen = new HashSet<string>();
        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            foreach (Address address in item.Addresses)
            {
                foreach (AddressController controller in address.AddressControllers)
                {
                    string key = controller.OwnedBy == null ? "?" : controller.OwnedBy.Name;
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    Console.WriteLine("  controller " + key);
                    foreach (Address registered in controller.RegisteredAddresses)
                    {
                        if (registered.StartAddress < 0)
                        {
                            continue;
                        }

                        Console.WriteLine("    " + registered.IoType + " " +
                            registered.StartAddress + "+" + registered.Length);
                    }
                }
            }
        }
    }

    private static void DumpChannels(Project project, string cpuName)
    {
        Console.WriteLine("=== " + cpuName + " Channels ===");
        Device device = FindDeviceByItem(project, cpuName);
        if (device == null)
        {
            return;
        }

        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            if (item.Channels == null || item.Channels.Count == 0)
            {
                continue;
            }

            Console.WriteLine("  " + item.Name + " channels=" + item.Channels.Count);
            foreach (Channel channel in item.Channels)
            {
                Console.WriteLine("    #" + channel.Number + " " +
                    channel.IoType + " " + channel.Type);
            }
        }
    }

    private static void DumpControllerAddresses(Project project)
    {
        Console.WriteLine("=== IoController.Addresses ===");
        foreach (Device device in EnumerateDevices(project))
        {
            foreach (DeviceItem item in HardwareBuilder.AllItems(device))
            {
                NetworkInterface net = item.GetService<NetworkInterface>();
                if (net == null)
                {
                    continue;
                }

                foreach (IoController controller in net.IoControllers)
                {
                    Console.WriteLine("  controller " + device.Name + " / " + item.Name);
                    foreach (Address address in controller.Addresses)
                    {
                        Console.WriteLine("    " + address.IoType + " " +
                            address.StartAddress + "+" + address.Length);
                    }

                    DumpAttrs(controller);
                }

                foreach (MulticastableTransferArea area in net.MulticastableTransferAreas)
                {
                    Console.WriteLine("  multicast " + device.Name + " / " + area.Name +
                        " " + area.Direction + " len=" + area.DataLength);
                    foreach (Address address in area.Addresses)
                    {
                        Console.WriteLine("    " + address.IoType + " " +
                            address.StartAddress + "+" + address.Length);
                    }
                }
            }
        }
    }

    private static void DumpTransferAreas(Project project)
    {
        Console.WriteLine("=== TransferArea ===");
        foreach (Device device in EnumerateDevices(project))
        {
            foreach (DeviceItem item in HardwareBuilder.AllItems(device))
            {
                NetworkInterface net = item.GetService<NetworkInterface>();
                if (net == null)
                {
                    continue;
                }

                foreach (TransferArea area in net.TransferAreas)
                {
                    Console.WriteLine("  " + device.Name + " / " + item.Name +
                        " " + area.Name + " " + area.Type + " " + area.Direction +
                        " L->P " + area.LocalToPartnerLength +
                        " P->L " + area.PartnerToLocalLength);
                    foreach (Address address in area.LocalAddresses)
                    {
                        Console.WriteLine("    local " + address.IoType + " " +
                            address.StartAddress + "+" + address.Length);
                    }

                    foreach (Address address in area.PartnerAddresses)
                    {
                        Console.WriteLine("    partner " + address.IoType + " " +
                            address.StartAddress + "+" + address.Length);
                    }
                }
            }
        }
    }

    private static void SyncMain(
        Project project,
        string cb1241,
        string cm1241,
        string sm1223Dc,
        string sm1238)
    {
        Device main = FindDeviceByItem(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 Main。");
            return;
        }

        DeviceItem cpu = FindCpu(main);
        DeviceItem rack = FindRack(main);
        if (cpu == null || rack == null)
        {
            Console.WriteLine("Main 找不到 CPU 或機架。");
            return;
        }

        DeviceItem extraDi = FindByOrder(main, "6ES7 221-1BH32-0XB0");
        if (extraDi != null)
        {
            Console.WriteLine("Main 刪圖上沒有的 SM1221：" + extraDi.Name +
                " slot " + extraDi.PositionNumber);
            extraDi.Delete();
        }
        else
        {
            Console.WriteLine("Main 沒有多的 SM1221。");
        }

        PlugIfMissing(cpu, cb1241, "CB 1241_Main", 3);
        PlugIfMissing(rack, cm1241, "CM 1241_Main", 101);
        PlugIfMissing(rack, sm1223Dc, "DI 16/DQ 16x24VDC_3", 4);
        PlugIfMissing(rack, sm1238, "SM 1238_Energy", 5);
    }

    private static void SyncTfCpu(Project project, string cpu1212)
    {
        Device tf = FindDeviceByItem(project, "25017_TF_PLC");
        if (tf == null)
        {
            Console.WriteLine("找不到 TF。");
            return;
        }

        DeviceItem cpu = FindCpu(tf);
        if (cpu == null)
        {
            Console.WriteLine("TF 找不到 CPU。");
            return;
        }

        if (HasOrder(cpu, "6ES7 212-1AE40-0XB0"))
        {
            Console.WriteLine("TF CPU 已是 1212C。");
            return;
        }

        Console.WriteLine("TF CPU " + cpu.TypeIdentifier + " -> " + cpu1212);
        try
        {
            cpu.ChangeType(cpu1212);
            Console.WriteLine("  TF ChangeType 完成。");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  TF 換 CPU 失敗：" + Flatten(ex));
        }
    }

    private static void SyncInsideSm(Project project, string cpuName, string sm1223Dc)
    {
        Device device = FindDeviceByItem(project, cpuName);
        if (device == null)
        {
            Console.WriteLine(cpuName + "：找不到裝置。");
            return;
        }

        DeviceItem sm = FindByOrder(device, "6ES7 223-1BH32-0XB0");
        if (sm == null)
        {
            if (FindByOrder(device, "6ES7 223-1BL32-0XB0") != null)
            {
                Console.WriteLine(cpuName + "：SM1223 已是 1BL32。");
            }
            else
            {
                Console.WriteLine(cpuName + "：找不到要換的 SM1223。");
            }

            return;
        }

        Console.WriteLine(cpuName + " SM " + sm.TypeIdentifier + " -> " + sm1223Dc);
        try
        {
            sm.ChangeType(sm1223Dc);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  換 SM 失敗：" + Flatten(ex));
        }
    }

    private static void ChangeItemType(
        Project project,
        string itemName,
        string fromOrder,
        string toType)
    {
        Device device = FindDeviceByItem(project, itemName);
        if (device == null)
        {
            Console.WriteLine(itemName + "：找不到裝置。");
            return;
        }

        DeviceItem item = HardwareBuilder.AllItems(device)
            .FirstOrDefault(i => string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            Console.WriteLine(itemName + "：找不到該項。");
            return;
        }

        if (!HasOrder(item, fromOrder))
        {
            Console.WriteLine(itemName + "：已不是 " + fromOrder + "（現在 " + item.TypeIdentifier + "）。");
            return;
        }

        Console.WriteLine(itemName + " " + item.TypeIdentifier + " -> " + toType);
        try
        {
            item.ChangeType(toType);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ChangeType 失敗：" + Flatten(ex));
        }
    }

    private static void CopyInsideAs24B(Project project)
    {
        if (FindDeviceByItem(project, "25017_24B_Inside_PLC") != null)
        {
            Console.WriteLine("24B Inside 已存在。");
            return;
        }

        Device source = FindDeviceByItem(project, "25017_20B_Inside_PLC") ??
            FindDeviceByItem(project, "25017_18B_Inside_PLC");
        if (source == null)
        {
            Console.WriteLine("沒有 20B/18B Inside 可複製成 24B。");
            return;
        }

        Device made = CopyDevice(project, source);
        if (made == null)
        {
            return;
        }

        RenameDeviceAndHead(made, "25017_24B_Inside_PLC", "25017_24B_Inside_PLC_station");
        SetIp(made, "192.168.1.8");
        RenameItemsContaining(made, "20B", "24B");
        RenameItemsContaining(made, "18B", "24B");
        Console.WriteLine("已複製 24B Inside，IP 192.168.1.8。");
    }

    private static void CopyRioAs24B(Project project)
    {
        if (FindDeviceByItem(project, "25017_24B_RIO") != null)
        {
            Console.WriteLine("24B RIO 已存在。");
            return;
        }

        Device source = FindDeviceByItem(project, "25017_20B_RIO") ??
            FindDeviceByItem(project, "25017_18B_RIO");
        if (source == null)
        {
            Console.WriteLine("沒有 20B/18B RIO 可複製成 24B。");
            return;
        }

        Device made = CopyDevice(project, source);
        if (made == null)
        {
            return;
        }

        RenameDeviceAndHead(made, "25017_24B_RIO", "25017_24B_RIO_station");
        SetIp(made, "192.168.0.23");
        RenameItemsContaining(made, "20B", "24B");
        RenameItemsContaining(made, "18B", "24B");
        ShiftRioAddresses(made);
        Console.WriteLine("已複製 24B RIO，IP 192.168.0.23，I/Q 50 段。");
    }

    private static void ShiftRioAddresses(Device device)
    {
        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            foreach (Address address in item.Addresses)
            {
                if (address.StartAddress < 0)
                {
                    continue;
                }

                int next = address.StartAddress;
                string io = address.IoType.ToString();
                if (address.Length == 64 && io.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    next = 274;
                }
                else if (address.StartAddress == 76 &&
                    io.IndexOf("Output", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    next = 80;
                }
                else if (address.StartAddress >= 40 && address.StartAddress < 50)
                {
                    next = address.StartAddress + 10;
                }
                else
                {
                    continue;
                }

                try
                {
                    address.SetAttribute("StartAddress", next);
                    Console.WriteLine("  " + item.Name + " " + address.IoType +
                        " -> " + next);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  改址失敗 " + item.Name + "：" + Flatten(ex));
                }
            }
        }
    }

    private static void Rename18Bto20B(Project project)
    {
        int count = 0;
        foreach (Device device in EnumerateDevices(project).ToList())
        {
            if (!string.IsNullOrEmpty(device.Name) &&
                device.Name.IndexOf("18B", StringComparison.Ordinal) >= 0)
            {
                string oldDevice = device.Name;
                try
                {
                    device.SetAttribute("Name", oldDevice.Replace("18B", "20B"));
                    Console.WriteLine("裝置：" + oldDevice + " -> " + device.Name);
                    count++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  裝置改名失敗 " + oldDevice + "：" + Flatten(ex));
                }
            }

            count += RenameItemsContaining(device, "18B", "20B");
        }

        Console.WriteLine("18B→20B 硬體名改了 " + count + " 項。PLC/HMI Tag 名沒動（程式綁名稱）。PlcSoftware 唯讀。");
    }

    private static void CopyDriveAs24B(Project project)
    {
        if (FindDeviceByItem(project, "25017_24B_Rotor_Drive") != null)
        {
            Console.WriteLine("24B Rotor Drive 已存在。");
            return;
        }

        Device source = FindDeviceByItem(project, "25017_20B_Rotor_Drive") ??
            FindDeviceByItem(project, "25017_18B_Rotor_Drive");
        if (source == null)
        {
            Console.WriteLine("沒有 20B/18B Rotor Drive 可複製成 24B。");
            return;
        }

        Device made = CopyDevice(project, source);
        if (made == null)
        {
            return;
        }

        RenameDeviceAndHead(made, "25017_24B_Rotor_Drive", made.Name);
        RenameItemsContaining(made, "20B", "24B");
        RenameItemsContaining(made, "18B", "24B");
        ConnectDeviceToMainIo(project, made, "192.168.0.34", "25017_24B_Rotor_Drive");
        Console.WriteLine("已複製 24B Rotor Drive，IP 192.168.0.34。GSD 要先接 IO 才能改址。");
    }

    private static void AlignDriveFromTemplate(
        Project project,
        string templateName,
        string targetName,
        int delta)
    {
        Device template = FindDeviceByItem(project, templateName);
        Device target = FindDeviceByItem(project, targetName);
        if (template == null || target == null)
        {
            Console.WriteLine("對齊 Drive 失敗：找不到 " + templateName + " 或 " + targetName);
            return;
        }

        Dictionary<string, List<Address>> byKey = new Dictionary<string, List<Address>>(StringComparer.OrdinalIgnoreCase);
        foreach (DeviceItem item in HardwareBuilder.AllItems(template))
        {
            foreach (Address address in item.Addresses)
            {
                if (address.StartAddress < 0)
                {
                    continue;
                }

                string key = DriveAddressKey(item, address);
                List<Address> list;
                if (!byKey.TryGetValue(key, out list))
                {
                    list = new List<Address>();
                    byKey[key] = list;
                }

                list.Add(address);
            }
        }

        foreach (DeviceItem item in HardwareBuilder.AllItems(target))
        {
            foreach (Address address in item.Addresses)
            {
                if (address.StartAddress < 0)
                {
                    continue;
                }

                string key = DriveAddressKey(item, address);
                List<Address> list;
                if (!byKey.TryGetValue(key, out list) || list.Count == 0)
                {
                    Console.WriteLine("  對不到樣板 " + item.Name + " " + address.IoType);
                    continue;
                }

                Address source = list[0];
                list.RemoveAt(0);
                int next = source.StartAddress + delta;
                int old = address.StartAddress;
                if (old == next)
                {
                    Console.WriteLine("  " + item.Name + " " + address.IoType + " 已是 " + next);
                    continue;
                }

                try
                {
                    address.SetAttribute("StartAddress", next);
                    Console.WriteLine("  " + item.Name + " " + address.IoType +
                        " " + old + " -> " + next);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  改址失敗 " + item.Name + "：" + Flatten(ex));
                }
            }
        }
    }

    private static string DriveAddressKey(DeviceItem item, Address address)
    {
        string owner = item.Name;
        HardwareObject container = item.Container;
        if (container != null && !string.IsNullOrEmpty(container.Name))
        {
            owner = container.Name;
        }

        return owner + "|" + address.IoType + "|" + address.Length;
    }

    private static Device CopyDevice(Project project, Device source)
    {
        DeviceUserGroup group = source.Parent as DeviceUserGroup;
        if (group == null)
        {
            Console.WriteLine("來源不在群組裡，無法複製：" + source.Name);
            return null;
        }

        MasterCopy copy = null;
        try
        {
            copy = project.ProjectLibrary.MasterCopyFolder.MasterCopies
                .Create((IMasterCopySource)source);
            Device made = group.Devices.CreateFrom(copy);
            Console.WriteLine("MasterCopy " + source.Name + " -> " + made.Name);
            return made;
        }
        catch (Exception ex)
        {
            Console.WriteLine("複製失敗 " + source.Name + "：" + Flatten(ex));
            return null;
        }
        finally
        {
            if (copy != null)
            {
                try
                {
                    copy.Delete();
                }
                catch
                {
                }
            }
        }
    }

    private static void RenameDeviceAndHead(Device device, string headName, string deviceName)
    {
        try
        {
            if (!string.Equals(device.Name, deviceName, StringComparison.Ordinal))
            {
                device.SetAttribute("Name", deviceName);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  裝置改名失敗：" + Flatten(ex));
        }

        DeviceItem head = FindCpu(device) ??
            HardwareBuilder.AllItems(device).FirstOrDefault(i =>
                string.Equals(TryAttr(i, "Classification"), "HM", StringComparison.OrdinalIgnoreCase) ||
                (i.TypeIdentifier ?? string.Empty).IndexOf("155-6", StringComparison.OrdinalIgnoreCase) >= 0);

        if (head == null)
        {
            return;
        }

        try
        {
            head.SetAttribute("Name", headName);
            Console.WriteLine("  " + head.Name);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  頭模組改名失敗：" + Flatten(ex));
        }
    }

    private static int RenameItemsContaining(Device device, string from, string to)
    {
        int count = 0;
        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            if (string.IsNullOrEmpty(item.Name) || item.Name.IndexOf(from, StringComparison.Ordinal) < 0)
            {
                continue;
            }

            string oldName = item.Name;
            try
            {
                item.SetAttribute("Name", oldName.Replace(from, to));
                Console.WriteLine("  硬體項：" + oldName + " -> " + item.Name);
                count++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  改名略過 " + oldName + "：" + Flatten(ex));
            }
        }

        return count;
    }

    private static void SetIp(Device device, string ip)
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
                try
                {
                    node.SetAttribute("Address", ip);
                    Console.WriteLine("  IP " + node.Name + " -> " + ip);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  設 IP 失敗：" + Flatten(ex));
                }
            }
        }
    }

    private static void RenameHmiDevice(Device device, string name)
    {
        try
        {
            device.SetAttribute("Name", name);
            Console.WriteLine("  裝置：" + device.Name);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  裝置改名失敗：" + Flatten(ex));
        }

        DeviceItem panel = HardwareBuilder.AllItems(device).FirstOrDefault(item =>
            (item.TypeIdentifier ?? string.Empty)
                .IndexOf("6AV2", StringComparison.OrdinalIgnoreCase) >= 0);
        if (panel != null)
        {
            try
            {
                panel.SetAttribute("Name", name);
                Console.WriteLine("  面板：" + panel.Name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  面板改名失敗：" + Flatten(ex));
            }
        }

        RenameItemsContaining(device, "25017_Main_HMI", name);
    }

    private static bool NodeNameMatches(Node node, string nodeName)
    {
        string current = node.Name ?? string.Empty;
        return string.Equals(current, nodeName, StringComparison.OrdinalIgnoreCase) ||
            current.StartsWith(nodeName + " ", StringComparison.OrdinalIgnoreCase) ||
            current.StartsWith(nodeName + " :", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetNodeIp(Device device, string nodeName, string ip)
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
                if (!NodeNameMatches(node, nodeName))
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
                    Console.WriteLine("  設 IP 失敗 " + node.Name + "：" + Flatten(ex));
                }
            }
        }
    }

    private static void ConnectNodeToSubnet(
        Project project,
        Device device,
        string nodeName,
        string subnetName)
    {
        Subnet subnet = project.Subnets.FirstOrDefault(s =>
            string.Equals(s.Name, subnetName, StringComparison.OrdinalIgnoreCase));
        if (subnet == null)
        {
            Console.WriteLine("找不到子網路 " + subnetName);
            return;
        }

        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            NetworkInterface net = item.GetService<NetworkInterface>();
            if (net == null)
            {
                continue;
            }

            foreach (Node node in net.Nodes)
            {
                if (!NodeNameMatches(node, nodeName))
                {
                    continue;
                }

                if (node.ConnectedSubnet != null)
                {
                    Console.WriteLine("  " + node.Name + " 已在 " + node.ConnectedSubnet.Name);
                    continue;
                }

                try
                {
                    node.ConnectToSubnet(subnet);
                    Console.WriteLine("  " + node.Name + " 接上 " + subnet.Name);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  接子網路失敗：" + Flatten(ex));
                }
            }
        }
    }

    private static HmiTarget FindHmiTarget(Device device)
    {
        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
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

    private static void DescribeHmiSoftware(Device device)
    {
        Console.WriteLine("  裝置：" + device.Name);
        HmiTarget hmi = FindHmiTarget(device);
        if (hmi == null)
        {
            Console.WriteLine("  沒有 HmiTarget。");
            return;
        }

        int tagCount = 0;
        int linked = 0;
        string sample = null;
        foreach (TagTable table in hmi.TagFolder.TagTables)
        {
            foreach (Tag tag in table.Tags)
            {
                tagCount++;
                object connection = null;
                try
                {
                    connection = tag.GetAttribute("Connection");
                }
                catch
                {
                }

                if (connection != null && !string.IsNullOrWhiteSpace(connection.ToString()))
                {
                    linked++;
                    if (sample == null)
                    {
                        sample = connection.ToString();
                    }
                }
            }
        }

        Console.WriteLine("  HMI 軟體：" + hmi.Name);
        Console.WriteLine("  畫面：" + hmi.ScreenFolder.Screens.Count);
        Console.WriteLine("  HMI Tag：" + tagCount + "（有連線 " + linked + "）");
        if (sample != null)
        {
            Console.WriteLine("  Tag 連線欄：" + sample);
        }

        Console.WriteLine("  軟體連線：" + hmi.Connections.Count);
        foreach (HmiSoftConnection connection in hmi.Connections)
        {
            Console.WriteLine("    " + connection.Name);
        }

#if !TIA_V19
        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            CommunicationManagement management = item.GetService<CommunicationManagement>();
            if (management == null)
            {
                continue;
            }

            Console.WriteLine("  HW 連線（" + item.Name + "）：" + management.Connections.Count);
            foreach (HwConnection connection in management.Connections)
            {
                HwHmiConnection hmiConnection = connection as HwHmiConnection;
                string name = hmiConnection != null
                    ? hmiConnection.LocalConnectionName
                    : connection.ConnectionType.ToString();
                Console.WriteLine("    " + connection.GetType().Name + " " + name +
                    " valid=" + connection.IsValid);
            }
        }
#endif
    }

    public static int EnsureNamedHmiPlcConnection(
        Project project,
        string hmiDeviceName,
        string plcDeviceName,
        string softConnectionName)
    {
        Device hmiDevice = FindDeviceByItem(project, hmiDeviceName);
        Device plcDevice = FindDeviceByItem(project, plcDeviceName);
        if (hmiDevice == null || plcDevice == null)
        {
            Console.WriteLine("找不到裝置：" + hmiDeviceName + " / " + plcDeviceName);
            return 0;
        }

        HmiTarget hmi = FindHmiTarget(hmiDevice);
        if (hmi == null)
        {
            Console.WriteLine(hmiDeviceName + " 沒有 HmiTarget。");
            return 0;
        }

        foreach (HmiSoftConnection existingSoft in hmi.Connections)
        {
            Console.WriteLine("已有軟體連線：" + existingSoft.Name);
            if (!string.Equals(existingSoft.Name, softConnectionName, StringComparison.Ordinal))
            {
                TryRenameConnection(hmi, softConnectionName);
            }

            return 1;
        }

#if TIA_V19
        Console.WriteLine("V19：略過 HW CommunicationConnections，只沿用軟體連線。");
        return 0;
#else
        CommunicationManagement management = null;
        foreach (DeviceItem item in HardwareBuilder.AllItems(hmiDevice))
        {
            CommunicationManagement found = item.GetService<CommunicationManagement>();
            if (found == null)
            {
                continue;
            }

            foreach (HwConnection existing in found.Connections)
            {
                HwHmiConnection existingHmi = existing as HwHmiConnection;
                if (existingHmi == null)
                {
                    continue;
                }

                Console.WriteLine("已有硬體連線（" + item.Name + "）：" +
                    existingHmi.LocalConnectionName + " valid=" + existing.IsValid);
                TryRenameConnection(hmi, softConnectionName);
                if (hmi.Connections.Count > 0)
                {
                    return 1;
                }

                // WinCC Basic：軟體 Connections 計數常為 0，但硬體連線名仍給 Tag 用。
                if (existing.IsValid &&
                    (string.IsNullOrEmpty(softConnectionName) ||
                     string.Equals(existingHmi.LocalConnectionName, softConnectionName, StringComparison.Ordinal)))
                {
                    Console.WriteLine("硬體連線有效，沿用（軟體計數 " + hmi.Connections.Count + "）。");
                    return 1;
                }
            }

            if (management == null)
            {
                management = found;
                Console.WriteLine("CommunicationManagement：" + item.Name);
            }
        }

        if (management == null)
        {
            Console.WriteLine(hmiDeviceName + " 找不到 CommunicationManagement。");
            return 0;
        }

        Node hmiNode = FindNamedNode(hmiDevice, "X1");
        Node plcNode = FindNamedNode(plcDevice, "X1");
        DeviceItem plcCpu = FindCpu(plcDevice);
        if (hmiNode == null || plcNode == null || plcCpu == null)
        {
            Console.WriteLine("補連線缺節點：HMI X1 / PLC X1 / CPU。");
            return 0;
        }

        try
        {
            HwHmiConnection created = management.Connections.Create<HwHmiConnection>(
                hmiNode,
                plcCpu,
                plcNode);
            Console.WriteLine("已建 " + hmiDeviceName + " → " + plcDeviceName + "：" +
                created.LocalConnectionName + " valid=" + created.IsValid);
            TryRenameConnection(hmi, softConnectionName);
            return hmi.Connections.Count > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("建 HMI 連線失敗：" + Flatten(ex));
            return 0;
        }
#endif
    }

    private static void EnsureHmiPlcConnection(Device hmiDevice, Device plcDevice)
    {
        if (hmiDevice == null || plcDevice == null)
        {
            Console.WriteLine("無法補 HMI 連線：找不到 HMI 或 Main PLC。");
            return;
        }

        HmiTarget hmi = FindHmiTarget(hmiDevice);
        if (hmi != null && hmi.Connections.Count > 0)
        {
            Console.WriteLine("OP2 已有軟體連線，不另建。");
            return;
        }

#if TIA_V19
        Console.WriteLine("V19：略過 HW CommunicationConnections（OP2）。");
        return;
#else
        CommunicationManagement management = null;
        foreach (DeviceItem item in HardwareBuilder.AllItems(hmiDevice))
        {
            CommunicationManagement found = item.GetService<CommunicationManagement>();
            if (found == null)
            {
                continue;
            }

            foreach (HwConnection existing in found.Connections)
            {
                HwHmiConnection existingHmi = existing as HwHmiConnection;
                if (existingHmi != null)
                {
                    Console.WriteLine("OP2 已有硬體連線（" + item.Name + "）：" +
                        existingHmi.LocalConnectionName);
                    return;
                }
            }

            if (management == null)
            {
                management = found;
                Console.WriteLine("CommunicationManagement：" + item.Name);
            }
        }

        if (management == null)
        {
            Console.WriteLine("OP2 找不到 CommunicationManagement。");
            return;
        }

        Node hmiNode = FindNamedNode(hmiDevice, "X1");
        Node plcNode = FindNamedNode(plcDevice, "X1");
        DeviceItem plcCpu = FindCpu(plcDevice);
        if (hmiNode == null || plcNode == null || plcCpu == null)
        {
            Console.WriteLine("補連線缺節點：HMI X1 / PLC X1 / CPU。");
            return;
        }

        try
        {
            HwHmiConnection created = management.Connections.Create<HwHmiConnection>(
                hmiNode,
                plcCpu,
                plcNode);
            Console.WriteLine("已建 OP2 → Main PLC 連線：" +
                created.LocalConnectionName);
            TryRenameConnection(hmi, "HMI_Connection_1");
        }
        catch (Exception ex)
        {
            Console.WriteLine("建 HMI 連線失敗：" + Flatten(ex));
        }
#endif
    }

    private static void TryRenameConnection(HmiTarget hmi, string name)
    {
        if (hmi == null)
        {
            return;
        }

        foreach (HmiSoftConnection connection in hmi.Connections)
        {
            if (string.Equals(connection.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                connection.SetAttribute("Name", name);
                Console.WriteLine("  軟體連線改名：" + connection.Name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  連線改名略過：" + Flatten(ex));
            }
        }
    }

    private static Node FindNamedNode(Device device, string nodeName)
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
                if (NodeNameMatches(node, nodeName))
                {
                    return node;
                }
            }
        }

        return null;
    }

    private static void SetModuleStart(Project project, string cpuName, string order, int start)
    {
        Device device = FindDeviceByItem(project, cpuName);
        if (device == null)
        {
            Console.WriteLine(cpuName + "：找不到。");
            return;
        }

        DeviceItem sm = FindByOrder(device, order);
        if (sm == null)
        {
            Console.WriteLine(cpuName + "：找不到 " + order);
            return;
        }

        SetItemAddresses(sm, start);
    }

    private static bool SetNamedStart(Project project, string cpuName, string itemName, int start)
    {
        Device device = FindDeviceByItem(project, cpuName);
        if (device == null)
        {
            return false;
        }

        DeviceItem item = HardwareBuilder.AllItems(device)
            .FirstOrDefault(i => string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            Console.WriteLine(cpuName + " 找不到 " + itemName);
            return false;
        }

        return SetItemAddresses(item, start);
    }

    private static void SetRioSlot(Project project, string rioName, int position, int start)
    {
        Device device = FindDeviceByItem(project, rioName);
        if (device == null)
        {
            Console.WriteLine(rioName + "：找不到。");
            return;
        }

        DeviceItem item = HardwareBuilder.AllItems(device)
            .FirstOrDefault(i => i.PositionNumber == position &&
                i.Container != null &&
                (i.Container.TypeIdentifier ?? string.Empty)
                    .IndexOf("Rack", StringComparison.OrdinalIgnoreCase) >= 0);
        if (item == null)
        {
            Console.WriteLine(rioName + " slot " + position + " 找不到。");
            return;
        }

        SetItemAddresses(item, start);
    }

    private static void AlignMainSm3(Project project)
    {
        Device main = FindDeviceByItem(project, "25017_Main_PLC");
        if (main == null)
        {
            return;
        }

        DeviceItem sm = HardwareBuilder.AllItems(main)
            .FirstOrDefault(i => string.Equals(i.Name, "DI 16/DQ 16x24VDC_3", StringComparison.OrdinalIgnoreCase));
        if (sm == null)
        {
            Console.WriteLine("Main SM3 找不到。");
            return;
        }

        // Q 不能跟 I 同時改時，先把 I 搬回跟 Q 同一邊，再一起推到 6。
        SetAddressesOfType(sm, "Input", 16);
        if (!SetAddressesOfType(sm, "Output", 6))
        {
            Console.WriteLine("Main SM3 Q6 仍被佔。現有 RegisteredAddresses 看不到 Q6–Q10。");
            SetAddressesOfType(sm, "Input", 6);
            return;
        }

        SetAddressesOfType(sm, "Input", 6);
    }

    private static bool SetAddressesOfType(DeviceItem item, string ioType, int start)
    {
        bool ok = true;
        bool any = false;
        List<DeviceItem> targets = new List<DeviceItem>();
        targets.Add(item);
        foreach (DeviceItem child in item.DeviceItems)
        {
            targets.Add(child);
        }

        foreach (DeviceItem target in targets)
        {
            foreach (Address address in target.Addresses)
            {
                if (address.StartAddress < 0)
                {
                    continue;
                }

                if (address.IoType.ToString().IndexOf(ioType, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                any = true;
                int current = address.StartAddress;
                if (current == start)
                {
                    Console.WriteLine("  " + target.Name + " " + address.IoType + " 已是 " + start);
                    continue;
                }

                try
                {
                    address.SetAttribute("StartAddress", start);
                    Console.WriteLine("  " + target.Name + " " + address.IoType +
                        " " + current + " -> " + start);
                }
                catch (Exception ex)
                {
                    ok = false;
                    Console.WriteLine("  改址失敗 " + target.Name + " " +
                        address.IoType + " " + current + "：" + Flatten(ex));
                }
            }
        }

        return any && ok;
    }

    private static void ReplugMainSm3(Project project, int start)
    {
        Device main = FindDeviceByItem(project, "25017_Main_PLC");
        if (main == null)
        {
            return;
        }

        DeviceItem sm = HardwareBuilder.AllItems(main)
            .FirstOrDefault(i => string.Equals(i.Name, "DI 16/DQ 16x24VDC_3", StringComparison.OrdinalIgnoreCase));
        DeviceItem rack = FindRack(main);
        if (sm == null || rack == null)
        {
            Console.WriteLine("Main SM3 重插：找不到模組或機架。");
            return;
        }

        string typeId = sm.TypeIdentifier;
        int position = sm.PositionNumber;
        Console.WriteLine("Main SM3 改 Q 失敗，刪了重插 slot " + position);
        try
        {
            sm.Delete();
            DeviceItem made = rack.PlugNew(typeId, "DI 16/DQ 16x24VDC_3", position);
            Console.WriteLine("  已重插 " + made.Name);
            SetItemAddresses(made, start);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  重插失敗：" + Flatten(ex));
        }
    }

    private static bool SetItemAddresses(DeviceItem item, int start)
    {
        List<DeviceItem> targets = new List<DeviceItem>();
        targets.Add(item);
        foreach (DeviceItem child in item.DeviceItems)
        {
            targets.Add(child);
        }

        bool ok = true;
        bool any = false;
        foreach (DeviceItem target in targets)
        {
            foreach (Address address in target.Addresses)
            {
                if (address.StartAddress < 0)
                {
                    continue;
                }

                any = true;
                int current = address.StartAddress;
                if (current == start)
                {
                    Console.WriteLine("  " + target.Name + " " + address.IoType +
                        " 已是 " + start);
                    continue;
                }

                try
                {
                    address.SetAttribute("StartAddress", start);
                    Console.WriteLine("  " + target.Name + " " + address.IoType +
                        " " + current + " -> " + start);
                }
                catch (Exception ex)
                {
                    ok = false;
                    Console.WriteLine("  改址失敗 " + target.Name + " " +
                        address.IoType + " " + current + "：" + Flatten(ex));
                }
            }
        }

        return any && ok;
    }

    private static void ShiftRioBySetAttribute(Project project, string rioName)
    {
        Device device = FindDeviceByItem(project, rioName);
        if (device == null)
        {
            Console.WriteLine(rioName + "：找不到。");
            return;
        }

        ShiftRioAddresses(device);
    }

    private static void ConnectRioToMain(Project project, string rioName, string ip)
    {
        Device rio = FindDeviceByItem(project, rioName);
        if (rio == null)
        {
            Console.WriteLine("接 PN 失敗：找不到 " + rioName);
            return;
        }

        ConnectDeviceToMainIo(project, rio, ip, rioName);
    }

    private static void ConnectDeviceToMainIo(
        Project project,
        Device device,
        string ip,
        string label)
    {
        Device main = FindDeviceByItem(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("接 PN 失敗：找不到 Main。");
            return;
        }

        SetIp(device, ip);

        Subnet subnet = project.Subnets.FirstOrDefault(s =>
            string.Equals(s.Name, "PN/IE_1", StringComparison.OrdinalIgnoreCase));
        IoController controller = null;
        foreach (DeviceItem item in HardwareBuilder.AllItems(main))
        {
            NetworkInterface net = item.GetService<NetworkInterface>();
            if (net == null)
            {
                continue;
            }

            controller = net.IoControllers.Cast<IoController>().FirstOrDefault();
            if (controller != null)
            {
                break;
            }
        }

        foreach (DeviceItem item in HardwareBuilder.AllItems(device))
        {
            NetworkInterface net = item.GetService<NetworkInterface>();
            if (net == null)
            {
                continue;
            }

            foreach (Node node in net.Nodes)
            {
                try
                {
                    if (node.ConnectedSubnet == null && subnet != null)
                    {
                        node.ConnectToSubnet(subnet);
                        Console.WriteLine("  " + label + " 接上 " + subnet.Name);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  接子網路失敗：" + Flatten(ex));
                }
            }

            foreach (IoConnector connector in net.IoConnectors)
            {
                if (connector.ConnectedToIoSystem != null)
                {
                    Console.WriteLine("  已接到 " + connector.ConnectedToIoSystem.Name);
                    continue;
                }

                if (controller == null)
                {
                    continue;
                }

                try
                {
                    IoSystem system = controller.IoSystem ??
                        controller.CreateIoSystem("PROFINET IO-System");
                    connector.ConnectToIoSystem(system);
                    Console.WriteLine("  " + label + " -> Main IO system");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  接 IO system 失敗：" + Flatten(ex));
                }
            }
        }
    }

    private static void PlugIfMissing(
        HardwareObject target,
        string typeId,
        string name,
        int position)
    {
        Device device = OwningDevice(target);
        if (device != null &&
            HardwareBuilder.AllItems(device).Any(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) ||
                (HasOrder(item, ArticleOf(typeId)) && item.PositionNumber == position)))
        {
            Console.WriteLine("  已有 " + name);
            return;
        }

        try
        {
            if (!target.CanPlugNew(typeId, name, position))
            {
                Console.WriteLine("  插不進去 " + name + " @ " + target.Name +
                    " slot " + position + " / " + typeId);
                return;
            }

            target.PlugNew(typeId, name, position);
            Console.WriteLine("  插入 " + name + " @ " + target.Name + " slot " + position);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  插入失敗 " + name + "：" + Flatten(ex));
        }
    }

    private static Device OwningDevice(HardwareObject target)
    {
        Device device = target as Device;
        if (device != null)
        {
            return device;
        }

        IEngineeringObject current = target;
        while (current != null)
        {
            device = current as Device;
            if (device != null)
            {
                return device;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string CatalogType(TiaPortal portal, string article)
    {
        IList<CatalogEntry> hits = portal.HardwareCatalog.Find(article);
        if (hits == null || hits.Count == 0)
        {
            hits = portal.HardwareCatalog.Find(article.Replace(" ", string.Empty));
        }

        if (hits == null || hits.Count == 0)
        {
            Console.WriteLine("目錄沒有：" + article);
            return null;
        }

        CatalogEntry best = hits
            .OrderByDescending(entry => entry.Version ?? string.Empty)
            .First();
        Console.WriteLine("目錄 " + article + " -> " + best.TypeIdentifier);
        return best.TypeIdentifier;
    }

    private static string CatalogHmiType(TiaPortal portal, string article, string mustContain)
    {
        IList<CatalogEntry> hits = portal.HardwareCatalog.Find(article);
        if (hits == null || hits.Count == 0)
        {
            Console.WriteLine("目錄沒有：" + article);
            return null;
        }

        List<CatalogEntry> ranked = hits
            .Where(entry =>
            {
                string typeId = entry.TypeIdentifier ?? string.Empty;
                string name = entry.TypeName ?? string.Empty;
                if (typeId.IndexOf("Portrait", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }

                if (name.IndexOf(mustContain, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }

                return Compact(typeId).IndexOf(Compact(article), StringComparison.OrdinalIgnoreCase) >= 0;
            })
            .OrderByDescending(entry => entry.Version ?? string.Empty)
            .ToList();

        if (ranked.Count == 0)
        {
            Console.WriteLine("目錄有命中但沒有可用的 " + article + " / " + mustContain);
            foreach (CatalogEntry entry in hits.Take(8))
            {
                Console.WriteLine("  " + entry.ArticleNumber + " | " + entry.TypeName +
                    " | " + entry.TypeIdentifier);
            }

            return null;
        }

        CatalogEntry best = ranked[0];
        Console.WriteLine("目錄確認 " + article + " = " + best.TypeName +
            " | " + best.TypeIdentifier);
        return best.TypeIdentifier;
    }

    private static DeviceItem FindHmiPanel(Device device)
    {
        return HardwareBuilder.AllItems(device).FirstOrDefault(item =>
            (item.TypeIdentifier ?? string.Empty)
                .IndexOf("6AV2", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static void ChangeHmiPanel(Project project, string deviceName, string typeId, string ip)
    {
        Device device = FindDeviceByItem(project, deviceName);
        if (device == null)
        {
            Console.WriteLine(deviceName + "：找不到。");
            return;
        }

        DeviceItem panel = FindHmiPanel(device);
        if (panel == null)
        {
            Console.WriteLine(deviceName + "：找不到面板項。");
            return;
        }

        Console.WriteLine("=== " + deviceName + " ===");
        Console.WriteLine("  現在：" + panel.TypeIdentifier);
        DescribeHmiSoftware(device);

        if (string.Equals(panel.TypeIdentifier, typeId, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  已是目標型號。");
            return;
        }

        try
        {
            panel.ChangeType(typeId);
            Console.WriteLine("  ChangeType 後：" + panel.TypeIdentifier);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ChangeType 失敗：" + Flatten(ex));
            return;
        }

        SetNodeIp(device, "X1", ip);
        ConnectNodeToSubnet(project, device, "X1", "PN/IE_1");
        DescribeHmiSoftware(device);
    }

    private static Device FindDeviceByItem(Project project, string itemName)
    {
        foreach (Device device in EnumerateDevices(project))
        {
            if (string.Equals(device.Name, itemName, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }

            if (HardwareBuilder.AllItems(device).Any(item =>
                string.Equals(item.Name, itemName, StringComparison.OrdinalIgnoreCase)))
            {
                return device;
            }
        }

        return null;
    }

    private static DeviceItem FindCpu(Device device)
    {
        return HardwareBuilder.AllItems(device).FirstOrDefault(item =>
            item.Classification == DeviceItemClassifications.CPU);
    }

    private static DeviceItem FindRack(Device device)
    {
        return device.DeviceItems.FirstOrDefault(item =>
            (item.TypeIdentifier ?? string.Empty)
                .IndexOf("Rack", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static DeviceItem FindByOrder(Device device, string order)
    {
        return HardwareBuilder.AllItems(device).FirstOrDefault(item => HasOrder(item, order));
    }

    private static bool HasOrder(DeviceItem item, string order)
    {
        string typeId = item.TypeIdentifier ?? string.Empty;
        string compactType = Compact(typeId);
        string compactOrder = Compact(order);
        return compactType.IndexOf(compactOrder, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ArticleOf(string typeId)
    {
        string value = typeId ?? string.Empty;
        if (value.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring("OrderNumber:".Length);
        }

        int slash = value.IndexOf('/');
        return slash > 0 ? value.Substring(0, slash) : value;
    }

    private static string Compact(string value)
    {
        return (value ?? string.Empty).Replace(" ", string.Empty);
    }

    private static string TryAttr(DeviceItem item, string name)
    {
        try
        {
            object value = item.GetAttribute(name);
            return value == null ? string.Empty : value.ToString();
        }
        catch
        {
            return string.Empty;
        }
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
            foreach (Device device in WalkGroup(group))
            {
                yield return device;
            }
        }
    }

    private static IEnumerable<Device> WalkGroup(DeviceUserGroup group)
    {
        foreach (Device device in group.Devices)
        {
            yield return device;
        }

        foreach (DeviceUserGroup child in group.Groups)
        {
            foreach (Device device in WalkGroup(child))
            {
                yield return device;
            }
        }
    }

    private static string Flatten(Exception ex)
    {
        return ex.GetBaseException().Message;
    }
}

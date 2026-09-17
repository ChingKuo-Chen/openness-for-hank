using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Communication;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.Types;

// 18B Tag/區塊 → 20B，並從 18B 拷一份 24B 程式進 Main。
internal static class StationSync
{
    private static readonly string[] CloneBlocks =
    {
        "18B_bobbin_load",
        "18B_Pos_Ramp_DB",
        "Rotor_Positioning_18B_DB",
        "Rotor_Positioning_18B_Retain_DB",
        "Modbus_Zigbee_18B",
        "Modbus_Comm_Zigbee_18B_DB"
    };

    private static readonly string[] PatchFbs =
    {
        "Main_Logic",
        "Event_Control",
        "Main",
        "Cyclic interrupt"
    };

    private static readonly string[] PosValveStations = { "6B", "12B", "20B", "24B" };

    public static int Apply(TiaPortal portal, Project project)
    {
        Console.WriteLine("18B→20B 名稱 + 加上 24B 程式（Main）。24B Inside 已有拷貝，不動。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string staging = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "station-sync");
        Directory.CreateDirectory(staging);

        Clone18BBlocksTo24B(main, staging);
        Add24BHardwareTags(main);
        Add24BDriveTags(main);
        Add24BEventTags(main);
        PatchGlobalDbMembers(main, staging, "Motors");
        PatchGlobalDbMembers(main, staging, "opt");

        bool fbOk = true;
        foreach (string fb in PatchFbs)
        {
            if (!TryPatchFb(main, staging, fb))
            {
                fbOk = false;
            }
        }

        List<string> renamedTags = Rename18BTo20B(main, project);
        RenameHmiForRenamedTags(project, staging, renamedTags);

        project.Save();
        Console.WriteLine("已存（改名 + 24B 區塊/Tag）。");

        CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");

        if (!fbOk)
        {
            Console.WriteLine("Main_Logic / Event_Control / Main 有匯不出的，24B 呼叫網路可能還沒進循環。");
        }

        return 0;
    }

    public static int FixCompile(TiaPortal portal, Project project)
    {
        Console.WriteLine("把 Motors/opt 的 20B 成員改回 18B（24B 留下），Event_Control 對 Main_Logic_DB 也改回 18B。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            return 1;
        }

        string staging = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "station-sync");
        Directory.CreateDirectory(staging);

        RevertBlockNames(main, staging, "Motors", "20B", "18B");
        RevertBlockNames(main, staging, "opt", "20B", "18B");
        RevertEventControlMainLogicRefs(main, staging);

        project.Save();
        CompilePlc(main);
        project.Save();
        Console.WriteLine("已存。");
        return 0;
    }

    private static void RevertBlockNames(PlcSoftware plc, string staging, string blockName, string from, string to)
    {
        PlcBlock block = FindBlock(plc, blockName);
        if (block == null)
        {
            return;
        }

        string path = Path.Combine(staging, blockName + "-revert.xml");
        if (!ExportBlockXml(block, path))
        {
            return;
        }

        XDocument doc = XDocument.Load(path);
        int n = RenameNameAttributes(doc.Root, from, to);
        doc.Save(path);
        Console.WriteLine("  " + blockName + " 改回 " + n + " 處。");
        PlcBlockGroup folder = block.Parent as PlcBlockGroup ?? plc.BlockGroup;
        folder.Blocks.Import(new FileInfo(path), ImportOptions.Override);
    }

    private static void RevertEventControlMainLogicRefs(PlcSoftware plc, string staging)
    {
        PlcBlock block = FindBlock(plc, "Event_Control");
        if (block == null)
        {
            return;
        }

        string path = Path.Combine(staging, "Event_Control-revert.xml");
        if (!ExportBlockXml(block, path))
        {
            return;
        }

        XDocument doc = XDocument.Load(path);
        int changed = 0;
        foreach (XElement access in doc.Descendants().Where(e => e.Name.LocalName == "Access").ToList())
        {
            bool touchesMain = access.Descendants().Any(e =>
                e.Name.LocalName == "Component" &&
                (string)e.Attribute("Name") == "Main_Logic_DB");
            if (!touchesMain)
            {
                continue;
            }

            foreach (XAttribute name in access.DescendantsAndSelf()
                .Select(e => e.Attribute("Name"))
                .Where(a => a != null && a.Value.IndexOf("20B", StringComparison.Ordinal) >= 0))
            {
                name.Value = name.Value.Replace("20B", "18B");
                changed++;
            }
        }

        doc.Save(path);
        Console.WriteLine("  Event_Control Main_Logic_DB 路徑改了 " + changed + " 處。");
        PlcBlockGroup folder = block.Parent as PlcBlockGroup ?? plc.BlockGroup;
        folder.Blocks.Import(new FileInfo(path), ImportOptions.Override);
    }

    public static int FinishUserRequest(TiaPortal portal, Project project)
    {
        Console.WriteLine("Tag 表 18B→20B，並把 20B 網路拷成 24B 接進循環。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string staging = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "station-sync");
        Directory.CreateDirectory(staging);

        RenameStationTables(project);
        project.Save();
        Console.WriteLine("已存（表名）。");

        if (!PatchDbMembersOnly(main, staging, "Main_Logic_DB"))
        {
            Console.WriteLine("Main_Logic_DB 匯不出或匯入失敗。");
        }

        project.Save();

        string[] fbs =
        {
            "Main_Logic",
            "Event_Control",
            "Main",
            "Cyclic interrupt",
            "Cal_Ratios",
            "Cal_Ratios_1"
        };

        foreach (string fb in fbs)
        {
            TryAdd24BCalls(main, staging, fb);
        }

        project.Save();
        Console.WriteLine("已存（24B 網路）。");
        CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return 0;
    }

    private static void RenameStationTables(Project project)
    {
        Console.WriteLine("Tag 表名：");
        foreach (PlcSoftware plc in LadBlockTools.FindAllPlcSoftwares(project))
        {
            string from = "18B";
            string to = "20B";
            if (plc.Name != null && plc.Name.IndexOf("24B", StringComparison.Ordinal) >= 0)
            {
                to = "24B";
            }
            else if (plc.Name != null &&
                (plc.Name.IndexOf("6B", StringComparison.Ordinal) >= 0 ||
                 plc.Name.IndexOf("12B", StringComparison.Ordinal) >= 0 ||
                 plc.Name.IndexOf("PF", StringComparison.Ordinal) >= 0 ||
                 plc.Name.IndexOf("TF", StringComparison.Ordinal) >= 0))
            {
                from = null;
            }

            foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup).ToList())
            {
                string name = table.Name ?? "";
                Console.WriteLine("  PLC " + plc.Name + " / " + name);
                if (from == null || name.IndexOf(from, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                string next = name.Replace(from, to);
                try
                {
                    table.SetAttribute("Name", next);
                    Console.WriteLine("    -> " + next);
                }
                catch (Exception ex)
                {
                    try
                    {
                        table.Name = next;
                        Console.WriteLine("    -> " + next);
                    }
                    catch (Exception ex2)
                    {
                        Console.WriteLine("    改名失敗：" + Flatten(ex) + " / " + Flatten(ex2));
                    }
                }
            }
        }

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

                foreach (TagTable table in hmi.TagFolder.TagTables.ToList())
                {
                    string name = table.Name ?? "";
                    Console.WriteLine("  HMI " + hmi.Name + " / " + name);
                    if (name.IndexOf("18B", StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    string next = name.Replace("18B", "20B");
                    try
                    {
                        table.SetAttribute("Name", next);
                        Console.WriteLine("    -> " + next);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("    HMI 表改名失敗：" + Flatten(ex));
                    }
                }
            }
        }
    }

    private static bool PatchDbMembersOnly(PlcSoftware plc, string staging, string blockName)
    {
        PlcBlock block = FindBlock(plc, blockName);
        if (block == null)
        {
            Console.WriteLine("找不到 " + blockName + "。");
            return false;
        }

        string path = Path.Combine(staging, blockName + "-24b.xml");
        if (!ExportBlockXml(block, path))
        {
            return false;
        }

        XDocument doc = XDocument.Load(path);
        int added = Clone18BMembers(doc.Root);
        doc.Save(path);
        Console.WriteLine("  " + blockName + " 加了 " + added + " 個 24B 成員。");
        PlcBlockGroup folder = block.Parent as PlcBlockGroup ?? plc.BlockGroup;
        try
        {
            folder.Blocks.Import(new FileInfo(path), ImportOptions.Override);
            Console.WriteLine("  已匯入 " + blockName);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯入失敗 " + blockName + "：" + Flatten(ex));
            return false;
        }
    }

    private static bool TryAdd24BCalls(PlcSoftware plc, string staging, string blockName)
    {
        PlcBlock block = FindBlock(plc, blockName);
        if (block == null)
        {
            Console.WriteLine("找不到 " + blockName + "，略過。");
            return true;
        }

        string path = Path.Combine(staging, Sanitize(blockName) + "-24b.xml");
        if (!ExportBlockXml(block, path))
        {
            return false;
        }

        XDocument doc = XDocument.Load(path);
        int members = 0;
        if (!string.Equals(blockName, "Main_Logic", StringComparison.OrdinalIgnoreCase))
        {
            members = Clone18BMembers(doc.Root);
        }

        int nets = Clone20BStationNetworks(doc.Root);
        Console.WriteLine("  " + blockName + " 成員+" + members + " 網路+" + nets);
        if (members == 0 && nets == 0)
        {
            Console.WriteLine("  無改動，不匯入 " + blockName);
            return true;
        }

        doc.Save(path);

        PlcBlockGroup folder = block.Parent as PlcBlockGroup ?? plc.BlockGroup;
        try
        {
            folder.Blocks.Import(new FileInfo(path), ImportOptions.Override);
            Console.WriteLine("  已匯入 " + blockName);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯入失敗 " + blockName + "：" + Flatten(ex));
            return false;
        }
    }

    public static int FixOb30(TiaPortal portal, Project project)
    {
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            return 1;
        }

        PlcBlock ob = FindBlock(main, "Cyclic interrupt");
        if (ob == null)
        {
            Console.WriteLine("找不到 Cyclic interrupt。");
            return 1;
        }

        try
        {
            ob.SetAttribute("CyclicTime", 10);
            Console.WriteLine("OB30 CyclicTime = 10");
        }
        catch (Exception ex)
        {
            Console.WriteLine("OB30 設定失敗：" + Flatten(ex));
            try
            {
                ob.SetAttribute("CyclicTime", 5);
                Console.WriteLine("OB30 CyclicTime = 5");
            }
            catch (Exception ex2)
            {
                Console.WriteLine("OB30 再試失敗：" + Flatten(ex2));
            }
        }

        project.Save();
        CompilePlc(main);
        project.Save();
        return 0;
    }

    private static bool IsCompileUnit(XElement e)
    {
        return e.Name.LocalName == "CompileUnit" ||
            e.Name.LocalName == "SW.Blocks.CompileUnit";
    }

    private static int Clone20BStationNetworks(XElement root)
    {
        List<XElement> units = root.Descendants()
            .Where(IsCompileUnit)
            .Where(HasNameToken("20B"))
            .Where(NotHasNameToken("24B"))
            .Where(NotHasNameToken("6B"))
            .Where(NotHasNameToken("12B"))
            .ToList();

        if (units.Count == 0)
        {
            return 0;
        }

        int maxUid = 0;
        foreach (XElement el in root.DescendantsAndSelf())
        {
            XAttribute uid = el.Attribute("UId");
            int n;
            if (uid != null && int.TryParse(uid.Value, out n) && n > maxUid)
            {
                maxUid = n;
            }
        }

        int maxId = 0;
        foreach (XElement el in root.DescendantsAndSelf())
        {
            XAttribute id = el.Attribute("ID");
            int n;
            if (id != null &&
                int.TryParse(id.Value, System.Globalization.NumberStyles.HexNumber, null, out n) &&
                n > maxId)
            {
                maxId = n;
            }
        }

        int added = 0;
        foreach (XElement unit in units)
        {
            XElement clone = new XElement(unit);
            maxUid += 500;
            maxId += 16;
            ShiftUids(clone, maxUid);
            ShiftIds(clone, maxId);
            RenameNameAttributes(clone, "20B", "24B");
            RenameNameAttributes(clone, "18B", "24B");
            unit.AddAfterSelf(clone);
            added++;
        }

        return added;
    }

    private static Func<XElement, bool> HasNameToken(string token)
    {
        return unit => unit.DescendantsAndSelf().Any(e =>
        {
            XAttribute name = e.Attribute("Name");
            if (name != null && name.Value.IndexOf(token, StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            return e.Name.LocalName == "Name" &&
                e.Value != null &&
                e.Value.IndexOf(token, StringComparison.Ordinal) >= 0;
        });
    }

    private static Func<XElement, bool> NotHasNameToken(string token)
    {
        Func<XElement, bool> has = HasNameToken(token);
        return unit => !has(unit);
    }

    public static int RenameRest(TiaPortal portal, Project project)
    {
        Console.WriteLine("繼續把剩下的 18B Tag/表/區塊改成 20B。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        Rename18BTo20B(main, project);
        project.Save();
        Console.WriteLine("已存。");
        CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return 0;
    }

    // MB_COMM_LOAD_* / MB_MASTER_* 在 System blocks / Program resources，
    // plc.BlockGroup.Blocks 列不到。只改 Instance DB 名，不匯入系統 FB。
    public static int HideVisible18B(TiaPortal portal, Project project)
    {
        Console.WriteLine("看得到的 18B → 20B（含系統 Instance DB）。連線屬性與 XML ID 不改。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        Console.WriteLine("系統區塊（Program resources）：");
        foreach (PlcBlock block in EnumerateSystemBlocks(main))
        {
            Console.WriteLine("  " + block.GetType().Name + "  " + block.Name + "  #" + block.Number);
        }

        Rename18BTo20B(main, project);

        string staging = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "station-sync");
        string outDir = Path.Combine(staging, "hide-18b");
        Directory.CreateDirectory(outDir);

        ImportWithVisible20B(main, Path.Combine(staging, "opt-live-24b-follow.xml"), outDir);
        ImportWithVisible20B(main, Path.Combine(staging, "PV-live-24b-follow.xml"), outDir);
        ImportWithVisible20B(main, Path.Combine(staging, "Event_Control-24b-prog-24b-follow.xml"), outDir);

        PlcBlock powerLoss = FindBlock(main, "Power_Loss_Ramp");
        if (powerLoss == null)
        {
            Console.WriteLine("找不到 Power_Loss_Ramp。");
        }
        else
        {
            string path = Path.Combine(outDir, "Power_Loss_Ramp.xml");
            if (ExportBlockXmlQuiet(powerLoss, path))
            {
                XDocument doc = XDocument.Load(path);
                int n = ReplaceVisible18B(doc.Root);
                Console.WriteLine("  Power_Loss_Ramp 可見 18B " + n);
                if (n > 0)
                {
                    doc.Save(path);
                    TryImportBlockFile(main, powerLoss, path);
                }
            }
        }

        PlcBlock ob = FindBlock(main, "Cyclic interrupt");
        if (ob != null)
        {
            try
            {
                ob.SetAttribute("CyclicTime", 10);
                Console.WriteLine("OB30 CyclicTime = 10");
            }
            catch (Exception ex)
            {
                Console.WriteLine("OB30：" + Flatten(ex));
            }
        }

        project.Save();
        Console.WriteLine("已存（匯入後）。");
        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");

        if (errors != 0)
        {
            Console.WriteLine("Main 還有編譯錯，畫面與 Energy Tag 先不動。");
            return 1;
        }

        string hmiDir = Path.Combine(staging, "rename-18b");
        Directory.CreateDirectory(hmiDir);
        int screens = RenameHmiScreens18B(project, hmiDir);
        Console.WriteLine("HMI 畫面 " + screens + "。");
        RewireEnergyTags800(project);
        project.Save();
        Console.WriteLine("完成。已存。");
        return 0;
    }

    public static int FixPowerLossRamp(TiaPortal portal, Project project)
    {
        Console.WriteLine("修 Power_Loss_Ramp 裡還看得到的 18B。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "station-sync", "hide-18b");
        Directory.CreateDirectory(dir);

        if (!PatchPowerLossRamp(main, project, dir))
        {
            return 1;
        }

        project.Save();
        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        if (errors != 0)
        {
            Console.WriteLine("Main 還有編譯錯，畫面與 Energy Tag 先不動。");
            return 1;
        }

        string hmiDir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "station-sync", "rename-18b");
        Directory.CreateDirectory(hmiDir);
        int screens = RenameHmiScreens18B(project, hmiDir);
        Console.WriteLine("HMI 畫面 " + screens + "。");
        RewireEnergyTags800(project);
        project.Save();
        Console.WriteLine("完成。已存。");
        return 0;
    }

    public static int RenameHmi18BOnly(Project project)
    {
        Console.WriteLine("HMI Tag 與畫面可見 18B → 20B。不動連線、不動 XML ID。");
        string staging = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "station-sync", "rename-18b");
        Directory.CreateDirectory(staging);
        int tags = ReplaceHmiTag18B(project, staging);
        project.Save();
        Console.WriteLine("HMI Tag 表 " + tags + "。已存。");
        int screens = RenameHmiScreens18B(project, staging);
        project.Save();
        Console.WriteLine("HMI 畫面 " + screens + "。已存。");
        return 0;
    }

    private static bool PatchPowerLossRamp(PlcSoftware plc, Project project, string dir)
    {
        PlcBlock fc = FindBlock(plc, "Power_Loss_Ramp");
        if (fc == null)
        {
            Console.WriteLine("找不到 Power_Loss_Ramp。");
            return false;
        }

        string fcPath = Path.Combine(dir, "Power_Loss_Ramp.xml");
        if (TryExportBlock(fc, fcPath, ExportOptions.None) ||
            TryExportBlock(fc, fcPath, ExportOptions.WithDefaults))
        {
            return ImportReplacedPowerLoss(plc, fc, fcPath);
        }

        Console.WriteLine("先暫時把 UDT ratio 的 18B 加回來才能匯出。");
        PlcType ratio = FindType(plc, "ratio");
        if (ratio == null)
        {
            Console.WriteLine("找不到 UDT ratio。");
            return false;
        }

        string typePath = Path.Combine(dir, "ratio-temp-18b.xml");
        if (!ExportTypeXml(ratio, typePath))
        {
            return false;
        }

        XDocument typeDoc = XDocument.Load(typePath);
        if (FindMember(typeDoc.Root, "18B") == null)
        {
            XElement src = FindMember(typeDoc.Root, "20B");
            if (src == null)
            {
                Console.WriteLine("UDT ratio 沒有 20B 成員。");
                return false;
            }

            XElement clone = new XElement(src);
            clone.SetAttributeValue("Name", "18B");
            src.AddAfterSelf(clone);
            typeDoc.Save(typePath);
            if (!ImportTypeFile(plc, ratio, typePath))
            {
                return false;
            }

            project.Save();
            Console.WriteLine("已暫加 ratio.18B 並存。");
        }

        Console.WriteLine("整台編譯後再匯出 Power_Loss_Ramp（不單獨編這個 FC）。");
        CompilePlc(plc);
        fc = FindBlock(plc, "Power_Loss_Ramp");
        if (fc == null)
        {
            Console.WriteLine("找不到 Power_Loss_Ramp。");
            return false;
        }

        if (!TryExportBlock(fc, fcPath, ExportOptions.WithDefaults) &&
            !TryExportBlock(fc, fcPath, ExportOptions.None))
        {
            Console.WriteLine("還是匯不出 Power_Loss_Ramp。");
            return false;
        }

        if (!ImportReplacedPowerLoss(plc, fc, fcPath))
        {
            return false;
        }

        ratio = FindType(plc, "ratio");
        if (ratio == null || !ExportTypeXml(ratio, typePath))
        {
            return false;
        }

        typeDoc = XDocument.Load(typePath);
        XElement extra = FindMember(typeDoc.Root, "18B");
        if (extra != null)
        {
            extra.Remove();
            typeDoc.Save(typePath);
            if (!ImportTypeFile(plc, ratio, typePath))
            {
                return false;
            }

            Console.WriteLine("已從 UDT ratio 拿掉 18B。");
        }

        return true;
    }

    private static bool ImportReplacedPowerLoss(PlcSoftware plc, PlcBlock fc, string path)
    {
        XDocument doc = XDocument.Load(path);
        int n = ReplaceVisible18B(doc.Root);
        Console.WriteLine("  Power_Loss_Ramp 可見 18B " + n);
        if (n == 0)
        {
            return true;
        }

        doc.Save(path);
        TryImportBlockFile(plc, fc, path);
        return true;
    }

    private static bool TryExportBlock(PlcBlock block, string path, ExportOptions options)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            block.Export(new FileInfo(path), options);
            Console.WriteLine("  已匯出 Power_Loss_Ramp（" + options + "）");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯出失敗（" + options + "）：" + Flatten(ex));
            return false;
        }
    }

    private static bool ImportTypeFile(PlcSoftware plc, PlcType existing, string path)
    {
        PlcTypeGroup folder = existing != null
            ? existing.Parent as PlcTypeGroup ?? plc.TypeGroup
            : plc.TypeGroup;
        try
        {
            folder.Types.Import(new FileInfo(path), ImportOptions.Override);
            Console.WriteLine("  已匯入 " + Path.GetFileName(path));
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  UDT 匯入失敗：" + Flatten(ex));
            return false;
        }
    }

    private static XElement FindMember(XElement root, string name)
    {
        foreach (XElement el in root.Descendants())
        {
            if (el.Name.LocalName == "Member" &&
                (string)el.Attribute("Name") == name)
            {
                return el;
            }
        }

        return null;
    }

    public static int RenamePlcProgram18BTo20B(Project project)
    {
        Console.WriteLine("TIA 看得到的 18B → 20B（PLC Tag/成員/程式/註解、HMI Tag/畫面、硬體名）。連線屬性不改。");
        string staging = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "station-sync", "rename-18b");
        Directory.CreateDirectory(staging);
        int plcChanged = 0;

        List<PlcSoftware> plcs = LadBlockTools.FindAllPlcSoftwares(project);
        plcs.Sort((a, b) =>
        {
            bool am = a.Name.IndexOf("Main_PLC", StringComparison.OrdinalIgnoreCase) >= 0;
            bool bm = b.Name.IndexOf("Main_PLC", StringComparison.OrdinalIgnoreCase) >= 0;
            return am == bm ? string.Compare(a.Name, b.Name, StringComparison.Ordinal) : (am ? -1 : 1);
        });

        foreach (PlcSoftware plc in plcs)
        {
            Console.WriteLine(new string('=', 50));
            Console.WriteLine("PLC：" + plc.Name);
            try
            {
                if (plc.Name.IndexOf("Main_PLC", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    CompilePlc(plc);
                }

                List<string> tags = Rename18BTo20B(plc, project);
                int types = Rename18BInTypes(plc, staging);
                int blocks = Rename18BInBlocks(plc, staging);
                Console.WriteLine("  Tag " + tags.Count + "、UDT " + types + "、區塊 " + blocks);
                if (tags.Count + types + blocks > 0)
                {
                    plcChanged++;
                    project.Save();
                    Console.WriteLine("  已存。");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  PLC 失敗：" + Flatten(ex));
            }
        }

        int hw = RenameHardware18B(project);
        int hmi = ReplaceHmiTag18B(project, staging);
        int screens = RenameHmiScreens18B(project, staging);
        Console.WriteLine("硬體名 " + hw + "、HMI Tag 表 " + hmi + "、畫面 " + screens + "。");
        project.Save();

        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main != null)
        {
            CompilePlc(main);
        }

        project.Save();
        Console.WriteLine("完成。動過的 PLC：" + plcChanged + "。已存。");
        return 0;
    }

    private static int ReplaceVisible18B(XElement root)
    {
        int count = RenameNameAttributes(root, "18B", "20B");
        foreach (XElement el in root.DescendantsAndSelf().ToList())
        {
            if (el.Name.LocalName != "Text")
            {
                continue;
            }

            if (el.HasElements)
            {
                foreach (XNode node in el.DescendantNodes().ToList())
                {
                    XText text = node as XText;
                    if (text == null || string.IsNullOrEmpty(text.Value))
                    {
                        continue;
                    }

                    if (text.Value.IndexOf("18B", StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    text.Value = text.Value.Replace("18B", "20B");
                    count++;
                }

                continue;
            }

            if (!string.IsNullOrEmpty(el.Value) &&
                el.Value.IndexOf("18B", StringComparison.Ordinal) >= 0)
            {
                el.Value = el.Value.Replace("18B", "20B");
                count++;
            }
        }

        return count;
    }

    private static int RenameHardware18B(Project project)
    {
        int count = 0;
        foreach (Device device in EnumerateDevices(project))
        {
            count += TryRenameObject(device, "裝置");
            foreach (DeviceItem item in WalkItems(device.DeviceItems))
            {
                count += TryRenameObject(item, "硬體");
            }
        }

        return count;
    }

    private static int TryRenameObject(IEngineeringObject target, string label)
    {
        try
        {
            object current = target.GetAttribute("Name");
            string name = current == null ? null : current.ToString();
            if (string.IsNullOrEmpty(name) || name.IndexOf("18B", StringComparison.Ordinal) < 0)
            {
                return 0;
            }

            string next = name.Replace("18B", "20B");
            target.SetAttribute("Name", next);
            Console.WriteLine("  " + label + " " + name + " -> " + next);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  " + label + " 改名失敗：" + Flatten(ex));
            return 0;
        }
    }

    private static int RenameHmiScreens18B(Project project, string staging)
    {
        int count = 0;
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

                List<string> names = new List<string>();
                List<Screen> listed = new List<Screen>();
                CollectScreens(hmi.ScreenFolder, listed);
                foreach (Screen s in listed)
                {
                    names.Add(s.Name);
                }

                string dir = Path.Combine(staging, "screens", Sanitize(device.Name));
                Directory.CreateDirectory(dir);
                foreach (string screenName in names)
                {
                    Screen screen = null;
                    List<Screen> live = new List<Screen>();
                    CollectScreens(hmi.ScreenFolder, live);
                    foreach (Screen candidate in live)
                    {
                        if (string.Equals(candidate.Name, screenName, StringComparison.Ordinal))
                        {
                            screen = candidate;
                            break;
                        }
                    }

                    if (screen == null)
                    {
                        continue;
                    }

                    string path = Path.Combine(dir, Sanitize(screenName) + ".xml");
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                        }

                        screen.Export(new FileInfo(path), ExportOptions.WithDefaults);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  畫面匯出失敗 " + screen.Name + "：" + Flatten(ex));
                        continue;
                    }

                    XDocument doc = XDocument.Load(path);
                    int n = ReplaceVisible18B(doc.Root);
                    if (n == 0)
                    {
                        continue;
                    }

                    doc.Save(path);
                    ScreenFolder folder = screen.Parent as ScreenFolder ?? hmi.ScreenFolder;
                    try
                    {
                        folder.Screens.Import(new FileInfo(path), ImportOptions.Override);
                        Console.WriteLine("  畫面內文字 " + device.Name + " / " + screenName + " " + n);
                        count++;
                        project.Save();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  畫面匯入失敗 " + screen.Name + "：" + Flatten(ex));
                    }
                }
            }
        }

        return count;
    }

    private static void CollectScreens(ScreenFolder folder, List<Screen> into)
    {
        foreach (Screen screen in folder.Screens)
        {
            into.Add(screen);
        }

        foreach (ScreenUserFolder child in folder.Folders)
        {
            CollectScreens(child, into);
        }
    }

    private static int Rename18BInTypes(PlcSoftware plc, string staging)
    {
        int changed = 0;
        string dir = Path.Combine(staging, Sanitize(plc.Name), "Types");
        Directory.CreateDirectory(dir);
        List<string> names = new List<string>();
        foreach (PlcType type in EnumerateTypes(plc.TypeGroup))
        {
            names.Add(type.Name);
        }

        foreach (string typeName in names)
        {
            PlcType type = FindType(plc, typeName);
            if (type == null)
            {
                continue;
            }

            string path = Path.Combine(dir, Sanitize(typeName) + ".xml");
            if (!ExportTypeXml(type, path))
            {
                continue;
            }

            XDocument doc = XDocument.Load(path);
            int n = ReplaceVisible18B(doc.Root);
            if (n == 0)
            {
                continue;
            }

            doc.Save(path);
            PlcTypeGroup folder = type.Parent as PlcTypeGroup ?? plc.TypeGroup;
            try
            {
                folder.Types.Import(new FileInfo(path), ImportOptions.Override);
                Console.WriteLine("  UDT " + typeName + " " + n);
                changed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  UDT 失敗 " + typeName + "：" + Flatten(ex));
            }
        }

        return changed;
    }

    private static int Rename18BInBlocks(PlcSoftware plc, string staging)
    {
        int changed = 0;
        string dir = Path.Combine(staging, Sanitize(plc.Name), "Blocks");
        Directory.CreateDirectory(dir);
        List<string> names = new List<string>();
        foreach (PlcBlock block in EnumerateBlocks(plc.BlockGroup))
        {
            if (block is InstanceDB)
            {
                continue;
            }

            names.Add(block.Name);
        }

        foreach (string blockName in names)
        {
            PlcBlock block = FindBlock(plc, blockName);
            if (block == null)
            {
                continue;
            }

            string path = Path.Combine(dir, Sanitize(blockName) + ".xml");
            if (!ExportBlockXmlQuiet(block, path))
            {
                continue;
            }

            XDocument doc = XDocument.Load(path);
            int n = ReplaceVisible18B(doc.Root);
            if (n == 0)
            {
                continue;
            }

            doc.Save(path);
            PlcBlockGroup folder = block.Parent as PlcBlockGroup ?? plc.BlockGroup;
            try
            {
                folder.Blocks.Import(new FileInfo(path), ImportOptions.Override);
                Console.WriteLine("  區塊 " + blockName + " " + n);
                changed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  區塊失敗 " + blockName + "：" + Flatten(ex));
            }
        }

        return changed;
    }

    private static int ReplaceHmiTag18B(Project project, string staging)
    {
        int files = 0;
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

                string dir = Path.Combine(staging, "hmi", Sanitize(device.Name));
                Directory.CreateDirectory(dir);
                foreach (TagTable table in hmi.TagFolder.TagTables.ToList())
                {
                    string path = Path.Combine(dir, Sanitize(table.Name) + ".xml");
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                        }

                        table.Export(new FileInfo(path), ExportOptions.WithDefaults);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  HMI Tag 表匯出失敗 " + device.Name + " / " + table.Name + "：" + Flatten(ex));
                        continue;
                    }

                    XDocument doc = XDocument.Load(path);
                    int n = ReplaceVisible18B(doc.Root);
                    if (n == 0)
                    {
                        continue;
                    }

                    doc.Save(path);
                    TagSystemFolder folder = table.Parent as TagSystemFolder;
                    if (folder == null)
                    {
                        Console.WriteLine("  找不到 Tag 表資料夾 " + table.Name);
                        continue;
                    }

                    try
                    {
                        folder.TagTables.Import(new FileInfo(path), ImportOptions.Override);
                        Console.WriteLine("  HMI Tag 表 " + device.Name + " / " + table.Name + " " + n);
                        files++;
                        project.Save();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  HMI Tag 表失敗 " + table.Name + "：" + Flatten(ex));
                    }
                }
            }
        }

        return files;
    }

    private static bool ExportBlockXmlQuiet(PlcBlock block, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            block.Export(new FileInfo(path), ExportOptions.WithDefaults);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯出失敗 " + block.Name + "：" + Flatten(ex));
            return false;
        }
    }

    private static bool ExportTypeXml(PlcType type, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            type.Export(new FileInfo(path), ExportOptions.WithDefaults);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  UDT 匯出失敗 " + type.Name + "：" + Flatten(ex));
            return false;
        }
    }

    private static IEnumerable<PlcType> EnumerateTypes(PlcTypeGroup group)
    {
        foreach (PlcType type in group.Types)
        {
            yield return type;
        }

        foreach (PlcTypeUserGroup child in group.Groups)
        {
            foreach (PlcType nested in EnumerateTypes(child))
            {
                yield return nested;
            }
        }
    }

    private static void Clone18BBlocksTo24B(PlcSoftware plc, string staging)
    {
        Console.WriteLine("拷 18B 區塊 → 24B…");
        foreach (string name in CloneBlocks)
        {
            string destName = name.Replace("18B", "24B");
            if (FindBlock(plc, destName) != null)
            {
                Console.WriteLine("  " + destName + " 已存在。");
                continue;
            }

            PlcBlock source = FindBlock(plc, name);
            if (source == null)
            {
                Console.WriteLine("  找不到 " + name + "，略過。");
                continue;
            }

            string path = Path.Combine(staging, destName + ".xml");
            if (!ExportBlockXml(source, path))
            {
                continue;
            }

            ReplaceStationInXml(path, "18B", "24B");
            PlcBlockGroup folder = source.Parent as PlcBlockGroup ?? plc.BlockGroup;
            try
            {
                folder.Blocks.Import(new FileInfo(path), ImportOptions.Override);
                Console.WriteLine("  已匯入 " + destName);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  匯入失敗 " + destName + "：" + Flatten(ex));
            }
        }
    }

    private static void Add24BHardwareTags(PlcSoftware plc)
    {
        PlcTagTable hardware = FindTable(plc, "Hardware");
        if (hardware == null)
        {
            Console.WriteLine("找不到 Hardware Tag 表。");
            return;
        }

        EnsureTag(hardware, "Q_24B_Mtr_Fan", "Bool", "%Q2.6");
        EnsureTag(hardware, "Q_24B_DC_Bus_On", "Bool", "%Q2.7");
        EnsureTag(hardware, "I_F_24B_MtrFan_Ovrld", "Bool", "%I2.6");
        EnsureTag(hardware, "I_F_24B_Mtr_OvrHt", "Bool", "%I2.7");
    }

    private static void Add24BDriveTags(PlcSoftware plc)
    {
        PlcTagTable table = FindTable(plc, "PN_Drive") ?? FindTable(plc, "Hardware");
        if (table == null)
        {
            return;
        }

        EnsureTag(table, "CT_PI_24B", "\"CT_PN_In\"", "%I220.0");
        EnsureTag(table, "CT_PO_24B", "\"CT_PN_Out_Sync_Ramp\"", "%Q220.0");
    }

    private static void Add24BEventTags(PlcSoftware plc)
    {
        PlcTagTable events = FindTable(plc, "Events");
        if (events == null)
        {
            Console.WriteLine("找不到 Events Tag 表。");
            return;
        }

        List<PlcTag> source = new List<PlcTag>();
        foreach (PlcTag tag in events.Tags)
        {
            if (tag.Name != null && tag.Name.IndexOf("18B", StringComparison.Ordinal) >= 0)
            {
                source.Add(tag);
            }
        }

        int bit = 400 * 8;
        foreach (PlcTag tag in source)
        {
            string name = tag.Name.Replace("18B", "24B");
            if (FindTag(plc, name) != null)
            {
                continue;
            }

            string address = "%M" + (bit / 8) + "." + (bit % 8);
            bit++;
            try
            {
                events.Tags.Create(name, tag.DataTypeName, address);
                Console.WriteLine("  Event " + name + " " + address);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Event 失敗 " + name + "：" + Flatten(ex));
            }
        }
    }

    private static void PatchGlobalDbMembers(PlcSoftware plc, string staging, string blockName)
    {
        PlcBlock block = FindBlock(plc, blockName);
        if (block == null)
        {
            Console.WriteLine("找不到 " + blockName + "。");
            return;
        }

        string path = Path.Combine(staging, blockName + ".xml");
        if (!ExportBlockXml(block, path))
        {
            return;
        }

        XDocument doc = XDocument.Load(path);
        int added = Clone18BMembers(doc.Root);
        int renamed = RenameNameAttributes(doc.Root, "18B", "20B");
        doc.Save(path);
        Console.WriteLine("  " + blockName + " 加了 " + added + " 個 24B 成員，改名 " + renamed + "。");

        PlcBlockGroup folder = block.Parent as PlcBlockGroup ?? plc.BlockGroup;
        try
        {
            folder.Blocks.Import(new FileInfo(path), ImportOptions.Override);
            Console.WriteLine("  已匯入 " + blockName);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯入失敗 " + blockName + "：" + Flatten(ex));
        }
    }

    private static bool TryPatchFb(PlcSoftware plc, string staging, string blockName)
    {
        PlcBlock block = FindBlock(plc, blockName);
        if (block == null)
        {
            Console.WriteLine("找不到 " + blockName + "，略過。");
            return true;
        }

        string path = Path.Combine(staging, Sanitize(blockName) + ".xml");
        if (!ExportBlockXml(block, path))
        {
            return false;
        }

        XDocument doc = XDocument.Load(path);
        int members = Clone18BMembers(doc.Root);
        int nets = Clone18BNetworks(doc.Root);
        int renamed = RenameNameAttributes(doc.Root, "18B", "20B");
        doc.Save(path);
        Console.WriteLine("  " + blockName + " 成員+" + members + " 網路+" + nets + " 改名 " + renamed);

        PlcBlockGroup folder = block.Parent as PlcBlockGroup ?? plc.BlockGroup;
        try
        {
            folder.Blocks.Import(new FileInfo(path), ImportOptions.Override);
            Console.WriteLine("  已匯入 " + blockName);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯入失敗 " + blockName + "：" + Flatten(ex));
            return false;
        }
    }

    private static List<string> Rename18BTo20B(PlcSoftware plc, Project project)
    {
        List<string> renamed = new List<string>();
        Console.WriteLine("18B → 20B 改名…");
        int sinceSave = 0;

        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup).ToList())
        {
            foreach (PlcTag tag in table.Tags.ToList())
            {
                if (tag.Name == null || tag.Name.IndexOf("18B", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                string next = tag.Name.Replace("18B", "20B");
                if (FindTag(plc, next) != null)
                {
                    Console.WriteLine("  Tag 已有 " + next + "，略過 " + tag.Name);
                    continue;
                }

                try
                {
                    string old = tag.Name;
                    tag.Name = next;
                    renamed.Add(old);
                    Console.WriteLine("  Tag " + old + " -> " + next);
                    sinceSave++;
                    if (sinceSave >= 20)
                    {
                        project.Save();
                        Console.WriteLine("  已存（改名中）。");
                        sinceSave = 0;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  Tag 改名失敗 " + tag.Name + "：" + Flatten(ex));
                }
            }

            if (table.Name != null && table.Name.IndexOf("18B", StringComparison.Ordinal) >= 0)
            {
                string next = table.Name.Replace("18B", "20B");
                try
                {
                    table.SetAttribute("Name", next);
                    Console.WriteLine("  表 " + table.Name + " -> " + next);
                }
                catch (Exception ex)
                {
                    try
                    {
                        table.Name = next;
                        Console.WriteLine("  表 -> " + next);
                    }
                    catch (Exception ex2)
                    {
                        Console.WriteLine("  表改名失敗：" + Flatten(ex) + " / " + Flatten(ex2));
                    }
                }
            }
        }

        foreach (PlcBlock block in EnumerateBlocks(plc.BlockGroup).ToList())
        {
            TryRenameBlock18B(plc, block);
        }

        Console.WriteLine("系統 Instance DB…");
        foreach (PlcBlock block in EnumerateSystemBlocks(plc).ToList())
        {
            TryRenameBlock18B(plc, block);
        }

        return renamed;
    }

    private static void TryRenameBlock18B(PlcSoftware plc, PlcBlock block)
    {
        if (block.Name == null || block.Name.IndexOf("18B", StringComparison.Ordinal) < 0)
        {
            return;
        }

        string old = block.Name;
        string next = old.Replace("18B", "20B");
        if (FindBlock(plc, next) != null)
        {
            Console.WriteLine("  區塊已有 " + next + "，略過 " + old);
            return;
        }

        try
        {
            block.SetAttribute("Name", next);
            Console.WriteLine("  區塊 " + old + " -> " + next);
            return;
        }
        catch (Exception ex)
        {
            try
            {
                block.Name = next;
                Console.WriteLine("  區塊 " + old + " -> " + next);
                return;
            }
            catch (Exception ex2)
            {
                Console.WriteLine("  區塊改名失敗 " + old + "：" + Flatten(ex) + " / " + Flatten(ex2));
            }
        }
    }

    private static void ImportWithVisible20B(PlcSoftware plc, string source, string outDir)
    {
        if (!File.Exists(source))
        {
            Console.WriteLine("  找不到 " + source);
            return;
        }

        XDocument doc = XDocument.Load(source);
        int n = ReplaceVisible18B(doc.Root);
        string name = ReadXmlBlockName(doc) ?? Path.GetFileNameWithoutExtension(source);
        string dest = Path.Combine(outDir, Sanitize(name) + ".xml");
        doc.Save(dest);
        Console.WriteLine("  " + name + " 可見 18B " + n);
        PlcBlock existing = FindBlock(plc, name);
        TryImportBlockFile(plc, existing, dest);
    }

    private static void TryImportBlockFile(PlcSoftware plc, PlcBlock existing, string path)
    {
        PlcBlockGroup folder = existing != null
            ? existing.Parent as PlcBlockGroup ?? plc.BlockGroup
            : plc.BlockGroup;
        try
        {
            folder.Blocks.Import(new FileInfo(path), ImportOptions.Override);
            Console.WriteLine("  已匯入 " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯入失敗 " + Path.GetFileName(path) + "：" + Flatten(ex));
        }
    }

    private static string ReadXmlBlockName(XDocument doc)
    {
        foreach (XElement el in doc.Descendants())
        {
            if (el.Name.LocalName == "Name" &&
                el.Parent != null &&
                el.Parent.Name.LocalName == "AttributeList")
            {
                return el.Value;
            }
        }

        return null;
    }

    private static void RenameHmiForRenamedTags(Project project, string staging, List<string> renamedTags)
    {
        if (renamedTags.Count == 0)
        {
            return;
        }

        Console.WriteLine("HMI Tag 跟 PLC 改名…");
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

                string dir = Path.Combine(staging, "hmi", Sanitize(hmi.Name));
                Directory.CreateDirectory(dir);
                foreach (TagTable table in hmi.TagFolder.TagTables.ToList())
                {
                    string path = Path.Combine(dir, Sanitize(table.Name) + ".xml");
                    table.Export(new FileInfo(path), ExportOptions.WithDefaults);
                    string xml = File.ReadAllText(path);
                    bool changed = false;
                    foreach (string old in renamedTags)
                    {
                        string next = old.Replace("18B", "20B");
                        if (xml.IndexOf(old, StringComparison.Ordinal) >= 0)
                        {
                            xml = xml.Replace(old, next);
                            changed = true;
                        }
                    }

                    if (!changed)
                    {
                        continue;
                    }

                    File.WriteAllText(path, xml);
                    TagSystemFolder folder = (TagSystemFolder)table.Parent;
                    folder.TagTables.Import(new FileInfo(path), ImportOptions.Override);
                    Console.WriteLine("  HMI 表 " + table.Name);
                }
            }
        }
    }

    private static int Clone18BMembers(XElement root)
    {
        int added = 0;
        List<XElement> members = root.Descendants()
            .Where(e => e.Name.LocalName == "Member")
            .ToList();

        foreach (XElement member in members)
        {
            XAttribute name = member.Attribute("Name");
            if (name == null || name.Value.IndexOf("18B", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            string dest = name.Value.Replace("18B", "24B");
            XElement parent = member.Parent;
            if (parent == null)
            {
                continue;
            }

            bool exists = parent.Elements().Any(e =>
                e.Name.LocalName == "Member" &&
                (string)e.Attribute("Name") == dest);
            if (exists)
            {
                continue;
            }

            XElement clone = new XElement(member);
            foreach (XElement el in clone.DescendantsAndSelf())
            {
                XAttribute n = el.Attribute("Name");
                if (n != null && n.Value.IndexOf("18B", StringComparison.Ordinal) >= 0)
                {
                    n.Value = n.Value.Replace("18B", "24B");
                }
            }

            member.AddAfterSelf(clone);
            added++;
        }

        return added;
    }

    private static int Clone18BNetworks(XElement root)
    {
        List<XElement> units = root.Descendants()
            .Where(IsCompileUnit)
            .Where(e => e.ToString().IndexOf("18B", StringComparison.Ordinal) >= 0)
            .ToList();

        if (units.Count == 0)
        {
            return 0;
        }

        int maxUid = 0;
        foreach (XElement el in root.DescendantsAndSelf())
        {
            XAttribute uid = el.Attribute("UId");
            int n;
            if (uid != null && int.TryParse(uid.Value, out n) && n > maxUid)
            {
                maxUid = n;
            }
        }

        int maxId = 0;
        foreach (XElement el in root.DescendantsAndSelf())
        {
            XAttribute id = el.Attribute("ID");
            int n;
            if (id != null && int.TryParse(id.Value, System.Globalization.NumberStyles.HexNumber, null, out n) && n > maxId)
            {
                maxId = n;
            }
        }

        int added = 0;
        foreach (XElement unit in units)
        {
            XElement clone = new XElement(unit);
            maxUid += 500;
            maxId += 16;
            ShiftUids(clone, maxUid);
            ShiftIds(clone, maxId);
            RenameNameAttributes(clone, "18B", "24B");
            unit.AddAfterSelf(clone);
            added++;
        }

        return added;
    }

    private static void ShiftUids(XElement root, int delta)
    {
        foreach (XElement el in root.DescendantsAndSelf())
        {
            XAttribute uid = el.Attribute("UId");
            int n;
            if (uid != null && int.TryParse(uid.Value, out n))
            {
                uid.Value = (n + delta).ToString();
            }
        }
    }

    private static void ShiftIds(XElement root, int delta)
    {
        foreach (XElement el in root.DescendantsAndSelf())
        {
            XAttribute id = el.Attribute("ID");
            int n;
            if (id != null &&
                int.TryParse(id.Value, System.Globalization.NumberStyles.HexNumber, null, out n))
            {
                id.Value = (n + delta).ToString("X");
            }
        }
    }

    private static int RenameNameAttributes(XElement root, string from, string to)
    {
        int count = 0;
        foreach (XElement el in root.DescendantsAndSelf())
        {
            XAttribute name = el.Attribute("Name");
            if (name != null && name.Value.IndexOf(from, StringComparison.Ordinal) >= 0)
            {
                name.Value = name.Value.Replace(from, to);
                count++;
            }

            if (el.Name.LocalName == "Name" &&
                el.Value != null &&
                el.Value.IndexOf(from, StringComparison.Ordinal) >= 0)
            {
                el.Value = el.Value.Replace(from, to);
                count++;
            }
        }

        return count;
    }

    private static void ReplaceStationInXml(string path, string from, string to)
    {
        XDocument doc = XDocument.Load(path);
        RenameNameAttributes(doc.Root, from, to);
        doc.Save(path);
    }

    private static bool ExportBlockXml(PlcBlock block, string path)
    {
        try
        {
            ICompilable compilable = block.GetService<ICompilable>();
            if (compilable != null)
            {
                compilable.Compile();
            }
        }
        catch
        {
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            block.Export(new FileInfo(path), ExportOptions.WithDefaults);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯出失敗 " + block.Name + "：" + Flatten(ex));
            return false;
        }
    }

    private static void EnsureTag(PlcTagTable table, string name, string type, string address)
    {
        foreach (PlcTag existing in table.Tags)
        {
            if (string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                Console.WriteLine("  Tag 已有 " + name);
                return;
            }
        }

        try
        {
            table.Tags.Create(name, type, address);
            Console.WriteLine("  Tag " + name + " " + address);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Tag 失敗 " + name + "：" + Flatten(ex));
        }
    }

    private static int CompilePlc(PlcSoftware plc)
    {
        ICompilable compilable = plc.GetService<ICompilable>();
        if (compilable == null)
        {
            return 0;
        }

        CompilerResult result = compilable.Compile();
        Console.WriteLine("編譯 " + plc.Name + "：" + result.State +
            "／錯誤 " + result.ErrorCount + "／警告 " + result.WarningCount);
        PrintCompileMessages(result.Messages, 1);
        return result.ErrorCount;
    }

    private static void PrintCompileMessages(CompilerResultMessageComposition messages, int depth)
    {
        if (messages == null)
        {
            return;
        }

        foreach (CompilerResultMessage message in messages)
        {
            Console.WriteLine(new string(' ', depth * 2) + message.State + " " + message.Path + "：" + message.Description);
            PrintCompileMessages(message.Messages, depth + 1);
        }
    }

    private static PlcSoftware FindPlc(Project project, string name)
    {
        Console.WriteLine("  列裝置名…");
        List<Device> devices = EnumerateDevices(project).ToList();
        foreach (Device device in devices)
        {
            Console.WriteLine("  裝置 " + device.Name);
        }

        PlcSoftware match = FindPlcOnDevices(devices, name, true);
        if (match != null)
        {
            return match;
        }

        devices = devices
            .OrderBy(d => d.Name.IndexOf("SIMATIC", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Console.WriteLine("  名稱沒對上，改掃可能的 CPU 裝置…");
        return FindPlcOnDevices(devices, name, false);
    }

    private static bool LooksLikeCpuStation(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            return false;
        }

        if (deviceName.IndexOf("HMI", StringComparison.OrdinalIgnoreCase) >= 0 ||
            deviceName.StartsWith("GSD", StringComparison.OrdinalIgnoreCase) ||
            deviceName.IndexOf("ET 200", StringComparison.OrdinalIgnoreCase) >= 0 ||
            deviceName.IndexOf("RIO", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        return deviceName.IndexOf("1200", StringComparison.OrdinalIgnoreCase) >= 0 ||
               deviceName.IndexOf("1500", StringComparison.OrdinalIgnoreCase) >= 0 ||
               deviceName.IndexOf("S7", StringComparison.OrdinalIgnoreCase) >= 0 ||
               deviceName.IndexOf("PLC", StringComparison.OrdinalIgnoreCase) >= 0 ||
               deviceName.IndexOf("SIMATIC", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static PlcSoftware FindPlcOnDevices(List<Device> devices, string name, bool nameFilter)
    {
        foreach (Device device in devices)
        {
            if (nameFilter &&
                device.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (!nameFilter && !LooksLikeCpuStation(device.Name))
            {
                continue;
            }

            Console.WriteLine("  開裝置軟體：" + device.Name);
            foreach (DeviceItem item in WalkItems(device.DeviceItems))
            {
                SoftwareContainer container = item.GetService<SoftwareContainer>();
                PlcSoftware plc = container == null ? null : container.Software as PlcSoftware;
                if (plc == null)
                {
                    continue;
                }

                Console.WriteLine("    PLC=" + plc.Name);
                if (string.Equals(plc.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return plc;
                }
            }
        }

        return null;
    }

    private static HmiTarget FindHmiTargetNamed(Project project, string softwareName, string deviceName)
    {
        foreach (Device device in EnumerateDevices(project))
        {
            if (!string.IsNullOrEmpty(deviceName) &&
                !string.Equals(device.Name, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Console.WriteLine("  HMI 裝置：" + device.Name);
            foreach (DeviceItem item in WalkItems(device.DeviceItems))
            {
                SoftwareContainer container = item.GetService<SoftwareContainer>();
                HmiTarget hmi = container == null ? null : container.Software as HmiTarget;
                if (hmi == null)
                {
                    continue;
                }

                Console.WriteLine("  HMI 軟體：" + hmi.Name);
                if (string.IsNullOrEmpty(softwareName) ||
                    string.Equals(hmi.Name, softwareName, StringComparison.OrdinalIgnoreCase))
                {
                    return hmi;
                }
            }
        }

        foreach (HmiTarget hmi in EnumerateHmiTargets(project))
        {
            if (string.Equals(hmi.Name, softwareName, StringComparison.OrdinalIgnoreCase))
            {
                return hmi;
            }
        }

        return null;
    }

    private static PlcType FindType(PlcSoftware plc, string name)
    {
        return EnumerateTypes(plc.TypeGroup)
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static PlcBlock FindBlock(PlcSoftware plc, string name)
    {
        foreach (PlcBlock block in EnumerateBlocks(plc.BlockGroup))
        {
            if (string.Equals(block.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return block;
            }
        }

        foreach (PlcBlock block in EnumerateSystemBlocks(plc))
        {
            if (string.Equals(block.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return block;
            }
        }

        return null;
    }

    private static IEnumerable<PlcBlock> EnumerateSystemBlocks(PlcSoftware plc)
    {
        PlcBlockSystemGroup root = plc.BlockGroup as PlcBlockSystemGroup;
        if (root == null)
        {
            yield break;
        }

        foreach (PlcSystemBlockGroup group in root.SystemBlockGroups)
        {
            foreach (PlcBlock block in EnumerateSystemBlockGroup(group))
            {
                yield return block;
            }
        }
    }

    private static IEnumerable<PlcBlock> EnumerateSystemBlockGroup(PlcSystemBlockGroup group)
    {
        foreach (PlcBlock block in group.Blocks)
        {
            yield return block;
        }

        foreach (PlcSystemBlockGroup child in group.Groups)
        {
            foreach (PlcBlock nested in EnumerateSystemBlockGroup(child))
            {
                yield return nested;
            }
        }
    }

    private static PlcTagTable FindTable(PlcSoftware plc, string name)
    {
        return EnumerateTagTables(plc.TagTableGroup)
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static PlcTag FindTag(PlcSoftware plc, string name)
    {
        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            foreach (PlcTag tag in table.Tags)
            {
                if (string.Equals(tag.Name, name, StringComparison.Ordinal))
                {
                    return tag;
                }
            }
        }

        return null;
    }

    private static IEnumerable<PlcBlock> EnumerateBlocks(PlcBlockGroup group)
    {
        foreach (PlcBlock block in group.Blocks)
        {
            yield return block;
        }

        foreach (PlcBlockUserGroup child in group.Groups)
        {
            foreach (PlcBlock nested in EnumerateBlocks(child))
            {
                yield return nested;
            }
        }
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

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }

    // Main OB 第一網只叫 6B/12B/20B ZigBee。24B 的 Modbus DB 在，但沒被掃描。
    // 系統 Instance DB 用 CreateInstanceDB（Modbus_Comm_Load / Modbus_Master）。
    // 這輪不改 Event_Control：watch_dog 陣列索引匯出常變成 0，Override 會寫壞 12B/20B。
    public static int Add24BZigbee(TiaPortal portal, Project project)
    {
        Console.WriteLine("Main 補 24B ZigBee 掃描。Inside 不接 PN。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        if (FindBlock(main, "Modbus_Zigbee_24B") == null ||
            FindBlock(main, "Modbus_Comm_Zigbee_24B_DB") == null)
        {
            Console.WriteLine("找不到 Modbus_Zigbee_24B / Modbus_Comm_Zigbee_24B_DB。");
            return 1;
        }

        EnsureModbusInstanceDb(project, main, "MB_COMM_LOAD_24B_DB", "Modbus_Comm_Load");
        EnsureModbusInstanceDb(project, main, "MB_MASTER_24B_DB", "Modbus_Master");
        project.Save();
        Console.WriteLine("已存（24B Modbus 實例 DB）。");

        PlcBlock ob = FindBlock(main, "Main");
        if (ob == null)
        {
            Console.WriteLine("找不到 Main。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "station-sync", "live-24b");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Main-zigbee24.xml");
        if (!ExportBlockXmlQuiet(ob, path))
        {
            return 1;
        }

        XDocument doc = XDocument.Load(path);
        int added = PatchMainZigbee24B(doc.Root);
        Console.WriteLine("  Main ZigBee 24B 呼叫：" + added);
        if (added < 0)
        {
            return 1;
        }

        if (added > 0)
        {
            doc.Save(path);
            TryImportBlockFile(main, ob, path);
        }

        project.Save();
        Console.WriteLine("已存（匯入 Main）。");
        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return errors == 0 ? 0 : 1;
    }

    // Event_Control 通訊 watchdog 讀 Modbus_Zigbee_*.Read.watch_dog。
    // 24B 的 DB／Tag 都在，只差沒呼叫 WatchDog。實例用 watch_dog[3]（6B/12B/20B 匯出是 [0]，TF 是 [6]）。
    public static int Add24BInsideWatchdog(TiaPortal portal, Project project)
    {
        Console.WriteLine("Event_Control 補 24B Inside 通訊 watchdog。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        if (FindTag(main, "AErr_24B_Inside_PLC_Comm_Error") == null)
        {
            Console.WriteLine("找不到 AErr_24B_Inside_PLC_Comm_Error。");
            return 1;
        }

        PlcBlock block = FindBlock(main, "Event_Control");
        if (block == null)
        {
            Console.WriteLine("找不到 Event_Control。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "station-sync", "live-24b");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Event_Control-watchdog24.xml");
        if (!ExportBlockXmlQuiet(block, path))
        {
            return 1;
        }

        XDocument doc = XDocument.Load(path);
        int added = PatchEventControlWatchdog24B(doc.Root);
        Console.WriteLine("  Event_Control 24B watchdog：" + added);
        if (added < 0)
        {
            return 1;
        }

        if (added > 0)
        {
            doc.Save(path);
            TryImportBlockFile(main, block, path);
        }

        project.Save();
        Console.WriteLine("已存（匯入 Event_Control）。");
        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return errors == 0 ? 0 : 1;
    }

    private static int PatchEventControlWatchdog24B(XElement root)
    {
        XElement flg = FindFlgNetWithComponent(root, "AErr_20B_Inside_PLC_Comm_Error");
        if (flg == null)
        {
            Console.WriteLine("  找不到 Inside 通訊 watchdog 那一網");
            return -1;
        }

        if (HasNamedComponent(flg, "AErr_24B_Inside_PLC_Comm_Error"))
        {
            return 0;
        }

        XElement call20 = FindCallWiredToAccessNamed(flg, "AErr_20B_Inside_PLC_Comm_Error");
        if (call20 == null)
        {
            Console.WriteLine("  找不到 20B WatchDog 呼叫");
            return -1;
        }

        XNamespace ns = flg.Name.Namespace;
        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        if (parts == null || wires == null)
        {
            return -1;
        }

        string call20Uid = (string)call20.Attribute("UId");
        List<XElement> srcAccess = new List<XElement>();
        foreach (XElement wire in wires.Elements().Where(e => e.Name.LocalName == "Wire"))
        {
            bool toCall = wire.Elements().Any(e =>
                e.Name.LocalName == "NameCon" &&
                (string)e.Attribute("UId") == call20Uid);
            if (!toCall)
            {
                continue;
            }

            foreach (XElement ident in wire.Elements().Where(e => e.Name.LocalName == "IdentCon"))
            {
                string accessUid = (string)ident.Attribute("UId");
                XElement access = parts.Elements().FirstOrDefault(e =>
                    e.Name.LocalName == "Access" &&
                    (string)e.Attribute("UId") == accessUid);
                if (access != null && !srcAccess.Contains(access))
                {
                    srcAccess.Add(access);
                }
            }
        }

        int next = MaxUid(flg) + 1;
        Dictionary<string, string> uidMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement access in srcAccess)
        {
            XElement clone = new XElement(access);
            string oldUid = (string)access.Attribute("UId");
            string newUid = next.ToString();
            next++;
            clone.SetAttributeValue("UId", newUid);
            uidMap[oldUid] = newUid;
            Replace20BWith24B(clone);
            parts.Add(clone);
        }

        XElement newCall = new XElement(call20);
        string newCallUid = next.ToString();
        next++;
        newCall.SetAttributeValue("UId", newCallUid);
        foreach (XElement inst in newCall.Descendants().Where(e => e.Name.LocalName == "Instance"))
        {
            inst.SetAttributeValue("UId", next.ToString());
            next++;
            foreach (XElement value in inst.Descendants().Where(e => e.Name.LocalName == "ConstantValue"))
            {
                value.Value = "3";
            }
        }

        Replace20BWith24B(newCall);
        parts.Add(newCall);

        XElement power = wires.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Wire" &&
            e.Elements().Any(c => c.Name.LocalName == "Powerrail"));
        if (power != null)
        {
            power.Add(new XElement(ns + "NameCon",
                new XAttribute("UId", newCallUid),
                new XAttribute("Name", "en")));
        }

        foreach (XElement wire in wires.Elements().Where(e => e.Name.LocalName == "Wire").ToList())
        {
            bool toCall = wire.Elements().Any(e =>
                e.Name.LocalName == "NameCon" &&
                (string)e.Attribute("UId") == call20Uid);
            if (!toCall || wire.Elements().Any(e => e.Name.LocalName == "Powerrail"))
            {
                continue;
            }

            XElement clone = new XElement(wire);
            clone.SetAttributeValue("UId", next.ToString());
            next++;
            foreach (XElement con in clone.Elements())
            {
                string uid = (string)con.Attribute("UId");
                if (con.Name.LocalName == "NameCon" && uid == call20Uid)
                {
                    con.SetAttributeValue("UId", newCallUid);
                }
                else if (con.Name.LocalName == "IdentCon" && uid != null && uidMap.ContainsKey(uid))
                {
                    con.SetAttributeValue("UId", uidMap[uid]);
                }
            }

            wires.Add(clone);
        }

        return 1;
    }

    private static XElement FindCallWiredToAccessNamed(XElement flg, string componentName)
    {
        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        if (parts == null || wires == null)
        {
            return null;
        }

        XElement access = parts.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Access" && HasNamedComponent(e, componentName));
        if (access == null)
        {
            return null;
        }

        string accessUid = (string)access.Attribute("UId");
        foreach (XElement wire in wires.Elements().Where(e => e.Name.LocalName == "Wire"))
        {
            bool fromAccess = wire.Elements().Any(e =>
                e.Name.LocalName == "IdentCon" &&
                (string)e.Attribute("UId") == accessUid);
            if (!fromAccess)
            {
                continue;
            }

            XElement nameCon = wire.Elements().FirstOrDefault(e => e.Name.LocalName == "NameCon");
            if (nameCon == null)
            {
                continue;
            }

            string callUid = (string)nameCon.Attribute("UId");
            return parts.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "Call" &&
                (string)e.Attribute("UId") == callUid);
        }

        return null;
    }

    private static void EnsureModbusInstanceDb(
        Project project,
        PlcSoftware plc,
        string dbName,
        string fbName)
    {
        PlcBlock existing = FindBlock(plc, dbName);
        if (existing != null)
        {
            Console.WriteLine("  已有 " + dbName + "（DB" + existing.Number + "）");
            return;
        }

        int dbNumber = NextFreeUserOrSystemDbNumber(plc);
        try
        {
            plc.BlockGroup.Blocks.CreateInstanceDB(dbName, false, dbNumber, fbName);
            Console.WriteLine("  建立 " + dbName + " <- " + fbName + "（DB" + dbNumber + "）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  無法建立 " + dbName + "：" + Flatten(ex));
        }
    }

    private static int NextFreeUserOrSystemDbNumber(PlcSoftware plc)
    {
        HashSet<int> used = new HashSet<int>();
        foreach (PlcBlock block in EnumerateBlocks(plc.BlockGroup))
        {
            used.Add(block.Number);
        }

        foreach (PlcBlock block in EnumerateSystemBlocks(plc))
        {
            used.Add(block.Number);
        }

        for (int n = 30; n < 2000; n++)
        {
            if (!used.Contains(n))
            {
                return n;
            }
        }

        throw new InvalidOperationException("沒有空的 DB 編號");
    }

    private static int PatchMainZigbee24B(XElement root)
    {
        XElement flg = FindFlgNetWithComponent(root, "Modbus_Comm_Zigbee_20B_DB");
        if (flg == null)
        {
            Console.WriteLine("  找不到 20B ZigBee 那一網");
            return -1;
        }

        if (HasNamedComponent(flg, "Modbus_Comm_Zigbee_24B_DB"))
        {
            return 0;
        }

        XElement call20 = FindCallWithInstance(flg, "Modbus_Comm_Zigbee_20B_DB");
        if (call20 == null)
        {
            Console.WriteLine("  找不到 Modbus_Comm_Zigbee_20B_DB 呼叫");
            return -1;
        }

        XNamespace ns = flg.Name.Namespace;
        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        if (parts == null || wires == null)
        {
            return -1;
        }

        string call20Uid = (string)call20.Attribute("UId");
        List<XElement> srcAccess = new List<XElement>();
        foreach (XElement wire in wires.Elements().Where(e => e.Name.LocalName == "Wire"))
        {
            bool toCall = wire.Elements().Any(e =>
                e.Name.LocalName == "NameCon" &&
                (string)e.Attribute("UId") == call20Uid);
            if (!toCall)
            {
                continue;
            }

            foreach (XElement ident in wire.Elements().Where(e => e.Name.LocalName == "IdentCon"))
            {
                string accessUid = (string)ident.Attribute("UId");
                XElement access = parts.Elements().FirstOrDefault(e =>
                    e.Name.LocalName == "Access" &&
                    (string)e.Attribute("UId") == accessUid);
                if (access != null && !srcAccess.Contains(access))
                {
                    srcAccess.Add(access);
                }
            }
        }

        if (srcAccess.Count == 0)
        {
            Console.WriteLine("  20B ZigBee 呼叫沒有接線");
            return -1;
        }

        int next = MaxUid(flg) + 1;
        Dictionary<string, string> uidMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement access in srcAccess)
        {
            XElement clone = new XElement(access);
            string oldUid = (string)access.Attribute("UId");
            string newUid = next.ToString();
            next++;
            clone.SetAttributeValue("UId", newUid);
            uidMap[oldUid] = newUid;
            Replace20BWith24B(clone);
            parts.Add(clone);
        }

        XElement newCall = new XElement(call20);
        string newCallUid = next.ToString();
        next++;
        newCall.SetAttributeValue("UId", newCallUid);
        foreach (XElement inst in newCall.Descendants().Where(e => e.Name.LocalName == "Instance"))
        {
            inst.SetAttributeValue("UId", next.ToString());
            next++;
        }

        Replace20BWith24B(newCall);
        parts.Add(newCall);

        XElement power = wires.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Wire" &&
            e.Elements().Any(c => c.Name.LocalName == "Powerrail"));
        if (power != null)
        {
            power.Add(new XElement(ns + "NameCon",
                new XAttribute("UId", newCallUid),
                new XAttribute("Name", "en")));
        }

        foreach (XElement wire in wires.Elements().Where(e => e.Name.LocalName == "Wire").ToList())
        {
            bool toCall = wire.Elements().Any(e =>
                e.Name.LocalName == "NameCon" &&
                (string)e.Attribute("UId") == call20Uid);
            if (!toCall || wire.Elements().Any(e => e.Name.LocalName == "Powerrail"))
            {
                continue;
            }

            XElement clone = new XElement(wire);
            clone.SetAttributeValue("UId", next.ToString());
            next++;
            foreach (XElement con in clone.Elements())
            {
                string uid = (string)con.Attribute("UId");
                if (con.Name.LocalName == "NameCon" && uid == call20Uid)
                {
                    con.SetAttributeValue("UId", newCallUid);
                }
                else if (con.Name.LocalName == "IdentCon" && uid != null && uidMap.ContainsKey(uid))
                {
                    con.SetAttributeValue("UId", uidMap[uid]);
                }
            }

            wires.Add(clone);
        }

        return 1;
    }

    private static XElement FindFlgNetWithComponent(XElement root, string name)
    {
        return root.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "FlgNet" && HasNamedComponent(e, name));
    }

    private static bool HasNamedComponent(XElement scope, string name)
    {
        return scope.Descendants().Any(e =>
            e.Name.LocalName == "Component" &&
            string.Equals((string)e.Attribute("Name"), name, StringComparison.Ordinal));
    }

    private static XElement FindCallWithInstance(XElement flg, string instanceName)
    {
        return flg.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Call" && HasNamedComponent(e, instanceName));
    }

    private static void Replace20BWith24B(XElement el)
    {
        foreach (XAttribute attr in el.DescendantsAndSelf().Attributes())
        {
            if (!string.IsNullOrEmpty(attr.Value) && attr.Value.Contains("20B"))
            {
                attr.Value = attr.Value.Replace("20B", "24B");
            }
        }

        foreach (XElement text in el.DescendantsAndSelf().Where(e => e.Name.LocalName == "ConstantValue"))
        {
            if (text.Value != null && text.Value.Contains("20B"))
            {
                text.Value = text.Value.Replace("20B", "24B");
            }
        }
    }

    public static int WireSection4(TiaPortal portal, Project project)
    {
        Console.WriteLine("Main_Logic 定位：section_no=4，接 I_Snr_*_Section4。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後再匯出）。");

        PlcBlock block = FindBlock(main, "Main_Logic");
        if (block == null)
        {
            Console.WriteLine("找不到 Main_Logic。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "station-sync", "section4");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Main_Logic.xml");
        if (!ExportBlockXmlQuiet(block, path))
        {
            return 1;
        }

        XDocument doc = XDocument.Load(path);
        int changed = PatchSection4(doc.Root);
        Console.WriteLine("  改了 " + changed + " 處。");
        if (changed == 0)
        {
            return 0;
        }

        doc.Save(path);
        TryImportBlockFile(main, block, path);
        project.Save();
        Console.WriteLine("已存（匯入 Main_Logic）。");
        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return errors == 0 ? 0 : 1;
    }

    // OB30 is a mixed SCL/LAD block.  Keep both languages in place:
    // SCL calculates the 1B line speed and LAD ORs the station tighten buttons.
    public static int Add24BToCyclic(TiaPortal portal, Project project)
    {
        Console.WriteLine("Cyclic interrupt：補 24B 緊線與 1B 線速壓縮比。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "station-sync", "cyclic-24b");
        Directory.CreateDirectory(dir);
        int compressionChanged = EnsureOptDieCompression24B(main, dir);
        if (compressionChanged < 0)
        {
            return 1;
        }

        PlcBlock block = FindBlock(main, "Cyclic interrupt");
        if (block == null)
        {
            Console.WriteLine("找不到 Cyclic interrupt（OB30）。");
            return 1;
        }

        if (!SetCyclicInterval(block))
        {
            return 1;
        }

        int preCompileErrors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後再匯出）。");
        if (preCompileErrors != 0)
        {
            return 1;
        }

        string sourcePath = Path.Combine(dir, "Cyclic interrupt-before.xml");
        string importPath = Path.Combine(dir, "Cyclic interrupt.xml");
        string verifyPath = Path.Combine(dir, "Cyclic interrupt-after.xml");
        if (!ExportBlockXmlQuiet(block, sourcePath))
        {
            return 1;
        }

        XDocument doc = XDocument.Load(sourcePath);
        int tightenChanged = PatchCyclicTighten24B(doc.Root);
        int speedChanged = PatchCyclicLineSpeed24B(doc.Root);
        Console.WriteLine("  24B 緊線：" + tightenChanged + "／1B 線速：" + speedChanged);
        if (tightenChanged < 0 || speedChanged < 0)
        {
            return 1;
        }

        if (tightenChanged == 0 && speedChanged == 0)
        {
            bool verifiedCurrent = VerifyCyclic24B(doc.Root);
            Console.WriteLine("  已是 24B 版本，未重匯；XML 驗證：" +
                (verifiedCurrent ? "通過" : "失敗"));
            return verifiedCurrent ? 0 : 1;
        }

        if (tightenChanged == 0 || speedChanged == 0 || !VerifyCyclic24B(doc.Root))
        {
            Console.WriteLine("  Cyclic XML 驗證不完整，未匯入。");
            return 1;
        }

        doc.Save(importPath);
        TryImportBlockFile(main, block, importPath);
        project.Save();
        Console.WriteLine("已存（匯入 Cyclic interrupt）。");

        PlcBlock importedBlock = FindBlock(main, "Cyclic interrupt");
        if (importedBlock == null || !SetCyclicInterval(importedBlock))
        {
            return 1;
        }

        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        if (errors != 0 || importedBlock == null || !ExportBlockXmlQuiet(importedBlock, verifyPath))
        {
            return 1;
        }

        bool verified = VerifyCyclic24B(XDocument.Load(verifyPath).Root);
        Console.WriteLine("  匯入後 XML 驗證：" + (verified ? "通過" : "失敗"));
        return verified ? 0 : 1;
    }

    // Valve 2 has no separate sequence yet. Follow valve 1 on the same scan:
    // clutch coils share the existing fork; pin lock copies the FB output tags.
    public static int MirrorPosValve2(TiaPortal portal, Project project)
    {
        Console.WriteLine("Main_Logic：定位銷／離合器閥2 跟隨閥1。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "station-sync", "pos-valve2");
        Directory.CreateDirectory(dir);

        int preCompileErrors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後再匯出）。");
        if (preCompileErrors != 0)
        {
            return 1;
        }

        PlcBlock block = FindBlock(main, "Main_Logic");
        if (block == null)
        {
            Console.WriteLine("找不到 Main_Logic。");
            return 1;
        }

        string sourcePath = Path.Combine(dir, "Main_Logic-before.xml");
        string importPath = Path.Combine(dir, "Main_Logic.xml");
        string verifyPath = Path.Combine(dir, "Main_Logic-after.xml");
        if (!ExportBlockXmlQuiet(block, sourcePath))
        {
            return 1;
        }

        XDocument doc = XDocument.Load(sourcePath);
        int changed = PatchPosValve2Follow(doc.Root);
        Console.WriteLine("  閥2跟隨網路：" + changed);
        if (changed < 0)
        {
            return 1;
        }

        if (changed == 0)
        {
            bool verifiedCurrent = VerifyPosValve2(doc.Root);
            Console.WriteLine("  已是閥2跟隨，未重匯；XML 驗證：" +
                (verifiedCurrent ? "通過" : "失敗"));
            return verifiedCurrent ? 0 : 1;
        }

        if (!VerifyPosValve2(doc.Root))
        {
            Console.WriteLine("  閥2 XML 驗證不完整，未匯入。");
            return 1;
        }

        doc.Save(importPath);
        TryImportBlockFile(main, block, importPath);
        project.Save();
        Console.WriteLine("已存（匯入 Main_Logic）。");

        PlcBlock importedBlock = FindBlock(main, "Main_Logic");
        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        if (errors != 0 || importedBlock == null || !ExportBlockXmlQuiet(importedBlock, verifyPath))
        {
            return 1;
        }

        bool verified = VerifyPosValve2(XDocument.Load(verifyPath).Root);
        Console.WriteLine("  匯入後 XML 驗證：" + (verified ? "通過" : "失敗"));
        return verified ? 0 : 1;
    }

    private static readonly string[] PreTwistStations = { "12B", "20B", "24B" };

    public static int WirePreTwist(TiaPortal portal, Project project)
    {
        Console.WriteLine("預扭成型：12B／20B／24B IO（沒有 6B）。台車不改。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "pretwist");
        Directory.CreateDirectory(dir);

        int preCompileErrors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後再改）。");
        if (preCompileErrors != 0)
        {
            return 1;
        }

        if (!EnsurePreTwistEventTags(main))
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（Events Tag）。");

        string specPath = Path.Combine(dir, "PreTwist_IO.lad.xml");
        string generatedPath = Path.Combine(dir, "PreTwist_IO.generated.xml");
        if (!File.Exists(specPath))
        {
            Console.WriteLine("找不到 " + specPath);
            return 1;
        }

        File.WriteAllText(generatedPath, LadWriter.Generate(specPath), new UTF8Encoding(false));
        TryImportBlockFile(main, FindBlock(main, "PreTwist_IO"), generatedPath);
        if (FindBlock(main, "PreTwist_IO") == null)
        {
            Console.WriteLine("匯入 PreTwist_IO 之後找不到區塊。");
            return 1;
        }

        if (CompilePlc(main) != 0)
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（匯入 PreTwist_IO）。");

        int mainChanged = PatchNamedBlock(
            main,
            dir,
            "Main",
            PatchMainPreTwistCall,
            root => root.Descendants().Any(e =>
                e.Name.LocalName == "CallInfo" &&
                (string)e.Attribute("Name") == "PreTwist_IO"));
        int logicChanged = PatchNamedBlock(
            main,
            dir,
            "Main_Logic",
            PatchMainLogicPreTwistJog,
            root => root.Descendants().Any(e =>
                e.Name.LocalName == "Component" &&
                (string)e.Attribute("Name") == "I_PB_12B_PreTwist_Jog") &&
            root.Descendants().Any(e =>
                e.Name.LocalName == "Component" &&
                (string)e.Attribute("Name") == "I_PB_20B_PreTwist_Jog") &&
            root.Descendants().Any(e =>
                e.Name.LocalName == "Component" &&
                (string)e.Attribute("Name") == "I_PB_24B_PreTwist_Jog"));
        int eventChanged = PatchNamedBlock(
            main,
            dir,
            "Event_Control",
            PatchEventControlPreTwist,
            VerifyEventControlPreTwist);
        Console.WriteLine("  Main：" + mainChanged +
            "／Main_Logic：" + logicChanged +
            "／Event_Control：" + eventChanged);
        if (mainChanged < 0 || logicChanged < 0 || eventChanged < 0)
        {
            return 1;
        }

        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return errors == 0 ? 0 : 1;
    }

    public static int Unwire6BPreTwist(TiaPortal portal, Project project)
    {
        Console.WriteLine("拆 6B 預扭（圖／22021 沒有）。回復 12B／20B／24B。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "pretwist");

        TechnologyBuilder.DeleteTag(main, "AErr_EStop_PB_Act_6B_PreTwist");
        TechnologyBuilder.DeleteTag(main, "EErr_6B_PreTwist_Drv_Fault");
        project.Save();
        Console.WriteLine("已存（刪 6B Event Tag）。");

        if (!ImportGeneratedBlock(main, dir, "PreTwist_IO"))
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（PreTwist_IO 無 6B）。");

        PlcBlock logic = FindBlock(main, "Main_Logic");
        if (logic != null)
        {
            string livePath = Path.Combine(dir, "Main_Logic-live.xml");
            if (ExportBlockXmlQuiet(logic, livePath))
            {
                bool hasJog = XDocument.Load(livePath).Root.Descendants().Any(e =>
                    e.Name.LocalName == "Component" &&
                    (string)e.Attribute("Name") == "I_PB_6B_PreTwist_Jog");
                string beforePath = Path.Combine(dir, "Main_Logic-before.xml");
                if (hasJog && File.Exists(beforePath))
                {
                    bool beforeHas6 = XDocument.Load(beforePath).Root.Descendants().Any(e =>
                        e.Name.LocalName == "Component" &&
                        (string)e.Attribute("Name") == "I_PB_6B_PreTwist_Jog");
                    if (beforeHas6)
                    {
                        Console.WriteLine("Main_Logic-before 仍有 6B 寸動，不回匯。");
                    }
                    else
                    {
                        TryImportBlockFile(main, logic, beforePath);
                        Console.WriteLine("已回匯 Main_Logic（去掉 6B 寸動）。");
                    }
                }
                else
                {
                    Console.WriteLine("Main_Logic 沒有 6B 寸動，不動。");
                }
            }
        }

        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（拆 6B 之後）。");
        return errors == 0 ? 0 : 1;
    }

    public static int WirePreTwistSoftGear(TiaPortal portal, Project project)
    {
        Console.WriteLine("預扭 SoftGear：12B／20B／24B，RS1＝CB-1241，站號 8／9／10。沒有 6B。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "pretwist");
        Directory.CreateDirectory(dir);

        int preCompileErrors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後再改）。");
        if (preCompileErrors != 0)
        {
            return 1;
        }

        if (FindBlock(main, "Delta_SoftGear_PreTwist") == null ||
            FindBlock(main, "Modbus_Delta_Drive_N1_M") == null)
        {
            Console.WriteLine("找不到 Delta_SoftGear_PreTwist 或 Modbus_Delta_Drive_N1_M。");
            return 1;
        }

        string port = FindCb1241MainPort(main);
        Console.WriteLine("CB-1241 PORT：" + port);

        if (!ImportGeneratedBlock(main, dir, "PreTwist_SoftGear"))
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（匯入 PreTwist_SoftGear）。");

        EnsureModbusInstanceDb(project, main, "PreTwist_SoftGear_DB", "PreTwist_SoftGear");
        if (FindBlock(main, "PreTwist_SoftGear_DB") == null)
        {
            Console.WriteLine("找不到 PreTwist_SoftGear_DB。");
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（Instance DB）。");

        int mainChanged = PatchNamedBlock(
            main,
            dir,
            "Main",
            root => PatchMainPreTwistSoftGearCall(root, dir, port),
            root => root.Descendants().Any(e =>
                e.Name.LocalName == "CallInfo" &&
                (string)e.Attribute("Name") == "PreTwist_SoftGear"));
        Console.WriteLine("  Main：" + mainChanged);
        if (mainChanged < 0)
        {
            return 1;
        }

        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return errors == 0 ? 0 : 1;
    }

    private static string FindCb1241MainPort(PlcSoftware plc)
    {
        List<string> matches = new List<string>();
        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            foreach (PlcSystemConstant constant in table.SystemConstants)
            {
                string name = constant.Name ?? string.Empty;
                string dataType = constant.DataTypeName ?? string.Empty;
                if (dataType.IndexOf("PORT", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (name.IndexOf("CB_1241", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("CB 1241", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (name.IndexOf("Zigbee", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Trv", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("CM_1241", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                matches.Add(name);
            }
        }

        string preferred = matches.FirstOrDefault(name =>
            name.IndexOf("Main", StringComparison.OrdinalIgnoreCase) >= 0);
        if (preferred != null)
        {
            return preferred;
        }

        if (matches.Count > 0)
        {
            return matches[0];
        }

        return "Local~CB_1241_Main";
    }

    private static int PatchMainPreTwistSoftGearCall(XElement root, string dir, string port)
    {
        if (root.Descendants().Any(e =>
            e.Name.LocalName == "CallInfo" &&
            (string)e.Attribute("Name") == "PreTwist_SoftGear"))
        {
            return 0;
        }

        string specPath = Path.Combine(dir, "Call-PreTwist_SoftGear.lad.xml");
        if (!File.Exists(specPath))
        {
            Console.WriteLine("找不到 " + specPath);
            return -1;
        }

        string specText = File.ReadAllText(specPath).Replace("__PORT__", port);
        string tempSpec = Path.Combine(dir, "Call-PreTwist_SoftGear.port.lad.xml");
        File.WriteAllText(tempSpec, specText, new UTF8Encoding(false));
        XDocument generated = XDocument.Parse(LadWriter.Generate(tempSpec));
        XElement unit = generated.Root.Descendants().FirstOrDefault(IsCompileUnit);
        if (unit == null)
        {
            Console.WriteLine("Call-PreTwist_SoftGear 沒有 CompileUnit。");
            return -1;
        }

        XElement after = null;
        foreach (XElement compileUnit in root.Descendants().Where(IsCompileUnit))
        {
            if (compileUnit.Descendants().Any(e =>
                e.Name.LocalName == "CallInfo" &&
                (string)e.Attribute("Name") == "PreTwist_IO"))
            {
                after = compileUnit;
                break;
            }
        }

        if (after == null)
        {
            foreach (XElement compileUnit in root.Descendants().Where(IsCompileUnit))
            {
                if (compileUnit.Descendants().Any(e =>
                    e.Name.LocalName == "CallInfo" &&
                    (string)e.Attribute("Name") == "Main_Logic"))
                {
                    after = compileUnit;
                    break;
                }
            }
        }

        if (after == null)
        {
            Console.WriteLine("Main 找不到 PreTwist_IO／Main_Logic 呼叫。");
            return -1;
        }

        int nextId = MaxHexId(root) + 16;
        ShiftIds(unit, nextId - FirstHexId(unit));
        after.AddAfterSelf(unit);
        return 1;
    }

    private static readonly string[] CExtraAerrNames =
    {
        "AErr_6B_Line_EStop_Loop",
        "AErr_12B_Line_EStop_Loop",
        "AErr_20B_Line_EStop_Loop",
        "AErr_24B_Line_EStop_Loop"
    };

    private static readonly string[] CExtraEerrNames =
    {
        "EErr_Payoff_Fault",
        "EErr_Takeup_Fault",
        "EErr_Takeup_Power_Mon"
    };

    public static int WireCExtraEvents(TiaPortal portal, Project project)
    {
        Console.WriteLine("C 表剩餘點：只接 Event_Control 警報。不驅動 Q_Payoff / Q_Takeup / Q_UPS。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "c-extra");
        Directory.CreateDirectory(dir);

        int preCompileErrors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後再改）。");
        if (preCompileErrors != 0)
        {
            return 1;
        }

        if (!EnsureCExtraEventTags(main))
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（Events Tag）。");
        DumpCExtraAndPretwistAddresses(main);

        int eventChanged = PatchNamedBlock(
            main,
            dir,
            "Event_Control",
            PatchEventControlCExtra,
            VerifyEventControlCExtra);
        Console.WriteLine("  Event_Control：" + eventChanged);
        if (eventChanged < 0)
        {
            return 1;
        }

        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return errors == 0 ? 0 : 1;
    }

    private static readonly string[] PayoffTakeupNerrNames =
    {
        "NErr_Payoff_Not_Ran",
        "NErr_Takeup_No_Run"
    };

    public static int WirePayoffTakeupRun(TiaPortal portal, Project project)
    {
        Console.WriteLine("N STOP：is.run 卻沒 I_S_Payoff_Run／I_S_Takeup_Run。不改 NErr_Takeup_Not_Ran（TF）。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "payoff-takeup");
        Directory.CreateDirectory(dir);

        int preCompileErrors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後再改）。");
        if (preCompileErrors != 0)
        {
            return 1;
        }

        PlcTagTable events = FindTable(main, "Events");
        if (events == null)
        {
            Console.WriteLine("找不到 Events Tag 表。");
            return 1;
        }

        HashSet<int> used = CollectAllMemoryBits(main);
        if (!CreateEventTagsInRange(events, PayoffTakeupNerrNames, used, 280 * 8, 287 * 8 + 7))
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（NErr Tag）。");
        foreach (string name in PayoffTakeupNerrNames)
        {
            PlcTag tag = FindTag(main, name);
            if (tag == null)
            {
                Console.WriteLine("  址 缺 " + name);
                continue;
            }

            int bit = ParseSimpleMemoryBit(tag.LogicalAddress);
            if (bit < 0)
            {
                Console.WriteLine("  址 " + name + " " + tag.LogicalAddress);
                continue;
            }

            int lw = (bit / 8 - 280) / 2;
            int iecBit = ((bit / 8 - 280) % 2) * 8 + (bit % 8);
            // WinCC Basic Discrete: even-byte Mxxx.0 is Trigger bit 8, not IEC 0.
            int hmiBit = iecBit ^ 8;
            Console.WriteLine("  址 " + name + " " + tag.LogicalAddress +
                " → NErr_LW{" + lw + "}." + hmiBit + " (HMI .8=M even.0)");
        }

        int eventChanged = PatchNamedBlock(
            main,
            dir,
            "Event_Control",
            PatchEventControlPayoffTakeup,
            VerifyEventControlPayoffTakeup);
        Console.WriteLine("  Event_Control：" + eventChanged);
        if (eventChanged < 0)
        {
            return 1;
        }

        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return errors == 0 ? 0 : 1;
    }

    private static int PatchEventControlPayoffTakeup(XElement root)
    {
        string specPath = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "payoff-takeup", "Event-PayoffTakeupRun.lad.xml");
        if (!File.Exists(specPath))
        {
            Console.WriteLine("找不到 " + specPath);
            return -1;
        }

        XDocument generated = XDocument.Parse(LadWriter.Generate(specPath));
        List<XElement> units = generated.Root.Descendants().Where(IsCompileUnit).ToList();
        if (units.Count == 0)
        {
            Console.WriteLine("Event-PayoffTakeupRun 沒有 CompileUnit。");
            return -1;
        }

        XElement after = root.Descendants().LastOrDefault(IsCompileUnit);
        if (after == null)
        {
            Console.WriteLine("Event_Control 沒有 CompileUnit。");
            return -1;
        }

        int nextId = MaxHexId(root) + 16;
        int added = 0;
        foreach (XElement source in units)
        {
            string coil = source.Descendants()
                .Where(e => e.Name.LocalName == "Component")
                .Select(e => (string)e.Attribute("Name"))
                .FirstOrDefault(name =>
                    name != null && name.StartsWith("NErr_", StringComparison.Ordinal));
            if (coil != null &&
                root.Descendants().Any(e =>
                    e.Name.LocalName == "Component" &&
                    (string)e.Attribute("Name") == coil))
            {
                continue;
            }

            XElement unit = new XElement(source);
            int first = FirstHexId(unit);
            ShiftIds(unit, nextId - first);
            nextId = MaxHexId(unit) + 16;
            after.AddAfterSelf(unit);
            after = unit;
            added++;
        }

        return added;
    }

    private static bool VerifyEventControlPayoffTakeup(XElement root)
    {
        foreach (string name in PayoffTakeupNerrNames)
        {
            if (!HasCoilNamed(root, name))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly string[] HomeAngleStations = { "6B", "12B", "20B", "24B" };

    public static int WireHomeAngle(TiaPortal portal, Project project)
    {
        Console.WriteLine("歸零近接：6B／12B／20B／24B 上升沿清 Enc_rotor HSC，再算角度。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "home-angle");
        Directory.CreateDirectory(dir);

        int preCompileErrors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後再改）。");
        if (preCompileErrors != 0)
        {
            return 1;
        }

        PlcTagTable hardware = FindTable(main, "Hardware");
        if (hardware == null)
        {
            Console.WriteLine("找不到 Hardware Tag 表。");
            return 1;
        }

        EnsureTag(hardware, "Enc_rotor_6B", "DInt", "%ID1020");
        EnsureTag(hardware, "Enc_rotor_12B", "DInt", "%ID1008");
        EnsureTag(hardware, "Enc_rotor_20B", "DInt", "%ID1012");
        EnsureTag(hardware, "Enc_rotor_24B", "DInt", "%ID1016");
        project.Save();
        Console.WriteLine("已存（Enc_rotor Tag）。");

        if (!ImportGeneratedBlock(main, dir, "HSC_Zero"))
        {
            return 1;
        }

        foreach (string station in HomeAngleStations)
        {
            EnsureModbusInstanceDb(project, main, "HSC_Zero_" + station + "_DB", "HSC_Zero");
        }

        if (!CloneInstanceDb(main, dir, "Rotor_Angle_12B_DB", "Rotor_Angle_6B_DB") ||
            !CloneInstanceDb(main, dir, "Rotor_Angle_12B_DB", "Rotor_Angle_20B_DB") ||
            !CloneInstanceDb(main, dir, "Rotor_Angle_12B_DB", "Rotor_Angle_24B_DB"))
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（HSC_Zero／Rotor_Angle DB）。");

        if (!ImportGeneratedBlock(main, dir, "Home_Angle_Zero"))
        {
            return 1;
        }

        EnsureModbusInstanceDb(project, main, "Home_Angle_Zero_DB", "Home_Angle_Zero");
        project.Save();
        Console.WriteLine("已存（Home_Angle_Zero）。");

        int mainChanged = PatchNamedBlock(
            main,
            dir,
            "Main",
            PatchMainHomeAngleCall,
            root => root.Descendants().Any(e =>
                e.Name.LocalName == "CallInfo" &&
                (string)e.Attribute("Name") == "Home_Angle_Zero"));
        Console.WriteLine("  Main：" + mainChanged);
        if (mainChanged < 0)
        {
            return 1;
        }

        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return errors == 0 ? 0 : 1;
    }

    public static int WireHomeHwInterrupt(TiaPortal portal, Project project)
    {
        Console.WriteLine("歸零改硬體中斷：Main I1.0–I1.3 → Angle_*_interrupt → HSC_Zero。角度仍走 Home_Angle_Zero。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "home-angle");
        Directory.CreateDirectory(dir);

        if (FindBlock(main, "HSC_Zero") == null)
        {
            Console.WriteLine("先做 --wire-home-angle（HSC_Zero 還沒有）。");
            return 1;
        }

        foreach (string station in HomeAngleStations)
        {
            if (FindBlock(main, "HSC_Zero_" + station + "_DB") == null)
            {
                Console.WriteLine("先做 --wire-home-angle 或 --wire-home-6b（缺 HSC_Zero_" + station + "_DB）。");
                return 1;
            }
        }

        foreach (string station in HomeAngleStations)
        {
            string name = "Angle_" + station + "_interrupt";
            if (!ImportGeneratedBlock(main, dir, name))
            {
                return 1;
            }

            project.Save();
            Console.WriteLine("已存（" + name + "）。");
        }

        if (!ImportGeneratedBlock(main, dir, "Home_Angle_Zero"))
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（Home_Angle_Zero 改成只算角度）。");

        bool hwOk = HardwareSync.TryEnableHomeRisingInterrupts(project);
        if (hwOk)
        {
            project.Save();
            Console.WriteLine("已存（DI 上升沿硬體中斷）。");
        }
        else
        {
            Console.WriteLine("Openness 設不到板載 DI 硬體中斷，接著用 GUI 開 I1.0–I1.3 上升沿並綁 OB。");
        }

        List<string> verifyNames = new List<string>();
        foreach (string station in HomeAngleStations)
        {
            verifyNames.Add("Angle_" + station + "_interrupt");
        }

        verifyNames.Add("Home_Angle_Zero");
        foreach (string name in verifyNames)
        {
            PlcBlock block = FindBlock(main, name);
            if (block == null || !ExportBlockXmlQuiet(block, Path.Combine(dir, name + "-after.xml")))
            {
                Console.WriteLine("匯出驗證失敗：" + name);
                return 1;
            }
        }

        if (!VerifyHomeHwInterruptXml(dir))
        {
            return 1;
        }

        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        if (errors != 0)
        {
            return 1;
        }

        return hwOk ? 0 : 2;
    }

    private static bool VerifyHomeHwInterruptXml(string dir)
    {
        foreach (string station in HomeAngleStations)
        {
            string path = Path.Combine(dir, "Angle_" + station + "_interrupt-after.xml");
            XDocument doc = XDocument.Load(path);
            bool hwOb = doc.Root.Descendants().Any(e =>
                e.Name.LocalName == "SecondaryType" && e.Value == "HardwareInterrupt");
            bool zero = doc.Root.Descendants().Any(e =>
                e.Name.LocalName == "CallInfo" &&
                (string)e.Attribute("Name") == "HSC_Zero");
            bool port = doc.Root.Descendants().Any(e =>
                e.Name.LocalName == "Constant" &&
                ((string)e.Attribute("Name") ?? "").IndexOf(
                    "Enc_rotor_" + station, StringComparison.OrdinalIgnoreCase) >= 0);
            Console.WriteLine("  驗證 Angle_" + station + "_interrupt：HW=" + hwOb +
                " HSC_Zero=" + zero + " port=" + port);
            if (!hwOb || !zero || !port)
            {
                return false;
            }
        }

        XDocument home = XDocument.Load(Path.Combine(dir, "Home_Angle_Zero-after.xml"));
        bool stillZero = home.Root.Descendants().Any(e =>
            e.Name.LocalName == "CallInfo" &&
            (string)e.Attribute("Name") == "HSC_Zero");
        bool stillEdge = home.Root.Descendants().Any(e =>
            (string)e.Attribute("Name") == "PContact");
        int angles = home.Root.Descendants().Count(e =>
            e.Name.LocalName == "CallInfo" &&
            (string)e.Attribute("Name") == "Rotor_Angle");
        Console.WriteLine("  驗證 Home_Angle_Zero：HSC_Zero=" + stillZero +
            " PContact=" + stillEdge + " Rotor_Angle=" + angles);
        return !stillZero && !stillEdge && angles == HomeAngleStations.Length;
    }

    public static int WireHome6B(TiaPortal portal, Project project)
    {
        Console.WriteLine("補 6B 角度：Main 剩的 HSC_6 → Enc_rotor_6B，I1.0 硬體中斷清零。");
        Console.WriteLine("脈波：C 表 Main／RIO／TF／PF 都沒有 6B 轉體編碼器。Inside I0.0 是籠內馬達編碼器，不是轉體角度。");
        Console.WriteLine("22021 的 6B 脈波在獨立 Rotor_Angle_PLC。26037 跟 12B／20B／24B 一樣做在 Main。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "home-angle");
        Directory.CreateDirectory(dir);

        HardwareSync.DumpMainHsc(project);
        if (!HardwareSync.RenameMainHsc6(project))
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（HSC_6 → Enc_rotor_6B）。");

        PlcTagTable hardware = FindTable(main, "Hardware");
        if (hardware == null)
        {
            Console.WriteLine("找不到 Hardware Tag 表。");
            return 1;
        }

        EnsureTag(hardware, "Enc_rotor_6B", "DInt", "%ID1020");
        project.Save();
        Console.WriteLine("已存（Enc_rotor_6B Tag）。");

        if (FindBlock(main, "HSC_Zero") == null)
        {
            Console.WriteLine("找不到 HSC_Zero，先做 --wire-home-angle。");
            return 1;
        }

        EnsureModbusInstanceDb(project, main, "HSC_Zero_6B_DB", "HSC_Zero");
        if (!CloneInstanceDb(main, dir, "Rotor_Angle_12B_DB", "Rotor_Angle_6B_DB"))
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（6B 實例 DB）。");

        if (!ImportGeneratedBlock(main, dir, "Angle_6B_interrupt"))
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（Angle_6B_interrupt）。");

        if (!ImportGeneratedBlock(main, dir, "Home_Angle_Zero"))
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（Home_Angle_Zero 加 6B）。");

        bool hwOk = HardwareSync.TryEnableHomeRisingInterrupt(project, 8, "6B I1.0");
        if (hwOk)
        {
            project.Save();
            Console.WriteLine("已存（I1.0 上升沿硬體中斷）。");
        }
        else
        {
            Console.WriteLine("Openness 設不到 I1.0 硬體中斷，接著用 GUI 開 Channel 8 上升沿並綁 Angle_6B_interrupt。");
        }

        if (!ExportBlockXmlQuiet(
                FindBlock(main, "Angle_6B_interrupt"),
                Path.Combine(dir, "Angle_6B_interrupt-after.xml")) ||
            !ExportBlockXmlQuiet(
                FindBlock(main, "Home_Angle_Zero"),
                Path.Combine(dir, "Home_Angle_Zero-after.xml")))
        {
            Console.WriteLine("匯出驗證失敗。");
            return 1;
        }

        if (!VerifyHomeHwInterruptXml(dir))
        {
            return 1;
        }

        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        if (errors != 0)
        {
            return 1;
        }

        return hwOk ? 0 : 2;
    }

    private static bool ImportGeneratedBlock(PlcSoftware plc, string dir, string name)
    {
        string specPath = Path.Combine(dir, name + ".lad.xml");
        string generatedPath = Path.Combine(dir, name + ".generated.xml");
        if (!File.Exists(specPath))
        {
            Console.WriteLine("找不到 " + specPath);
            return false;
        }

        File.WriteAllText(generatedPath, LadWriter.Generate(specPath), new UTF8Encoding(false));
        TryImportBlockFile(plc, FindBlock(plc, name), generatedPath);
        if (FindBlock(plc, name) == null)
        {
            Console.WriteLine("匯入 " + name + " 之後找不到區塊。");
            return false;
        }

        return CompilePlc(plc) == 0;
    }

    private static bool CloneInstanceDb(
        PlcSoftware plc,
        string dir,
        string sourceName,
        string destName)
    {
        if (FindBlock(plc, destName) != null)
        {
            Console.WriteLine("  已有 " + destName);
            return true;
        }

        PlcBlock source = FindBlock(plc, sourceName);
        if (source == null)
        {
            Console.WriteLine("找不到 " + sourceName + "，無法複製成 " + destName + "。");
            return false;
        }

        string sourcePath = Path.Combine(dir, sourceName + ".xml");
        if (!ExportBlockXmlQuiet(source, sourcePath))
        {
            return false;
        }

        XDocument doc = XDocument.Load(sourcePath);
        XElement nameNode = doc.Root.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Name" &&
                e.Parent != null &&
                e.Parent.Name.LocalName == "AttributeList" &&
                e.Parent.Elements().Any(c => c.Name.LocalName == "InstanceOfName"));
        XElement numberNode = nameNode == null
            ? null
            : nameNode.Parent.Elements().FirstOrDefault(e => e.Name.LocalName == "Number");
        if (nameNode == null || numberNode == null)
        {
            Console.WriteLine("  " + sourceName + " XML 沒有 Name/Number。");
            return false;
        }

        nameNode.Value = destName;
        numberNode.Value = NextFreeUserOrSystemDbNumber(plc).ToString();
        string destPath = Path.Combine(dir, destName + ".xml");
        doc.Save(destPath);
        TryImportBlockFile(plc, null, destPath);
        if (FindBlock(plc, destName) == null)
        {
            Console.WriteLine("  複製 " + destName + " 失敗。");
            return false;
        }

        Console.WriteLine("  複製 " + sourceName + " → " + destName);
        return true;
    }

    private static int PatchMainHomeAngleCall(XElement root)
    {
        if (root.Descendants().Any(e =>
            e.Name.LocalName == "CallInfo" &&
            (string)e.Attribute("Name") == "Home_Angle_Zero"))
        {
            return 0;
        }

        string specPath = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "home-angle", "Call-Home_Angle_Zero.lad.xml");
        if (!File.Exists(specPath))
        {
            Console.WriteLine("找不到 " + specPath);
            return -1;
        }

        XDocument generated = XDocument.Parse(LadWriter.Generate(specPath));
        XElement unit = generated.Root.Descendants().FirstOrDefault(IsCompileUnit);
        if (unit == null)
        {
            Console.WriteLine("Call-Home_Angle_Zero 沒有 CompileUnit。");
            return -1;
        }

        XElement after = null;
        foreach (XElement compileUnit in root.Descendants().Where(IsCompileUnit))
        {
            if (compileUnit.Descendants().Any(e =>
                e.Name.LocalName == "CallInfo" &&
                (string)e.Attribute("Name") == "PreTwist_IO"))
            {
                after = compileUnit;
                break;
            }
        }

        if (after == null)
        {
            foreach (XElement compileUnit in root.Descendants().Where(IsCompileUnit))
            {
                if (compileUnit.Descendants().Any(e =>
                    e.Name.LocalName == "CallInfo" &&
                    (string)e.Attribute("Name") == "Main_Logic"))
                {
                    after = compileUnit;
                    break;
                }
            }
        }

        if (after == null)
        {
            Console.WriteLine("Main 找不到 PreTwist_IO／Main_Logic 呼叫。");
            return -1;
        }

        int nextId = MaxHexId(root) + 16;
        ShiftIds(unit, nextId - FirstHexId(unit));
        after.AddAfterSelf(unit);
        return 1;
    }

    private static int PatchNamedBlock(
        PlcSoftware plc,
        string dir,
        string blockName,
        Func<XElement, int> patch,
        Func<XElement, bool> verify)
    {
        PlcBlock block = FindBlock(plc, blockName);
        if (block == null)
        {
            Console.WriteLine("找不到 " + blockName + "。");
            return -1;
        }

        string sourcePath = Path.Combine(dir, blockName + "-before.xml");
        string importPath = Path.Combine(dir, blockName + ".xml");
        string verifyPath = Path.Combine(dir, blockName + "-after.xml");
        if (!ExportBlockXmlQuiet(block, sourcePath))
        {
            return -1;
        }

        XDocument doc = XDocument.Load(sourcePath);
        int changed = patch(doc.Root);
        Console.WriteLine("  " + blockName + " 變更：" + changed);
        if (changed < 0)
        {
            return -1;
        }

        if (changed == 0)
        {
            bool ok = verify(doc.Root);
            Console.WriteLine("  " + blockName + " 已接，未重匯；驗證：" + (ok ? "通過" : "失敗"));
            return ok ? 0 : -1;
        }

        if (!verify(doc.Root))
        {
            Console.WriteLine("  " + blockName + " XML 驗證不完整，未匯入。");
            return -1;
        }

        doc.Save(importPath);
        TryImportBlockFile(plc, block, importPath);
        if (CompilePlc(plc) != 0)
        {
            Console.WriteLine("  " + blockName + " 匯入後編譯失敗。");
            return -1;
        }

        PlcBlock imported = FindBlock(plc, blockName);
        if (imported == null || !ExportBlockXmlQuiet(imported, verifyPath))
        {
            return -1;
        }

        bool verified = verify(XDocument.Load(verifyPath).Root);
        Console.WriteLine("  " + blockName + " 匯入後驗證：" + (verified ? "通過" : "失敗"));
        return verified ? 1 : -1;
    }

    private static bool EnsurePreTwistEventTags(PlcSoftware plc)
    {
        PlcTagTable events = FindTable(plc, "Events");
        if (events == null)
        {
            Console.WriteLine("找不到 Events Tag 表。");
            return false;
        }

        HashSet<int> used = CollectAllMemoryBits(plc);
        int bit = NextFreeBit(used, 200 * 8, 239 * 8 + 7);
        if (bit < 0)
        {
            bit = NextFreeMemoryBit(plc);
        }

        foreach (string station in PreTwistStations)
        {
            string[] names =
            {
                "AErr_EStop_PB_Act_" + station + "_PreTwist",
                "EErr_" + station + "_PreTwist_Drv_Fault"
            };
            foreach (string name in names)
            {
                if (FindTag(plc, name) != null)
                {
                    Console.WriteLine("  Tag 已有 " + name);
                    continue;
                }

                if (used.Contains(bit))
                {
                    int next = NextFreeBit(used, bit, 239 * 8 + 7);
                    bit = next >= 0 ? next : NextFreeMemoryBit(plc);
                }

                string address = "%M" + (bit / 8) + "." + (bit % 8);
                used.Add(bit);
                bit++;
                try
                {
                    events.Tags.Create(name, "Bool", address);
                    Console.WriteLine("  Tag " + name + " " + address);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  Tag 失敗 " + name + "：" + Flatten(ex));
                    return false;
                }
            }
        }

        return true;
    }

    private static int PatchMainPreTwistCall(XElement root)
    {
        if (root.Descendants().Any(e =>
            e.Name.LocalName == "CallInfo" &&
            (string)e.Attribute("Name") == "PreTwist_IO"))
        {
            return 0;
        }

        string specPath = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "pretwist", "Call-PreTwist_IO.lad.xml");
        if (!File.Exists(specPath))
        {
            Console.WriteLine("找不到 " + specPath);
            return -1;
        }

        XDocument generated = XDocument.Parse(LadWriter.Generate(specPath));
        XElement unit = generated.Root.Descendants().FirstOrDefault(IsCompileUnit);
        if (unit == null)
        {
            Console.WriteLine("Call-PreTwist_IO 沒有 CompileUnit。");
            return -1;
        }

        XElement after = null;
        foreach (XElement compileUnit in root.Descendants().Where(IsCompileUnit))
        {
            if (compileUnit.Descendants().Any(e =>
                e.Name.LocalName == "CallInfo" &&
                (string)e.Attribute("Name") == "Main_Logic"))
            {
                after = compileUnit;
                break;
            }
        }

        if (after == null)
        {
            Console.WriteLine("Main 找不到 Main_Logic 呼叫。");
            return -1;
        }

        int nextId = MaxHexId(root) + 16;
        ShiftIds(unit, nextId - FirstHexId(unit));
        after.AddAfterSelf(unit);
        return 1;
    }

    private static int PatchMainLogicPreTwistJog(XElement root)
    {
        XElement flg = null;
        XElement orPart = null;
        foreach (XElement net in root.Descendants().Where(e => e.Name.LocalName == "FlgNet"))
        {
            if (FindAccessNamed(net, "I_PB_Jog") == null ||
                FindAccessNamed(net, "I_PB_Jog_OP2") == null)
            {
                continue;
            }

            orPart = FindOrFedByTag(net, "I_PB_Jog");
            if (orPart != null)
            {
                flg = net;
                break;
            }
        }

        if (flg == null || orPart == null)
        {
            Console.WriteLine("Main_Logic 找不到 LINE 寸動 OR（I_PB_Jog）。");
            return -1;
        }

        int added = 0;
        foreach (string station in PreTwistStations)
        {
            string tag = "I_PB_" + station + "_PreTwist_Jog";
            if (flg.Descendants().Any(e =>
                e.Name.LocalName == "Component" &&
                (string)e.Attribute("Name") == tag))
            {
                continue;
            }

            if (!AddContactToOr(flg, orPart, tag))
            {
                return -1;
            }

            added++;
        }

        return added;
    }

    private static XElement FindOrFedByTag(XElement flg, string tagName)
    {
        XElement access = FindAccessNamed(flg, tagName);
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        if (access == null || wires == null || parts == null)
        {
            return null;
        }

        XElement operand = FindOperandWire(wires, (string)access.Attribute("UId"));
        if (operand == null)
        {
            return null;
        }

        XElement contactCon = operand.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "NameCon" &&
            (string)e.Attribute("Name") == "operand");
        if (contactCon == null)
        {
            return null;
        }

        string contactUid = (string)contactCon.Attribute("UId");
        XElement outWire = wires.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Wire" &&
            e.Elements().Any(c =>
                c.Name.LocalName == "NameCon" &&
                (string)c.Attribute("UId") == contactUid &&
                (string)c.Attribute("Name") == "out"));
        if (outWire == null)
        {
            return null;
        }

        XElement orCon = outWire.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "NameCon" &&
            ((string)e.Attribute("Name") ?? string.Empty).StartsWith("in", StringComparison.Ordinal));
        if (orCon == null)
        {
            return null;
        }

        string orUid = (string)orCon.Attribute("UId");
        return parts.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Part" &&
            (string)e.Attribute("Name") == "O" &&
            (string)e.Attribute("UId") == orUid);
    }

    private static bool AddContactToOr(XElement flg, XElement orPart, string tagName)
    {
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        if (orPart == null || wires == null || parts == null)
        {
            Console.WriteLine("  找不到 OR／Wires。");
            return false;
        }

        XElement card = orPart.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "TemplateValue" &&
            (string)e.Attribute("Name") == "Card");
        if (card == null)
        {
            Console.WriteLine("  OR 沒有 Card。");
            return false;
        }

        int n;
        if (!int.TryParse(card.Value, out n))
        {
            return false;
        }

        string orUid = (string)orPart.Attribute("UId");
        XElement lastOut = null;
        string lastContactUid = null;
        int lastIn = 0;
        foreach (XElement wire in wires.Elements().Where(e => e.Name.LocalName == "Wire"))
        {
            XElement orCon = wire.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "NameCon" &&
                (string)e.Attribute("UId") == orUid &&
                ((string)e.Attribute("Name") ?? string.Empty).StartsWith("in", StringComparison.Ordinal));
            if (orCon == null)
            {
                continue;
            }

            int port;
            if (!int.TryParse(((string)orCon.Attribute("Name")).Substring(2), out port) ||
                port < lastIn)
            {
                continue;
            }

            XElement contactCon = wire.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "NameCon" &&
                (string)e.Attribute("Name") == "out");
            if (contactCon == null)
            {
                continue;
            }

            lastIn = port;
            lastOut = wire;
            lastContactUid = (string)contactCon.Attribute("UId");
        }

        if (lastOut == null || lastContactUid == null)
        {
            Console.WriteLine("  OR 沒有既有輸入。");
            return false;
        }

        XElement lastContact = parts.Elements().FirstOrDefault(e =>
            (string)e.Attribute("UId") == lastContactUid);
        XElement lastOperand = wires.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Wire" &&
            e.Elements().Any(c =>
                c.Name.LocalName == "NameCon" &&
                (string)c.Attribute("UId") == lastContactUid &&
                (string)c.Attribute("Name") == "operand"));
        XElement lastAccess = null;
        if (lastOperand != null)
        {
            XElement ident = lastOperand.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "IdentCon");
            if (ident != null)
            {
                string accessUidExisting = (string)ident.Attribute("UId");
                lastAccess = parts.Elements().FirstOrDefault(e =>
                    (string)e.Attribute("UId") == accessUidExisting);
            }
        }

        if (lastContact == null || lastAccess == null)
        {
            Console.WriteLine("  找不到最後一個寸動接點。");
            return false;
        }

        n++;
        card.Value = n.ToString();

        int nextUid = 1;
        foreach (XElement el in flg.Descendants())
        {
            int uid;
            XAttribute attr = el.Attribute("UId");
            if (attr != null && int.TryParse(attr.Value, out uid) && uid >= nextUid)
            {
                nextUid = uid + 1;
            }
        }

        XNamespace ns = flg.Name.Namespace;
        int accessUid = nextUid++;
        int contactUid = nextUid++;
        lastAccess.AddAfterSelf(new XElement(ns + "Access",
            new XAttribute("Scope", "GlobalVariable"),
            new XAttribute("UId", accessUid.ToString()),
            new XElement(ns + "Symbol",
                new XElement(ns + "Component", new XAttribute("Name", tagName)))));
        lastContact.AddAfterSelf(new XElement(ns + "Part",
            new XAttribute("Name", "Contact"),
            new XAttribute("UId", contactUid.ToString())));

        XElement rail = wires.Elements().FirstOrDefault(e =>
            e.Elements().Any(c => c.Name.LocalName == "Powerrail"));
        if (rail != null)
        {
            XElement railAfter = rail.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "NameCon" &&
                (string)e.Attribute("UId") == lastContactUid &&
                (string)e.Attribute("Name") == "in");
            XElement railCon = new XElement(ns + "NameCon",
                new XAttribute("UId", contactUid.ToString()),
                new XAttribute("Name", "in"));
            if (railAfter != null)
            {
                railAfter.AddAfterSelf(railCon);
            }
            else
            {
                rail.Add(railCon);
            }
        }

        XElement operandWire = new XElement(ns + "Wire",
            new XAttribute("UId", (nextUid++).ToString()),
            new XElement(ns + "IdentCon", new XAttribute("UId", accessUid.ToString())),
            new XElement(ns + "NameCon",
                new XAttribute("UId", contactUid.ToString()),
                new XAttribute("Name", "operand")));
        XElement outWire = new XElement(ns + "Wire",
            new XAttribute("UId", (nextUid++).ToString()),
            new XElement(ns + "NameCon",
                new XAttribute("UId", contactUid.ToString()),
                new XAttribute("Name", "out")),
            new XElement(ns + "NameCon",
                new XAttribute("UId", orUid),
                new XAttribute("Name", "in" + n)));
        lastOperand.AddAfterSelf(operandWire);
        lastOut.AddAfterSelf(outWire);
        return true;
    }

    private static int PatchEventControlPreTwist(XElement root)
    {
        string specPath = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "pretwist", "Event-PreTwist.lad.xml");
        if (!File.Exists(specPath))
        {
            Console.WriteLine("找不到 " + specPath);
            return -1;
        }

        XDocument generated = XDocument.Parse(LadWriter.Generate(specPath));
        List<XElement> units = generated.Root.Descendants().Where(IsCompileUnit).ToList();
        if (units.Count == 0)
        {
            Console.WriteLine("Event-PreTwist 沒有 CompileUnit。");
            return -1;
        }

        XElement after = root.Descendants().LastOrDefault(IsCompileUnit);
        if (after == null)
        {
            Console.WriteLine("Event_Control 沒有 CompileUnit。");
            return -1;
        }

        int nextId = MaxHexId(root) + 16;
        int added = 0;
        foreach (XElement source in units)
        {
            string coil = source.Descendants()
                .Where(e => e.Name.LocalName == "Component")
                .Select(e => (string)e.Attribute("Name"))
                .FirstOrDefault(name =>
                    name != null &&
                    (name.StartsWith("AErr_EStop_PB_Act_", StringComparison.Ordinal) ||
                     name.StartsWith("EErr_", StringComparison.Ordinal)));
            if (coil != null &&
                root.Descendants().Any(e =>
                    e.Name.LocalName == "Component" &&
                    (string)e.Attribute("Name") == coil))
            {
                continue;
            }

            XElement unit = new XElement(source);
            int first = FirstHexId(unit);
            ShiftIds(unit, nextId - first);
            nextId = MaxHexId(unit) + 16;
            after.AddAfterSelf(unit);
            after = unit;
            added++;
        }

        return added;
    }

    private static bool VerifyEventControlPreTwist(XElement root)
    {
        foreach (string station in PreTwistStations)
        {
            if (!HasCoilNamed(root, "AErr_EStop_PB_Act_" + station + "_PreTwist") ||
                !HasCoilNamed(root, "EErr_" + station + "_PreTwist_Drv_Fault"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EnsureCExtraEventTags(PlcSoftware plc)
    {
        PlcTagTable events = FindTable(plc, "Events");
        if (events == null)
        {
            Console.WriteLine("找不到 Events Tag 表。");
            return false;
        }

        HashSet<int> used = CollectAllMemoryBits(plc);
        if (!CreateEventTagsInRange(events, CExtraAerrNames, used, 200 * 8, 239 * 8 + 7) ||
            !CreateEventTagsInRange(events, CExtraEerrNames, used, 240 * 8, 279 * 8 + 7))
        {
            return false;
        }

        return true;
    }

    private static bool CreateEventTagsInRange(
        PlcTagTable events,
        string[] names,
        HashSet<int> used,
        int startBit,
        int lastBit)
    {
        int bit = NextFreeBit(used, startBit, lastBit);
        foreach (string name in names)
        {
            if (FindTagByNameInTable(events, name) != null)
            {
                Console.WriteLine("  Tag 已有 " + name);
                continue;
            }

            if (bit < 0 || used.Contains(bit))
            {
                bit = NextFreeBit(used, bit < 0 ? startBit : bit, lastBit);
            }

            if (bit < 0)
            {
                Console.WriteLine("  沒有空 bit（" + startBit + "–" + lastBit + "）：" + name);
                return false;
            }

            string address = "%M" + (bit / 8) + "." + (bit % 8);
            used.Add(bit);
            bit++;
            try
            {
                events.Tags.Create(name, "Bool", address);
                Console.WriteLine("  Tag " + name + " " + address);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Tag 失敗 " + name + "：" + Flatten(ex));
                return false;
            }
        }

        return true;
    }

    private static PlcTag FindTagByNameInTable(PlcTagTable table, string name)
    {
        foreach (PlcTag tag in table.Tags)
        {
            if (string.Equals(tag.Name, name, StringComparison.Ordinal))
            {
                return tag;
            }
        }

        return null;
    }

    private static void DumpCExtraAndPretwistAddresses(PlcSoftware plc)
    {
        string[] extra =
        {
            "AErr_EStop_PB_Act_12B_PreTwist",
            "AErr_EStop_PB_Act_20B_PreTwist",
            "AErr_EStop_PB_Act_24B_PreTwist",
            "EErr_12B_PreTwist_Drv_Fault",
            "EErr_20B_PreTwist_Drv_Fault",
            "EErr_24B_PreTwist_Drv_Fault"
        };
        foreach (string name in extra.Concat(CExtraAerrNames).Concat(CExtraEerrNames))
        {
            PlcTag tag = FindTag(plc, name);
            if (tag == null)
            {
                Console.WriteLine("  址 缺 " + name);
                continue;
            }

            int bit = ParseSimpleMemoryBit(tag.LogicalAddress);
            if (bit < 0)
            {
                Console.WriteLine("  址 " + name + " " + tag.LogicalAddress);
                continue;
            }

            int baseByte = name.StartsWith("EErr_", StringComparison.Ordinal) && bit >= 240 * 8
                ? 240
                : 200;
            int lw = (bit / 8 - baseByte) / 2;
            int wordBit = ((bit / 8 - baseByte) % 2) * 8 + (bit % 8);
            string prefix = baseByte == 240 ? "EErr_LW" : "AErr_LW";
            Console.WriteLine("  址 " + name + " " + tag.LogicalAddress +
                " → " + prefix + "{" + lw + "}." + wordBit);
        }
    }

    private static int ParseSimpleMemoryBit(string address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return -1;
        }

        string raw = address.Trim().ToUpperInvariant();
        if (raw.Length < 4 || raw[0] != '%' || raw[1] != 'M')
        {
            return -1;
        }

        int i = 2;
        if (i < raw.Length && (raw[i] == 'B' || raw[i] == 'W' || raw[i] == 'D'))
        {
            return -1;
        }

        int dot = raw.IndexOf('.');
        int number;
        int bit;
        if (dot < 0 ||
            !int.TryParse(raw.Substring(2, dot - 2), out number) ||
            !int.TryParse(raw.Substring(dot + 1), out bit))
        {
            return -1;
        }

        return number * 8 + bit;
    }

    private static int PatchEventControlCExtra(XElement root)
    {
        string specPath = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "c-extra", "Event-CExtra.lad.xml");
        if (!File.Exists(specPath))
        {
            Console.WriteLine("找不到 " + specPath);
            return -1;
        }

        XDocument generated = XDocument.Parse(LadWriter.Generate(specPath));
        List<XElement> units = generated.Root.Descendants().Where(IsCompileUnit).ToList();
        if (units.Count == 0)
        {
            Console.WriteLine("Event-CExtra 沒有 CompileUnit。");
            return -1;
        }

        XElement after = root.Descendants().LastOrDefault(IsCompileUnit);
        if (after == null)
        {
            Console.WriteLine("Event_Control 沒有 CompileUnit。");
            return -1;
        }

        int nextId = MaxHexId(root) + 16;
        int added = 0;
        foreach (XElement source in units)
        {
            string coil = source.Descendants()
                .Where(e => e.Name.LocalName == "Component")
                .Select(e => (string)e.Attribute("Name"))
                .FirstOrDefault(name =>
                    name != null &&
                    (name.StartsWith("AErr_", StringComparison.Ordinal) ||
                     name.StartsWith("EErr_", StringComparison.Ordinal)));
            if (coil != null &&
                root.Descendants().Any(e =>
                    e.Name.LocalName == "Component" &&
                    (string)e.Attribute("Name") == coil))
            {
                continue;
            }

            XElement unit = new XElement(source);
            int first = FirstHexId(unit);
            ShiftIds(unit, nextId - first);
            nextId = MaxHexId(unit) + 16;
            after.AddAfterSelf(unit);
            after = unit;
            added++;
        }

        return added;
    }

    private static bool VerifyEventControlCExtra(XElement root)
    {
        foreach (string name in CExtraAerrNames.Concat(CExtraEerrNames))
        {
            if (!HasCoilNamed(root, name))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly string[] InsidePlcNames =
    {
        "25017_6B_Inside_PLC",
        "25017_12B_Inside_PLC",
        "25017_20B_Inside_PLC",
        "25017_24B_Inside_PLC"
    };

    private static bool IsInsideFourFace(string plcName)
    {
        return plcName.IndexOf("_20B_", StringComparison.Ordinal) >= 0 ||
            plcName.IndexOf("_24B_", StringComparison.Ordinal) >= 0;
    }

    private static bool IsInsideS4HardwareTag(string name)
    {
        return name != null && name.EndsWith("_S4", StringComparison.Ordinal);
    }

    // 名留程式底；址對 C。C 說明寫進 Comment。Spare 不建。
    private static readonly string[][] InsideHardwareTags =
    {
        new[] { "I_F_Wire_Broken", "Bool", "%I0.6" },
        new[] { "I_F_Tension_Drv_Fault", "Bool", "%I0.7" },
        new[] { "I_Snr_Bobbin_S1", "Bool", "%I1.0" },
        new[] { "I_Snr_Bobbin_S2", "Bool", "%I1.1" },
        new[] { "I_Snr_Bobbin_S3", "Bool", "%I1.2" },
        new[] { "I_Snr_Bobbin_S4", "Bool", "%I1.3" },
        new[] { "I_Snr_Air_Pressure_Ok", "Bool", "%I1.5" },
        new[] { "I_Snr_Safe_Pin_Unlocked_S1", "Bool", "%I2.0" },
        new[] { "I_Snr_Safe_Pin_locked_S1", "Bool", "%I2.1" },
        new[] { "I_Snr_Safe_Pin_Unlocked_S2", "Bool", "%I2.2" },
        new[] { "I_Snr_Safe_Pin_locked_S2", "Bool", "%I2.3" },
        new[] { "I_Snr_Safe_Pin_Unlocked_S3", "Bool", "%I2.4" },
        new[] { "I_Snr_Safe_Pin_locked_S3", "Bool", "%I2.5" },
        new[] { "I_Snr_Safe_Pin_Unlocked_S4", "Bool", "%I2.6" },
        new[] { "I_Snr_Safe_Pin_locked_S4", "Bool", "%I2.7" },
        new[] { "Q_PicoServer_Reset", "Bool", "%Q0.1" },
        new[] { "Q_Safe_Pin_Unlock_S1", "Bool", "%Q2.0" },
        new[] { "Q_Safe_Pin_Unlock_S2", "Bool", "%Q2.1" },
        new[] { "Q_Safe_Pin_Unlock_S3", "Bool", "%Q2.2" },
        new[] { "Q_Safe_Pin_Unlock_S4", "Bool", "%Q2.3" },
        new[] { "Q_Bobbin_Lock_S1", "Bool", "%Q3.0" },
        new[] { "Q_Bobbin_Lock_S2", "Bool", "%Q3.1" },
        new[] { "Q_Bobbin_Lock_S3", "Bool", "%Q3.2" },
        new[] { "Q_Bobbin_Lock_S4", "Bool", "%Q3.3" },
    };

    public static int SyncInsideIo(TiaPortal portal, Project project)
    {
        Console.WriteLine("四台 Inside Hardware Tag 對 C 表 Rotor inside PLC（名不改、址對 C、Comment 寫 C）。");
        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "inside-io");
        Directory.CreateDirectory(dir);

        Dictionary<string, string> cDesc = LoadInsideCDesc(
            Path.Combine(dir, "..", "inside-c-io.tsv"));
        if (cDesc.Count == 0)
        {
            Console.WriteLine("找不到 inside-c-io.tsv，先跑 python Practice\\io-merge\\Write-InsideCTsv.py");
            return 1;
        }

        List<string> problems = new List<string>();
        problems.Add("名留程式底；C 說明寫 Comment。同址功能敘述對不起來只列 PROBLEMS，不改名。");
        problems.Add("");
        foreach (string[] row in InsideHardwareTags)
        {
            string name = row[0];
            string addr = NormalizeAddr(row[2]);
            string desc;
            if (!cDesc.TryGetValue(addr, out desc) || IsSpareDesc(desc))
            {
                continue;
            }

            if (LooksNameDescMismatch(name, desc))
            {
                problems.Add("MISMATCH " + addr + "  Tag=" + name + "  C=" + desc);
            }
        }

        problems.Add("");

        int failed = 0;
        HashSet<string> firstNames = null;
        foreach (string plcName in InsidePlcNames)
        {
            PlcSoftware plc = FindPlc(project, plcName);
            if (plc == null)
            {
                Console.WriteLine("找不到 " + plcName);
                failed++;
                continue;
            }

            Console.WriteLine("==== " + plcName + " ====");
            DumpInsideModules(project, plcName, problems);

            PlcTagTable hardware = FindTable(plc, "Hardware");
            if (hardware == null)
            {
                Console.WriteLine("  沒有 Hardware 變數表");
                failed++;
                continue;
            }

            int created = 0;
            int moved = 0;
            foreach (string[] row in InsideHardwareTags)
            {
                string name = row[0];
                string type = row[1];
                string addr = row[2];
                if (!IsInsideFourFace(plcName) && IsInsideS4HardwareTag(name))
                {
                    continue;
                }

                PlcTag tag = FindTagInTable(hardware, name);
                if (tag == null)
                {
                    PlcTag occupant = FindTagByAddress(hardware, addr);
                    if (occupant != null &&
                        !string.Equals(occupant.Name, name, StringComparison.Ordinal))
                    {
                        problems.Add(plcName + " " + addr + " 已有 " + occupant.Name +
                            "，要建 " + name + "（不改名、不擠走）");
                        continue;
                    }

                    try
                    {
                        hardware.Tags.Create(name, type, addr);
                        created++;
                        Console.WriteLine("  建 " + name + " " + addr);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  建失敗 " + name + "：" + Flatten(ex));
                        failed++;
                    }

                    continue;
                }

                string cur = tag.LogicalAddress ?? string.Empty;
                if (!string.Equals(NormalizeAddr(cur), NormalizeAddr(addr), StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        tag.LogicalAddress = addr;
                        moved++;
                        Console.WriteLine("  搬家 " + name + " " + cur + " → " + addr);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  搬家失敗 " + name + "：" + Flatten(ex));
                        problems.Add(plcName + " " + name + " 搬家失敗 " + cur + "→" + addr);
                    }
                }
            }

            string path = Path.Combine(dir, Sanitize(plcName) + "-Hardware.xml");
            if (!ExportTagTableQuiet(hardware, path))
            {
                failed++;
                continue;
            }

            int comments = PatchInsideHardwareComments(path, cDesc, problems, plcName);
            try
            {
                PlcTagTableGroup parentGroup = hardware.Parent as PlcTagTableGroup;
                if (parentGroup != null)
                {
                    parentGroup.TagTables.Import(new FileInfo(path), ImportOptions.Override);
                }
                else
                {
                    plc.TagTableGroup.TagTables.Import(new FileInfo(path), ImportOptions.Override);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  匯入 Hardware 失敗：" + Flatten(ex));
                failed++;
                continue;
            }

            Console.WriteLine("  建 " + created + "、搬家 " + moved + "、Comment " + comments);

            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            hardware = FindTable(plc, "Hardware");
            foreach (PlcTag tag in hardware.Tags)
            {
                names.Add(tag.Name);
            }

            if (firstNames == null)
            {
                firstNames = names;
            }
            else if (!names.SetEquals(firstNames))
            {
                problems.Add(plcName + " Hardware 名與 6B 不一致");
                foreach (string n in firstNames.Except(names).OrderBy(x => x))
                {
                    problems.Add("  缺 " + n);
                }

                foreach (string n in names.Except(firstNames).OrderBy(x => x))
                {
                    problems.Add("  多 " + n);
                }
            }

            int miss = 0;
            foreach (KeyValuePair<string, string> kv in cDesc)
            {
                if (IsSpareDesc(kv.Value))
                {
                    continue;
                }

                if (FindTagByAddress(hardware, kv.Key) == null)
                {
                    miss++;
                    problems.Add(plcName + " 缺址 " + kv.Key + "  " + kv.Value);
                }
            }

            Console.WriteLine("  Hardware " + hardware.Tags.Count + "、C 有說明缺 " + miss);
            project.Save();
            Console.WriteLine("已存（" + plcName + "）。");
        }

        string probPath = Path.Combine(dir, "PROBLEMS.txt");
        File.WriteAllLines(probPath, problems, new UTF8Encoding(false));
        Console.WriteLine("PROBLEMS → " + probPath);
        return failed == 0 ? 0 : 1;
    }

    private static Dictionary<string, string> LoadInsideCDesc(string path)
    {
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return map;
        }

        foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("address", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            map[NormalizeAddr(parts[0])] = parts[1].Trim();
        }

        return map;
    }

    private static bool IsSpareDesc(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc))
        {
            return true;
        }

        return Regex.IsMatch(desc, "spare|備用|预留|預留", RegexOptions.IgnoreCase);
    }

    private static string NormalizeAddr(string addr)
    {
        if (string.IsNullOrWhiteSpace(addr))
        {
            return string.Empty;
        }

        addr = addr.Trim().Replace(" ", string.Empty);
        if (!addr.StartsWith("%", StringComparison.Ordinal))
        {
            addr = "%" + addr;
        }

        return addr.ToUpperInvariant();
    }

    private static PlcTag FindTagInTable(PlcTagTable table, string name)
    {
        foreach (PlcTag tag in table.Tags)
        {
            if (string.Equals(tag.Name, name, StringComparison.Ordinal))
            {
                return tag;
            }
        }

        return null;
    }

    private static PlcTag FindTagByAddress(PlcTagTable table, string address)
    {
        string want = NormalizeAddr(address);
        foreach (PlcTag tag in table.Tags)
        {
            if (string.Equals(NormalizeAddr(tag.LogicalAddress), want, StringComparison.OrdinalIgnoreCase))
            {
                return tag;
            }
        }

        return null;
    }

    private static bool ExportTagTableQuiet(PlcTagTable table, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            table.Export(new FileInfo(path), ExportOptions.WithDefaults);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯出 Tag 表失敗：" + Flatten(ex));
            return false;
        }
    }

    private static int PatchInsideHardwareComments(
        string path,
        Dictionary<string, string> cDesc,
        List<string> problems,
        string plcName)
    {
        XDocument doc = XDocument.Load(path);
        int changed = 0;
        foreach (XElement tag in doc.Descendants().Where(e => e.Name.LocalName == "PlcTag"))
        {
            XElement attrs = tag.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            if (attrs == null)
            {
                continue;
            }

            XElement addrEl = attrs.Elements().FirstOrDefault(e => e.Name.LocalName == "LogicalAddress");
            XElement nameEl = attrs.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
            string addr = NormalizeAddr(addrEl == null ? null : addrEl.Value);
            string name = nameEl == null || nameEl.Value == null ? string.Empty : nameEl.Value;
            if (string.IsNullOrEmpty(addr) || !cDesc.ContainsKey(addr))
            {
                continue;
            }

            string desc = cDesc[addr];
            if (IsSpareDesc(desc))
            {
                continue;
            }

            // 名與 C 敘述對不起來：只記 PROBLEMS
            if (LooksNameDescMismatch(name, desc) &&
                !problems.Any(p => p.StartsWith("MISMATCH " + addr, StringComparison.Ordinal)))
            {
                problems.Add("MISMATCH " + addr + "  Tag=" + name + "  C=" + desc);
            }

            XElement objects = tag.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
            if (objects == null)
            {
                objects = new XElement(tag.Name.Namespace + "ObjectList");
                tag.Add(objects);
            }

            XElement comment = objects.Elements()
                .FirstOrDefault(e =>
                    e.Name.LocalName == "MultilingualText" &&
                    (string)e.Attribute("CompositionName") == "Comment");
            if (comment == null)
            {
                // 簡寫：有 Comment 才改；沒有就跳過（不硬塞 ID）
                continue;
            }

            foreach (XElement item in comment.Descendants().Where(e => e.Name.LocalName == "MultilingualTextItem"))
            {
                XElement itemAttrs = item.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                if (itemAttrs == null)
                {
                    continue;
                }

                XElement culture = itemAttrs.Elements().FirstOrDefault(e => e.Name.LocalName == "Culture");
                XElement text = itemAttrs.Elements().FirstOrDefault(e => e.Name.LocalName == "Text");
                if (culture == null || text == null)
                {
                    continue;
                }

                string c = culture.Value ?? string.Empty;
                if (c == "id-ID")
                {
                    continue;
                }

                if (!string.Equals(text.Value ?? string.Empty, desc, StringComparison.Ordinal))
                {
                    text.Value = desc;
                    changed++;
                }
            }
        }

        doc.Save(path);
        Console.WriteLine("  Comment 寫入 " + changed + " 格（" + plcName + "）");
        return changed;
    }

    private static bool LooksNameDescMismatch(string name, string desc)
    {
        // 已知：Bit 編號與 C 表 bit 字樣對不起來、Bit3 名對「允取頂退軸」
        if (name.IndexOf("Run_Status_Bit2", StringComparison.OrdinalIgnoreCase) >= 0 &&
            desc.IndexOf("bit3", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (name.IndexOf("Run_Status_Bit3", StringComparison.OrdinalIgnoreCase) >= 0 &&
            (desc.IndexOf("頂退", StringComparison.Ordinal) >= 0 ||
             desc.IndexOf("允取", StringComparison.Ordinal) >= 0))
        {
            return true;
        }

        if (name.IndexOf("Run_Status_Bit2", StringComparison.OrdinalIgnoreCase) >= 0 &&
            desc.IndexOf("bit0", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (name.IndexOf("Run_Status_Bit0", StringComparison.OrdinalIgnoreCase) >= 0 &&
            desc.IndexOf("bit1", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (name.IndexOf("Run_Status_Bit1", StringComparison.OrdinalIgnoreCase) >= 0 &&
            desc.IndexOf("bit2", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return false;
    }

    private static void DumpInsideModules(Project project, string plcName, List<string> problems)
    {
        Device device = null;
        foreach (Device d in EnumerateDevices(project))
        {
            foreach (DeviceItem item in WalkItems(d.DeviceItems))
            {
                if (string.Equals(item.Name, plcName, StringComparison.OrdinalIgnoreCase))
                {
                    device = d;
                    break;
                }
            }

            if (device != null)
            {
                break;
            }
        }

        if (device == null)
        {
            problems.Add(plcName + "：找不到裝置（模組核對略過）");
            return;
        }

        bool hasCpu = false;
        bool hasSm = false;
        foreach (DeviceItem item in WalkItems(device.DeviceItems))
        {
            string typeId = item.TypeIdentifier ?? string.Empty;
            if (typeId.IndexOf("214-1AG40", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hasCpu = true;
                Console.WriteLine("  CPU " + item.Name + " " + typeId);
            }

            if (typeId.IndexOf("223-1BH32", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hasSm = true;
                Console.WriteLine("  SM  " + item.Name + " " + typeId);
            }

            if (typeId.IndexOf("223-1BL32", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                problems.Add(plcName + " SM 是 223-1BL32（C 表要 223-1BH32） " + item.Name);
            }
        }

        if (!hasCpu)
        {
            problems.Add(plcName + " 找不到 CPU 214-1AG40");
        }

        if (!hasSm)
        {
            problems.Add(plcName + " 找不到 SM 223-1BH32");
        }
    }

    public static int FixInsideSections(TiaPortal portal, Project project)
    {
        // 6B／12B＝3 面（S1–S3）；20B／24B＝4 面（S1–S4）。面專用 Instance DB＝Bobbin_Load_Sn_DB。
        Console.WriteLine("Inside 段面數：6B／12B→3，20B／24B→4。補／清 Bobbin_Load_Sn_DB。");
        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "inside-io", "fix-sections");
        Directory.CreateDirectory(dir);

        var jobs = new[]
        {
            new { Plc = "25017_6B_Inside_PLC", Faces = 3 },
            new { Plc = "25017_12B_Inside_PLC", Faces = 3 },
            new { Plc = "25017_20B_Inside_PLC", Faces = 4 },
            new { Plc = "25017_24B_Inside_PLC", Faces = 4 },
        };

        int failed = 0;
        foreach (var job in jobs)
        {
            PlcSoftware plc = FindPlc(project, job.Plc);
            if (plc == null)
            {
                Console.WriteLine("找不到 " + job.Plc);
                failed++;
                continue;
            }

            Console.WriteLine("==== " + job.Plc + " 面數 " + job.Faces + " ====");
            string plcDir = Path.Combine(dir, job.Plc);
            Directory.CreateDirectory(plcDir);

            for (int n = 1; n <= job.Faces; n++)
            {
                string dbName = "Bobbin_Load_S" + n + "_DB";
                if (FindBlock(plc, dbName) != null)
                {
                    Console.WriteLine("  已有 " + dbName);
                    continue;
                }

                if (FindBlock(plc, "Bobbin_Load") == null)
                {
                    Console.WriteLine("  沒有 FB Bobbin_Load，跳過建 " + dbName);
                    failed++;
                    continue;
                }

                try
                {
                    EnsureBobbinLoadInstanceDb(plc, dbName);
                    Console.WriteLine("  建 " + dbName);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  建失敗 " + dbName + "：" + Flatten(ex));
                    failed++;
                }
            }

            if (job.Faces < 4)
            {
                // Main_Logic 不一致時匯不出：先暫建 S4_DB 讓它能匯，清掉 S4 網後再刪 DB。
                bool tempDb = FindBlock(plc, "Bobbin_Load_S4_DB") == null;
                if (tempDb)
                {
                    try
                    {
                        EnsureBobbinLoadInstanceDb(plc, "Bobbin_Load_S4_DB");
                        Console.WriteLine("  暫建 Bobbin_Load_S4_DB（僅為清網）");
                        project.Save();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  暫建 S4_DB 失敗：" + Flatten(ex));
                        failed++;
                        continue;
                    }
                }

                CompilePlc(plc);
                int stripped = StripBobbinLoadS4Calls(plc, plcDir);
                Console.WriteLine("  清掉誤加的 S4 呼叫／網：" + stripped);
                if (stripped < 0)
                {
                    failed++;
                }

                if (tempDb || FindBlock(plc, "Bobbin_Load_S4_DB") != null)
                {
                    PlcBlock s4 = FindBlock(plc, "Bobbin_Load_S4_DB");
                    if (s4 != null)
                    {
                        try
                        {
                            s4.Delete();
                            Console.WriteLine("  已刪 Bobbin_Load_S4_DB（三面不需要）");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("  刪 S4_DB 失敗：" + Flatten(ex));
                            failed++;
                        }
                    }
                }
            }

            if (CompilePlc(plc) != 0)
            {
                failed++;
            }

            project.Save();
            Console.WriteLine("已存（" + job.Plc + "）。");
        }

        return failed == 0 ? 0 : 1;
    }

    private static void EnsureBobbinLoadInstanceDb(PlcSoftware plc, string dbName)
    {
        if (FindBlock(plc, dbName) != null)
        {
            return;
        }

        HashSet<int> used = new HashSet<int>(
            EnumerateBlocks(plc.BlockGroup).Select(b => b.Number));
        int dbNumber = Enumerable.Range(1, 60000).First(n => !used.Contains(n));
        plc.BlockGroup.Blocks.CreateInstanceDB(dbName, false, dbNumber, "Bobbin_Load");
    }

    private static int StripBobbinLoadS4Calls(PlcSoftware plc, string plcDir)
    {
        PlcBlock block = FindBlock(plc, "Main_Logic");
        if (block == null)
        {
            return 0;
        }

        string path = Path.Combine(plcDir, "Main_Logic-before.xml");
        if (!ExportBlockXmlQuiet(block, path))
        {
            return -1;
        }

        XDocument doc = XDocument.Load(path);
        XElement root = doc.Root;
        List<XElement> units = root.Descendants().Where(IsCompileUnit).ToList();
        int removed = 0;
        foreach (XElement unit in units)
        {
            string xml = unit.ToString(SaveOptions.DisableFormatting);
            if (xml.IndexOf("Bobbin_Load_S4_DB", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            unit.Remove();
            removed++;
        }

        if (removed == 0)
        {
            return 0;
        }

        string afterPath = Path.Combine(plcDir, "Main_Logic-after.xml");
        doc.Save(afterPath);
        try
        {
            PlcBlockGroup folder = block.Parent as PlcBlockGroup ?? plc.BlockGroup;
            folder.Blocks.Import(new FileInfo(afterPath), ImportOptions.Override);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯入 Main_Logic 失敗：" + Flatten(ex));
            return -1;
        }

        return removed;
    }

    public static int WireInsideS4(TiaPortal portal, Project project)
    {
        Console.WriteLine("Inside：#4 鐵軸／安全／頂退，編碼器腳，線速／運轉。不接 PN/IE_1。");
        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "inside-s4");
        Directory.CreateDirectory(dir);

        int failed = 0;
        foreach (string plcName in InsidePlcNames)
        {
            PlcSoftware plc = FindPlc(project, plcName);
            if (plc == null)
            {
                Console.WriteLine("找不到 " + plcName);
                failed++;
                continue;
            }

            string plcDir = Path.Combine(dir, plcName);
            Directory.CreateDirectory(plcDir);
            Console.WriteLine("==== " + plcName + " ====");
            if (CompilePlc(plc) != 0)
            {
                Console.WriteLine("  編譯失敗，先列區塊再試匯出。");
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + " 編譯後）。");

            List<string> names = new List<string>();
            foreach (PlcBlock block in EnumerateBlocks(plc.BlockGroup))
            {
                names.Add(block.Name);
            }

            File.WriteAllLines(Path.Combine(plcDir, "blocks.txt"), names, new UTF8Encoding(false));
            Console.WriteLine("  區塊 " + names.Count + " 個。");

            string[] want =
            {
                "Main", "Main_Logic", "Event_Control", "SaftyDoor",
                "Bobbin_Load", "Bobbin_Load_S1", "Bobbin_Load_S2", "Bobbin_Load_S3",
                "SPD_PTO_Ctrl", "PTO_Ctrl"
            };
            int exported = 0;
            foreach (string name in want)
            {
                PlcBlock block = FindBlock(plc, name);
                if (block == null)
                {
                    continue;
                }

                string path = Path.Combine(plcDir, Sanitize(name) + ".xml");
                if (ExportBlockXmlQuiet(block, path))
                {
                    exported++;
                    Console.WriteLine("  匯出 " + name);
                }
                else
                {
                    Console.WriteLine("  匯不出 " + name);
                }
            }

            foreach (PlcBlock block in EnumerateBlocks(plc.BlockGroup))
            {
                if (block.Name == null)
                {
                    continue;
                }

                if (block.Name.IndexOf("Bobbin", StringComparison.OrdinalIgnoreCase) < 0 &&
                    block.Name.IndexOf("Safty", StringComparison.OrdinalIgnoreCase) < 0 &&
                    block.Name.IndexOf("Safety", StringComparison.OrdinalIgnoreCase) < 0 &&
                    block.Name.IndexOf("PTO", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                string path = Path.Combine(plcDir, Sanitize(block.Name) + ".xml");
                if (File.Exists(path))
                {
                    continue;
                }

                if (ExportBlockXmlQuiet(block, path))
                {
                    exported++;
                    Console.WriteLine("  匯出 " + block.Name);
                }
            }

            if (exported == 0)
            {
                Console.WriteLine("  程式區塊匯不出，列問題。");
                failed++;
                continue;
            }

            int changed = PatchInsidePlc(plc, plcDir);
            Console.WriteLine("  變更區塊：" + changed);
            if (changed < 0)
            {
                failed++;
                continue;
            }

            if (CompilePlc(plc) != 0)
            {
                failed++;
                continue;
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + "）。");
        }

        return failed == 0 ? 0 : 1;
    }

    private static int PatchInsidePlc(PlcSoftware plc, string plcDir)
    {
        int total = 0;
        string[] targets = { "Main_Logic", "Main", "Event_Control" };
        foreach (string name in targets)
        {
            if (FindBlock(plc, name) == null)
            {
                continue;
            }

            int changed = PatchNamedBlock(
                plc,
                plcDir,
                name,
                root => PatchInsideS4Clone(root, plcDir, name),
                root => VerifyInsideS4(root));
            if (changed < 0)
            {
                return -1;
            }

            total += changed;
        }

        return total;
    }

    private static int PatchInsideS4Clone(XElement root, string plcDir, string blockName)
    {
        string before = root.ToString(SaveOptions.DisableFormatting);
        if (VerifyInsideS4(root) &&
            before.IndexOf("Q_Safe_Pin_Unlock_S4", StringComparison.Ordinal) >= 0 &&
            before.IndexOf("Q_Bobbin_Lock_S4", StringComparison.Ordinal) >= 0 &&
            before.IndexOf("Q_LineRun_To_Servo", StringComparison.Ordinal) >= 0)
        {
            return 0;
        }

        int added = 0;
        List<XElement> units = root.Descendants().Where(IsCompileUnit).ToList();
        XElement after = units.LastOrDefault();
        if (after == null)
        {
            Console.WriteLine("  " + blockName + " 沒有 CompileUnit。");
            return -1;
        }

        int nextId = MaxHexId(root) + 16;
        foreach (XElement unit in units.ToList())
        {
            string xml = unit.ToString(SaveOptions.DisableFormatting);
            bool hasS3 = xml.IndexOf("_S3", StringComparison.Ordinal) >= 0;
            bool hasS4 = xml.IndexOf("_S4", StringComparison.Ordinal) >= 0;
            if (!LooksLikeInsideSectionUnit(xml) || hasS4 || !hasS3)
            {
                continue;
            }

            string cloned = xml.Replace("_S3", "_S4");
            if (cloned == xml)
            {
                continue;
            }

            XElement copy = XElement.Parse(cloned);
            int first = FirstHexId(copy);
            ShiftIds(copy, nextId - first);
            nextId = MaxHexId(copy) + 16;
            after.AddAfterSelf(copy);
            after = copy;
            added++;
        }

        File.WriteAllText(
            Path.Combine(plcDir, blockName + "-s4-note.txt"),
            "cloned-units=" + added + Environment.NewLine,
            new UTF8Encoding(false));
        return added;
    }

    private static bool LooksLikeInsideSectionUnit(string xml)
    {
        return xml.IndexOf("Q_Safe_Pin_Unlock_S", StringComparison.Ordinal) >= 0 ||
            xml.IndexOf("I_Snr_Safe_Pin_", StringComparison.Ordinal) >= 0 ||
            xml.IndexOf("I_Snr_Bobbin_S", StringComparison.Ordinal) >= 0 ||
            xml.IndexOf("Q_Bobbin_Lock_S", StringComparison.Ordinal) >= 0 ||
            xml.IndexOf("SaftyDoor", StringComparison.Ordinal) >= 0;
    }

    private static int EnsureInsideLineBits(XElement root, ref int nextId, XElement after)
    {
        int added = 0;
        if (!HasCoilNamed(root, "Q_LineRun_To_Servo") && after != null)
        {
            string spec =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<Lad kind=\"FB\" name=\"_LineRun\" number=\"1\"><Interface />" +
                "<Network title=\"line run to servo\">" +
                "<Series><Contact tag=\"is.run\" /><Coil tag=\"Q_LineRun_To_Servo\" /></Series>" +
                "</Network></Lad>";
            added += AppendGeneratedNetworks(root, spec, ref nextId, ref after);
        }

        if (!HasCoilNamed(root, "Q_LineSpeed_To_DSP") && after != null)
        {
            string spec =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<Lad kind=\"FB\" name=\"_LineSpeed\" number=\"1\"><Interface />" +
                "<Network title=\"line speed to DSP\">" +
                "<Series><Contact tag=\"is.run\" /><Coil tag=\"Q_LineSpeed_To_DSP\" /></Series>" +
                "</Network></Lad>";
            added += AppendGeneratedNetworks(root, spec, ref nextId, ref after);
        }

        return added;
    }

    private static int AppendGeneratedNetworks(
        XElement root,
        string specXml,
        ref int nextId,
        ref XElement after)
    {
        string temp = Path.Combine(Path.GetTempPath(), "inside-line-" + Guid.NewGuid().ToString("N") + ".lad.xml");
        File.WriteAllText(temp, specXml, new UTF8Encoding(false));
        XDocument generated;
        try
        {
            generated = XDocument.Parse(LadWriter.Generate(temp));
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }

        int added = 0;
        foreach (XElement source in generated.Root.Descendants().Where(IsCompileUnit))
        {
            XElement unit = new XElement(source);
            int first = FirstHexId(unit);
            ShiftIds(unit, nextId - first);
            nextId = MaxHexId(unit) + 16;
            after.AddAfterSelf(unit);
            after = unit;
            added++;
        }

        return added;
    }

    private static int InsertGeneratedNetworksBefore(
        XElement root,
        string specXml,
        ref int nextId,
        XElement before)
    {
        string temp = Path.Combine(Path.GetTempPath(), "inside-line-" + Guid.NewGuid().ToString("N") + ".lad.xml");
        File.WriteAllText(temp, specXml, new UTF8Encoding(false));
        XDocument generated;
        try
        {
            generated = XDocument.Parse(LadWriter.Generate(temp));
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }

        int added = 0;
        foreach (XElement source in generated.Root.Descendants().Where(IsCompileUnit))
        {
            XElement unit = new XElement(source);
            int first = FirstHexId(unit);
            ShiftIds(unit, nextId - first);
            nextId = MaxHexId(unit) + 16;
            before.AddBeforeSelf(unit);
            added++;
        }

        return added;
    }

    private static bool VerifyInsideS4(XElement root)
    {
        bool hasS4 = root.Descendants().Any(e =>
            e.Name.LocalName == "Component" &&
            (string)e.Attribute("Name") != null &&
            ((string)e.Attribute("Name")).IndexOf("_S4", StringComparison.Ordinal) >= 0);
        if (hasS4)
        {
            return true;
        }

        foreach (XElement unit in root.Descendants().Where(IsCompileUnit))
        {
            string xml = unit.ToString(SaveOptions.DisableFormatting);
            if (LooksLikeInsideSectionUnit(xml) && xml.IndexOf("_S3", StringComparison.Ordinal) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    public static int WireTowerLamps(TiaPortal portal, Project project)
    {
        Console.WriteLine("Main_Logic：塔燈運轉／停止跟隨 OP1 PBL 燈。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "station-sync", "tower-lamps");
        Directory.CreateDirectory(dir);

        int preCompileErrors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後再匯出）。");
        if (preCompileErrors != 0)
        {
            return 1;
        }

        PlcBlock block = FindBlock(main, "Main_Logic");
        if (block == null)
        {
            Console.WriteLine("找不到 Main_Logic。");
            return 1;
        }

        string sourcePath = Path.Combine(dir, "Main_Logic-before.xml");
        string importPath = Path.Combine(dir, "Main_Logic.xml");
        string verifyPath = Path.Combine(dir, "Main_Logic-after.xml");
        if (!ExportBlockXmlQuiet(block, sourcePath))
        {
            return 1;
        }

        XDocument doc = XDocument.Load(sourcePath);
        int changed = PatchTowerLamps(doc.Root);
        Console.WriteLine("  塔燈跟隨網路：" + changed);
        if (changed < 0)
        {
            return 1;
        }

        if (changed == 0)
        {
            bool verifiedCurrent = VerifyTowerLamps(doc.Root);
            Console.WriteLine("  已接塔燈，未重匯；XML 驗證：" +
                (verifiedCurrent ? "通過" : "失敗"));
            return verifiedCurrent ? 0 : 1;
        }

        if (!VerifyTowerLamps(doc.Root))
        {
            Console.WriteLine("  塔燈 XML 驗證不完整，未匯入。");
            return 1;
        }

        doc.Save(importPath);
        TryImportBlockFile(main, block, importPath);
        project.Save();
        Console.WriteLine("已存（匯入 Main_Logic）。");

        PlcBlock importedBlock = FindBlock(main, "Main_Logic");
        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        if (errors != 0 || importedBlock == null || !ExportBlockXmlQuiet(importedBlock, verifyPath))
        {
            return 1;
        }

        bool verified = VerifyTowerLamps(XDocument.Load(verifyPath).Root);
        Console.WriteLine("  匯入後 XML 驗證：" + (verified ? "通過" : "失敗"));
        return verified ? 0 : 1;
    }

    private static readonly string[] LoaderStations = { "6B", "12B", "20B", "24B" };

    public static int WireLoaderStopAndAir(TiaPortal portal, Project project)
    {
        Console.WriteLine("Main_Logic：四站台車停止燈；24B 空氣閥照 6B／12B／20B Charge_Air。離合器推出磁簧不接。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "station-sync", "loader-stop-air");
        Directory.CreateDirectory(dir);

        int preCompileErrors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後再匯出）。");
        if (preCompileErrors != 0)
        {
            return 1;
        }

        int changed = PatchNamedBlock(
            main,
            dir,
            "Main_Logic",
            PatchLoaderStopAndAir,
            VerifyLoaderStopAndAir);
        Console.WriteLine("  Main_Logic：" + changed);
        if (changed < 0)
        {
            return 1;
        }

        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return errors == 0 ? 0 : 1;
    }

    public static int CompleteTf(TiaPortal portal, Project project)
    {
        Console.WriteLine("TF：停止燈跟隨未運轉；Q_EStop 跟隨既有 STO 條件。");
        PlcSoftware tf = FindPlc(project, "25017_TF_PLC");
        if (tf == null)
        {
            Console.WriteLine("找不到 25017_TF_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "tf-live");
        Directory.CreateDirectory(dir);

        int preCompileErrors = CompilePlc(tf);
        project.Save();
        Console.WriteLine("已存（編譯後再匯出）。");
        if (preCompileErrors != 0)
        {
            return 1;
        }

        int lamp = PatchTfBlock(
            tf,
            project,
            dir,
            "Main_Logic",
            PatchTfStopLamp,
            VerifyTfStopLamp);
        int estop = PatchTfBlock(
            tf,
            project,
            dir,
            "Event_Control",
            PatchTfEStop,
            VerifyTfEStop);
        Console.WriteLine("  停止燈：" + lamp + "／Q_EStop：" + estop);
        if (lamp < 0 || estop < 0)
        {
            return 1;
        }

        int errors = CompilePlc(tf);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return errors == 0 ? 0 : 1;
    }

    public static int WireTfTension(TiaPortal portal, Project project)
    {
        Console.WriteLine("TF：IW_Tension_VR / 27648.0 → Speed_Loop ten_norm / tension.ramped。");
        PlcSoftware tf = FindPlc(project, "25017_TF_PLC");
        if (tf == null)
        {
            Console.WriteLine("找不到 25017_TF_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "tf-live");
        Directory.CreateDirectory(dir);

        int changed = PatchTfBlock(
            tf,
            project,
            dir,
            "Speed_Loop",
            PatchTfTensionVr,
            VerifyTfTensionVr);
        Console.WriteLine("  張力 VR：" + changed);
        if (changed < 0)
        {
            return 1;
        }

        int errors = CompilePlc(tf);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return errors == 0 ? 0 : 1;
    }

    private static int PatchTfBlock(
        PlcSoftware tf,
        Project project,
        string dir,
        string blockName,
        Func<XElement, int> patch,
        Func<XElement, bool> verify)
    {
        PlcBlock block = FindBlock(tf, blockName);
        if (block == null)
        {
            Console.WriteLine("找不到 " + blockName + "。");
            return -1;
        }

        string sourcePath = Path.Combine(dir, blockName + "-before.xml");
        string importPath = Path.Combine(dir, blockName + ".xml");
        string verifyPath = Path.Combine(dir, blockName + "-after.xml");
        if (!ExportBlockXmlQuiet(block, sourcePath))
        {
            return -1;
        }

        XDocument doc = XDocument.Load(sourcePath);
        int changed = patch(doc.Root);
        if (changed < 0)
        {
            return -1;
        }

        if (changed == 0)
        {
            bool verifiedCurrent = verify(doc.Root);
            Console.WriteLine("  " + blockName + " 已接，未重匯；驗證：" +
                (verifiedCurrent ? "通過" : "失敗"));
            return verifiedCurrent ? 0 : -1;
        }

        if (!verify(doc.Root))
        {
            Console.WriteLine("  " + blockName + " XML 驗證不完整，未匯入。");
            return -1;
        }

        doc.Save(importPath);
        TryImportBlockFile(tf, block, importPath);
        project.Save();
        Console.WriteLine("已存（匯入 " + blockName + "）。");
        if (CompilePlc(tf) != 0)
        {
            return -1;
        }

        project.Save();
        PlcBlock imported = FindBlock(tf, blockName);
        if (imported == null || !ExportBlockXmlQuiet(imported, verifyPath))
        {
            return -1;
        }

        bool verified = verify(XDocument.Load(verifyPath).Root);
        Console.WriteLine("  " + blockName + " 匯入後驗證：" + (verified ? "通過" : "失敗"));
        return verified ? changed : -1;
    }

    private static int PatchTfStopLamp(XElement root)
    {
        if (HasCoilNamed(root, "Q_Lamp_Stop"))
        {
            return 0;
        }

        XElement lampNet = null;
        foreach (XElement compileUnit in root.Descendants().Where(IsCompileUnit))
        {
            if (FlgHasAccessNamed(compileUnit, "Q_Lamp_Run") &&
                FlgHasAccessNamed(compileUnit, "Q_Lamp_Fault"))
            {
                lampNet = compileUnit;
            }
        }

        XElement template = FindSimpleContactCoilUnit(root);
        if (lampNet == null || template == null)
        {
            Console.WriteLine("  找不到運轉燈網路。");
            return -1;
        }

        XElement unit = new XElement(template);
        if (!RewriteContactCoilAccess(unit, "is.run", "Q_Lamp_Stop"))
        {
            return -1;
        }

        XElement runAccess = unit.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Access");
        if (runAccess != null)
        {
            runAccess.SetAttributeValue("Scope", "LocalVariable");
            XNamespace ns = runAccess.Name.Namespace;
            runAccess.RemoveNodes();
            runAccess.Add(new XElement(ns + "Symbol",
                new XElement(ns + "Component", new XAttribute("Name", "is")),
                new XElement(ns + "Component", new XAttribute("Name", "run"))));
        }

        XElement contact = unit.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Part" &&
            (string)e.Attribute("Name") == "Contact");
        if (contact != null)
        {
            contact.Add(new XElement(contact.Name.Namespace + "Negated",
                new XAttribute("Name", "operand")));
        }

        SetCompileUnitTitle(unit, "stop lamp", "停止燈");
        int nextId = MaxHexId(root) + 16;
        ShiftIds(unit, nextId - FirstHexId(unit));
        lampNet.AddAfterSelf(unit);
        return 1;
    }

    private static bool VerifyTfStopLamp(XElement root)
    {
        return HasCoilNamed(root, "Q_Lamp_Stop");
    }

    private static int PatchTfEStop(XElement root)
    {
        if (HasCoilNamed(root, "Q_EStop"))
        {
            return 0;
        }

        XElement flg = null;
        foreach (XElement net in root.Descendants().Where(e => e.Name.LocalName == "FlgNet"))
        {
            if (net.Descendants().Any(e =>
                    e.Name.LocalName == "Component" &&
                    (string)e.Attribute("Name") == "CT_PO_TF") &&
                net.Descendants().Any(e =>
                    e.Name.LocalName == "Component" &&
                    (string)e.Attribute("Name") == "pb_reset") &&
                net.Descendants().Any(e =>
                    e.Name.LocalName == "Part" &&
                    (string)e.Attribute("Name") == "Coil"))
            {
                flg = net;
                break;
            }
        }

        if (flg == null)
        {
            Console.WriteLine("  找不到 CT_PO_TF STO 網路。");
            return -1;
        }

        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        if (parts == null || wires == null)
        {
            return -1;
        }

        XElement sourceCoil = parts.Elements().LastOrDefault(e =>
            e.Name.LocalName == "Part" &&
            (string)e.Attribute("Name") == "Coil");
        if (sourceCoil == null)
        {
            return -1;
        }

        XNamespace ns = flg.Name.Namespace;
        int nextUid = MaxUid(flg) + 1;
        XElement newAccess = new XElement(ns + "Access",
            new XAttribute("Scope", "GlobalVariable"),
            new XAttribute("UId", nextUid.ToString()),
            new XElement(ns + "Symbol",
                new XElement(ns + "Component", new XAttribute("Name", "Q_EStop"))));
        string accessUid = nextUid.ToString();
        nextUid++;

        XElement lastAccess = parts.Elements().LastOrDefault(e => e.Name.LocalName == "Access");
        if (lastAccess == null)
        {
            return -1;
        }

        lastAccess.AddAfterSelf(newAccess);

        XElement newCoil = new XElement(sourceCoil);
        string coilUid = nextUid.ToString();
        nextUid++;
        newCoil.SetAttributeValue("UId", coilUid);
        foreach (XElement negated in newCoil.Elements().Where(e => e.Name.LocalName == "Negated").ToList())
        {
            negated.Remove();
        }

        sourceCoil.AddAfterSelf(newCoil);

        XElement coilIn = FindCoilInputWire(wires, (string)sourceCoil.Attribute("UId"));
        if (coilIn == null)
        {
            return -1;
        }

        coilIn.Add(new XElement(ns + "NameCon",
            new XAttribute("UId", coilUid),
            new XAttribute("Name", "in")));

        XElement operand = new XElement(ns + "Wire", new XAttribute("UId", nextUid.ToString()));
        operand.Add(new XElement(ns + "IdentCon", new XAttribute("UId", accessUid)));
        operand.Add(new XElement(ns + "NameCon",
            new XAttribute("UId", coilUid),
            new XAttribute("Name", "operand")));
        wires.Add(operand);
        return 1;
    }

    private static bool VerifyTfEStop(XElement root)
    {
        return HasCoilNamed(root, "Q_EStop");
    }

    private static int PatchTfTensionVr(XElement root)
    {
        if (VerifyTfTensionVr(root))
        {
            return 0;
        }

        string specPath = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "tf-live", "Tension_VR.lad.xml");
        if (!File.Exists(specPath))
        {
            Console.WriteLine("找不到 " + specPath);
            return -1;
        }

        XDocument generated = XDocument.Parse(LadWriter.Generate(specPath));
        XElement unit = generated.Root.Descendants().FirstOrDefault(IsCompileUnit);
        if (unit == null)
        {
            Console.WriteLine("Tension_VR 規格沒有 CompileUnit。");
            return -1;
        }

        XElement tensionNet = null;
        foreach (XElement compileUnit in root.Descendants().Where(IsCompileUnit))
        {
            if (compileUnit.Descendants().Any(e =>
                    e.Name.LocalName == "Text" &&
                    e.Value == "tension reference output") &&
                FlgHasAccessNamed(compileUnit, "ten_cmd"))
            {
                tensionNet = compileUnit;
            }
        }

        if (tensionNet == null)
        {
            Console.WriteLine("找不到 tension reference 網路。");
            return -1;
        }

        int nextId = MaxHexId(root) + 16;
        ShiftIds(unit, nextId - FirstHexId(unit));
        tensionNet.AddBeforeSelf(unit);
        return 1;
    }

    private static bool VerifyTfTensionVr(XElement root)
    {
        bool hasAi = false;
        bool hasScale = false;
        foreach (XElement el in root.Descendants())
        {
            if (el.Name.LocalName == "Component" &&
                (string)el.Attribute("Name") == "IW_Tension_VR")
            {
                hasAi = true;
            }

            if (el.Name.LocalName == "ConstantValue" &&
                (el.Value == "27648.0" || el.Value == "27648"))
            {
                hasScale = true;
            }
        }

        return hasAi && hasScale;
    }

    private static bool HasCoilNamed(XElement root, string tagName)
    {
        foreach (XElement flg in root.Descendants().Where(e => e.Name.LocalName == "FlgNet"))
        {
            XElement access = FindAccessNamed(flg, tagName);
            if (access == null)
            {
                continue;
            }

            string uid = (string)access.Attribute("UId");
            XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
            if (wires != null && FindOperandWire(wires, uid) != null)
            {
                return true;
            }
        }

        return false;
    }

    private static int PatchPosValve2Follow(XElement root)
    {
        if (root.Descendants().Any(e =>
            e.Name.LocalName == "Component" &&
            (string)e.Attribute("Name") == "Q_6B_Pos_Pin_Lock_2") &&
            root.Descendants().Any(e =>
            e.Name.LocalName == "Component" &&
            (string)e.Attribute("Name") == "Q_6B_Pos_Clutch_close_2"))
        {
            return 0;
        }

        XElement clutchFlg = FindClutchValveNet(root);
        XElement after = clutchFlg == null
            ? null
            : clutchFlg.Ancestors().FirstOrDefault(IsCompileUnit);
        if (after == null)
        {
            foreach (XElement compileUnit in root.Descendants().Where(IsCompileUnit))
            {
                if (compileUnit.Descendants().Any(e =>
                    e.Name.LocalName == "CallInfo" &&
                    string.Equals((string)e.Attribute("Name"), "Rotor_Positioning_5-2V", StringComparison.Ordinal)))
                {
                    after = compileUnit;
                }
            }
        }

        XElement template = FindSimpleContactCoilUnit(root);
        if (after == null || template == null)
        {
            Console.WriteLine("  找不到可插入閥2跟隨網路。");
            return -1;
        }

        int nextId = MaxHexId(root) + 16;
        int added = 0;
        string[] kinds = { "Pos_Pin_Lock", "Pos_Clutch_close" };
        foreach (string kind in kinds)
        {
            foreach (string station in PosValveStations)
            {
                string source = "Q_" + station + "_" + kind;
                string dest = source + "_2";
                if (root.Descendants().Any(e =>
                    e.Name.LocalName == "Component" &&
                    (string)e.Attribute("Name") == dest))
                {
                    continue;
                }

                XElement unit = new XElement(template);
                if (!RewriteContactCoilAccess(unit, source, dest))
                {
                    return -1;
                }

                SetCompileUnitTitle(unit, kind + " valve2 follow " + station, kind + " 閥2跟隨閥1 " + station);
                int first = FirstHexId(unit);
                ShiftIds(unit, nextId - first);
                nextId = MaxHexId(unit) + 16;
                after.AddAfterSelf(unit);
                after = unit;
                added++;
            }
        }

        return added;
    }

    private static int PatchTowerLamps(XElement root)
    {
        if (VerifyTowerLamps(root))
        {
            return 0;
        }

        XElement after = null;
        foreach (XElement compileUnit in root.Descendants().Where(IsCompileUnit))
        {
            if (FlgHasAccessNamed(compileUnit, "Q_Lamp_Run") &&
                FlgHasAccessNamed(compileUnit, "Q_Lamp_Stop") &&
                FlgHasAccessNamed(compileUnit, "Q_Lamp_Alarm"))
            {
                after = compileUnit;
            }
        }

        XElement template = FindSimpleContactCoilUnit(root);
        if (after == null || template == null)
        {
            Console.WriteLine("  找不到運轉燈網路或可複製的接觸點→線圈網。");
            return -1;
        }

        string[][] pairs =
        {
            new[] { "Q_Lamp_Run", "Q_Lamp_Tower_Run", "tower run", "塔燈運轉" },
            new[] { "Q_Lamp_Stop", "Q_Lamp_Tower_Stop", "tower stop", "塔燈停止" }
        };

        int nextId = MaxHexId(root) + 16;
        int added = 0;
        foreach (string[] pair in pairs)
        {
            if (root.Descendants().Any(e =>
                e.Name.LocalName == "Component" &&
                (string)e.Attribute("Name") == pair[1]))
            {
                continue;
            }

            XElement unit = new XElement(template);
            if (!RewriteContactCoilAccess(unit, pair[0], pair[1]))
            {
                return -1;
            }

            SetCompileUnitTitle(unit, pair[2], pair[3]);
            int first = FirstHexId(unit);
            ShiftIds(unit, nextId - first);
            nextId = MaxHexId(unit) + 16;
            after.AddAfterSelf(unit);
            after = unit;
            added++;
        }

        return added;
    }

    private static bool VerifyTowerLamps(XElement root)
    {
        return HasCoilNamed(root, "Q_Lamp_Tower_Run") &&
            HasCoilNamed(root, "Q_Lamp_Tower_Stop");
    }

    private static int PatchLoaderStopAndAir(XElement root)
    {
        int lamps = PatchLoaderStopLamps(root);
        int air = Patch24BAirSupply(root);
        if (lamps < 0 || air < 0)
        {
            return -1;
        }

        return lamps + air;
    }

    private static bool VerifyLoaderStopAndAir(XElement root)
    {
        return VerifyLoaderStopLamps(root) && Verify24BAirSupply(root);
    }

    private static int PatchLoaderStopLamps(XElement root)
    {
        if (VerifyLoaderStopLamps(root))
        {
            return 0;
        }

        XElement after = null;
        foreach (XElement compileUnit in root.Descendants().Where(IsCompileUnit))
        {
            if (compileUnit.Descendants().Any(e =>
                e.Name.LocalName == "CallInfo" &&
                (string)e.Attribute("Name") == "Bobbin_Loading"))
            {
                after = compileUnit;
            }
        }

        XElement template = FindSimpleContactCoilUnit(root);
        if (after == null || template == null)
        {
            Console.WriteLine("  找不到台車網路或可複製的接觸點→線圈網。");
            return -1;
        }

        int nextId = MaxHexId(root) + 16;
        int added = 0;
        foreach (string station in LoaderStations)
        {
            string dest = "Q_Lamp_" + station + "_Bobbin_Loader_Stop";
            if (HasCoilNamed(root, dest))
            {
                continue;
            }

            XElement unit = new XElement(template);
            if (!RewriteContactCoilAccess(unit, "Q_" + station + "_Bobbin_Loader_On", dest))
            {
                return -1;
            }

            NegateContact(unit);
            SetCompileUnitTitle(unit, station + " loader stop lamp", station + " 台車停止燈");
            int first = FirstHexId(unit);
            ShiftIds(unit, nextId - first);
            nextId = MaxHexId(unit) + 16;
            after.AddAfterSelf(unit);
            after = unit;
            added++;
        }

        return added;
    }

    private static bool VerifyLoaderStopLamps(XElement root)
    {
        foreach (string station in LoaderStations)
        {
            if (!HasCoilNamed(root, "Q_Lamp_" + station + "_Bobbin_Loader_Stop"))
            {
                return false;
            }
        }

        return true;
    }

    private static void NegateContact(XElement unit)
    {
        XElement contact = unit.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Part" &&
            (string)e.Attribute("Name") == "Contact");
        if (contact == null ||
            contact.Elements().Any(e => e.Name.LocalName == "Negated"))
        {
            return;
        }

        contact.Add(new XElement(contact.Name.Namespace + "Negated",
            new XAttribute("Name", "operand")));
    }

    private static int Patch24BAirSupply(XElement root)
    {
        if (Verify24BAirSupply(root))
        {
            return 0;
        }

        XElement flg = null;
        foreach (XElement net in root.Descendants().Where(e => e.Name.LocalName == "FlgNet"))
        {
            if (FlgHasAccessNamed(net, "Q_20B_Air_Supply") &&
                net.Descendants().Any(e =>
                    e.Name.LocalName == "CallInfo" &&
                    (string)e.Attribute("Name") == "Charge_Air") &&
                net.Descendants().Any(e =>
                    e.Name.LocalName == "Component" &&
                    (string)e.Attribute("Name") == "20B_charge_air"))
            {
                flg = net;
                break;
            }
        }

        if (flg == null)
        {
            Console.WriteLine("  找不到 20B Charge_Air 網路。");
            return -1;
        }

        XElement srcCall = flg.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Call" &&
            e.Descendants().Any(c =>
                c.Name.LocalName == "Component" &&
                (string)c.Attribute("Name") == "20B_charge_air"));
        if (srcCall == null)
        {
            Console.WriteLine("  找不到 20B_charge_air 呼叫。");
            return -1;
        }

        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        if (parts == null || wires == null)
        {
            return -1;
        }

        string srcCallUid = (string)srcCall.Attribute("UId");
        XElement srcContact = FindContactFeedingCall(flg, srcCallUid, "low");
        XElement srcLow = FindIdentAccessForPort(flg, srcContact == null ? null : (string)srcContact.Attribute("UId"), "operand");
        XElement srcTm = FindIdentAccessForPort(flg, srcCallUid, "Low_tm");
        XElement srcOvr = FindIdentAccessForPort(flg, srcCallUid, "ovrchg_tm");
        XElement srcAir = FindIdentAccessForPort(flg, srcCallUid, "charge_air");
        if (srcContact == null || srcLow == null || srcTm == null || srcOvr == null || srcAir == null)
        {
            Console.WriteLine("  20B Charge_Air 接線不完整，無法複製。");
            return -1;
        }

        int next = MaxUid(flg) + 1;
        XElement zig = new XElement(srcLow);
        zig.SetAttributeValue("UId", (next++).ToString());
        RenameComponent(zig, "Modbus_Zigbee_20B", "Modbus_Zigbee_24B");
        XElement tm = new XElement(srcTm);
        tm.SetAttributeValue("UId", (next++).ToString());
        XElement ovr = new XElement(srcOvr);
        ovr.SetAttributeValue("UId", (next++).ToString());
        XElement air = new XElement(srcAir);
        air.SetAttributeValue("UId", (next++).ToString());
        RenameComponent(air, "Q_20B_Air_Supply", "Q_24B_Air_Supply");

        XElement contact = new XElement(srcContact);
        contact.SetAttributeValue("UId", (next++).ToString());
        XElement call = new XElement(srcCall);
        call.SetAttributeValue("UId", (next++).ToString());
        XElement inst = call.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Instance");
        if (inst != null)
        {
            inst.SetAttributeValue("UId", (next++).ToString());
        }

        RenameComponent(call, "20B_charge_air", "24B_charge_air");

        srcAir.AddAfterSelf(zig);
        zig.AddAfterSelf(tm);
        tm.AddAfterSelf(ovr);
        ovr.AddAfterSelf(air);
        srcCall.AddAfterSelf(contact);
        contact.AddAfterSelf(call);

        XElement rail = wires.Elements().FirstOrDefault(e =>
            e.Elements().Any(c => c.Name.LocalName == "Powerrail"));
        XNamespace ns = flg.Name.Namespace;
        if (rail != null)
        {
            rail.Add(new XElement(ns + "NameCon",
                new XAttribute("UId", (string)call.Attribute("UId")),
                new XAttribute("Name", "en")));
            rail.Add(new XElement(ns + "NameCon",
                new XAttribute("UId", (string)contact.Attribute("UId")),
                new XAttribute("Name", "in")));
        }

        wires.Add(IdentToPort(ns, next++, zig, contact, "operand"));
        wires.Add(NameToName(ns, next++, contact, "out", call, "low"));
        wires.Add(IdentToPort(ns, next++, tm, call, "Low_tm"));
        wires.Add(IdentToPort(ns, next++, ovr, call, "ovrchg_tm"));
        wires.Add(IdentToPort(ns, next++, air, call, "charge_air"));
        return 1;
    }

    private static bool Verify24BAirSupply(XElement root)
    {
        foreach (XElement net in root.Descendants().Where(e => e.Name.LocalName == "FlgNet"))
        {
            bool air = net.Descendants().Any(e =>
                e.Name.LocalName == "Component" &&
                (string)e.Attribute("Name") == "Q_24B_Air_Supply");
            bool inst = net.Descendants().Any(e =>
                e.Name.LocalName == "Component" &&
                (string)e.Attribute("Name") == "24B_charge_air");
            bool call = net.Descendants().Any(e =>
                e.Name.LocalName == "CallInfo" &&
                (string)e.Attribute("Name") == "Charge_Air");
            if (air && inst && call)
            {
                return true;
            }
        }

        return false;
    }

    private static void RenameComponent(XElement root, string from, string to)
    {
        foreach (XElement comp in root.DescendantsAndSelf().Where(e =>
            e.Name.LocalName == "Component" &&
            (string)e.Attribute("Name") == from))
        {
            comp.SetAttributeValue("Name", to);
        }
    }

    private static XElement FindContactFeedingCall(XElement flg, string callUid, string port)
    {
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        if (wires == null || parts == null)
        {
            return null;
        }

        XElement wire = wires.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Wire" &&
            e.Elements().Any(c =>
                c.Name.LocalName == "NameCon" &&
                (string)c.Attribute("UId") == callUid &&
                (string)c.Attribute("Name") == port));
        if (wire == null)
        {
            return null;
        }

        XElement from = wire.Elements().FirstOrDefault(c =>
            c.Name.LocalName == "NameCon" &&
            (string)c.Attribute("Name") == "out");
        if (from == null)
        {
            return null;
        }

        string contactUid = (string)from.Attribute("UId");
        return parts.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Part" &&
            (string)e.Attribute("Name") == "Contact" &&
            (string)e.Attribute("UId") == contactUid);
    }

    private static XElement FindIdentAccessForPort(XElement flg, string targetUid, string port)
    {
        if (targetUid == null)
        {
            return null;
        }

        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        if (wires == null || parts == null)
        {
            return null;
        }

        XElement wire = wires.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Wire" &&
            e.Elements().Any(c =>
                c.Name.LocalName == "NameCon" &&
                (string)c.Attribute("UId") == targetUid &&
                (string)c.Attribute("Name") == port) &&
            e.Elements().Any(c => c.Name.LocalName == "IdentCon"));
        if (wire == null)
        {
            return null;
        }

        XElement ident = wire.Elements().FirstOrDefault(c => c.Name.LocalName == "IdentCon");
        string accessUid = ident == null ? null : (string)ident.Attribute("UId");
        return parts.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Access" &&
            (string)e.Attribute("UId") == accessUid);
    }

    private static XElement IdentToPort(
        XNamespace ns,
        int uid,
        XElement access,
        XElement target,
        string port)
    {
        return new XElement(ns + "Wire",
            new XAttribute("UId", uid.ToString()),
            new XElement(ns + "IdentCon",
                new XAttribute("UId", (string)access.Attribute("UId"))),
            new XElement(ns + "NameCon",
                new XAttribute("UId", (string)target.Attribute("UId")),
                new XAttribute("Name", port)));
    }

    private static XElement NameToName(
        XNamespace ns,
        int uid,
        XElement from,
        string fromPort,
        XElement to,
        string toPort)
    {
        return new XElement(ns + "Wire",
            new XAttribute("UId", uid.ToString()),
            new XElement(ns + "NameCon",
                new XAttribute("UId", (string)from.Attribute("UId")),
                new XAttribute("Name", fromPort)),
            new XElement(ns + "NameCon",
                new XAttribute("UId", (string)to.Attribute("UId")),
                new XAttribute("Name", toPort)));
    }

    private static XElement FindSimpleContactCoilUnit(XElement root)
    {
        foreach (XElement unit in root.Descendants().Where(IsCompileUnit))
        {
            List<XElement> parts = unit.Descendants()
                .Where(e => e.Name.LocalName == "Part")
                .ToList();
            if (parts.Count == 2 &&
                parts.Any(p => (string)p.Attribute("Name") == "Contact") &&
                parts.Any(p => (string)p.Attribute("Name") == "Coil") &&
                unit.Descendants().Count(e => e.Name.LocalName == "Access") == 2)
            {
                return unit;
            }
        }

        return null;
    }

    private static bool RewriteContactCoilAccess(XElement unit, string contactTag, string coilTag)
    {
        List<XElement> accesses = unit.Descendants()
            .Where(e => e.Name.LocalName == "Access")
            .ToList();
        if (accesses.Count < 2)
        {
            return false;
        }

        SetGlobalTagAccess(accesses[0], contactTag);
        SetGlobalTagAccess(accesses[1], coilTag);
        return true;
    }

    private static void SetGlobalTagAccess(XElement access, string tagName)
    {
        XNamespace ns = access.Name.Namespace;
        access.SetAttributeValue("Scope", "GlobalVariable");
        access.RemoveNodes();
        access.Add(new XElement(ns + "Symbol",
            new XElement(ns + "Component", new XAttribute("Name", tagName))));
    }

    private static void SetCompileUnitTitle(XElement unit, string en, string zhTw)
    {
        foreach (XElement title in unit.Descendants().Where(e =>
            e.Name.LocalName == "MultilingualText" &&
            (string)e.Attribute("CompositionName") == "Title"))
        {
            foreach (XElement item in title.Descendants().Where(e =>
                e.Name.LocalName == "MultilingualTextItem"))
            {
                XElement culture = item.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Culture");
                XElement text = item.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Text");
                if (culture == null || text == null)
                {
                    continue;
                }

                if (culture.Value == "en-US")
                {
                    text.Value = en;
                }
                else if (culture.Value == "zh-TW")
                {
                    text.Value = zhTw;
                }
            }
        }
    }

    private static XElement FindClutchValveNet(XElement root)
    {
        foreach (XElement flg in root.Descendants().Where(e => e.Name.LocalName == "FlgNet"))
        {
            if (FlgHasAccessNamed(flg, "Q_6B_Pos_Clutch_close") &&
                FlgHasAccessNamed(flg, "Q_12B_Pos_Clutch_close") &&
                FlgHasAccessNamed(flg, "Q_20B_Pos_Clutch_close") &&
                FlgHasAccessNamed(flg, "Q_24B_Pos_Clutch_close") &&
                flg.Descendants().Any(e =>
                    e.Name.LocalName == "Part" &&
                    (string)e.Attribute("Name") == "Coil"))
            {
                return flg;
            }
        }

        return null;
    }

    private static bool FlgHasAccessNamed(XElement flg, string name)
    {
        return FindAccessNamed(flg, name) != null;
    }

    private static XElement FindAccessNamed(XElement flg, string name)
    {
        return flg.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Access" &&
            string.Equals(LastComponentName(e), name, StringComparison.Ordinal));
    }

    private static XElement FindOperandWire(XElement wires, string accessUid)
    {
        return wires.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Wire" &&
            e.Elements().Any(c =>
                c.Name.LocalName == "IdentCon" &&
                (string)c.Attribute("UId") == accessUid) &&
            e.Elements().Any(c =>
                c.Name.LocalName == "NameCon" &&
                (string)c.Attribute("Name") == "operand"));
    }

    private static XElement FindCoilInputWire(XElement wires, string coilUid)
    {
        return wires.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Wire" &&
            e.Elements().Any(c =>
                c.Name.LocalName == "NameCon" &&
                (string)c.Attribute("UId") == coilUid &&
                (string)c.Attribute("Name") == "in"));
    }

    private static bool VerifyPosValve2(XElement root)
    {
        string[] kinds = { "Pos_Pin_Lock", "Pos_Clutch_close" };
        foreach (string kind in kinds)
        {
            foreach (string station in PosValveStations)
            {
                string dest = "Q_" + station + "_" + kind + "_2";
                if (!root.Descendants().Any(e =>
                    e.Name.LocalName == "Component" &&
                    (string)e.Attribute("Name") == dest))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int MaxHexId(XElement root)
    {
        int max = 0;
        foreach (XElement el in root.DescendantsAndSelf())
        {
            XAttribute id = el.Attribute("ID");
            int n;
            if (id != null &&
                int.TryParse(id.Value, System.Globalization.NumberStyles.HexNumber, null, out n) &&
                n > max)
            {
                max = n;
            }
        }

        return max;
    }

    private static int FirstHexId(XElement root)
    {
        XAttribute id = root.Attribute("ID");
        int n;
        if (id != null &&
            int.TryParse(id.Value, System.Globalization.NumberStyles.HexNumber, null, out n))
        {
            return n;
        }

        return 0;
    }

    private static int EnsureOptDieCompression24B(PlcSoftware main, string dir)
    {
        PlcBlock opt = FindBlock(main, "opt");
        if (opt == null)
        {
            Console.WriteLine("找不到 opt DB。");
            return -1;
        }

        string sourcePath = Path.Combine(dir, "opt-before.xml");
        string importPath = Path.Combine(dir, "opt.xml");
        if (!ExportBlockXmlQuiet(opt, sourcePath))
        {
            return -1;
        }

        XDocument doc = XDocument.Load(sourcePath);
        XElement compression = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Member" && (string)e.Attribute("Name") == "die_compression");
        if (compression == null)
        {
            Console.WriteLine("opt 找不到 die_compression。");
            return -1;
        }

        if (compression.Elements().Any(e =>
            e.Name.LocalName == "Member" && (string)e.Attribute("Name") == "24B"))
        {
            return 0;
        }

        XElement source = compression.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Member" && (string)e.Attribute("Name") == "20B");
        if (source == null)
        {
            Console.WriteLine("opt.die_compression 找不到 20B 範本。");
            return -1;
        }

        XElement clone = new XElement(source);
        clone.SetAttributeValue("Name", "24B");
        source.AddAfterSelf(clone);
        doc.Save(importPath);
        TryImportBlockFile(main, opt, importPath);
        Console.WriteLine("  opt.die_compression.24B ← 20B（初值 " +
            (clone.Elements().FirstOrDefault(e => e.Name.LocalName == "StartValue") == null
                ? "未設定"
                : clone.Elements().First(e => e.Name.LocalName == "StartValue").Value) + "）。");
        return 1;
    }

    private static bool SetCyclicInterval(PlcBlock block)
    {
        try
        {
            block.SetAttribute("CyclicTime", 10);
            Console.WriteLine("  OB30 CyclicTime = 10 ms");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  OB30 CyclicTime 設定失敗：" + Flatten(ex));
            return false;
        }
    }

    private static int PatchCyclicTighten24B(XElement root)
    {
        XElement flg = FindFlgNetWithComponent(root, "tighten_wire_20B");
        if (flg == null)
        {
            Console.WriteLine("  找不到 1B 緊線網路。");
            return -1;
        }

        if (HasNamedComponent(flg, "tighten_wire_24B"))
        {
            return VerifyCyclicTighten24B(flg) ? 0 : -1;
        }

        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        XElement sourceAccess = parts == null ? null : parts.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Access" && LastComponentName(e) == "tighten_wire_20B");
        XElement orPart = parts == null ? null : parts.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Part" && (string)e.Attribute("Name") == "O");
        if (parts == null || wires == null || sourceAccess == null || orPart == null)
        {
            Console.WriteLine("  1B 緊線網路結構不完整。");
            return -1;
        }

        string sourceAccessUid = (string)sourceAccess.Attribute("UId");
        XElement sourceInputWire = wires.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Wire" &&
            e.Elements().Any(c => c.Name.LocalName == "IdentCon" &&
                (string)c.Attribute("UId") == sourceAccessUid) &&
            e.Elements().Any(c => c.Name.LocalName == "NameCon" &&
                (string)c.Attribute("Name") == "operand"));
        if (sourceInputWire == null)
        {
            Console.WriteLine("  找不到 20B 緊線輸入接線。");
            return -1;
        }

        XElement sourceInputCon = sourceInputWire.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "NameCon" && (string)e.Attribute("Name") == "operand");
        string sourceContactUid = sourceInputCon == null ? null : (string)sourceInputCon.Attribute("UId");
        XElement sourceContact = parts.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Part" &&
            (string)e.Attribute("Name") == "Contact" &&
            (string)e.Attribute("UId") == sourceContactUid);
        string orUid = (string)orPart.Attribute("UId");
        XElement sourceOutputWire = wires.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Wire" &&
            e.Elements().Any(c => c.Name.LocalName == "NameCon" &&
                (string)c.Attribute("UId") == sourceContactUid &&
                (string)c.Attribute("Name") == "out") &&
            e.Elements().Any(c => c.Name.LocalName == "NameCon" &&
                (string)c.Attribute("UId") == orUid &&
                (string)c.Attribute("Name") == "in3"));
        XElement cardinality = orPart.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "TemplateValue" &&
            (string)e.Attribute("Name") == "Card");
        if (sourceContact == null || sourceOutputWire == null || cardinality == null ||
            cardinality.Value != "3")
        {
            Console.WriteLine("  20B 緊線 OR 接線／Cardinality 不符預期。");
            return -1;
        }

        int nextUid = MaxUid(root) + 1;
        XElement newAccess = new XElement(sourceAccess);
        AssignFreshUids(newAccess, ref nextUid);
        ReplaceComponentName(newAccess, "tighten_wire_20B", "tighten_wire_24B");
        string newAccessUid = (string)newAccess.Attribute("UId");

        XElement newContact = new XElement(sourceContact);
        newContact.SetAttributeValue("UId", nextUid.ToString());
        string newContactUid = nextUid.ToString();
        nextUid++;

        XElement newInputWire = new XElement(sourceInputWire);
        newInputWire.SetAttributeValue("UId", nextUid.ToString());
        nextUid++;
        ReplaceConnectorUid(newInputWire, "IdentCon", sourceAccessUid, newAccessUid);
        ReplaceConnectorUid(newInputWire, "NameCon", sourceContactUid, newContactUid);

        XElement newOutputWire = new XElement(sourceOutputWire);
        newOutputWire.SetAttributeValue("UId", nextUid.ToString());
        nextUid++;
        ReplaceConnectorUid(newOutputWire, "NameCon", sourceContactUid, newContactUid);
        XElement orInput = newOutputWire.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "NameCon" &&
            (string)e.Attribute("UId") == orUid &&
            (string)e.Attribute("Name") == "in3");
        if (orInput == null)
        {
            return -1;
        }

        orInput.SetAttributeValue("Name", "in4");
        XElement powerrail = wires.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Wire" &&
            e.Elements().Any(c => c.Name.LocalName == "Powerrail"));
        if (powerrail == null)
        {
            Console.WriteLine("  找不到 1B 緊線網路的 Powerrail。");
            return -1;
        }

        XNamespace ns = flg.Name.Namespace;
        XElement sourcePowerInput = powerrail.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "NameCon" &&
            (string)e.Attribute("UId") == sourceContactUid &&
            (string)e.Attribute("Name") == "in");
        if (sourcePowerInput == null)
        {
            Console.WriteLine("  找不到 20B 緊線的 Powerrail 接線。");
            return -1;
        }

        XElement newPowerInput = new XElement(ns + "NameCon",
            new XAttribute("UId", newContactUid),
            new XAttribute("Name", "in"));
        cardinality.Value = "4";
        sourceAccess.AddAfterSelf(newAccess);
        sourceContact.AddAfterSelf(newContact);
        sourceInputWire.AddAfterSelf(newInputWire);
        newInputWire.AddAfterSelf(newOutputWire);
        sourcePowerInput.AddAfterSelf(newPowerInput);
        return VerifyCyclicTighten24B(flg) ? 1 : -1;
    }

    private static int PatchCyclicLineSpeed24B(XElement root)
    {
        XElement structured = FindCyclicLineSpeedStructuredText(root);
        if (structured == null)
        {
            Console.WriteLine("  找不到 1B 線速 SCL 網路。");
            return -1;
        }

        bool has24Enable = structured.Descendants()
            .Any(e => e.Name.LocalName == "Access" && LastComponentName(e) == "24B_enable");
        bool has24Compression = structured.Descendants()
            .Any(e => e.Name.LocalName == "Access" &&
                LastComponentName(e) == "24B" && HasNamedComponent(e, "die_compression"));
        int elsifCount = structured.Elements().Count(e =>
            e.Name.LocalName == "Token" && (string)e.Attribute("Text") == "ELSIF");
        if (has24Enable || has24Compression || elsifCount > 1)
        {
            return VerifyCyclicLineSpeed24B(structured) ? 0 : -1;
        }

        XElement firstThen = FindDirectToken(structured, null, "THEN");
        XElement firstElsif = structured.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Token" && (string)e.Attribute("Text") == "ELSIF");
        XElement elseToken = structured.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Token" && (string)e.Attribute("Text") == "ELSE");
        if (firstThen == null || firstElsif == null || elseToken == null)
        {
            Console.WriteLine("  1B 線速 SCL IF／ELSIF／ELSE 結構不完整。");
            return -1;
        }

        XElement first20Enable = FindDirectAccessBefore(structured, null, firstThen, "20B_enable");
        XElement firstInstruction = FindInstructionBefore(structured, firstThen, firstElsif);
        XElement compression20 = firstInstruction == null ? null :
            firstInstruction.Descendants().FirstOrDefault(e =>
                e.Name.LocalName == "Access" &&
                LastComponentName(e) == "20B" &&
                HasNamedComponent(e, "die_compression"));
        if (first20Enable == null || firstInstruction == null || compression20 == null)
        {
            Console.WriteLine("  找不到 20B 啟用／壓縮比樣板。");
            return -1;
        }

        // Preserve the original 12B-only branch before turning the current
        // ELSIF into the new 12B+20B branch.
        List<XElement> thirdBranch = CloneDirectRange(structured, firstElsif, elseToken);
        if (thirdBranch.Count == 0)
        {
            return -1;
        }

        int nextUid = MaxUid(root) + 1;
        AppendSclAndTerm(firstThen, first20Enable, "20B_enable", "24B_enable", ref nextUid);
        AppendSclFactor(firstInstruction, compression20, "20B", "24B", ref nextUid);

        XElement secondThen = FindDirectToken(structured, firstElsif, "THEN");
        XElement secondInstruction = FindInstructionBefore(structured, secondThen, elseToken);
        if (secondThen == null || secondInstruction == null)
        {
            Console.WriteLine("  找不到 12B 線速分支。");
            return -1;
        }

        AppendSclAndTerm(secondThen, first20Enable, "20B_enable", "20B_enable", ref nextUid);
        AppendSclFactor(secondInstruction, compression20, "20B", "20B", ref nextUid);

        foreach (XElement element in thirdBranch)
        {
            AssignFreshUids(element, ref nextUid);
            elseToken.AddBeforeSelf(element);
        }

        return VerifyCyclicLineSpeed24B(structured) ? 1 : -1;
    }

    private static bool VerifyCyclic24B(XElement root)
    {
        XElement flg = FindFlgNetWithComponent(root, "tighten_wire_20B");
        XElement structured = FindCyclicLineSpeedStructuredText(root);
        return flg != null &&
            structured != null &&
            VerifyCyclicTighten24B(flg) &&
            VerifyCyclicLineSpeed24B(structured);
    }

    private static bool VerifyCyclicTighten24B(XElement flg)
    {
        XElement orPart = flg.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Part" && (string)e.Attribute("Name") == "O");
        XElement access = flg.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Access" && LastComponentName(e) == "tighten_wire_24B");
        if (orPart == null || access == null)
        {
            return false;
        }

        XElement cardinality = orPart.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "TemplateValue" &&
            (string)e.Attribute("Name") == "Card");
        string orUid = (string)orPart.Attribute("UId");
        string accessUid = (string)access.Attribute("UId");
        XElement inputWire = flg.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Wire" &&
            e.Elements().Any(c => c.Name.LocalName == "IdentCon" &&
                (string)c.Attribute("UId") == accessUid) &&
            e.Elements().Any(c => c.Name.LocalName == "NameCon" &&
                (string)c.Attribute("Name") == "operand"));
        XElement operand = inputWire == null ? null : inputWire.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "NameCon" && (string)e.Attribute("Name") == "operand");
        string contactUid = operand == null ? null : (string)operand.Attribute("UId");
        bool powered = flg.Descendants().Any(e =>
            e.Name.LocalName == "Wire" &&
            e.Elements().Any(c => c.Name.LocalName == "Powerrail") &&
            e.Elements().Any(c => c.Name.LocalName == "NameCon" &&
                (string)c.Attribute("UId") == contactUid &&
                (string)c.Attribute("Name") == "in"));
        return cardinality != null &&
            cardinality.Value == "4" &&
            contactUid != null &&
            powered &&
            flg.Descendants().Any(e =>
                e.Name.LocalName == "NameCon" &&
                (string)e.Attribute("UId") == orUid &&
                (string)e.Attribute("Name") == "in4");
    }

    private static bool VerifyCyclicLineSpeed24B(XElement structured)
    {
        int elsifCount = structured.Elements().Count(e =>
            e.Name.LocalName == "Token" && (string)e.Attribute("Text") == "ELSIF");
        return elsifCount == 2 &&
            structured.Descendants().Any(e =>
                e.Name.LocalName == "Access" && LastComponentName(e) == "24B_enable") &&
            structured.Descendants().Any(e =>
                e.Name.LocalName == "Access" &&
                LastComponentName(e) == "24B" &&
                HasNamedComponent(e, "die_compression"));
    }

    private static XElement FindCyclicLineSpeedStructuredText(XElement root)
    {
        return root.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "StructuredText" &&
            e.Descendants().Any(child =>
                child.Name.LocalName == "Access" && LastComponentName(child) == "1B_line_speed") &&
            e.Descendants().Any(child =>
                child.Name.LocalName == "Access" && LastComponentName(child) == "20B_enable"));
    }

    private static XElement FindDirectToken(XElement structured, XElement startAfter, string text)
    {
        bool inRange = startAfter == null;
        foreach (XElement child in structured.Elements())
        {
            if (child == startAfter)
            {
                inRange = true;
                continue;
            }

            if (inRange &&
                child.Name.LocalName == "Token" &&
                (string)child.Attribute("Text") == text)
            {
                return child;
            }
        }

        return null;
    }

    private static XElement FindDirectAccessBefore(
        XElement structured,
        XElement startAfter,
        XElement endBefore,
        string lastComponent)
    {
        bool inRange = startAfter == null;
        foreach (XElement child in structured.Elements())
        {
            if (child == startAfter)
            {
                inRange = true;
                continue;
            }

            if (child == endBefore)
            {
                break;
            }

            if (inRange &&
                child.Name.LocalName == "Access" &&
                LastComponentName(child) == lastComponent)
            {
                return child;
            }
        }

        return null;
    }

    private static XElement FindInstructionBefore(
        XElement structured,
        XElement startAfter,
        XElement endBefore)
    {
        bool inRange = false;
        foreach (XElement child in structured.Elements())
        {
            if (child == startAfter)
            {
                inRange = true;
                continue;
            }

            if (child == endBefore)
            {
                break;
            }

            if (!inRange)
            {
                continue;
            }

            XElement instruction = child.Descendants().FirstOrDefault(e =>
                e.Name.LocalName == "Instruction" &&
                (string)e.Attribute("Name") == "REAL_TO_UINT");
            if (instruction != null)
            {
                return instruction;
            }
        }

        return null;
    }

    private static List<XElement> CloneDirectRange(
        XElement parent,
        XElement startInclusive,
        XElement endExclusive)
    {
        List<XElement> result = new List<XElement>();
        bool inRange = false;
        foreach (XElement child in parent.Elements())
        {
            if (child == startInclusive)
            {
                inRange = true;
            }

            if (child == endExclusive)
            {
                break;
            }

            if (inRange)
            {
                result.Add(new XElement(child));
            }
        }

        return result;
    }

    private static void AppendSclAndTerm(
        XElement thenToken,
        XElement sourceAccess,
        string oldComponent,
        string newComponent,
        ref int nextUid)
    {
        XNamespace ns = thenToken.Name.Namespace;
        XElement blank1 = new XElement(ns + "Blank", new XAttribute("Num", "1"));
        XElement andToken = new XElement(ns + "Token", new XAttribute("Text", "AND"));
        XElement blank2 = new XElement(ns + "Blank", new XAttribute("Num", "1"));
        XElement access = new XElement(sourceAccess);
        ReplaceComponentName(access, oldComponent, newComponent);
        AssignFreshUids(blank1, ref nextUid);
        AssignFreshUids(andToken, ref nextUid);
        AssignFreshUids(blank2, ref nextUid);
        AssignFreshUids(access, ref nextUid);
        thenToken.AddBeforeSelf(blank1);
        thenToken.AddBeforeSelf(andToken);
        thenToken.AddBeforeSelf(blank2);
        thenToken.AddBeforeSelf(access);
    }

    private static void AppendSclFactor(
        XElement instruction,
        XElement sourceAccess,
        string oldComponent,
        string newComponent,
        ref int nextUid)
    {
        XElement parameter = instruction.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "NamelessParameter");
        if (parameter == null)
        {
            throw new InvalidOperationException("找不到 REAL_TO_UINT 的參數。");
        }

        XNamespace ns = instruction.Name.Namespace;
        XElement blank1 = new XElement(ns + "Blank", new XAttribute("Num", "1"));
        XElement multiply = new XElement(ns + "Token", new XAttribute("Text", "*"));
        XElement blank2 = new XElement(ns + "Blank", new XAttribute("Num", "1"));
        XElement access = new XElement(sourceAccess);
        ReplaceComponentName(access, oldComponent, newComponent);
        AssignFreshUids(blank1, ref nextUid);
        AssignFreshUids(multiply, ref nextUid);
        AssignFreshUids(blank2, ref nextUid);
        AssignFreshUids(access, ref nextUid);
        parameter.Add(blank1);
        parameter.Add(multiply);
        parameter.Add(blank2);
        parameter.Add(access);
    }

    private static void AssignFreshUids(XElement element, ref int nextUid)
    {
        foreach (XElement child in element.DescendantsAndSelf())
        {
            child.SetAttributeValue("UId", nextUid.ToString());
            nextUid++;
        }
    }

    private static string LastComponentName(XElement element)
    {
        XElement last = element.Descendants()
            .Where(e => e.Name.LocalName == "Component")
            .LastOrDefault();
        return last == null ? null : (string)last.Attribute("Name");
    }

    private static void ReplaceComponentName(XElement element, string oldName, string newName)
    {
        foreach (XElement component in element.DescendantsAndSelf()
            .Where(e => e.Name.LocalName == "Component" &&
                string.Equals((string)e.Attribute("Name"), oldName, StringComparison.Ordinal)))
        {
            component.SetAttributeValue("Name", newName);
        }
    }

    private static void ReplaceConnectorUid(
        XElement wire,
        string connectorKind,
        string oldUid,
        string newUid)
    {
        foreach (XElement connector in wire.Elements().Where(e =>
            e.Name.LocalName == connectorKind &&
            (string)e.Attribute("UId") == oldUid))
        {
            connector.SetAttributeValue("UId", newUid);
        }
    }

    private static int PatchSection4(XElement root)
    {
        int changed = 0;
        foreach (XElement flg in root.Descendants().Where(e => e.Name.LocalName == "FlgNet").ToList())
        {
            bool hasCall = flg.Descendants().Any(e =>
                e.Name.LocalName == "CallInfo" &&
                string.Equals((string)e.Attribute("Name"), "Rotor_Positioning_5-2V", StringComparison.Ordinal));
            if (!hasCall)
            {
                continue;
            }

            string station = null;
            foreach (XElement comp in flg.Descendants().Where(e => e.Name.LocalName == "Component"))
            {
                string name = (string)comp.Attribute("Name");
                if (name == null)
                {
                    continue;
                }

                if (name.StartsWith("I_Snr_", StringComparison.Ordinal) &&
                    name.EndsWith("_Section1", StringComparison.Ordinal))
                {
                    station = name.Substring("I_Snr_".Length);
                    station = station.Substring(0, station.Length - "_Section1".Length);
                    break;
                }
            }

            if (string.IsNullOrEmpty(station))
            {
                continue;
            }

            XElement call = flg.Descendants()
                .First(e => e.Name.LocalName == "CallInfo" &&
                    string.Equals((string)e.Attribute("Name"), "Rotor_Positioning_5-2V", StringComparison.Ordinal))
                .Ancestors()
                .First(e => e.Name.LocalName == "Call");
            string callUid = (string)call.Attribute("UId");
            Dictionary<string, XElement> access = new Dictionary<string, XElement>();
            foreach (XElement acc in flg.Descendants().Where(e => e.Name.LocalName == "Access"))
            {
                string uid = (string)acc.Attribute("UId");
                if (uid != null)
                {
                    access[uid] = acc;
                }
            }

            foreach (XElement wire in flg.Descendants().Where(e => e.Name.LocalName == "Wire"))
            {
                XElement ident = wire.Elements().FirstOrDefault(e => e.Name.LocalName == "IdentCon");
                XElement pin = wire.Elements().FirstOrDefault(e =>
                    e.Name.LocalName == "NameCon" &&
                    string.Equals((string)e.Attribute("UId"), callUid, StringComparison.Ordinal));
                if (ident == null || pin == null)
                {
                    continue;
                }

                string pinName = (string)pin.Attribute("Name");
                string accUid = (string)ident.Attribute("UId");
                XElement acc;
                if (accUid == null || !access.TryGetValue(accUid, out acc))
                {
                    continue;
                }

                if (pinName == "section_no")
                {
                    foreach (XElement value in acc.Descendants().Where(e => e.Name.LocalName == "ConstantValue"))
                    {
                        if (value.Value == "3")
                        {
                            value.Value = "4";
                            changed++;
                            Console.WriteLine("  " + station + " section_no 3→4");
                        }
                    }
                }
                else if (pinName == "pos_snr_4" &&
                    string.Equals((string)acc.Attribute("Scope"), "LiteralConstant", StringComparison.Ordinal))
                {
                    XNamespace ns = acc.Name.Namespace;
                    acc.SetAttributeValue("Scope", "GlobalVariable");
                    acc.RemoveNodes();
                    acc.Add(new XElement(ns + "Symbol",
                        new XElement(ns + "Component",
                            new XAttribute("Name", "I_Snr_" + station + "_Section4"))));
                    changed++;
                    Console.WriteLine("  " + station + " pos_snr_4 ← I_Snr_" + station + "_Section4");
                }
            }
        }

        return changed;
    }

    public static int Extend20BEvents(TiaPortal portal, Project project)
    {
        Console.WriteLine("20B 斷線／張力／Pico 事件補到 #1–#20，並整理警報順序。");
        return ExtendNumberedEvents(
            portal,
            project,
            "20B",
            19,
            20,
            true,
            Path.Combine(
                Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
                "Practice", "io-merge", "station-sync", "events-20b"));
    }

    public static int Extend24BEvents(TiaPortal portal, Project project)
    {
        Console.WriteLine("24B 斷線／張力／Pico 事件補到 #1–#24。");
        return ExtendNumberedEvents(
            portal,
            project,
            "24B",
            19,
            24,
            false,
            Path.Combine(
                Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
                "Practice", "io-merge", "station-sync", "events-24b"));
    }

    private static int ExtendNumberedEvents(
        TiaPortal portal,
        Project project,
        string station,
        int fromN,
        int toN,
        bool sortAlarms,
        string dir)
    {
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        PlcTagTable events = FindTable(main, "Events");
        if (events == null)
        {
            Console.WriteLine("找不到 Events Tag 表。");
            return 1;
        }

        string[] families = new[]
        {
            "AErr_" + station + "_Wire_Broken",
            "AErr_" + station + "_Tension_Drive_Fault",
            "AErr_" + station + "_Pico_Comm_Error"
        };

        HashSet<int> used = CollectAllMemoryBits(main);
        int bit = NextFreeBit(used, 200 * 8, 239 * 8 + 7);
        if (bit < 0)
        {
            bit = NextFreeMemoryBit(main);
        }

        int created = 0;
        foreach (string family in families)
        {
            for (int n = fromN; n <= toN; n++)
            {
                string name = family + "_#" + n;
                if (FindTag(main, name) != null)
                {
                    Console.WriteLine("  已有 " + name);
                    continue;
                }

                if (used.Contains(bit))
                {
                    int next = NextFreeBit(used, bit, 239 * 8 + 7);
                    bit = next >= 0 ? next : NextFreeMemoryBit(main);
                }

                string address = "%M" + (bit / 8) + "." + (bit % 8);
                used.Add(bit);
                bit++;
                try
                {
                    events.Tags.Create(name, "Bool", address);
                    Console.WriteLine("  Tag " + name + " " + address);
                    created++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  Tag 失敗 " + name + "：" + Flatten(ex));
                    return 1;
                }
            }
        }

        project.Save();
        Console.WriteLine("已存（新 Tag " + created + "）。");

        PlcBlock block = FindBlock(main, "Event_Control");
        if (block == null)
        {
            Console.WriteLine("找不到 Event_Control。");
            return 1;
        }

        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Event_Control.xml");
        if (!ExportBlockXmlQuiet(block, path))
        {
            return 1;
        }

        XDocument doc = XDocument.Load(path);
        int added = AppendNumberedEventAssignments(doc.Root, families, fromN - 1, fromN, toN);
        Console.WriteLine("  Event_Control 加了 " + added + " 條 SCL。");
        int sortedScl = 0;
        if (sortAlarms)
        {
            sortedScl = SortNumberedEventAssignments(doc.Root);
            Console.WriteLine("  Event_Control 編號句重排 " + sortedScl + " 條。");
        }

        if (added > 0 || sortedScl > 0)
        {
            doc.Save(path);
            TryImportBlockFile(main, block, path);
        }

        project.Save();
        Console.WriteLine("已存（匯入 Event_Control）。");

        if (sortAlarms)
        {
            SortEventTable(main, dir);
            project.Save();
            Console.WriteLine("已存（Events 表重排）。");
        }

        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return errors == 0 ? 0 : 1;
    }

    private static int Append24BEventAssignments(XElement root, string[] families)
    {
        return AppendNumberedEventAssignments(root, families, 18, 19, 24);
    }

    private static int AppendNumberedEventAssignments(
        XElement root,
        string[] families,
        int lastN,
        int fromN,
        int toN)
    {
        int added = 0;
        int nextUid = MaxUid(root) + 1;
        string oldIndex = (lastN - 1).ToString();
        foreach (string family in families)
        {
            string last = family + "_#" + lastN;
            foreach (XElement st in root.Descendants().Where(e => e.Name.LocalName == "StructuredText").ToList())
            {
                XElement marker = st.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Component" &&
                        string.Equals((string)e.Attribute("Name"), last, StringComparison.Ordinal));
                if (marker == null)
                {
                    continue;
                }

                XElement start = StructuredTextChild(marker);
                if (start == null)
                {
                    continue;
                }

                List<XElement> template = CollectSclStatement(start);
                if (template == null || template.Count == 0)
                {
                    Console.WriteLine("  找不到 " + last + " 的 SCL 句。");
                    continue;
                }

                XElement insertAfter = template[template.Count - 1];
                for (int n = fromN; n <= toN; n++)
                {
                    string already = family + "_#" + n;
                    if (st.Descendants().Any(e => e.Name.LocalName == "Component" &&
                        string.Equals((string)e.Attribute("Name"), already, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    List<XElement> clones = CloneSclStatement(
                        template, last, already, oldIndex, (n - 1).ToString(), ref nextUid);
                    foreach (XElement clone in clones)
                    {
                        insertAfter.AddAfterSelf(clone);
                        insertAfter = clone;
                    }

                    added++;
                }
            }
        }

        return added;
    }

    private static readonly Regex NumberedAerrRe = new Regex(
        @"^AErr_(6B|12B|20B|24B)_(Wire_Broken|Tension_Drive_Fault|Pico_Comm_Error)_#(\d+)$",
        RegexOptions.Compiled);

    private static int SortNumberedEventAssignments(XElement root)
    {
        int total = 0;
        foreach (XElement st in root.Descendants().Where(e => e.Name.LocalName == "StructuredText").ToList())
        {
            List<List<XElement>> statements = new List<List<XElement>>();
            HashSet<XElement> taken = new HashSet<XElement>();
            foreach (XElement comp in st.Descendants().Where(e => e.Name.LocalName == "Component").ToList())
            {
                string name = (string)comp.Attribute("Name");
                if (name == null || !NumberedAerrRe.IsMatch(name))
                {
                    continue;
                }

                XElement start = StructuredTextChild(comp);
                if (start == null || taken.Contains(start))
                {
                    continue;
                }

                List<XElement> stmt = CollectSclStatement(start);
                if (stmt == null || stmt.Count == 0)
                {
                    continue;
                }

                foreach (XElement el in stmt)
                {
                    taken.Add(el);
                }

                statements.Add(stmt);
            }

            if (statements.Count < 2)
            {
                continue;
            }

            List<XElement> children = st.Elements().ToList();
            int firstIdx = int.MaxValue;
            foreach (List<XElement> stmt in statements)
            {
                int idx = children.IndexOf(stmt[0]);
                if (idx >= 0 && idx < firstIdx)
                {
                    firstIdx = idx;
                }
            }

            XElement before = firstIdx > 0 && firstIdx < children.Count
                ? children[firstIdx - 1]
                : null;

            statements.Sort(CompareNumberedStatement);
            foreach (List<XElement> stmt in statements)
            {
                foreach (XElement el in stmt)
                {
                    el.Remove();
                }
            }

            if (before != null && before.Parent == st)
            {
                XElement cursor = before;
                foreach (List<XElement> stmt in statements)
                {
                    foreach (XElement el in stmt)
                    {
                        cursor.AddAfterSelf(el);
                        cursor = el;
                    }
                }
            }
            else
            {
                XElement firstChild = st.Elements().FirstOrDefault();
                XElement cursor = null;
                foreach (List<XElement> stmt in statements)
                {
                    foreach (XElement el in stmt)
                    {
                        if (cursor == null)
                        {
                            if (firstChild == null)
                            {
                                st.Add(el);
                            }
                            else
                            {
                                firstChild.AddBeforeSelf(el);
                            }

                            cursor = el;
                        }
                        else
                        {
                            cursor.AddAfterSelf(el);
                            cursor = el;
                        }
                    }
                }
            }

            total += statements.Count;
        }

        return total;
    }

    private static int CompareNumberedStatement(List<XElement> a, List<XElement> b)
    {
        return CompareEventNames(NumberedStatementName(a), NumberedStatementName(b));
    }

    private static string NumberedStatementName(List<XElement> stmt)
    {
        foreach (XElement el in stmt)
        {
            foreach (XElement comp in el.DescendantsAndSelf().Where(e => e.Name.LocalName == "Component"))
            {
                string name = (string)comp.Attribute("Name");
                if (name != null && NumberedAerrRe.IsMatch(name))
                {
                    return name;
                }
            }
        }

        return "";
    }

    private static void SortEventTable(PlcSoftware main, string dir)
    {
        PlcTagTable events = FindTable(main, "Events");
        if (events == null)
        {
            return;
        }

        string path = Path.Combine(dir, "Events.xml");
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            events.Export(new FileInfo(path), ExportOptions.WithDefaults);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Events 匯出失敗：" + Flatten(ex));
            return;
        }

        XDocument doc = XDocument.Load(path);
        XElement tableEl = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "SW.Tags.PlcTagTable" ||
                e.Name.LocalName.EndsWith("PlcTagTable", StringComparison.Ordinal));
        XElement objectList = tableEl == null
            ? null
            : tableEl.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
        if (objectList == null)
        {
            Console.WriteLine("  Events XML 沒有 ObjectList。");
            return;
        }

        List<XElement> tags = objectList.Elements()
            .Where(e => e.Name.LocalName == "SW.Tags.PlcTag" ||
                (e.Name.LocalName.EndsWith("PlcTag", StringComparison.Ordinal) &&
                 !e.Name.LocalName.EndsWith("PlcTagTable", StringComparison.Ordinal)))
            .ToList();
        if (tags.Count < 2)
        {
            return;
        }

        tags.Sort((a, b) => CompareEventNames(PlcTagName(a), PlcTagName(b)));
        foreach (XElement tag in tags)
        {
            tag.Remove();
        }

        foreach (XElement tag in tags)
        {
            objectList.Add(tag);
        }

        doc.Save(path);
        try
        {
            main.TagTableGroup.TagTables.Import(new FileInfo(path), ImportOptions.Override);
            Console.WriteLine("  Events 表已依 6B→12B→20B→24B 重排（" + tags.Count + "）。");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Events 匯入失敗：" + Flatten(ex));
        }
    }

    private static string PlcTagName(XElement tag)
    {
        XElement attrs = tag.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
        if (attrs == null)
        {
            return "";
        }

        XElement name = attrs.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
        return name == null ? "" : name.Value;
    }

    private static int CompareEventNames(string a, string b)
    {
        int cmp = EventStationRank(a).CompareTo(EventStationRank(b));
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = EventClassRank(a).CompareTo(EventClassRank(b));
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = EventFamilyRank(a).CompareTo(EventFamilyRank(b));
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = EventNumber(a).CompareTo(EventNumber(b));
        if (cmp != 0)
        {
            return cmp;
        }

        return string.CompareOrdinal(a, b);
    }

    private static int EventStationRank(string name)
    {
        string st = EventStation(name);
        if (st == "6B")
        {
            return 1;
        }

        if (st == "12B")
        {
            return 2;
        }

        if (st == "20B")
        {
            return 3;
        }

        if (st == "24B")
        {
            return 4;
        }

        return 0;
    }

    private static string EventStation(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "";
        }

        foreach (string st in new[] { "12B", "20B", "24B", "6B" })
        {
            if (name.IndexOf(st, StringComparison.Ordinal) >= 0)
            {
                return st;
            }
        }

        return "";
    }

    private static int EventClassRank(string name)
    {
        if (name.StartsWith("AErr_", StringComparison.Ordinal))
        {
            return 0;
        }

        if (name.StartsWith("EErr_", StringComparison.Ordinal))
        {
            return 1;
        }

        if (name.StartsWith("NErr_", StringComparison.Ordinal))
        {
            return 2;
        }

        if (name.StartsWith("QErr_", StringComparison.Ordinal))
        {
            return 3;
        }

        return 4;
    }

    private static readonly string[] EventFamilyOrder =
    {
        "EStop_PB_Act_Die_Stand",
        "EStop_PB_Act",
        "Inside_PLC_Comm_Error",
        "Pos_Time_Setting_Error",
        "Wire_Broken",
        "Tension_Drive_Fault",
        "step_timeout",
        "Pico_Comm_Error",
        "SafetyBar",
        "Rolling_Door",
        "Bobbin_Loader"
    };

    private static int EventFamilyRank(string name)
    {
        for (int i = 0; i < EventFamilyOrder.Length; i++)
        {
            if (name.IndexOf(EventFamilyOrder[i], StringComparison.Ordinal) >= 0)
            {
                return i;
            }
        }

        return EventFamilyOrder.Length;
    }

    private static int EventNumber(string name)
    {
        Match m = Regex.Match(name ?? "", @"_#(\d+)$");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    private static XElement StructuredTextChild(XElement el)
    {
        XElement cur = el;
        while (cur.Parent != null && cur.Parent.Name.LocalName != "StructuredText")
        {
            cur = cur.Parent;
        }

        if (cur.Parent == null || cur.Parent.Name.LocalName != "StructuredText")
        {
            return null;
        }

        return cur;
    }

    private static List<XElement> CollectSclStatement(XElement start)
    {
        List<XElement> siblings = start.Parent.Elements().ToList();
        int idx = siblings.IndexOf(start);
        if (idx < 0)
        {
            return null;
        }

        List<XElement> list = new List<XElement>();
        for (int i = idx; i < siblings.Count; i++)
        {
            list.Add(siblings[i]);
            if (siblings[i].Name.LocalName == "Token" &&
                string.Equals((string)siblings[i].Attribute("Text"), ";", StringComparison.Ordinal))
            {
                if (i + 1 < siblings.Count && siblings[i + 1].Name.LocalName == "NewLine")
                {
                    list.Add(siblings[i + 1]);
                }

                return list;
            }
        }

        return null;
    }

    private static List<XElement> CloneSclStatement(
        List<XElement> template,
        string oldName,
        string newName,
        string oldIndex,
        string newIndex,
        ref int nextUid)
    {
        List<XElement> clones = new List<XElement>();
        bool indexReplaced = false;
        foreach (XElement src in template)
        {
            XElement clone = new XElement(src);
            foreach (XElement comp in clone.DescendantsAndSelf()
                .Where(e => e.Name.LocalName == "Component"))
            {
                if (string.Equals((string)comp.Attribute("Name"), oldName, StringComparison.Ordinal))
                {
                    comp.SetAttributeValue("Name", newName);
                }
            }

            foreach (XElement value in clone.DescendantsAndSelf()
                .Where(e => e.Name.LocalName == "ConstantValue"))
            {
                if (!indexReplaced && string.Equals(value.Value, oldIndex, StringComparison.Ordinal))
                {
                    value.Value = newIndex;
                    indexReplaced = true;
                }
            }

            foreach (XElement el in clone.DescendantsAndSelf())
            {
                if (el.Attribute("UId") != null)
                {
                    el.SetAttributeValue("UId", nextUid.ToString());
                    nextUid++;
                }
            }

            clones.Add(clone);
        }

        return clones;
    }

    private static int MaxUid(XElement root)
    {
        int max = 0;
        foreach (XElement el in root.DescendantsAndSelf())
        {
            XAttribute uid = el.Attribute("UId");
            int n;
            if (uid != null && int.TryParse(uid.Value, out n) && n > max)
            {
                max = n;
            }
        }

        return max;
    }

    private static HashSet<int> CollectAllMemoryBits(PlcSoftware plc)
    {
        HashSet<int> used = new HashSet<int>();
        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            foreach (PlcTag tag in table.Tags)
            {
                MarkMemoryBits(tag.LogicalAddress, used);
            }
        }

        return used;
    }

    private static int NextFreeBit(HashSet<int> used, int start, int last)
    {
        int bit = start;
        while (bit <= last && used.Contains(bit))
        {
            bit++;
        }

        return bit <= last ? bit : -1;
    }

    private static int NextFreeMemoryBit(PlcSoftware plc)
    {
        HashSet<int> used = CollectAllMemoryBits(plc);
        int bit = 410 * 8 + 2;
        while (used.Contains(bit))
        {
            bit++;
        }

        return bit;
    }

    private static void MarkMemoryBits(string address, HashSet<int> used)
    {
        if (string.IsNullOrEmpty(address))
        {
            return;
        }

        string raw = address.Trim().ToUpperInvariant();
        if (raw.Length < 3 || raw[0] != '%' || raw[1] != 'M')
        {
            return;
        }

        int i = 2;
        int width = 1;
        if (i < raw.Length && raw[i] == 'B')
        {
            width = 8;
            i++;
        }
        else if (i < raw.Length && raw[i] == 'W')
        {
            width = 16;
            i++;
        }
        else if (i < raw.Length && raw[i] == 'D')
        {
            width = 32;
            i++;
        }

        int start = i;
        while (i < raw.Length && char.IsDigit(raw[i]))
        {
            i++;
        }

        if (start == i)
        {
            return;
        }

        int number;
        if (!int.TryParse(raw.Substring(start, i - start), out number))
        {
            return;
        }

        int bit = 0;
        if (i < raw.Length && raw[i] == '.' && width == 1)
        {
            int b;
            if (int.TryParse(raw.Substring(i + 1), out b))
            {
                bit = b;
            }
        }

        int first = number * 8 + bit;
        for (int n = 0; n < width; n++)
        {
            used.Add(first + n);
        }
    }

    public static int RewireEnergyTags800(Project project)
    {
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        PlcTagTable table = FindTable(main, "S7Energy");
        if (table == null)
        {
            Console.WriteLine("找不到 S7Energy 變數表。");
            return 1;
        }

        const int oldBase = 256;
        const int newBase = 800;
        const int delta = newBase - oldBase;
        int changed = 0;
        int skipped = 0;

        foreach (PlcTag tag in table.Tags)
        {
            string current = tag.LogicalAddress;
            if (string.IsNullOrEmpty(current))
            {
                continue;
            }

            string next = ShiftEnergyAddress(current, oldBase, oldBase + 143, delta);
            if (next == null || string.Equals(next, current, StringComparison.OrdinalIgnoreCase))
            {
                if (LooksEnergyAddress(current) && !IsAlreadyNewEnergy(current, newBase))
                {
                    Console.WriteLine("  略過 " + tag.Name + " " + current);
                    skipped++;
                }

                continue;
            }

            try
            {
                tag.LogicalAddress = next;
                Console.WriteLine("  " + tag.Name + " " + current + " -> " + next);
                changed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  失敗 " + tag.Name + " " + current + "：" + Flatten(ex));
            }
        }

        project.Save();
        Console.WriteLine("S7Energy 改址：" + changed + " 點，略過 " + skipped + "。已存。");
        return 0;
    }

    private static bool LooksEnergyAddress(string address)
    {
        string a = address.Trim().ToUpperInvariant();
        return a.StartsWith("%I", StringComparison.Ordinal) || a.StartsWith("%Q", StringComparison.Ordinal);
    }

    private static bool IsAlreadyNewEnergy(string address, int newBase)
    {
        string shifted = ShiftEnergyAddress(address, newBase, newBase + 143, 0);
        return shifted != null;
    }

    private static string ShiftEnergyAddress(string address, int fromByte, int toByteInclusive, int delta)
    {
        string raw = address.Trim();
        if (raw.Length < 3 || raw[0] != '%')
        {
            return null;
        }

        int i = 1;
        if (i >= raw.Length || (raw[i] != 'I' && raw[i] != 'Q' && raw[i] != 'i' && raw[i] != 'q'))
        {
            return null;
        }

        i++;
        if (i < raw.Length && "BWDXbwdx".IndexOf(raw[i]) >= 0)
        {
            i++;
        }

        int start = i;
        while (i < raw.Length && char.IsDigit(raw[i]))
        {
            i++;
        }

        if (start == i)
        {
            return null;
        }

        int number;
        if (!int.TryParse(raw.Substring(start, i - start), out number))
        {
            return null;
        }

        if (number < fromByte || number > toByteInclusive)
        {
            return null;
        }

        return raw.Substring(0, start) + (number + delta) + raw.Substring(i);
    }

    public static int OrganizeBlockFolders(TiaPortal portal, Project project)
    {
        Console.WriteLine("把根目錄亂堆的 FB／FC／DB 搬進既有編號資料夾。凡是 OB 都留外層，跟 Main 同一層。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "block-folders");
        Directory.CreateDirectory(dir);

        Console.WriteLine("先編譯再搬，避免匯出 Inconsistent。");
        CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後再搬）。");

        string[][] mainMoves =
        {
            new[] { "2.Communication/TCP", "tcp_setting", "TCP_Config", "TCP_Buf", "TCP_Seq", "MB_CLIENT_DB",
                "for_6B_plc", "for_12B_plc", "for_20B_plc", "for_24B_plc", "for_dsp_plc_zone1" },
            new[] { "2.Communication/Zigbee", "Modbus_Comm_Zigbee", "Modbus_Comm_Zigbee_6B_DB", "Modbus_Comm_Zigbee_12B_DB",
                "Modbus_Comm_Zigbee_20B_DB", "Modbus_Comm_Zigbee_24B_DB",
                "Modbus_Zigbee_6B", "Modbus_Zigbee_12B", "Modbus_Zigbee_20B", "Modbus_Zigbee_24B" },
            new[] { "6.Rotor_Angle", "HSC_Zero", "HSC_Zero_6B_DB", "HSC_Zero_12B_DB", "HSC_Zero_20B_DB", "HSC_Zero_24B_DB",
                "Home_Angle_Zero", "Home_Angle_Zero_DB",
                "Rotor_Angle_6B_DB", "Rotor_Angle_20B_DB", "Rotor_Angle_24B_DB" },
            new[] { "7.Speed_Loop", "Rotor_Sync_Ctrl", "Rotor_Sync_Ctrl_DB",
                "PID_Rotor_Sync_12B_DB", "PID_Rotor_Sync_20B_DB" },
            new[] { "11.PreTwist", "PreTwist_IO", "PreTwist_SoftGear", "PreTwist_SoftGear_DB" },
            new[] { "3.Main", "opt" },
            new[] { "Library/Basic", "Word_Swap" },
            new[] { "99.Test", "test_db" }
        };

        List<BlockMove> pending = new List<BlockMove>();
        foreach (string[] row in mainMoves)
        {
            PlcBlockGroup dest = EnsureUserBlockGroup(main.BlockGroup, row[0]);
            for (int i = 1; i < row.Length; i++)
            {
                BlockMove item = PrepareBlockMove(main, dest, row[0], row[i], dir);
                if (item != null)
                {
                    pending.Add(item);
                }
            }
        }

        foreach (PlcBlock block in EnumerateBlocks(main.BlockGroup).ToList())
        {
            if (!(block is OB))
            {
                continue;
            }

            if (string.IsNullOrEmpty(BlockGroupPath(main, block)))
            {
                continue;
            }

            BlockMove item = PrepareBlockMove(main, main.BlockGroup, string.Empty, block.Name, dir);
            if (item != null)
            {
                pending.Add(item);
            }
        }

        foreach (string insideName in new[] { "25017_20B_Inside_PLC", "25017_24B_Inside_PLC" })
        {
            PlcSoftware inside = FindPlc(project, insideName);
            if (inside == null)
            {
                continue;
            }

            PlcBlockGroup dest = EnsureUserBlockGroup(inside.BlockGroup, "4.Bobbin Loading");
            BlockMove item = PrepareBlockMove(inside, dest, "4.Bobbin Loading", "Bobbin_Load_S4_DB", dir);
            if (item != null)
            {
                pending.Add(item);
            }
        }

        int moved = 0;
        int failed = 0;
        foreach (BlockMove item in pending)
        {
            if (CommitBlockMove(item))
            {
                moved++;
            }
            else
            {
                failed++;
            }
        }

        project.Save();
        Console.WriteLine("已存（搬資料夾）。搬 " + moved + "／失敗 " + failed);

        int leftover = 0;
        foreach (string[] row in mainMoves)
        {
            for (int i = 1; i < row.Length; i++)
            {
                PlcBlock block = FindBlock(main, row[i]);
                if (block == null)
                {
                    continue;
                }

                string here = BlockGroupPath(main, block);
                if (!string.Equals(here, row[0], StringComparison.OrdinalIgnoreCase))
                {
                    leftover++;
                    Console.WriteLine("  還在 " + (string.IsNullOrEmpty(here) ? "(根)" : here) + "：" + row[i]);
                }
            }
        }

        foreach (PlcBlock block in EnumerateBlocks(main.BlockGroup))
        {
            if (!(block is OB))
            {
                continue;
            }

            string here = BlockGroupPath(main, block);
            if (!string.IsNullOrEmpty(here))
            {
                leftover++;
                Console.WriteLine("  OB 還在 " + here + "：" + block.Name);
            }
        }

        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。剩餘未搬 " + leftover);
        return errors == 0 && leftover == 0 && failed == 0 ? 0 : 1;
    }

    private static PlcBlockGroup EnsureUserBlockGroup(PlcBlockGroup root, string path)
    {
        PlcBlockGroup current = root;
        foreach (string part in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            PlcBlockUserGroup next = current.Groups.Find(part);
            if (next == null)
            {
                next = current.Groups.Create(part);
                Console.WriteLine("  建資料夾：" + part);
            }

            current = next;
        }

        return current;
    }

    private static string BlockGroupPath(PlcSoftware plc, PlcBlock block)
    {
        List<string> parts = new List<string>();
        PlcBlockGroup group = block.Parent as PlcBlockGroup;
        while (group != null && group != plc.BlockGroup)
        {
            PlcBlockUserGroup user = group as PlcBlockUserGroup;
            if (user == null)
            {
                break;
            }

            parts.Insert(0, user.Name);
            group = user.Parent as PlcBlockGroup;
        }

        return string.Join("/", parts.ToArray());
    }

    private sealed class BlockMove
    {
        public PlcSoftware Plc;
        public PlcBlockGroup Dest;
        public string DestPath;
        public string BlockName;
        public string CurrentPath;
        public string ExportPath;
    }

    private static BlockMove PrepareBlockMove(
        PlcSoftware plc,
        PlcBlockGroup dest,
        string destPath,
        string blockName,
        string dir)
    {
        PlcBlock block = FindBlock(plc, blockName);
        if (block == null)
        {
            Console.WriteLine("  找不到 " + blockName + "，略過。");
            return null;
        }

        if (block is OB && dest != plc.BlockGroup)
        {
            Console.WriteLine("  OB 留外層，不搬進夾：" + blockName);
            return null;
        }

        string current = BlockGroupPath(plc, block);
        if (string.Equals(current, destPath, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  已在 " + (string.IsNullOrEmpty(destPath) ? "(根)" : destPath) + "：" + blockName);
            return null;
        }

        string exportPath = Path.Combine(dir, plc.Name + "_" + blockName.Replace(' ', '_') + ".xml");
        if (!ExportBlockXmlQuiet(block, exportPath))
        {
            return null;
        }

        return new BlockMove
        {
            Plc = plc,
            Dest = dest,
            DestPath = destPath,
            BlockName = blockName,
            CurrentPath = current,
            ExportPath = exportPath
        };
    }

    private static bool CommitBlockMove(BlockMove item)
    {
        PlcBlock block = FindBlock(item.Plc, item.BlockName);
        if (block == null)
        {
            Console.WriteLine("  找不到 " + item.BlockName + "，無法搬。");
            return false;
        }

        try
        {
            block.Delete();
        }
        catch (Exception ex)
        {
            Console.WriteLine("  刪除失敗 " + item.BlockName + "：" + Flatten(ex));
            return false;
        }

        try
        {
            item.Dest.Blocks.Import(new FileInfo(item.ExportPath), ImportOptions.Override);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯入失敗 " + item.BlockName + " → " + item.DestPath + "：" + Flatten(ex));
            item.Plc.BlockGroup.Blocks.Import(new FileInfo(item.ExportPath), ImportOptions.Override);
            return false;
        }

        if (FindBlock(item.Plc, item.BlockName) == null)
        {
            Console.WriteLine("  搬完找不到 " + item.BlockName);
            return false;
        }

        Console.WriteLine("  " + (string.IsNullOrEmpty(item.CurrentPath) ? "(根)" : item.CurrentPath) +
            " → " + (string.IsNullOrEmpty(item.DestPath) ? "(根)" : item.DestPath) + "：" + item.BlockName);
        return true;
    }

    public static int WireMainTcp(TiaPortal portal, Project project)
    {
        Console.WriteLine("Main Zigbee master 拿掉，改 PLC_TCP：TCP_Seq + 一顆 MB_CLIENT。四站 GW 參數同一套。");
        Console.WriteLine("Main_Logic／Event_Control 仍讀 Modbus_Zigbee_*，這輪不改對應。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "tcp-main");
        Directory.CreateDirectory(dir);

        string sclDir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "Scl");
        string[] sclFiles =
        {
            "tcp_setting.scl",
            "TCP_Config.scl",
            "TCP_Buf.scl",
            "for_dsp_plc_zone1.scl",
            "for_6B_plc.scl",
            "for_12B_plc.scl",
            "for_20B_plc.scl",
            "for_24B_plc.scl",
            "TCP_Seq.scl"
        };

        foreach (string file in sclFiles)
        {
            string path = Path.Combine(sclDir, file);
            if (!File.Exists(path))
            {
                Console.WriteLine("找不到 SCL：" + path);
                return 1;
            }

            string blockName = SclBlockName(path);
            if (blockName != null && FindBlock(main, blockName) != null)
            {
                Console.WriteLine("已有 " + blockName + "，略過 SCL。");
                continue;
            }

            if (!ImportSclFile(main, path))
            {
                return 1;
            }
        }

        project.Save();
        Console.WriteLine("已存（SCL）。");

        if (!EnsureMbClientDb(main, dir))
        {
            Console.WriteLine("找不到 MB_CLIENT_DB。");
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（MB_CLIENT_DB）。");

        int mainChanged = PatchNamedBlock(
            main,
            dir,
            "Main",
            root => PatchMainZigbeeToTcp(root, dir),
            root => HasTcpSeqCall(root) && !HasZigbeeMasterCall(root));
        Console.WriteLine("  Main：" + mainChanged);
        if (mainChanged < 0)
        {
            return 1;
        }

        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return errors == 0 ? 0 : 1;
    }

    public static int WireInsideRtu(TiaPortal portal, Project project)
    {
        Console.WriteLine("四台 Inside 掛 plc_2_rs485：DC12_RTU_Slave，PORT=CM 1241，站號 30。");
        Console.WriteLine("只換通訊。Main_Logic 仍讀 Modbus_PLC_48B。Pico 仍走 CB 1241。");

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "inside-rtu");
        Directory.CreateDirectory(dir);
        string sclDir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "Scl");

        string[] plcNames =
        {
            "25017_6B_Inside_PLC",
            "25017_12B_Inside_PLC",
            "25017_20B_Inside_PLC",
            "25017_24B_Inside_PLC"
        };

        int failed = 0;
        foreach (string plcName in plcNames)
        {
            Console.WriteLine("---- " + plcName + " ----");
            if (WireOneInsideRtu(project, plcName, dir, sclDir) != 0)
            {
                failed++;
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + "）。");
        }

        return failed == 0 ? 0 : 1;
    }

    public static int StripInsideOldRs485(TiaPortal portal, Project project)
    {
        Console.WriteLine("檢查四台 Inside 的 RS485。只留 DC12_RTU_Slave（CM）。");
        Console.WriteLine("舊 Zigbee 從站拿掉。Pico 仍走 CB，資料 DB 不動。");

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "inside-rtu", "strip-old-rs485");
        Directory.CreateDirectory(dir);

        string[] plcNames =
        {
            "25017_6B_Inside_PLC",
            "25017_12B_Inside_PLC",
            "25017_20B_Inside_PLC",
            "25017_24B_Inside_PLC"
        };

        int failed = 0;
        foreach (string plcName in plcNames)
        {
            Console.WriteLine("---- " + plcName + " ----");
            if (StripOneInsideOldRs485(project, plcName, dir) != 0)
            {
                failed++;
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + "）。");
        }

        return failed == 0 ? 0 : 1;
    }

    private static int StripOneInsideOldRs485(Project project, string plcName, string dir)
    {
        PlcSoftware plc = FindPlc(project, plcName);
        if (plc == null)
        {
            Console.WriteLine("找不到 " + plcName + "。");
            return 1;
        }

        string[] keep =
        {
            "DC12_RTU_Slave", "DC12_RTU_Slave_DB", "DC12_RTU_Config", "for_dc12_plc"
        };
        string[] drop =
        {
            "Modbus_Comm_PLC_Zigbee_DB", "Modbus_Comm_PLC_Zigbee"
        };

        Console.WriteLine("  通訊相關區塊：");
        foreach (PlcBlock block in EnumerateBlocks(plc.BlockGroup))
        {
            if (!LooksRs485Block(block.Name))
            {
                continue;
            }

            Console.WriteLine("    " + block.Name + "  " + block.GetType().Name + " #" + block.Number);
        }

        string plcDir = Path.Combine(dir, plcName.Replace("25017_", string.Empty));
        Directory.CreateDirectory(plcDir);
        PlcBlock main = FindBlock(plc, "Main");
        if (main == null || !ExportBlockXmlQuiet(main, Path.Combine(plcDir, "Main-before.xml")))
        {
            Console.WriteLine("匯不出 Main。");
            return 1;
        }

        XElement root = XDocument.Load(Path.Combine(plcDir, "Main-before.xml")).Root;
        PrintInsideMainRs485(root);
        if (!VerifyInsideMainRtu(root))
        {
            Console.WriteLine("Main 還不是 DC12＋Pico，未刪。");
            return 1;
        }

        if (HasCall(root, "Modbus_Comm_PLC_Zigbee"))
        {
            Console.WriteLine("Main 還在叫 Modbus_Comm_PLC_Zigbee，未刪。");
            return 1;
        }

        foreach (string name in drop)
        {
            PlcBlock block = FindBlock(plc, name);
            if (block == null)
            {
                Console.WriteLine("  沒有 " + name + "，略過。");
                continue;
            }

            try
            {
                block.Delete();
                Console.WriteLine("  已刪 " + name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  刪不掉 " + name + "：" + Flatten(ex));
                return 1;
            }
        }

        foreach (string name in keep)
        {
            if (FindBlock(plc, name) == null)
            {
                Console.WriteLine("  不該不見：" + name);
                return 1;
            }
        }

        int errors = CompilePlc(plc);
        if (errors != 0)
        {
            return 1;
        }

        PlcBlock after = FindBlock(plc, "Main");
        if (after == null || !ExportBlockXmlQuiet(after, Path.Combine(plcDir, "Main-after.xml")))
        {
            return 1;
        }

        XElement verify = XDocument.Load(Path.Combine(plcDir, "Main-after.xml")).Root;
        if (!VerifyInsideMainRtu(verify) || HasCall(verify, "Modbus_Comm_PLC_Zigbee"))
        {
            Console.WriteLine("刪完後 Main 驗證失敗。");
            return 1;
        }

        if (FindBlock(plc, "Modbus_Comm_PLC_Zigbee") != null ||
            FindBlock(plc, "Modbus_Comm_PLC_Zigbee_DB") != null)
        {
            Console.WriteLine("舊 Zigbee 通訊區塊還在。");
            return 1;
        }

        return 0;
    }

    private static bool LooksRs485Block(string name)
    {
        return name.IndexOf("Modbus", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("DC12", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Zigbee", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Pico", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("RTU", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("for_dc12", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void PrintInsideMainRs485(XElement root)
    {
        IEnumerable<string> calls = root.Descendants()
            .Where(e => e.Name.LocalName == "CallInfo")
            .Select(e => (string)e.Attribute("Name"))
            .Where(n => !string.IsNullOrEmpty(n) && LooksRs485Block(n))
            .Distinct();
        Console.WriteLine("  Main 還在叫：" + string.Join("、", calls));

        IEnumerable<string> ports = root.Descendants()
            .Where(e => e.Name.LocalName == "Constant")
            .Select(e => (string)e.Attribute("Name"))
            .Where(n => !string.IsNullOrEmpty(n) &&
                (n.IndexOf("1241", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 n.IndexOf("CM_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 n.IndexOf("CB_", StringComparison.OrdinalIgnoreCase) >= 0))
            .Distinct();
        Console.WriteLine("  Main PORT：" + string.Join("、", ports));
    }

    private static bool HasCall(XElement root, string name)
    {
        return root.Descendants().Any(e =>
            e.Name.LocalName == "CallInfo" &&
            (string)e.Attribute("Name") == name);
    }

    private static readonly string[] SectionIdRemapBlocks =
    {
        "Main_Logic",
        "Bobbin_Loaded_Check",
        "Bobbin_Unloaded_Check"
    };

    public static int RemapInsideSectionId(TiaPortal portal, Project project)
    {
        Console.WriteLine("四台 Inside：Modbus_DB_PLC_Zigbee.Write.rotor_section_id → for_dc12_plc.Read.rotor_section_id。");

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "inside-rtu", "remap-section-id");
        Directory.CreateDirectory(dir);

        int failed = 0;
        foreach (string plcName in InsidePlcNames)
        {
            Console.WriteLine("---- " + plcName + " ----");
            if (RemapOneInsideSectionId(project, plcName, dir) != 0)
            {
                failed++;
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + "）。");
        }

        return failed == 0 ? 0 : 1;
    }

    private static int RemapOneInsideSectionId(Project project, string plcName, string dir)
    {
        PlcSoftware plc = FindPlc(project, plcName);
        if (plc == null)
        {
            Console.WriteLine("找不到 " + plcName + "。");
            return 1;
        }

        if (FindBlock(plc, "for_dc12_plc") == null)
        {
            Console.WriteLine("找不到 for_dc12_plc。");
            return 1;
        }

        string plcDir = Path.Combine(dir, plcName.Replace("25017_", string.Empty));
        Directory.CreateDirectory(plcDir);

        foreach (string name in SectionIdRemapBlocks)
        {
            int changed = PatchNamedBlock(
                plc,
                plcDir,
                name,
                RemapZigbeeWriteSectionToDc12Read,
                VerifySectionIdRemap);
            Console.WriteLine("  " + name + "：" + changed);
            if (changed < 0)
            {
                return 1;
            }
        }

        ListRemainingZigbeeUses(plc, plcDir);
        if (CountZigbeeWriteSectionInDir(plcDir) != 0)
        {
            Console.WriteLine("還有 Write.rotor_section_id 沒改完。");
            return 1;
        }

        return CompilePlc(plc) == 0 ? 0 : 1;
    }

    private static int RemapZigbeeWriteSectionToDc12Read(XElement root)
    {
        int n = 0;
        foreach (XElement symbol in root.Descendants().Where(e => e.Name.LocalName == "Symbol"))
        {
            List<XElement> comps = SymbolComponents(symbol);
            if (!IsSymbolPath(comps, "Modbus_DB_PLC_Zigbee", "Write", "rotor_section_id"))
            {
                continue;
            }

            comps[0].SetAttributeValue("Name", "for_dc12_plc");
            comps[1].SetAttributeValue("Name", "Read");
            n++;
        }

        return n;
    }

    private static bool VerifySectionIdRemap(XElement root)
    {
        return CountSymbolPath(root, "Modbus_DB_PLC_Zigbee", "Write", "rotor_section_id") == 0 &&
            CountSymbolPath(root, "for_dc12_plc", "Read", "rotor_section_id") > 0;
    }

    private static int CountZigbeeWriteSectionInDir(string plcDir)
    {
        int n = 0;
        foreach (string name in SectionIdRemapBlocks)
        {
            string after = Path.Combine(plcDir, name + "-after.xml");
            string before = Path.Combine(plcDir, name + "-before.xml");
            string path = File.Exists(after) ? after : before;
            if (!File.Exists(path))
            {
                continue;
            }

            n += CountSymbolPath(
                XDocument.Load(path).Root,
                "Modbus_DB_PLC_Zigbee",
                "Write",
                "rotor_section_id");
        }

        return n;
    }

    private static List<XElement> SymbolComponents(XElement symbol)
    {
        return symbol.Elements().Where(e => e.Name.LocalName == "Component").ToList();
    }

    private static int CountTwoPartSymbol(XElement root, string db, string member)
    {
        int n = 0;
        foreach (XElement symbol in root.Descendants().Where(e => e.Name.LocalName == "Symbol"))
        {
            List<XElement> comps = SymbolComponents(symbol);
            if (comps.Count == 2 &&
                (string)comps[0].Attribute("Name") == db &&
                (string)comps[1].Attribute("Name") == member)
            {
                n++;
            }
        }

        return n;
    }

    private static bool IsSymbolPath(List<XElement> comps, string db, string section, string member)
    {
        return comps.Count >= 3 &&
            (string)comps[0].Attribute("Name") == db &&
            (string)comps[1].Attribute("Name") == section &&
            (string)comps[2].Attribute("Name") == member;
    }

    private static int CountSymbolPath(XElement root, string db, string section, string member)
    {
        int n = 0;
        foreach (XElement symbol in root.Descendants().Where(e => e.Name.LocalName == "Symbol"))
        {
            if (IsSymbolPath(SymbolComponents(symbol), db, section, member))
            {
                n++;
            }
        }

        return n;
    }

    public static int RemapInsideUnloadSnr(TiaPortal portal, Project project)
    {
        Console.WriteLine("四台 Inside：Bobbin_Snr_and_Valve.bobbin_unloaded_snesor → for_dc12_plc.Write_From_DC12.bobbin_unload_snr。");

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "inside-rtu", "remap-unload-snr");
        Directory.CreateDirectory(dir);

        int failed = 0;
        foreach (string plcName in InsidePlcNames)
        {
            Console.WriteLine("---- " + plcName + " ----");
            if (RemapOneInsideUnloadSnr(project, plcName, dir) != 0)
            {
                failed++;
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + "）。");
        }

        return failed == 0 ? 0 : 1;
    }

    private static readonly string[] UnloadSnrWidenBlocks =
    {
        "Bobbin_Loaded_Check",
        "Bobbin_Unloaded_Check",
        "Main_Logic"
    };

    private static int RemapOneInsideUnloadSnr(Project project, string plcName, string dir)
    {
        PlcSoftware plc = FindPlc(project, plcName);
        if (plc == null)
        {
            Console.WriteLine("找不到 " + plcName + "。");
            return 1;
        }

        if (FindBlock(plc, "for_dc12_plc") == null)
        {
            Console.WriteLine("找不到 for_dc12_plc。");
            return 1;
        }

        string plcDir = Path.Combine(dir, plcName.Replace("25017_", string.Empty));
        Directory.CreateDirectory(plcDir);

        List<string> patched = new List<string>();
        foreach (string name in UnloadSnrWidenBlocks)
        {
            PlcBlock block = FindBlock(plc, name);
            if (block == null)
            {
                Console.WriteLine("找不到 " + name + "。");
                return 1;
            }

            string before = Path.Combine(plcDir, name + "-before.xml");
            if (!ExportBlockXmlQuiet(block, before))
            {
                return 1;
            }

            XDocument doc = XDocument.Load(before);
            int changed = WidenBobbinUnloadSensor(doc.Root);
            if (name == "Main_Logic")
            {
                changed += RemapBobbinUnloadSnrSymbols(doc.Root);
            }

            Console.WriteLine("  " + name + " 變更：" + changed);
            if (name == "Main_Logic" &&
                CountTwoPartSymbol(doc.Root, "Bobbin_Snr_and_Valve", "bobbin_unloaded_snesor") != 0)
            {
                Console.WriteLine("  Main_Logic 還有舊下紗 SNR，未匯入。");
                return 1;
            }

            if (CountSymbolPath(doc.Root, "for_dc12_plc", "Write_From_DC12", "bobbin_unload_snr") == 0 &&
                name == "Main_Logic")
            {
                Console.WriteLine("  Main_Logic 沒對到 bobbin_unload_snr，未匯入。");
                return 1;
            }

            string importPath = Path.Combine(plcDir, name + ".xml");
            doc.Save(importPath);
            patched.Add(name);
        }

        foreach (string name in patched)
        {
            TryImportBlockFile(plc, FindBlock(plc, name), Path.Combine(plcDir, name + ".xml"));
        }

        if (CompilePlc(plc) != 0)
        {
            return 1;
        }

        foreach (string name in patched)
        {
            PlcBlock imported = FindBlock(plc, name);
            string after = Path.Combine(plcDir, name + "-after.xml");
            if (imported == null || !ExportBlockXmlQuiet(imported, after))
            {
                return 1;
            }
        }

        XElement logic = XDocument.Load(Path.Combine(plcDir, "Main_Logic-after.xml")).Root;
        if (CountTwoPartSymbol(logic, "Bobbin_Snr_and_Valve", "bobbin_unloaded_snesor") != 0 ||
            CountSymbolPath(logic, "for_dc12_plc", "Write_From_DC12", "bobbin_unload_snr") == 0)
        {
            Console.WriteLine("  匯入後驗證失敗。");
            return 1;
        }

        Console.WriteLine("  匯入後驗證：通過");
        return 0;
    }

    private static int RemapBobbinUnloadSnrSymbols(XElement root)
    {
        int n = 0;
        foreach (XElement symbol in root.Descendants().Where(e => e.Name.LocalName == "Symbol"))
        {
            List<XElement> comps = SymbolComponents(symbol);
            if (comps.Count < 2 ||
                (string)comps[0].Attribute("Name") != "Bobbin_Snr_and_Valve" ||
                (string)comps[1].Attribute("Name") != "bobbin_unloaded_snesor")
            {
                continue;
            }

            comps[0].SetAttributeValue("Name", "for_dc12_plc");
            comps[1].SetAttributeValue("Name", "Write_From_DC12");
            if (comps.Count >= 3)
            {
                comps[2].SetAttributeValue("Name", "bobbin_unload_snr");
            }
            else
            {
                XNamespace ns = comps[1].Name.Namespace;
                comps[1].AddAfterSelf(new XElement(
                    ns + "Component",
                    new XAttribute("Name", "bobbin_unload_snr")));
            }

            n++;
        }

        return n;
    }

    private static int WidenBobbinUnloadSensor(XElement root)
    {
        int n = 0;
        foreach (XElement node in root.Descendants().Where(e =>
            (e.Name.LocalName == "Member" || e.Name.LocalName == "Parameter") &&
            (string)e.Attribute("Name") == "bobbin_unloaded_snesor"))
        {
            XAttribute dt = node.Attribute("Datatype") ?? node.Attribute("Type");
            if (dt == null || dt.Value.IndexOf("bobbin_total", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            dt.Value = "Array[0..63] of Bool";
            n++;
        }

        return n;
    }

    public static int FixInsideLeftover(TiaPortal portal, Project project)
    {
        Console.WriteLine("Inside 有把握的剩餘：pico_comm_ok 改 TRUE；6B／12B 安全桿第三面 S4→S3。");

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "inside-rtu", "fix-leftover");
        Directory.CreateDirectory(dir);

        int failed = 0;
        foreach (string plcName in InsidePlcNames)
        {
            Console.WriteLine("---- " + plcName + " ----");
            if (FixOneInsideLeftover(project, plcName, dir) != 0)
            {
                failed++;
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + "）。");
        }

        return failed == 0 ? 0 : 1;
    }

    private static int FixOneInsideLeftover(Project project, string plcName, string dir)
    {
        PlcSoftware plc = FindPlc(project, plcName);
        if (plc == null)
        {
            Console.WriteLine("找不到 " + plcName + "。");
            return 1;
        }

        string plcDir = Path.Combine(dir, plcName.Replace("25017_", string.Empty));
        Directory.CreateDirectory(plcDir);

        PlcBlock block = FindBlock(plc, "Main_Logic");
        if (block == null)
        {
            Console.WriteLine("找不到 Main_Logic。");
            return 1;
        }

        string before = Path.Combine(plcDir, "Main_Logic-before.xml");
        if (!ExportBlockXmlQuiet(block, before))
        {
            return 1;
        }

        XDocument doc = XDocument.Load(before);
        int pico = ForcePicoCommOkLiteral(doc.Root);
        int face = 0;
        bool threeFace = plcName.IndexOf("_6B_", StringComparison.Ordinal) >= 0 ||
            plcName.IndexOf("_12B_", StringComparison.Ordinal) >= 0;
        if (threeFace)
        {
            face = RenameLockedS4ToS3(doc.Root);
        }

        Console.WriteLine("  pico_comm_ok→TRUE：" + pico + "；S4→S3：" + face);
        if (pico == 0)
        {
            Console.WriteLine("  沒改到 pico_comm_ok，未匯入。");
            return 1;
        }

        if (threeFace && face == 0)
        {
            Console.WriteLine("  6B／12B 沒改到 locked_S4，未匯入。");
            return 1;
        }

        string importPath = Path.Combine(plcDir, "Main_Logic.xml");
        doc.Save(importPath);
        TryImportBlockFile(plc, block, importPath);
        if (CompilePlc(plc) != 0)
        {
            return 1;
        }

        string after = Path.Combine(plcDir, "Main_Logic-after.xml");
        PlcBlock imported = FindBlock(plc, "Main_Logic");
        if (imported == null || !ExportBlockXmlQuiet(imported, after))
        {
            return 1;
        }

        XElement logic = XDocument.Load(after).Root;
        if (CountLocalPicoCommOk(logic) != 0)
        {
            Console.WriteLine("  匯入後還有 pico_comm_ok 變數接腳。");
            return 1;
        }

        if (threeFace && CountNamedComponent(logic, "I_Snr_Safe_Pin_locked_S4") != 0)
        {
            Console.WriteLine("  匯入後 6B／12B 還有 locked_S4。");
            return 1;
        }

        Console.WriteLine("  匯入後驗證：通過");
        return 0;
    }

    private static int ForcePicoCommOkLiteral(XElement root)
    {
        int n = 0;
        foreach (XElement access in root.Descendants().Where(e => e.Name.LocalName == "Access").ToList())
        {
            if ((string)access.Attribute("Scope") != "LocalVariable")
            {
                continue;
            }

            XElement symbol = access.Elements().FirstOrDefault(e => e.Name.LocalName == "Symbol");
            if (symbol == null)
            {
                continue;
            }

            List<XElement> comps = SymbolComponents(symbol);
            if (comps.Count != 1 || (string)comps[0].Attribute("Name") != "pico_comm_ok")
            {
                continue;
            }

            XNamespace ns = access.Name.Namespace;
            access.SetAttributeValue("Scope", "LiteralConstant");
            access.RemoveNodes();
            access.Add(new XElement(ns + "Constant",
                new XElement(ns + "ConstantType", "Bool"),
                new XElement(ns + "ConstantValue", "true")));
            n++;
        }

        return n;
    }

    private static int RenameLockedS4ToS3(XElement root)
    {
        int n = 0;
        foreach (XElement comp in root.Descendants().Where(e => e.Name.LocalName == "Component"))
        {
            if ((string)comp.Attribute("Name") != "I_Snr_Safe_Pin_locked_S4")
            {
                continue;
            }

            comp.SetAttributeValue("Name", "I_Snr_Safe_Pin_locked_S3");
            n++;
        }

        return n;
    }

    private static int CountLocalPicoCommOk(XElement root)
    {
        int n = 0;
        foreach (XElement access in root.Descendants().Where(e => e.Name.LocalName == "Access"))
        {
            if ((string)access.Attribute("Scope") != "LocalVariable")
            {
                continue;
            }

            XElement symbol = access.Elements().FirstOrDefault(e => e.Name.LocalName == "Symbol");
            if (symbol == null)
            {
                continue;
            }

            List<XElement> comps = SymbolComponents(symbol);
            if (comps.Count == 1 && (string)comps[0].Attribute("Name") == "pico_comm_ok")
            {
                n++;
            }
        }

        return n;
    }

    private static int CountNamedComponent(XElement root, string name)
    {
        int n = 0;
        foreach (XElement comp in root.Descendants().Where(e => e.Name.LocalName == "Component"))
        {
            if ((string)comp.Attribute("Name") == name)
            {
                n++;
            }
        }

        return n;
    }

    public static int MapInsideSafetyBar(TiaPortal portal, Project project)
    {
        Console.WriteLine("Inside 安全桿：S1→[1]、S2→[2]、S3→[3]、S4→[4]。20B／24B 全鎖四路 AND。");

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "inside-rtu", "map-safety-bar");
        Directory.CreateDirectory(dir);

        int failed = 0;
        foreach (string plcName in InsidePlcNames)
        {
            Console.WriteLine("---- " + plcName + " ----");
            if (MapOneInsideSafetyBar(project, plcName, dir) != 0)
            {
                failed++;
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + "）。");
        }

        return failed == 0 ? 0 : 1;
    }

    private static int MapOneInsideSafetyBar(Project project, string plcName, string dir)
    {
        PlcSoftware plc = FindPlc(project, plcName);
        if (plc == null)
        {
            Console.WriteLine("找不到 " + plcName + "。");
            return 1;
        }

        string plcDir = Path.Combine(dir, plcName.Replace("25017_", string.Empty));
        Directory.CreateDirectory(plcDir);
        PlcBlock block = FindBlock(plc, "Main_Logic");
        if (block == null)
        {
            Console.WriteLine("找不到 Main_Logic。");
            return 1;
        }

        string before = Path.Combine(plcDir, "Main_Logic-before.xml");
        if (!ExportBlockXmlQuiet(block, before))
        {
            return 1;
        }

        XDocument doc = XDocument.Load(before);
        bool fourFace = plcName.IndexOf("_20B_", StringComparison.Ordinal) >= 0 ||
            plcName.IndexOf("_24B_", StringComparison.Ordinal) >= 0;
        int changed = PatchSafetyBarUnits(doc.Root, fourFace);
        Console.WriteLine("  變更：" + changed);
        if (changed == 0)
        {
            Console.WriteLine("  沒改到，未匯入。");
            return 1;
        }

        string importPath = Path.Combine(plcDir, "Main_Logic.xml");
        doc.Save(importPath);
        TryImportBlockFile(plc, block, importPath);
        if (CompilePlc(plc) != 0)
        {
            return 1;
        }

        string after = Path.Combine(plcDir, "Main_Logic-after.xml");
        PlcBlock imported = FindBlock(plc, "Main_Logic");
        if (imported == null || !ExportBlockXmlQuiet(imported, after))
        {
            return 1;
        }

        if (!VerifySafetyBarMap(XDocument.Load(after).Root, fourFace))
        {
            Console.WriteLine("  匯入後驗證失敗。");
            return 1;
        }

        Console.WriteLine("  匯入後驗證：通過");
        return 0;
    }

    private static int PatchSafetyBarUnits(XElement root, bool fourFace)
    {
        int n = 0;
        List<XElement> locks = root.Descendants().Where(IsCompileUnit).Where(IsAllSafetyBarLockedNet).ToList();
        List<XElement> reports = root.Descendants().Where(IsCompileUnit).Where(IsSafetyBarLockReport).ToList();
        if (locks.Count == 0 || reports.Count == 0)
        {
            Console.WriteLine("  找不到全鎖／回報網。");
            return 0;
        }

        if (fourFace)
        {
            n += EnsureAndHasTag(locks[0], "I_Snr_Safe_Pin_locked_S4");
            n += EnsureReportFace(reports[0], "I_Snr_Safe_Pin_locked_S4", "4");
            n += EnsureAskLiftHasS4(root);
        }

        for (int i = 1; i < locks.Count; i++)
        {
            locks[i].Remove();
            n++;
        }

        for (int i = 1; i < reports.Count; i++)
        {
            reports[i].Remove();
            n++;
        }

        return n;
    }

    private static bool IsAllSafetyBarLockedNet(XElement unit)
    {
        return CountNamedComponent(unit, "Q_All_Safety_Bar_Locked") > 0;
    }

    private static bool IsSafetyBarLockReport(XElement unit)
    {
        return CountNamedComponent(unit, "I_Snr_Safe_Pin_locked_S1") > 0 &&
            HasSaftyBarIndex(unit, "1") &&
            CountNamedComponent(unit, "ask_lift_back") == 0 &&
            CountNamedComponent(unit, "Q_All_Safety_Bar_Locked") == 0;
    }

    private static bool HasSaftyBarIndex(XElement unit, string index)
    {
        foreach (XElement comp in unit.Descendants().Where(e =>
            e.Name.LocalName == "Component" &&
            (string)e.Attribute("Name") == "safty_bar_snr"))
        {
            if (SaftyBarIndex(comp) == index)
            {
                return true;
            }
        }

        return false;
    }

    private static string SaftyBarIndex(XElement snrComp)
    {
        XElement lit = snrComp.Descendants().FirstOrDefault(e => e.Name.LocalName == "ConstantValue");
        return lit == null ? null : lit.Value;
    }

    private static int EnsureAndHasTag(XElement unit, string tag)
    {
        if (CountNamedComponent(unit, tag) > 0)
        {
            return 0;
        }

        XElement flg = unit.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        if (flg == null)
        {
            return 0;
        }

        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        XElement coil = parts == null ? null : parts.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Part" && (string)e.Attribute("Name") == "Coil");
        if (parts == null || wires == null || coil == null)
        {
            return 0;
        }

        string coilUid = (string)coil.Attribute("UId");
        XElement inWire = wires.Elements().FirstOrDefault(w =>
            w.Elements().Any(c =>
                c.Name.LocalName == "NameCon" &&
                (string)c.Attribute("UId") == coilUid &&
                (string)c.Attribute("Name") == "in"));
        if (inWire == null)
        {
            return 0;
        }

        XElement fromOut = inWire.Elements().FirstOrDefault(c =>
            c.Name.LocalName == "NameCon" && (string)c.Attribute("Name") == "out");
        if (fromOut == null)
        {
            return 0;
        }

        int next = NextFlgUid(flg);
        XNamespace ns = flg.Name.Namespace;
        string accessUid = next.ToString();
        string contactUid = (next + 1).ToString();
        parts.Add(new XElement(ns + "Access",
            new XAttribute("Scope", "GlobalVariable"),
            new XAttribute("UId", accessUid),
            new XElement(ns + "Symbol",
                new XElement(ns + "Component", new XAttribute("Name", tag)))));
        parts.Add(new XElement(ns + "Part",
            new XAttribute("Name", "Contact"),
            new XAttribute("UId", contactUid)));

        XElement coilIn = inWire.Elements().First(c =>
            c.Name.LocalName == "NameCon" &&
            (string)c.Attribute("UId") == coilUid &&
            (string)c.Attribute("Name") == "in");
        coilIn.SetAttributeValue("UId", contactUid);
        wires.Add(new XElement(ns + "Wire",
            new XAttribute("UId", (next + 2).ToString()),
            new XElement(ns + "IdentCon", new XAttribute("UId", accessUid)),
            new XElement(ns + "NameCon",
                new XAttribute("UId", contactUid),
                new XAttribute("Name", "operand"))));
        wires.Add(new XElement(ns + "Wire",
            new XAttribute("UId", (next + 3).ToString()),
            new XElement(ns + "NameCon",
                new XAttribute("UId", contactUid),
                new XAttribute("Name", "out")),
            new XElement(ns + "NameCon",
                new XAttribute("UId", coilUid),
                new XAttribute("Name", "in"))));
        return 1;
    }

    private static int EnsureReportFace(XElement unit, string tag, string index)
    {
        foreach (XElement access in unit.Descendants().Where(e => e.Name.LocalName == "Access"))
        {
            List<XElement> comps = access.Descendants().Where(e => e.Name.LocalName == "Component").ToList();
            if (comps.Count == 1 && (string)comps[0].Attribute("Name") == tag)
            {
                return 0;
            }
        }

        XElement flg = unit.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        if (flg == null)
        {
            return 0;
        }

        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        XElement rail = wires == null ? null : wires.Elements().FirstOrDefault(e =>
            e.Elements().Any(c => c.Name.LocalName == "Powerrail"));
        if (parts == null || wires == null || rail == null)
        {
            return 0;
        }

        int next = NextFlgUid(flg);
        XNamespace ns = flg.Name.Namespace;
        string sensorUid = next.ToString();
        string coilAccessUid = (next + 1).ToString();
        string contactUid = (next + 2).ToString();
        string coilUid = (next + 3).ToString();
        parts.Add(new XElement(ns + "Access",
            new XAttribute("Scope", "GlobalVariable"),
            new XAttribute("UId", sensorUid),
            new XElement(ns + "Symbol",
                new XElement(ns + "Component", new XAttribute("Name", tag)))));
        parts.Add(new XElement(ns + "Access",
            new XAttribute("Scope", "GlobalVariable"),
            new XAttribute("UId", coilAccessUid),
            new XElement(ns + "Symbol",
                new XElement(ns + "Component", new XAttribute("Name", "for_dc12_plc")),
                new XElement(ns + "Component", new XAttribute("Name", "Read")),
                new XElement(ns + "Component",
                    new XAttribute("Name", "safty_bar_snr"),
                    new XAttribute("AccessModifier", "Array"),
                    new XElement(ns + "Access",
                        new XAttribute("Scope", "LiteralConstant"),
                        new XElement(ns + "Constant",
                            new XElement(ns + "ConstantType", "DInt"),
                            new XElement(ns + "ConstantValue", index)))))));
        parts.Add(new XElement(ns + "Part",
            new XAttribute("Name", "Contact"),
            new XAttribute("UId", contactUid)));
        parts.Add(new XElement(ns + "Part",
            new XAttribute("Name", "Coil"),
            new XAttribute("UId", coilUid)));
        rail.Add(new XElement(ns + "NameCon",
            new XAttribute("UId", contactUid),
            new XAttribute("Name", "in")));
        wires.Add(new XElement(ns + "Wire",
            new XAttribute("UId", (next + 4).ToString()),
            new XElement(ns + "IdentCon", new XAttribute("UId", sensorUid)),
            new XElement(ns + "NameCon",
                new XAttribute("UId", contactUid),
                new XAttribute("Name", "operand"))));
        wires.Add(new XElement(ns + "Wire",
            new XAttribute("UId", (next + 5).ToString()),
            new XElement(ns + "NameCon",
                new XAttribute("UId", contactUid),
                new XAttribute("Name", "out")),
            new XElement(ns + "NameCon",
                new XAttribute("UId", coilUid),
                new XAttribute("Name", "in"))));
        wires.Add(new XElement(ns + "Wire",
            new XAttribute("UId", (next + 6).ToString()),
            new XElement(ns + "IdentCon", new XAttribute("UId", coilAccessUid)),
            new XElement(ns + "NameCon",
                new XAttribute("UId", coilUid),
                new XAttribute("Name", "operand"))));
        return 1;
    }

    private static int EnsureAskLiftHasS4(XElement root)
    {
        foreach (XElement unit in root.Descendants().Where(IsCompileUnit))
        {
            if (CountNamedComponent(unit, "ask_lift_back") == 0 ||
                !HasSaftyBarIndex(unit, "0"))
            {
                continue;
            }

            if (unit.Descendants().Any(e =>
                e.Name.LocalName == "Component" &&
                (string)e.Attribute("Name") == "S4" &&
                e.Parent != null &&
                e.Parent.Elements().Any(c =>
                    c.Name.LocalName == "Component" &&
                    (string)c.Attribute("Name") == "ask_lift_back")))
            {
                return 0;
            }

            XElement flg = unit.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
            XElement or = flg == null ? null : flg.Descendants().FirstOrDefault(e =>
                e.Name.LocalName == "Part" && (string)e.Attribute("Name") == "O");
            if (flg == null || or == null)
            {
                return 0;
            }

            XElement card = or.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "TemplateValue" && (string)e.Attribute("Name") == "Card");
            XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
            XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
            XElement rail = wires == null ? null : wires.Elements().FirstOrDefault(e =>
                e.Elements().Any(c => c.Name.LocalName == "Powerrail"));
            if (card == null || parts == null || wires == null || rail == null)
            {
                return 0;
            }

            int n = 0;
            int.TryParse(card.Value, out n);
            n++;
            card.Value = n.ToString();
            int next = NextFlgUid(flg);
            XNamespace ns = flg.Name.Namespace;
            string accessUid = next.ToString();
            string contactUid = (next + 1).ToString();
            string orUid = (string)or.Attribute("UId");
            parts.Add(new XElement(ns + "Access",
                new XAttribute("Scope", "LocalVariable"),
                new XAttribute("UId", accessUid),
                new XElement(ns + "Symbol",
                    new XElement(ns + "Component", new XAttribute("Name", "ask_lift_back")),
                    new XElement(ns + "Component", new XAttribute("Name", "S4")))));
            parts.Add(new XElement(ns + "Part",
                new XAttribute("Name", "Contact"),
                new XAttribute("UId", contactUid)));
            rail.Add(new XElement(ns + "NameCon",
                new XAttribute("UId", contactUid),
                new XAttribute("Name", "in")));
            wires.Add(new XElement(ns + "Wire",
                new XAttribute("UId", (next + 2).ToString()),
                new XElement(ns + "IdentCon", new XAttribute("UId", accessUid)),
                new XElement(ns + "NameCon",
                    new XAttribute("UId", contactUid),
                    new XAttribute("Name", "operand"))));
            wires.Add(new XElement(ns + "Wire",
                new XAttribute("UId", (next + 3).ToString()),
                new XElement(ns + "NameCon",
                    new XAttribute("UId", contactUid),
                    new XAttribute("Name", "out")),
                new XElement(ns + "NameCon",
                    new XAttribute("UId", orUid),
                    new XAttribute("Name", "in" + n))));
            return 1;
        }

        return 0;
    }

    private static int NextFlgUid(XElement flg)
    {
        int next = 1;
        foreach (XElement el in flg.Descendants())
        {
            int uid;
            XAttribute attr = el.Attribute("UId");
            if (attr != null && int.TryParse(attr.Value, out uid) && uid >= next)
            {
                next = uid + 1;
            }
        }

        return next;
    }

    private static bool VerifySafetyBarMap(XElement root, bool fourFace)
    {
        List<XElement> locks = root.Descendants().Where(IsCompileUnit).Where(IsAllSafetyBarLockedNet).ToList();
        List<XElement> reports = root.Descendants().Where(IsCompileUnit).Where(IsSafetyBarLockReport).ToList();
        if (locks.Count != 1 || reports.Count != 1)
        {
            Console.WriteLine("  全鎖／回報網數量：" + locks.Count + "／" + reports.Count);
            return false;
        }

        if (fourFace)
        {
            if (CountNamedComponent(locks[0], "I_Snr_Safe_Pin_locked_S3") == 0 ||
                CountNamedComponent(locks[0], "I_Snr_Safe_Pin_locked_S4") == 0)
            {
                Console.WriteLine("  全鎖沒有 S3／S4。");
                return false;
            }
        }
        else if (CountNamedComponent(locks[0], "I_Snr_Safe_Pin_locked_S4") != 0)
        {
            Console.WriteLine("  三面機全鎖不該有 S4。");
            return false;
        }

        Dictionary<string, string> map = SafetyBarReportMap(reports[0]);
        string[] want = fourFace
            ? new[] { "1:S1", "2:S2", "3:S3", "4:S4" }
            : new[] { "1:S1", "2:S2", "3:S3" };
        foreach (string item in want)
        {
            string[] parts = item.Split(':');
            string face;
            if (!map.TryGetValue(parts[0], out face) || face != parts[1])
            {
                Console.WriteLine("  回報 [" + parts[0] + "] 是 " + face + "，要 " + parts[1]);
                return false;
            }
        }

        string extra;
        if (map.TryGetValue("3", out extra) && extra == "S4")
        {
            Console.WriteLine("  [3] 還是 S4。");
            return false;
        }

        return true;
    }

    private static Dictionary<string, string> SafetyBarReportMap(XElement unit)
    {
        var map = new Dictionary<string, string>();
        XElement flg = unit.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        if (flg == null)
        {
            return map;
        }

        Dictionary<string, string> accessFace = new Dictionary<string, string>();
        Dictionary<string, string> accessIndex = new Dictionary<string, string>();
        foreach (XElement access in flg.Descendants().Where(e => e.Name.LocalName == "Access"))
        {
            string uid = (string)access.Attribute("UId");
            if (uid == null || access.Parent == null || access.Parent.Name.LocalName != "Parts")
            {
                continue;
            }

            List<XElement> comps = SymbolComponents(
                access.Elements().FirstOrDefault(e => e.Name.LocalName == "Symbol") ?? access);
            if (comps.Count == 1)
            {
                string name = (string)comps[0].Attribute("Name");
                if (name != null && name.StartsWith("I_Snr_Safe_Pin_locked_S", StringComparison.Ordinal))
                {
                    accessFace[uid] = name.Substring(name.Length - 2);
                }
            }

            XElement snr = comps.FirstOrDefault(c => (string)c.Attribute("Name") == "safty_bar_snr");
            if (snr != null)
            {
                accessIndex[uid] = SaftyBarIndex(snr);
            }
        }

        foreach (XElement coil in flg.Descendants().Where(e =>
            e.Name.LocalName == "Part" && (string)e.Attribute("Name") == "Coil"))
        {
            string coilUid = (string)coil.Attribute("UId");
            string contactUid = null;
            string indexUid = null;
            foreach (XElement wire in flg.Descendants().Where(e => e.Name.LocalName == "Wire"))
            {
                bool toCoilIn = wire.Elements().Any(c =>
                    c.Name.LocalName == "NameCon" &&
                    (string)c.Attribute("UId") == coilUid &&
                    (string)c.Attribute("Name") == "in");
                bool toCoilOp = wire.Elements().Any(c =>
                    c.Name.LocalName == "NameCon" &&
                    (string)c.Attribute("UId") == coilUid &&
                    (string)c.Attribute("Name") == "operand");
                if (toCoilIn)
                {
                    XElement from = wire.Elements().FirstOrDefault(c =>
                        c.Name.LocalName == "NameCon" && (string)c.Attribute("Name") == "out");
                    if (from != null)
                    {
                        contactUid = (string)from.Attribute("UId");
                    }
                }

                if (toCoilOp)
                {
                    XElement ident = wire.Elements().FirstOrDefault(c => c.Name.LocalName == "IdentCon");
                    if (ident != null)
                    {
                        indexUid = (string)ident.Attribute("UId");
                    }
                }
            }

            string sensorUid = null;
            if (contactUid != null)
            {
                foreach (XElement wire in flg.Descendants().Where(e => e.Name.LocalName == "Wire"))
                {
                    if (!wire.Elements().Any(c =>
                        c.Name.LocalName == "NameCon" &&
                        (string)c.Attribute("UId") == contactUid &&
                        (string)c.Attribute("Name") == "operand"))
                    {
                        continue;
                    }

                    XElement ident = wire.Elements().FirstOrDefault(c => c.Name.LocalName == "IdentCon");
                    if (ident != null)
                    {
                        sensorUid = (string)ident.Attribute("UId");
                    }
                }
            }

            string face;
            string index;
            if (sensorUid != null && indexUid != null &&
                accessFace.TryGetValue(sensorUid, out face) &&
                accessIndex.TryGetValue(indexUid, out index))
            {
                map[index] = face;
            }
        }

        return map;
    }

    public static int StripInsideLineIo(TiaPortal portal, Project project)
    {
        return StripInsideUnusedTags(
            project,
            new[] { "Q_LineSpeed_To_DSP", "Q_LineRun_To_Servo" },
            "Inside：刪 Q_LineSpeed_To_DSP／Q_LineRun_To_Servo。DC12 DB 沒這欄，程式也沒接。");
    }

    public static int StripInsideThreeFaceS4(TiaPortal portal, Project project)
    {
        Console.WriteLine("6B／12B 只有三面，刪 S4 硬體 Tag。20B／24B 留到 S4。");

        string[] names =
        {
            "I_Snr_Bobbin_S4",
            "I_Snr_Safe_Pin_Unlocked_S4",
            "I_Snr_Safe_Pin_locked_S4",
            "Q_Safe_Pin_Unlock_S4",
            "Q_Bobbin_Lock_S4",
        };

        int failed = 0;
        foreach (string plcName in InsidePlcNames)
        {
            if (IsInsideFourFace(plcName))
            {
                Console.WriteLine("---- " + plcName + " ---- 四面，S4 留下。");
                continue;
            }

            Console.WriteLine("---- " + plcName + " ----");
            PlcSoftware plc = FindPlc(project, plcName);
            if (plc == null)
            {
                Console.WriteLine("找不到 " + plcName + "。");
                failed++;
                continue;
            }

            foreach (string name in names)
            {
                if (FindTag(plc, name) == null)
                {
                    Console.WriteLine("  已無 " + name);
                    continue;
                }

                if (!TechnologyBuilder.DeleteTag(plc, name))
                {
                    failed++;
                }
            }

            if (CompilePlc(plc) != 0)
            {
                failed++;
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + "）。");
        }

        return failed == 0 ? 0 : 1;
    }

    public static int FixInsideAudit(TiaPortal portal, Project project)
    {
        Console.WriteLine("Inside 審核：復歸改 bobbin_ctrl_code=16；刪開發板／滑環 Q；進來的值回寫 Main；刪 LSP_PTO；刪張力致能回報。1241_PICO 不寫程式。");

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "inside-rtu", "fix-audit");
        Directory.CreateDirectory(dir);

        string[] tags =
        {
            "Q_PicoServer_Check_Wire_Broken_Enable",
            "Q_Pico#1_Tension_Enable",
            "Q_Alarm_Satus_Bit0",
            "Q_Alarm_Satus_Bit1",
            "Q_All_Safety_Bar_Locked",
        };

        int failed = 0;
        foreach (string plcName in InsidePlcNames)
        {
            Console.WriteLine("---- " + plcName + " ----");
            PlcSoftware plc = FindPlc(project, plcName);
            if (plc == null)
            {
                Console.WriteLine("找不到 " + plcName + "。");
                failed++;
                continue;
            }

            string plcDir = Path.Combine(dir, plcName.Replace("25017_", string.Empty));
            Directory.CreateDirectory(plcDir);
            int changed = PatchNamedBlock(
                plc,
                plcDir,
                "Main_Logic",
                PatchInsideAudit,
                VerifyInsideAudit);
            if (changed < 0)
            {
                failed++;
                continue;
            }

            foreach (string name in tags)
            {
                if (FindTag(plc, name) == null)
                {
                    Console.WriteLine("  已無 " + name);
                    continue;
                }

                if (!TechnologyBuilder.DeleteTag(plc, name))
                {
                    failed++;
                }
            }

            DeleteInsideLspPto(plc);

            if (CompilePlc(plc) != 0)
            {
                failed++;
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + "）。");
        }

        return failed == 0 ? 0 : 1;
    }

    private static int PatchInsideAudit(XElement root)
    {
        if (VerifyInsideAudit(root))
        {
            return 0;
        }

        List<XElement> drop = new List<XElement>();
        XElement reset = null;
        XElement echo = null;
        foreach (XElement unit in root.Descendants().Where(IsCompileUnit))
        {
            if (IsSlipRingUnit(unit) || IsBit4ToSnr10Unit(unit))
            {
                drop.Add(unit);
            }

            if (reset == null && IsResetPbUnit(unit))
            {
                reset = unit;
            }

            if (echo == null && IsEchoHandshakeUnit(unit))
            {
                echo = unit;
            }
        }

        int n = 0;
        int nextId = MaxHexId(root) + 16;
        if (reset != null)
        {
            n += InsertGeneratedNetworksBefore(
                root,
                InsideAuditResetSpec(),
                ref nextId,
                reset);
            reset.Remove();
            n++;
        }

        if (echo != null)
        {
            n += InsertGeneratedNetworksBefore(
                root,
                InsideAuditEchoSpec(),
                ref nextId,
                echo);
            echo.Remove();
            n++;
        }

        foreach (XElement unit in drop)
        {
            Console.WriteLine("    刪網：" + CompileUnitTitle(unit));
            unit.Remove();
            n++;
        }

        n += RemoveSectionMembers(
            root,
            "run_status_code",
            "LSP_PTO_norm",
            "EStop",
            "NStop",
            "QStop");
        return n == 0 ? -1 : n;
    }

    private static bool VerifyInsideAudit(XElement root)
    {
        if (CountNamedComponent(root, "run_status_code") > 0 ||
            CountNamedComponent(root, "LSP_PTO_norm") > 0 ||
            CountNamedComponent(root, "Q_Alarm_Satus_Bit0") > 0 ||
            CountNamedComponent(root, "Q_Alarm_Satus_Bit1") > 0 ||
            CountNamedComponent(root, "Q_All_Safety_Bar_Locked") > 0 ||
            CountNamedComponent(root, "Q_PicoServer_Reset") == 0)
        {
            return false;
        }

        XElement reset = root.Descendants().FirstOrDefault(e =>
            IsCompileUnit(e) &&
            CountNamedComponent(e, "bobbin_ctrl_code") > 0 &&
            CountNamedComponent(e, "reset") > 0 &&
            UnitHasLiteral(e, "16"));
        if (reset == null)
        {
            return false;
        }

        if (UnitMovesPath(
                root,
                new[] { "for_dc12_plc", "Read", "ctrl_bit" },
                new[] { "for_dc12_plc", "Write_From_DC12", "ctrl_bit" }) ||
            UnitMovesPath(
                root,
                new[] { "for_dc12_plc", "Read", "rotor_section_id" },
                new[] { "for_dc12_plc", "Write_From_DC12", "rotor_section_id" }))
        {
            return false;
        }

        if (!UnitMovesPath(
                root,
                new[] { "for_dc12_plc", "Write_From_DC12", "ctrl_bit" },
                new[] { "for_dc12_plc", "Read", "ctrl_bit" }) ||
            !UnitMovesPath(
                root,
                new[] { "for_dc12_plc", "Write_From_DC12", "rotor_section_id" },
                new[] { "for_dc12_plc", "Read", "rotor_section_id" }))
        {
            return false;
        }

        return !HasBit4ToSnr10(root);
    }

    private static bool IsSlipRingUnit(XElement unit)
    {
        string title = CompileUnitTitle(unit);
        return title.IndexOf("via slip ring", StringComparison.OrdinalIgnoreCase) >= 0 ||
            CountNamedComponent(unit, "Q_Alarm_Satus_Bit0") > 0 ||
            CountNamedComponent(unit, "Q_Alarm_Satus_Bit1") > 0 ||
            CountNamedComponent(unit, "Q_All_Safety_Bar_Locked") > 0;
    }

    private static bool IsResetPbUnit(XElement unit)
    {
        string title = CompileUnitTitle(unit);
        if (title.Equals("PB groups", StringComparison.OrdinalIgnoreCase) ||
            title.IndexOf("reset from bobbin_ctrl_code", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return CountNamedComponent(unit, "run_status_code") > 0 &&
            CountNamedComponent(unit, "reset") > 0 &&
            unit.Descendants().Any(e =>
                e.Name.LocalName == "Part" &&
                (string)e.Attribute("Name") == "Eq");
    }

    private static bool IsEchoHandshakeUnit(XElement unit)
    {
        bool hasMove = unit.Descendants().Any(e =>
            e.Name.LocalName == "Part" &&
            (string)e.Attribute("Name") == "Move");
        return hasMove &&
            CountNamedComponent(unit, "rotor_section_id") > 0 &&
            (CountNamedComponent(unit, "ctrl_bit") > 0 ||
             CompileUnitTitle(unit).IndexOf("echo received", StringComparison.OrdinalIgnoreCase) >= 0 ||
             CompileUnitTitle(unit).IndexOf("section_id has been received", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool HasBit4ToSnr10(XElement root)
    {
        return root.DescendantsAndSelf().Any(IsBit4ToSnr10Unit);
    }

    private static bool IsBit4ToSnr10Unit(XElement unit)
    {
        return IsCompileUnit(unit) &&
            CountNamedComponent(unit, "ctrl_bit") > 0 &&
            CountNamedComponent(unit, "safty_bar_snr") > 0 &&
            UnitHasLiteral(unit, "10") &&
            unit.Descendants().Any(e =>
                e.Name.LocalName == "Part" &&
                (string)e.Attribute("Name") == "Contact");
    }

    private static bool UnitHasLiteral(XElement unit, string value)
    {
        return unit.Descendants().Any(e =>
            e.Name.LocalName == "ConstantValue" && e.Value == value);
    }

    private static bool UnitMovesPath(XElement root, string[] inPath, string[] outPath)
    {
        foreach (XElement unit in root.Descendants().Where(IsCompileUnit))
        {
            XElement flg = unit.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
            if (flg == null)
            {
                continue;
            }

            XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
            if (parts == null)
            {
                continue;
            }

            Dictionary<string, XElement> accesses = parts.Elements()
                .Where(e => e.Name.LocalName == "Access")
                .ToDictionary(e => (string)e.Attribute("UId") ?? string.Empty, e => e);
            if (accesses.Count == 0)
            {
                continue;
            }

            foreach (XElement move in flg.Descendants().Where(e =>
                e.Name.LocalName == "Part" &&
                (string)e.Attribute("Name") == "Move"))
            {
                string moveUid = (string)move.Attribute("UId");
                string inUid = FindIdentForPort(flg, moveUid, "in");
                string outUid = FindIdentForPort(flg, moveUid, "out1");
                XElement inAccess;
                XElement outAccess;
                if (inUid != null &&
                    outUid != null &&
                    accesses.TryGetValue(inUid, out inAccess) &&
                    accesses.TryGetValue(outUid, out outAccess) &&
                    AccessMatchesPath(inAccess, inPath) &&
                    AccessMatchesPath(outAccess, outPath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string FindIdentForPort(XElement flg, string partUid, string port)
    {
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        if (wires == null)
        {
            return null;
        }

        foreach (XElement wire in wires.Elements().Where(e => e.Name.LocalName == "Wire"))
        {
            bool hit = wire.Elements().Any(e =>
                e.Name.LocalName == "NameCon" &&
                (string)e.Attribute("UId") == partUid &&
                (string)e.Attribute("Name") == port);
            if (!hit)
            {
                continue;
            }

            XElement ident = wire.Elements().FirstOrDefault(e => e.Name.LocalName == "IdentCon");
            if (ident != null)
            {
                return (string)ident.Attribute("UId");
            }
        }

        return null;
    }

    private static bool AccessMatchesPath(XElement access, string[] path)
    {
        XElement symbol = access.Descendants().FirstOrDefault(e => e.Name.LocalName == "Symbol");
        if (symbol == null)
        {
            return false;
        }

        List<XElement> comps = SymbolComponents(symbol);
        if (comps.Count < path.Length)
        {
            return false;
        }

        for (int i = 0; i < path.Length; i++)
        {
            if ((string)comps[i].Attribute("Name") != path[i])
            {
                return false;
            }
        }

        return true;
    }

    private static int RemoveSectionMembers(XElement root, params string[] names)
    {
        HashSet<string> want = new HashSet<string>(names, StringComparer.Ordinal);
        int n = 0;
        foreach (XElement section in root.Descendants().Where(e => e.Name.LocalName == "Section"))
        {
            foreach (XElement member in section.Elements()
                .Where(e => e.Name.LocalName == "Member")
                .ToList())
            {
                string name = (string)member.Attribute("Name");
                if (!want.Contains(name))
                {
                    continue;
                }

                Console.WriteLine("    刪介面：" + name);
                member.Remove();
                n++;
            }
        }

        return n;
    }

    private static string InsideAuditResetSpec()
    {
        return
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<Lad kind=\"FB\" name=\"_ResetCode16\" number=\"1\"><Interface />" +
            "<Network title=\"reset from bobbin_ctrl_code\">" +
            "<Series><Box name=\"Eq\" SrcType=\"Int\">" +
            "<In port=\"in1\" var=\"bobbin_ctrl_code\" />" +
            "<In port=\"in2\" const=\"16\" type=\"Int\" /></Box>" +
            "<Coil var=\"pb.reset\" /></Series></Network></Lad>";
    }

    private static string InsideAuditEchoSpec()
    {
        return
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<Lad kind=\"FB\" name=\"_EchoReceived\" number=\"1\"><Interface />" +
            "<Network title=\"echo received values to Main\">" +
            "<Fork>" +
            "<Series><Box name=\"Move\" eno=\"false\">" +
            "<In port=\"in\" tag=\"for_dc12_plc.Write_From_DC12.ctrl_bit\" />" +
            "<Out port=\"out1\" tag=\"for_dc12_plc.Read.ctrl_bit\" /></Box></Series>" +
            "<Series><Box name=\"Move\" eno=\"false\">" +
            "<In port=\"in\" tag=\"for_dc12_plc.Write_From_DC12.rotor_section_id\" />" +
            "<Out port=\"out1\" tag=\"for_dc12_plc.Read.rotor_section_id\" /></Box></Series>" +
            "<Series><Box name=\"Move\" eno=\"false\">" +
            "<In port=\"in\" var=\"bobbin_ctrl_code\" />" +
            "<Out port=\"out1\" tag=\"for_dc12_plc.Read.bobbin_ctrl_code\" /></Box></Series>" +
            "<Series><Box name=\"Move\" eno=\"false\">" +
            "<In port=\"in\" var=\"safety_bar_ctrl_code\" />" +
            "<Out port=\"out1\" tag=\"for_dc12_plc.Read.safety_bar_ctrl_code\" /></Box></Series>" +
            "</Fork></Network></Lad>";
    }

    private static void DeleteInsideLspPto(PlcSoftware plc)
    {
        List<TechnologicalInstanceDB> items = new List<TechnologicalInstanceDB>();
        CollectTechObjects(plc.TechnologicalObjectGroup, items);
        foreach (TechnologicalInstanceDB item in items)
        {
            Console.WriteLine("  工藝對象：" + item.Name + " DB" + item.Number);
            if (item.Name.IndexOf("LSP_PTO", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            try
            {
                string name = item.Name;
                item.Delete();
                Console.WriteLine("  已刪工藝對象：" + name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  刪工藝對象失敗：" + Flatten(ex));
            }
        }

        PlcBlock leftover = FindBlock(plc, "LSP_PTO");
        if (leftover != null)
        {
            try
            {
                leftover.Delete();
                Console.WriteLine("  已刪區塊 LSP_PTO");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  刪區塊 LSP_PTO 失敗：" + Flatten(ex));
            }
        }
    }

    private static void CollectTechObjects(
        TechnologicalInstanceDBGroup group,
        List<TechnologicalInstanceDB> dest)
    {
        foreach (TechnologicalInstanceDB item in group.TechnologicalObjects)
        {
            dest.Add(item);
        }

        foreach (TechnologicalInstanceDBUserGroup child in group.Groups)
        {
            CollectTechObjects(child, dest);
        }
    }

    public static int StripInsideEncoder(TiaPortal portal, Project project)
    {
        return StripInsideUnusedTags(
            project,
            new[] { "I_Enc_Rotor", "Enc_LSP_Hz" },
            "Inside：刪籠內編碼器 I_Enc_Rotor／Enc_LSP_Hz。功能沒了，程式也沒接。");
    }

    public static int StripInsideRunBits(TiaPortal portal, Project project)
    {
        Console.WriteLine("Inside：運轉狀態 bit 沒了。解碼網拆掉，裝／下紗碼改從 DC12 來，再刪 I／Q Tag。");

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "inside-rtu", "strip-run-bits");
        Directory.CreateDirectory(dir);

        string[] tags =
        {
            "I_S_Run_Status_Bit0",
            "I_S_Run_Status_Bit1",
            "I_S_Run_Status_Bit2",
            "I_S_Run_Status_Bit3",
            "Q_Pico#1_Run_Status_Bit0",
            "Q_Pico#1_Run_Status_Bit1",
            "Q_Pico#1_Run_Status_Bit2",
        };

        int failed = 0;
        foreach (string plcName in InsidePlcNames)
        {
            Console.WriteLine("---- " + plcName + " ----");
            PlcSoftware plc = FindPlc(project, plcName);
            if (plc == null)
            {
                Console.WriteLine("找不到 " + plcName + "。");
                failed++;
                continue;
            }

            string plcDir = Path.Combine(dir, plcName.Replace("25017_", string.Empty));
            Directory.CreateDirectory(plcDir);
            int changed = PatchNamedBlock(
                plc,
                plcDir,
                "Main_Logic",
                PatchInsideRunBits,
                VerifyInsideRunBits);
            if (changed < 0)
            {
                failed++;
                continue;
            }

            foreach (string name in tags)
            {
                if (FindTag(plc, name) == null)
                {
                    Console.WriteLine("  已無 " + name);
                    continue;
                }

                if (!TechnologyBuilder.DeleteTag(plc, name))
                {
                    failed++;
                }
            }

            if (CompilePlc(plc) != 0)
            {
                failed++;
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + "）。");
        }

        return failed == 0 ? 0 : 1;
    }

    private static int PatchInsideRunBits(XElement root)
    {
        if (VerifyInsideRunBits(root))
        {
            return 0;
        }

        List<XElement> drop = new List<XElement>();
        XElement allow = null;
        XElement echo = null;
        foreach (XElement unit in root.Descendants().Where(IsCompileUnit))
        {
            string title = CompileUnitTitle(unit);
            if (title.Equals("decoder", StringComparison.OrdinalIgnoreCase) ||
                CountNamedComponent(unit, "I_S_Run_Status_Bit0") > 0 ||
                CountNamedComponent(unit, "I_S_Run_Status_Bit1") > 0 ||
                CountNamedComponent(unit, "I_S_Run_Status_Bit2") > 0 ||
                CountNamedComponent(unit, "I_S_Run_Status_Bit3") > 0)
            {
                drop.Add(unit);
            }

            if (allow == null &&
                title.IndexOf("allow bobbin", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                allow = unit;
            }

            if (echo == null &&
                title.IndexOf("section_id has been received", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                echo = unit;
            }
        }

        XElement insertBefore = drop.Count > 0
            ? drop[0]
            : root.Descendants().FirstOrDefault(IsCompileUnit);
        if (insertBefore == null)
        {
            Console.WriteLine("  Main_Logic 沒有網路。");
            return -1;
        }

        int n = 0;
        int nextId = MaxHexId(root) + 16;
        n += InsertGeneratedNetworksBefore(
            root,
            InsideRunBitsCopySpec(),
            ref nextId,
            insertBefore);
        if (allow != null)
        {
            InsertGeneratedNetworksBefore(
                root,
                InsideRunBitsAllowSpec(),
                ref nextId,
                allow);
            allow.Remove();
            n++;
        }

        foreach (XElement unit in drop)
        {
            Console.WriteLine("    刪網：" + CompileUnitTitle(unit));
            unit.Remove();
            n++;
        }

        if (echo != null)
        {
            n += RetargetCtrlCodeToRead(echo);
        }

        return n == 0 ? -1 : n;
    }

    private static bool VerifyInsideRunBits(XElement root)
    {
        if (CountNamedComponent(root, "I_S_Run_Status_Bit0") > 0 ||
            CountNamedComponent(root, "I_S_Run_Status_Bit1") > 0 ||
            CountNamedComponent(root, "I_S_Run_Status_Bit2") > 0 ||
            CountNamedComponent(root, "I_S_Run_Status_Bit3") > 0 ||
            CountNamedComponent(root, "Q_Pico#1_Run_Status_Bit0") > 0 ||
            CountNamedComponent(root, "Q_Pico#1_Run_Status_Bit1") > 0 ||
            CountNamedComponent(root, "Q_Pico#1_Run_Status_Bit2") > 0)
        {
            return false;
        }

        if (CountSymbolPath(root, "for_dc12_plc", "Write_From_DC12", "bobbin_ctrl_code") == 0 ||
            CountSymbolPath(root, "for_dc12_plc", "Write_From_DC12", "safety_bar_ctrl_code") == 0 ||
            CountSymbolPath(root, "for_dc12_plc", "Read", "bobbin_ctrl_code") == 0 ||
            CountSymbolPath(root, "for_dc12_plc", "Read", "safety_bar_ctrl_code") == 0)
        {
            return false;
        }

        XElement allow = root.Descendants().FirstOrDefault(e =>
            IsCompileUnit(e) &&
            CompileUnitTitle(e).IndexOf("allow bobbin", StringComparison.OrdinalIgnoreCase) >= 0);
        return allow != null &&
            CountNamedComponent(allow, "bobbin_ctrl_code") > 0 &&
            CountNamedComponent(allow, "run_status_code") == 0;
    }

    private static int RetargetCtrlCodeToRead(XElement unit)
    {
        int n = 0;
        foreach (XElement symbol in unit.Descendants().Where(e => e.Name.LocalName == "Symbol"))
        {
            List<XElement> comps = SymbolComponents(symbol);
            if (comps.Count < 3 ||
                (string)comps[0].Attribute("Name") != "for_dc12_plc" ||
                (string)comps[1].Attribute("Name") != "Write_From_DC12")
            {
                continue;
            }

            string member = (string)comps[2].Attribute("Name");
            if (member != "bobbin_ctrl_code" && member != "safety_bar_ctrl_code")
            {
                continue;
            }

            comps[1].SetAttributeValue("Name", "Read");
            n++;
        }

        return n;
    }

    private static string InsideRunBitsCopySpec()
    {
        return
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<Lad kind=\"FB\" name=\"_CopyDc12Codes\" number=\"1\"><Interface />" +
            "<Network title=\"codes from DC12\">" +
            "<Fork>" +
            "<Series><Box name=\"Move\" eno=\"false\">" +
            "<In port=\"in\" tag=\"for_dc12_plc.Write_From_DC12.bobbin_ctrl_code\" />" +
            "<Out port=\"out1\" var=\"bobbin_ctrl_code\" /></Box></Series>" +
            "<Series><Box name=\"Move\" eno=\"false\">" +
            "<In port=\"in\" tag=\"for_dc12_plc.Write_From_DC12.safety_bar_ctrl_code\" />" +
            "<Out port=\"out1\" var=\"safety_bar_ctrl_code\" /></Box></Series>" +
            "</Fork></Network></Lad>";
    }

    private static string InsideRunBitsAllowSpec()
    {
        return
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<Lad kind=\"FB\" name=\"_AllowBobbin\" number=\"1\"><Interface />" +
            "<Network title=\"allow bobbin loading/unloading\">" +
            "<Series><Parallel>" +
            "<Box name=\"Ne\" SrcType=\"Int\">" +
            "<In port=\"in1\" var=\"bobbin_ctrl_code\" />" +
            "<In port=\"in2\" const=\"0\" type=\"Int\" /></Box>" +
            "<Box name=\"Ne\" SrcType=\"Int\">" +
            "<In port=\"in1\" var=\"safety_bar_ctrl_code\" />" +
            "<In port=\"in2\" const=\"0\" type=\"Int\" /></Box>" +
            "</Parallel>" +
            "<Coil var=\"allow_bobbin_load_unload\" /></Series></Network></Lad>";
    }

    private static int StripInsideUnusedTags(Project project, string[] names, string banner)
    {
        Console.WriteLine(banner);
        int failed = 0;
        foreach (string plcName in InsidePlcNames)
        {
            Console.WriteLine("---- " + plcName + " ----");
            PlcSoftware plc = FindPlc(project, plcName);
            if (plc == null)
            {
                Console.WriteLine("找不到 " + plcName + "。");
                failed++;
                continue;
            }

            foreach (string name in names)
            {
                if (FindTag(plc, name) == null)
                {
                    Console.WriteLine("  已無 " + name);
                    continue;
                }

                if (!TechnologyBuilder.DeleteTag(plc, name))
                {
                    failed++;
                }
            }

            if (CompilePlc(plc) != 0)
            {
                failed++;
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + "）。");
        }

        return failed == 0 ? 0 : 1;
    }

    public static int WireInsideLock(TiaPortal portal, Project project)
    {
        Console.WriteLine("Inside 頂退＋鐵軸：I_Snr_Bobbin 進 Bobbin_Load，Q_Bobbin_Lock 用裝／下紗 S/R。原本有就取代。");

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "inside-rtu", "wire-lock");
        Directory.CreateDirectory(dir);

        int failed = 0;
        foreach (string plcName in InsidePlcNames)
        {
            Console.WriteLine("---- " + plcName + " ----");
            if (WireOneInsideLock(project, plcName, dir) != 0)
            {
                failed++;
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + "）。");
        }

        return failed == 0 ? 0 : 1;
    }

    private static int WireOneInsideLock(Project project, string plcName, string dir)
    {
        PlcSoftware plc = FindPlc(project, plcName);
        if (plc == null)
        {
            Console.WriteLine("找不到 " + plcName + "。");
            return 1;
        }

        bool fourFace = plcName.IndexOf("_20B_", StringComparison.Ordinal) >= 0 ||
            plcName.IndexOf("_24B_", StringComparison.Ordinal) >= 0;
        int faces = fourFace ? 4 : 3;
        string plcDir = Path.Combine(dir, plcName.Replace("25017_", string.Empty));
        Directory.CreateDirectory(plcDir);

        for (int face = 1; face <= faces; face++)
        {
            if (FindTag(plc, "I_Snr_Bobbin_S" + face) == null ||
                FindTag(plc, "Q_Bobbin_Lock_S" + face) == null)
            {
                Console.WriteLine("  缺 I_Snr_Bobbin_S" + face + " 或 Q_Bobbin_Lock_S" + face + "。");
                return 1;
            }
        }

        if (fourFace)
        {
            if (EnsureSectionTotal(plc, 3) < 0)
            {
                return 1;
            }

            try
            {
                EnsureBobbinLoadInstanceDb(plc, "Bobbin_Load_S4_DB");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  建 Bobbin_Load_S4_DB 失敗：" + Flatten(ex));
                return 1;
            }
        }

        int fbChanged = PatchNamedBlock(
            plc,
            plcDir,
            "Bobbin_Load",
            PatchBobbinLoadLockSensors,
            VerifyBobbinLoadLockSensors);
        if (fbChanged < 0)
        {
            return 1;
        }

        int logicChanged = PatchNamedBlock(
            plc,
            plcDir,
            "Main_Logic",
            root => PatchMainLogicLock(root, faces),
            root => VerifyMainLogicLock(root, faces));
        if (logicChanged < 0)
        {
            return 1;
        }

        return 0;
    }

    private static int EnsureSectionTotal(PlcSoftware plc, int value)
    {
        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            foreach (PlcUserConstant constant in table.UserConstants)
            {
                if (!string.Equals(constant.Name, "section_total", StringComparison.Ordinal))
                {
                    continue;
                }

                string want = value.ToString();
                if (string.Equals(constant.Value, want, StringComparison.Ordinal))
                {
                    Console.WriteLine("  section_total 已是 " + want + "。");
                    return 0;
                }

                try
                {
                    constant.Value = want;
                }
                catch
                {
                    constant.SetAttribute("Value", want);
                }

                Console.WriteLine("  section_total " + constant.Value + "（20B／24B 四面，S4 用 [3]）。");
                return 1;
            }
        }

        Console.WriteLine("  找不到 section_total。");
        return -1;
    }

    private static int PatchBobbinLoadLockSensors(XElement root)
    {
        if (VerifyBobbinLoadLockSensors(root))
        {
            return 0;
        }

        int n = 0;
        n += EnsureBobbinLoadSnrMember(root);
        n += EnsureBobbinLoadRetractTemp(root);
        n += EnsureBobbinLoadRetractNet(root);
        n += RewireStepSensor(root, "bobbin_load", "Snr_bobbin");
        n += RewireStepSensor(root, "bobbin_unload", "snr_bobbin_retracted");
        return n == 0 ? -1 : n;
    }

    private static bool VerifyBobbinLoadLockSensors(XElement root)
    {
        return HasAnyInterfaceMember(root, "Snr_bobbin") &&
            HasAnyInterfaceMember(root, "snr_bobbin_retracted") &&
            HasCoilNamed(root, "snr_bobbin_retracted") &&
            StepSensorIs(root, "bobbin_load", "Snr_bobbin") &&
            StepSensorIs(root, "bobbin_unload", "snr_bobbin_retracted");
    }

    private static int EnsureBobbinLoadSnrMember(XElement root)
    {
        if (HasAnyInterfaceMember(root, "Snr_bobbin"))
        {
            return 0;
        }

        XElement air = root.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Member" &&
            (string)e.Attribute("Name") == "Snr_air_pressure_ok");
        if (air == null)
        {
            Console.WriteLine("  Bobbin_Load 找不到 Snr_air_pressure_ok。");
            return 0;
        }

        XElement member = new XElement(air);
        member.SetAttributeValue("Name", "Snr_bobbin");
        XElement comment = member.Descendants().FirstOrDefault(e => e.Name.LocalName == "MultiLanguageText");
        if (comment != null)
        {
            comment.Value = "鐵軸確認";
        }

        air.AddAfterSelf(member);
        return 1;
    }

    private static int EnsureBobbinLoadRetractTemp(XElement root)
    {
        if (HasAnyInterfaceMember(root, "snr_bobbin_retracted"))
        {
            return 0;
        }

        XElement temp = root.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Section" &&
            (string)e.Attribute("Name") == "Temp");
        if (temp == null)
        {
            Console.WriteLine("  Bobbin_Load 找不到 Temp。");
            return 0;
        }

        XNamespace ns = temp.Name.Namespace;
        temp.Add(new XElement(ns + "Member",
            new XAttribute("Name", "snr_bobbin_retracted"),
            new XAttribute("Datatype", "Bool")));
        return 1;
    }

    private static int EnsureBobbinLoadRetractNet(XElement root)
    {
        if (HasCoilNamed(root, "snr_bobbin_retracted"))
        {
            return 0;
        }

        XElement unload = root.Descendants().FirstOrDefault(unit =>
            IsCompileUnit(unit) &&
            (StepSensorIs(unit, "bobbin_unload", "bobbin_unloaded") ||
             StepSensorIs(unit, "bobbin_unload", "snr_bobbin_retracted")));
        if (unload == null)
        {
            Console.WriteLine("  Bobbin_Load 找不到退軸完成網。");
            return 0;
        }

        string spec =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<Lad kind=\"FB\" name=\"_Retract\" number=\"1\"><Interface />" +
            "<Network title=\"鐵軸退到位\">" +
            "<Series><Contact var=\"Snr_bobbin\" negated=\"true\" />" +
            "<Coil var=\"snr_bobbin_retracted\" /></Series>" +
            "</Network></Lad>";
        int nextId = MaxHexId(root) + 16;
        XElement after = unload.ElementsBeforeSelf().LastOrDefault(IsCompileUnit);
        if (after != null)
        {
            return AppendGeneratedNetworks(root, spec, ref nextId, ref after);
        }

        return InsertGeneratedNetworksBefore(root, spec, ref nextId, unload);
    }

    private static int RewireStepSensor(XElement root, string stepLeaf, string sensor)
    {
        int n = 0;
        foreach (XElement flg in root.Descendants().Where(e => e.Name.LocalName == "FlgNet"))
        {
            foreach (XElement call in flg.Descendants().Where(e =>
                e.Name.LocalName == "CallInfo" &&
                (string)e.Attribute("Name") == "Step_Wait_Single_LS"))
            {
                XElement callEl = call.Parent;
                if (callEl == null || callEl.Name.LocalName != "Call")
                {
                    continue;
                }

                XElement stepAccess = FindCallPortAccess(flg, callEl, "step");
                if (stepAccess == null || !AccessEndsWith(stepAccess, stepLeaf))
                {
                    continue;
                }

                XElement sensorAccess = FindCallPortAccess(flg, callEl, "Sensor");
                if (sensorAccess == null)
                {
                    continue;
                }

                XElement comp = sensorAccess.Descendants().FirstOrDefault(e => e.Name.LocalName == "Component");
                if (comp == null || (string)comp.Attribute("Name") == sensor)
                {
                    continue;
                }

                comp.SetAttributeValue("Name", sensor);
                n++;
            }
        }

        return n;
    }

    private static bool StepSensorIs(XElement root, string stepLeaf, string sensor)
    {
        foreach (XElement flg in root.Descendants().Where(e => e.Name.LocalName == "FlgNet"))
        {
            foreach (XElement call in flg.Descendants().Where(e =>
                e.Name.LocalName == "CallInfo" &&
                (string)e.Attribute("Name") == "Step_Wait_Single_LS"))
            {
                XElement callEl = call.Parent;
                if (callEl == null)
                {
                    continue;
                }

                XElement stepAccess = FindCallPortAccess(flg, callEl, "step");
                XElement sensorAccess = FindCallPortAccess(flg, callEl, "Sensor");
                if (stepAccess != null &&
                    sensorAccess != null &&
                    AccessEndsWith(stepAccess, stepLeaf) &&
                    string.Equals(LastComponentName(sensorAccess), sensor, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static XElement FindCallPortAccess(XElement flg, XElement call, string port)
    {
        string callUid = (string)call.Attribute("UId");
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        if (wires == null || parts == null || callUid == null)
        {
            return null;
        }

        XElement wire = wires.Elements().FirstOrDefault(w =>
            w.Elements().Any(c =>
                c.Name.LocalName == "NameCon" &&
                (string)c.Attribute("UId") == callUid &&
                (string)c.Attribute("Name") == port));
        if (wire == null)
        {
            return null;
        }

        XElement ident = wire.Elements().FirstOrDefault(c => c.Name.LocalName == "IdentCon");
        if (ident == null)
        {
            return null;
        }

        string accessUid = (string)ident.Attribute("UId");
        return parts.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Access" &&
            (string)e.Attribute("UId") == accessUid);
    }

    private static bool AccessEndsWith(XElement access, string name)
    {
        return string.Equals(LastComponentName(access), name, StringComparison.Ordinal);
    }

    private static bool HasAnyInterfaceMember(XElement root, string name)
    {
        return root.Descendants().Any(e =>
            e.Name.LocalName == "Member" &&
            (string)e.Attribute("Name") == name);
    }

    private static int PatchMainLogicLock(XElement root, int faces)
    {
        if (VerifyMainLogicLock(root, faces))
        {
            return 0;
        }

        int n = 0;
        List<XElement> loadUnits = root.Descendants()
            .Where(IsCompileUnit)
            .Where(IsBobbinLoadCallUnit)
            .ToList();
        if (loadUnits.Count == 0)
        {
            Console.WriteLine("  找不到 Bobbin_Load 呼叫網。");
            return -1;
        }

        XElement keep = loadUnits[0];
        for (int i = 1; i < loadUnits.Count; i++)
        {
            loadUnits[i].Remove();
            n++;
        }

        if (faces >= 4)
        {
            int added = EnsureBobbinLoadS4Call(keep);
            if (added < 0)
            {
                return -1;
            }

            n += added;
        }

        int wired = EnsureSnrBobbinOnCalls(keep, faces);
        if (wired < 0)
        {
            return -1;
        }

        n += wired;
        n += ReplaceBobbinLockValves(root, keep, faces);
        return n == 0 ? -1 : n;
    }

    private static bool VerifyMainLogicLock(XElement root, int faces)
    {
        for (int face = 1; face <= faces; face++)
        {
            if (CountInstanceCalls(root, "Bobbin_Load_S" + face + "_DB") != 1)
            {
                return false;
            }

            if (CountNamedComponent(root, "I_Snr_Bobbin_S" + face) < 1)
            {
                return false;
            }

            if (!HasCoilNamed(root, "Q_Bobbin_Lock_S" + face))
            {
                return false;
            }
        }

        if (faces < 4 &&
            (CountInstanceCalls(root, "Bobbin_Load_S4_DB") != 0 ||
             CountNamedComponent(root, "Q_Bobbin_Lock_S4") > 0 ||
             CountNamedComponent(root, "I_Snr_Bobbin_S4") > 0))
        {
            return false;
        }

        if (root.Descendants().Where(IsCompileUnit).Count(IsBobbinLoadCallUnit) != 1)
        {
            return false;
        }

        return true;
    }

    private static bool IsBobbinLoadCallUnit(XElement unit)
    {
        XElement flg = unit.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
        XElement net = flg == null
            ? null
            : flg.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        if (net == null)
        {
            net = unit.Elements().SelectMany(e => e.DescendantsAndSelf())
                .FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        }

        if (net == null)
        {
            return false;
        }

        return net.Descendants().Any(e =>
            e.Name.LocalName == "CallInfo" &&
            (string)e.Attribute("Name") == "Bobbin_Load");
    }

    private static int CountInstanceCalls(XElement root, string dbName)
    {
        int n = 0;
        foreach (XElement inst in root.Descendants().Where(e => e.Name.LocalName == "Instance"))
        {
            if (inst.Elements().Any(e =>
                e.Name.LocalName == "Component" &&
                (string)e.Attribute("Name") == dbName))
            {
                n++;
            }
        }

        return n;
    }

    private static XElement FindBobbinLoadCall(XElement root, string dbName)
    {
        foreach (XElement call in root.Descendants().Where(e => e.Name.LocalName == "Call"))
        {
            XElement inst = call.Descendants().FirstOrDefault(e => e.Name.LocalName == "Instance");
            if (inst != null &&
                inst.Elements().Any(e =>
                    e.Name.LocalName == "Component" &&
                    (string)e.Attribute("Name") == dbName))
            {
                return call;
            }
        }

        return null;
    }

    private static int EnsureBobbinLoadS4Call(XElement unit)
    {
        if (FindBobbinLoadCall(unit, "Bobbin_Load_S4_DB") != null)
        {
            return FixS4CallNames(unit);
        }

        XElement src = FindBobbinLoadCall(unit, "Bobbin_Load_S3_DB");
        XElement flg = unit.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        if (src == null || flg == null)
        {
            Console.WriteLine("  找不到 Bobbin_Load_S3_DB，無法補 S4。");
            return -1;
        }

        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        if (parts == null || wires == null)
        {
            return -1;
        }

        string srcUid = (string)src.Attribute("UId");
        int next = NextFlgUid(flg);
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement wire in wires.Elements().ToList())
        {
            if (!WireTouches(wire, srcUid))
            {
                continue;
            }

            foreach (XElement ident in wire.Elements().Where(e => e.Name.LocalName == "IdentCon"))
            {
                string accessUid = (string)ident.Attribute("UId");
                if (accessUid == null || map.ContainsKey(accessUid))
                {
                    continue;
                }

                XElement access = parts.Elements().FirstOrDefault(e =>
                    e.Name.LocalName == "Access" &&
                    (string)e.Attribute("UId") == accessUid);
                if (access == null)
                {
                    continue;
                }

                XElement copy = new XElement(access);
                AssignNewUids(copy, map, ref next);
                ReplaceS3ToS4(copy);
                parts.Add(copy);
            }
        }

        XElement newCall = new XElement(src);
        AssignNewUids(newCall, map, ref next);
        ReplaceS3ToS4(newCall);
        parts.Add(newCall);
        string newUid = (string)newCall.Attribute("UId");

        foreach (XElement wire in wires.Elements().ToList())
        {
            if (!WireTouches(wire, srcUid))
            {
                continue;
            }

            if (wire.Elements().Any(e => e.Name.LocalName == "Powerrail"))
            {
                XNamespace ns = wire.Name.Namespace;
                wire.Add(new XElement(ns + "NameCon",
                    new XAttribute("UId", newUid),
                    new XAttribute("Name", "en")));
                continue;
            }

            XElement copy = new XElement(wire);
            copy.SetAttributeValue("UId", next.ToString());
            next++;
            foreach (XElement con in copy.Elements())
            {
                string uid = (string)con.Attribute("UId");
                if (uid == null)
                {
                    continue;
                }

                if (uid == srcUid)
                {
                    con.SetAttributeValue("UId", newUid);
                }
                else if (map.ContainsKey(uid))
                {
                    con.SetAttributeValue("UId", map[uid]);
                }
                else
                {
                    string fresh = next.ToString();
                    map[uid] = fresh;
                    next++;
                    con.SetAttributeValue("UId", fresh);
                }
            }

            wires.Add(copy);
        }

        return 1;
    }

    private static int FixS4CallNames(XElement unit)
    {
        XElement call = FindBobbinLoadCall(unit, "Bobbin_Load_S4_DB");
        XElement flg = unit.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        if (call == null || flg == null)
        {
            return 0;
        }

        int n = 0;
        string callUid = (string)call.Attribute("UId");
        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        if (parts == null || wires == null)
        {
            return 0;
        }

        foreach (XElement wire in wires.Elements())
        {
            if (!WireTouches(wire, callUid))
            {
                continue;
            }

            foreach (XElement ident in wire.Elements().Where(e => e.Name.LocalName == "IdentCon"))
            {
                XElement access = parts.Elements().FirstOrDefault(e =>
                    e.Name.LocalName == "Access" &&
                    (string)e.Attribute("UId") == (string)ident.Attribute("UId"));
                if (access == null)
                {
                    continue;
                }

                string before = access.ToString(SaveOptions.DisableFormatting);
                ReplaceS3ToS4(access);
                if (access.ToString(SaveOptions.DisableFormatting) != before)
                {
                    n++;
                }
            }
        }

        return n;
    }

    private static bool WireTouches(XElement wire, string uid)
    {
        return wire.Elements().Any(e =>
            (e.Name.LocalName == "NameCon" || e.Name.LocalName == "IdentCon") &&
            (string)e.Attribute("UId") == uid);
    }

    private static void AssignNewUids(XElement el, Dictionary<string, string> map, ref int next)
    {
        foreach (XElement node in el.DescendantsAndSelf())
        {
            XAttribute uid = node.Attribute("UId");
            if (uid == null)
            {
                continue;
            }

            string mapped;
            if (!map.TryGetValue(uid.Value, out mapped))
            {
                mapped = next.ToString();
                map[uid.Value] = mapped;
                next++;
            }

            uid.Value = mapped;
        }
    }

    private static void ReplaceS3ToS4(XElement el)
    {
        foreach (XElement comp in el.Descendants().Where(e => e.Name.LocalName == "Component"))
        {
            string name = (string)comp.Attribute("Name");
            if (name == null)
            {
                continue;
            }

            if (name.IndexOf("_S3", StringComparison.Ordinal) >= 0)
            {
                comp.SetAttributeValue("Name", name.Replace("_S3", "_S4"));
            }
            else if (name == "S3")
            {
                comp.SetAttributeValue("Name", "S4");
            }
        }

        foreach (XElement value in el.Descendants().Where(e => e.Name.LocalName == "ConstantValue"))
        {
            XElement type = value.Parent == null
                ? null
                : value.Parent.Elements().FirstOrDefault(e => e.Name.LocalName == "ConstantType");
            if (type == null)
            {
                continue;
            }

            if (type.Value == "Int" && value.Value == "3")
            {
                value.Value = "4";
            }
            else if (type.Value == "DInt" && value.Value == "2")
            {
                value.Value = "3";
            }
        }
    }

    private static int EnsureSnrBobbinOnCalls(XElement unit, int faces)
    {
        int n = 0;
        for (int face = 1; face <= faces; face++)
        {
            XElement call = FindBobbinLoadCall(unit, "Bobbin_Load_S" + face + "_DB");
            if (call == null)
            {
                Console.WriteLine("  找不到 Bobbin_Load_S" + face + "_DB。");
                return -1;
            }

            n += EnsureCallParameter(call, "Snr_bobbin", "Snr_air_pressure_ok");
            n += EnsureCallWiredTag(unit, call, "Snr_bobbin", "I_Snr_Bobbin_S" + face);
        }

        return n;
    }

    private static int EnsureCallParameter(XElement call, string name, string after)
    {
        XElement info = call.Elements().FirstOrDefault(e => e.Name.LocalName == "CallInfo") ??
            call.Descendants().FirstOrDefault(e => e.Name.LocalName == "CallInfo");
        if (info == null)
        {
            return 0;
        }

        if (info.Elements().Any(e =>
            e.Name.LocalName == "Parameter" &&
            (string)e.Attribute("Name") == name))
        {
            return 0;
        }

        XElement afterEl = info.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Parameter" &&
            (string)e.Attribute("Name") == after);
        XElement param = new XElement(info.Name.Namespace + "Parameter",
            new XAttribute("Name", name),
            new XAttribute("Section", "Input"),
            new XAttribute("Type", "Bool"));
        if (afterEl != null)
        {
            afterEl.AddAfterSelf(param);
        }
        else
        {
            info.Add(param);
        }

        return 1;
    }

    private static int EnsureCallWiredTag(XElement unit, XElement call, string port, string tag)
    {
        XElement flg = call.Ancestors().FirstOrDefault(e => e.Name.LocalName == "FlgNet")
            ?? unit.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        if (flg == null)
        {
            Console.WriteLine("  EnsureCallWiredTag：沒有 FlgNet。");
            return 0;
        }

        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts")
            ?? flg.Descendants().FirstOrDefault(e => e.Name.LocalName == "Parts");
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires")
            ?? flg.Descendants().FirstOrDefault(e => e.Name.LocalName == "Wires");
        string callUid = (string)call.Attribute("UId");
        if (parts == null || wires == null || callUid == null)
        {
            Console.WriteLine("  EnsureCallWiredTag：parts/wires/uid 空。");
            return 0;
        }

        XElement existing = wires.Elements().FirstOrDefault(w =>
            w.Elements().Any(c =>
                c.Name.LocalName == "NameCon" &&
                (string)c.Attribute("UId") == callUid &&
                (string)c.Attribute("Name") == port));
        XElement ident = existing == null
            ? null
            : existing.Elements().FirstOrDefault(c => c.Name.LocalName == "IdentCon");
        XElement access = ident == null
            ? null
            : parts.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "Access" &&
                (string)e.Attribute("UId") == (string)ident.Attribute("UId"));
        XElement comp = access == null
            ? null
            : access.Descendants().FirstOrDefault(e => e.Name.LocalName == "Component");
        if (comp != null && (string)comp.Attribute("Name") == tag)
        {
            foreach (XElement open in existing.Elements().Where(e => e.Name.LocalName == "OpenCon").ToList())
            {
                open.Remove();
            }

            return 0;
        }

        if (comp != null)
        {
            comp.SetAttributeValue("Name", tag);
            foreach (XElement open in existing.Elements().Where(e => e.Name.LocalName == "OpenCon").ToList())
            {
                open.Remove();
            }

            return 1;
        }

        int next = NextFlgUid(flg);
        XNamespace ns = flg.Name.Namespace;
        string accessUid = next.ToString();
        parts.Add(new XElement(ns + "Access",
            new XAttribute("Scope", "GlobalVariable"),
            new XAttribute("UId", accessUid),
            new XElement(ns + "Symbol",
                new XElement(ns + "Component", new XAttribute("Name", tag)))));
        XElement identCon = new XElement(ns + "IdentCon", new XAttribute("UId", accessUid));
        if (existing != null)
        {
            foreach (XElement extra in existing.Elements().Where(e =>
                e.Name.LocalName == "OpenCon" ||
                e.Name.LocalName == "IdentCon").ToList())
            {
                extra.Remove();
            }

            existing.AddFirst(identCon);
            return 1;
        }

        wires.Add(new XElement(ns + "Wire",
            new XAttribute("UId", (next + 1).ToString()),
            identCon,
            new XElement(ns + "NameCon",
                new XAttribute("UId", callUid),
                new XAttribute("Name", port))));
        return 1;
    }

    private static int ReplaceBobbinLockValves(XElement root, XElement afterLoad, int faces)
    {
        int n = 0;
        foreach (XElement unit in root.Descendants().Where(IsCompileUnit).ToList())
        {
            if (IsBobbinLoadCallUnit(unit))
            {
                continue;
            }

            bool lockNet = false;
            for (int face = 1; face <= 4; face++)
            {
                if (CountNamedComponent(unit, "Q_Bobbin_Lock_S" + face) > 0)
                {
                    lockNet = true;
                    break;
                }
            }

            if (!lockNet)
            {
                continue;
            }

            unit.Remove();
            n++;
        }

        bool allPresent = true;
        for (int face = 1; face <= faces; face++)
        {
            if (!HasCoilNamed(root, "Q_Bobbin_Lock_S" + face))
            {
                allPresent = false;
                break;
            }
        }

        if (allPresent)
        {
            return n;
        }

        StringBuilder spec = new StringBuilder();
        spec.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        spec.Append("<Lad kind=\"FB\" name=\"_Lock\" number=\"1\"><Interface />");
        for (int face = 1; face <= faces; face++)
        {
            int idx = face - 1;
            spec.Append("<Network title=\"S").Append(face).Append(" 頂退軸\">");
            spec.Append("<Series><Contact var=\"ask_bobbin_load[").Append(idx).Append("]\" />");
            spec.Append("<SCoil tag=\"Q_Bobbin_Lock_S").Append(face).Append("\" /></Series></Network>");
            spec.Append("<Network title=\"S").Append(face).Append(" 退軸\">");
            spec.Append("<Series><Contact var=\"ask_bobbin_unload[").Append(idx).Append("]\" />");
            spec.Append("<RCoil tag=\"Q_Bobbin_Lock_S").Append(face).Append("\" /></Series></Network>");
        }

        spec.Append("</Lad>");
        int nextId = MaxHexId(root) + 16;
        XElement after = afterLoad;
        n += AppendGeneratedNetworks(root, spec.ToString(), ref nextId, ref after);
        return n;
    }

    public static int RemapMainZigbee(TiaPortal portal, Project project)
    {
        Console.WriteLine("Main：對得到的 Modbus_Zigbee_* 改 for_*_plc。對不到的先留，列出問。");

        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "tcp-main", "remap-zigbee");
        Directory.CreateDirectory(dir);

        List<string> targets = new List<string>();
        foreach (PlcBlock block in EnumerateBlocks(main.BlockGroup))
        {
            if (block.Name.IndexOf("Modbus_Zigbee", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue;
            }

            string before = Path.Combine(dir, SafeFile(block.Name) + "-scan.xml");
            if (!ExportBlockXmlQuiet(block, before))
            {
                continue;
            }

            XElement root = XDocument.Load(before).Root;
            if (CountMainZigbeeSymbols(root) == 0)
            {
                continue;
            }

            targets.Add(block.Name);
            Console.WriteLine("  會改：" + block.Name);
        }

        if (targets.Count == 0)
        {
            Console.WriteLine("沒有區塊還用 Modbus_Zigbee。");
            return 0;
        }

        List<string> patched = new List<string>();
        foreach (string name in targets)
        {
            string before = Path.Combine(dir, SafeFile(name) + "-scan.xml");
            XDocument doc = XDocument.Load(before);
            int changed = RemapMainZigbeeSymbols(doc.Root);
            changed += ReplaceZigbeeText(doc.Root);
            Console.WriteLine("  " + name + " 對到：" + changed);
            string importPath = Path.Combine(dir, SafeFile(name) + ".xml");
            doc.Save(importPath);
            File.Copy(before, Path.Combine(dir, SafeFile(name) + "-before.xml"), true);
            patched.Add(name);
        }

        foreach (string name in patched)
        {
            TryImportBlockFile(main, FindBlock(main, name), Path.Combine(dir, SafeFile(name) + ".xml"));
        }

        if (CompilePlc(main) != 0)
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（Main PLC）。");

        foreach (string name in patched)
        {
            PlcBlock block = FindBlock(main, name);
            string after = Path.Combine(dir, SafeFile(name) + "-after.xml");
            if (block == null || !ExportBlockXmlQuiet(block, after))
            {
                return 1;
            }

            ListRemainingMainZigbee(XDocument.Load(after).Root, name);
        }

        return 0;
    }

    private static string SafeFile(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }

    private static int CountMainZigbeeSymbols(XElement root)
    {
        int n = 0;
        foreach (XElement symbol in root.Descendants().Where(e => e.Name.LocalName == "Symbol"))
        {
            List<XElement> comps = SymbolComponents(symbol);
            if (comps.Count > 0 && IsMainZigbeeDb((string)comps[0].Attribute("Name")))
            {
                n++;
            }
        }

        return n;
    }

    private static bool IsMainZigbeeDb(string name)
    {
        return name != null && name.StartsWith("Modbus_Zigbee_", StringComparison.Ordinal);
    }

    private static string MainZigbeeToForPlc(string zigbeeDb)
    {
        if (zigbeeDb == "Modbus_Zigbee_6B")
        {
            return "for_6B_plc";
        }

        if (zigbeeDb == "Modbus_Zigbee_12B")
        {
            return "for_12B_plc";
        }

        if (zigbeeDb == "Modbus_Zigbee_20B")
        {
            return "for_20B_plc";
        }

        if (zigbeeDb == "Modbus_Zigbee_24B")
        {
            return "for_24B_plc";
        }

        return null;
    }

    private static int RemapMainZigbeeSymbols(XElement root)
    {
        int n = 0;
        foreach (XElement symbol in root.Descendants().Where(e => e.Name.LocalName == "Symbol"))
        {
            List<XElement> comps = SymbolComponents(symbol);
            if (comps.Count < 3 || !IsMainZigbeeDb((string)comps[0].Attribute("Name")))
            {
                continue;
            }

            string dest = MainZigbeeToForPlc((string)comps[0].Attribute("Name"));
            if (dest == null)
            {
                continue;
            }

            string section = (string)comps[1].Attribute("Name");
            string member = (string)comps[2].Attribute("Name");
            string newSection;
            string newMember;
            if (!TryMapMainZigbeeMember(section, member, out newSection, out newMember))
            {
                continue;
            }

            comps[0].SetAttributeValue("Name", dest);
            comps[1].SetAttributeValue("Name", newSection);
            comps[2].SetAttributeValue("Name", newMember);
            n++;
        }

        return n;
    }

    private static bool TryMapMainZigbeeMember(
        string section,
        string member,
        out string newSection,
        out string newMember)
    {
        newSection = null;
        newMember = null;
        if (section == "Write")
        {
            if (member == "rotor_section_id" ||
                member == "bobbin_ctrl_code" ||
                member == "safety_bar_ctrl_code")
            {
                newSection = "Write_From_DC12";
                newMember = member;
                return true;
            }

            if (member == "ctrl_bits_1" || member == "ctrl_bits_2")
            {
                newSection = "Write_From_DC12";
                newMember = "ctrl_bit";
                return true;
            }
        }

        if (section == "Read")
        {
            if (member == "rotor_section_id" ||
                member == "bobbin_ctrl_code" ||
                member == "safety_bar_ctrl_code")
            {
                newSection = "Read";
                newMember = member;
                return true;
            }

            if (member == "ctrl_bits_1" || member == "ctrl_bits_2")
            {
                newSection = "Read";
                newMember = "ctrl_bit";
                return true;
            }

            if (member == "watch_dog")
            {
                newSection = "Read";
                newMember = "plc_WatchDog";
                return true;
            }

            if (member == "bobbin_status_bits_1")
            {
                newSection = "Read";
                newMember = "safty_bar_snr";
                return true;
            }
        }

        return false;
    }

    private static void ListRemainingMainZigbee(XElement root, string blockName)
    {
        HashSet<string> paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement symbol in root.Descendants().Where(e => e.Name.LocalName == "Symbol"))
        {
            List<XElement> comps = SymbolComponents(symbol);
            if (comps.Count < 2 || !IsMainZigbeeDb((string)comps[0].Attribute("Name")))
            {
                continue;
            }

            paths.Add(string.Join(".", comps.Select(c => (string)c.Attribute("Name"))));
        }

        if (paths.Count == 0)
        {
            Console.WriteLine("  " + blockName + " Zigbee 已清。");
            return;
        }

        Console.WriteLine("  " + blockName + " 還留（新表沒欄）：");
        foreach (string path in paths.OrderBy(p => p))
        {
            Console.WriteLine("    " + path);
        }
    }

    private static readonly string[] MainZigbeeDropTitles =
    {
        "check when to read or write bobbin dia",
        "bobbin dia setting",
        "send factors to inside PLC",
        "bobbin status code for HMI",
        "wire broken alarm",
        "tension drive fault",
        "pico comm error"
    };

    private static readonly string[] MainZigbeeDropMembers =
    {
        "wire_tension",
        "bobbin_dia",
        "Current_Line_Speed",
        "Length_counter",
        "wire_broken",
        "drv_fault",
        "pico_comm_err",
        "status_and_error"
    };

    public static int StripMainZigbeeDrop(TiaPortal portal, Project project)
    {
        Console.WriteLine("Main：刪沒欄的 Zigbee（張力／直徑／線速／計尺／斷線／Drive／Pico／status_and_error）與對應 Event。下紗 bits_2 先留。");

        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "tcp-main", "strip-zigbee-drop");
        Directory.CreateDirectory(dir);

        string[] targets = { "Main_Logic", "Event_Control" };
        HashSet<string> dropEvents = new HashSet<string>(StringComparer.Ordinal);
        List<string> patched = new List<string>();
        foreach (string name in targets)
        {
            PlcBlock block = FindBlock(main, name);
            if (block == null)
            {
                Console.WriteLine("找不到 " + name + "。");
                return 1;
            }

            string before = Path.Combine(dir, SafeFile(name) + "-before.xml");
            if (!ExportBlockXmlQuiet(block, before))
            {
                return 1;
            }

            XDocument doc = XDocument.Load(before);
            int changed = StripMainZigbeeDropRoot(doc.Root, dropEvents);
            Console.WriteLine("  " + name + " 改：" + changed + " 網／段");
            string importPath = Path.Combine(dir, SafeFile(name) + ".xml");
            doc.Save(importPath);
            patched.Add(name);
        }

        foreach (string name in patched)
        {
            TryImportBlockFile(main, FindBlock(main, name), Path.Combine(dir, SafeFile(name) + ".xml"));
        }

        if (CompilePlc(main) != 0)
        {
            return 1;
        }

        HashSet<string> stillUsed = ScanPlcForNames(main, dir, dropEvents);
        int deleted = 0;
        foreach (string name in dropEvents.OrderBy(n => n))
        {
            if (stillUsed.Contains(name))
            {
                Console.WriteLine("  Event 還有人用，不刪：" + name);
                continue;
            }

            if (TechnologyBuilder.DeleteTag(main, name))
            {
                deleted++;
            }
        }

        Console.WriteLine("  已刪 Event：" + deleted);
        if (CompilePlc(main) != 0)
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（Main PLC）。");

        foreach (string name in patched)
        {
            PlcBlock block = FindBlock(main, name);
            string after = Path.Combine(dir, SafeFile(name) + "-after.xml");
            if (block == null || !ExportBlockXmlQuiet(block, after))
            {
                return 1;
            }

            ListRemainingMainZigbee(XDocument.Load(after).Root, name);
        }

        return 0;
    }

    public static int StripMainZigbeeLeft(TiaPortal portal, Project project)
    {
        Console.WriteLine("Main：刪剩下的舊 Zigbee 網與沒用的 Zigbee 區塊。上下軸這輪不接。");

        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "tcp-main", "strip-zigbee-left");
        Directory.CreateDirectory(dir);

        List<string> patched = new List<string>();
        foreach (PlcBlock block in EnumerateBlocks(main.BlockGroup).ToList())
        {
            if (block.Name.IndexOf("zigbee", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue;
            }

            string before = Path.Combine(dir, SafeFile(block.Name) + "-scan.xml");
            if (!ExportBlockXmlQuiet(block, before))
            {
                continue;
            }

            XDocument doc = XDocument.Load(before);
            if (CountMainZigbeeSymbols(doc.Root) == 0)
            {
                continue;
            }

            int changed = StripLeftMainZigbeeUnits(doc.Root);
            Console.WriteLine("  " + block.Name + " 刪網：" + changed);
            if (CountMainZigbeeSymbols(doc.Root) > 0)
            {
                ListRemainingMainZigbee(doc.Root, block.Name);
                Console.WriteLine("  " + block.Name + " 還有 Zigbee 符號，未匯入。");
                return 1;
            }

            string importPath = Path.Combine(dir, SafeFile(block.Name) + ".xml");
            doc.Save(importPath);
            File.Copy(before, Path.Combine(dir, SafeFile(block.Name) + "-before.xml"), true);
            patched.Add(block.Name);
        }

        foreach (string name in patched)
        {
            TryImportBlockFile(main, FindBlock(main, name), Path.Combine(dir, SafeFile(name) + ".xml"));
        }

        if (patched.Count > 0 && CompilePlc(main) != 0)
        {
            return 1;
        }

        HashSet<string> zigbeeNames = new HashSet<string>(
            CollectZigbeeNamedBlocks(main),
            StringComparer.Ordinal);
        HashSet<string> stillUsed = ScanPlcForNames(main, dir, zigbeeNames);
        foreach (string name in CollectZigbeeNamedBlocks(main))
        {
            if (stillUsed.Contains(name))
            {
                Console.WriteLine("  程式還在用，不刪：" + name);
                continue;
            }

            TryDeleteNamedBlock(main, name);
        }

        TryDeleteNamedType(main, "Modbus_PLC_48B");

        if (CompilePlc(main) != 0)
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（Main PLC）。");
        ReportZigbeeLeft(project, main, dir);
        return 0;
    }

    public static int StripHmiZigbee(TiaPortal portal, Project project)
    {
        Console.WriteLine("HMI：刪 Zigbee 名 Tag。不 Compile HMI。");

        int deleted = 0;
        int failed = 0;
        foreach (HmiTarget hmi in EnumerateHmiTargets(project))
        {
            int before = CountHmiZigbeeTags(hmi);
            Console.WriteLine("  " + hmi.Name + " 刪前：" + before);
            foreach (TagTable table in hmi.TagFolder.TagTables.ToList())
            {
                foreach (Tag tag in table.Tags.ToList())
                {
                    if (tag.Name.IndexOf("zigbee", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    string name = tag.Name;
                    try
                    {
                        tag.Delete();
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.WriteLine("    刪不掉 " + name + "：" + Flatten(ex));
                    }
                }
            }

            Console.WriteLine("  " + hmi.Name + " 刪後：" + CountHmiZigbeeTags(hmi));
        }

        Console.WriteLine("已刪 HMI Tag：" + deleted + "／失敗 " + failed);
        project.Save();
        Console.WriteLine("已存（HMI Zigbee Tag）。沒有 Compile HMI。");
        return failed == 0 ? 0 : 1;
    }

    public static int ExportFixTfHmi(TiaPortal portal, Project project, bool fixScreens)
    {
        HmiTarget hmi = EnumerateHmiTargets(project).FirstOrDefault(t => t.Name == "HMI_RT_1");
        if (hmi == null)
        {
            Console.WriteLine("找不到 HMI_RT_1。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "hmi-rewire", "tf-live");
        Directory.CreateDirectory(dir);
        Console.WriteLine("匯出 TF HMI：" + hmi.Name + " → " + dir);

        int tables = 0;
        int screens = 0;
        foreach (TagTable table in hmi.TagFolder.TagTables.ToList())
        {
            string path = Path.Combine(dir, "tags-" + SafeFile(table.Name) + ".xml");
            if (ExportHmiXml(table, path))
            {
                tables++;
                Console.WriteLine("  Tag 表 " + table.Name + "：" + path);
                ReportTfTagExport(path);
            }
        }

        foreach (Screen screen in hmi.ScreenFolder.Screens.ToList())
        {
            string path = Path.Combine(dir, "screen-" + SafeFile(screen.Name) + ".xml");
            if (ExportHmiXml(screen, path))
            {
                screens++;
                int oldNames = CountText(path, "24137_TF_PLC");
                int newNames = CountText(path, "25017_TF_PLC");
                Console.WriteLine("  畫面 " + screen.Name + "：24137=" + oldNames + "／25017=" + newNames);
            }
        }

        Console.WriteLine("  軟體連線：" + hmi.Connections.Count);
        foreach (Connection connection in hmi.Connections)
        {
            ExportHmiXml(connection, Path.Combine(dir, "conn-" + SafeFile(connection.Name) + ".xml"));
            Console.WriteLine("    " + connection.Name);
        }

        WriteTfNameMap(dir);
        DumpTfTagLinks(hmi, dir);
        Console.WriteLine("已匯出 Tag 表 " + tables + "／畫面 " + screens + "。");
        if (!fixScreens)
        {
            return 0;
        }

        return ImportTfHmiScreens(portal, project);
    }

    private static void DumpTfTagLinks(HmiTarget hmi, string dir)
    {
        string outDir = Path.Combine(dir, "linked-probe");
        Directory.CreateDirectory(outDir);
        Console.WriteLine("逐顆 WithReadOnly 探針 → " + outDir);
        int linked = 0;
        foreach (TagTable table in hmi.TagFolder.TagTables.ToList())
        {
            foreach (Tag tag in table.Tags.ToList())
            {
                string path = Path.Combine(outDir, SafeFile(tag.Name) + ".xml");
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    tag.Export(new FileInfo(path), ExportOptions.WithDefaults | ExportOptions.WithReadOnly);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  匯出失敗 " + tag.Name + "：" + Flatten(ex));
                    continue;
                }

                string plcAttr = string.Empty;
                try
                {
                    object value = tag.GetAttribute("PlcTag");
                    plcAttr = value == null ? string.Empty : value.ToString();
                }
                catch (Exception ex)
                {
                    plcAttr = "<讀失敗:" + Flatten(ex) + ">";
                }

                string text = File.ReadAllText(path, Encoding.UTF8);
                bool hasController = text.IndexOf("ControllerTag", StringComparison.OrdinalIgnoreCase) >= 0;
                bool hasPlcElement = text.IndexOf("<PlcTag", StringComparison.OrdinalIgnoreCase) >= 0;
                string controllerName = string.Empty;
                try
                {
                    XDocument doc = XDocument.Load(path);
                    XElement ctl = doc.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "ControllerTag");
                    if (ctl != null)
                    {
                        XElement name = ctl.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
                        controllerName = name == null ? "<empty>" : name.Value;
                    }

                    XElement logical = doc.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "LogicalAddress");
                    if (logical != null && !string.IsNullOrWhiteSpace(logical.Value))
                    {
                        Console.WriteLine("  LogicalAddress " + tag.Name + " = " + logical.Value);
                    }
                }
                catch
                {
                }

                if (hasController || hasPlcElement || !string.IsNullOrWhiteSpace(plcAttr))
                {
                    linked++;
                    Console.WriteLine("  LINKED " + tag.Name +
                        " ControllerTag=" + (string.IsNullOrEmpty(controllerName) ? (hasController ? "yes" : "no") : controllerName) +
                        " PlcTagAttr=" + (string.IsNullOrEmpty(plcAttr) ? "<empty>" : plcAttr));
                }
            }
        }

        Console.WriteLine("有連結痕跡的 Tag：" + linked);
    }

    public static int ImportTfHmiScreens(TiaPortal portal, Project project)
    {
        HmiTarget hmi = EnumerateHmiTargets(project).FirstOrDefault(t => t.Name == "HMI_RT_1");
        if (hmi == null)
        {
            Console.WriteLine("找不到 HMI_RT_1。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "hmi-rewire", "tf-live");
        int patched = 0;
        foreach (string path in Directory.GetFiles(dir, "screen-*.xml"))
        {
            if (path.EndsWith("-25017.xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.IndexOf("24137_TF_PLC", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            string next = text.Replace("24137_TF_PLC", "25017_TF_PLC");
            string patchedPath = Path.Combine(
                dir,
                Path.GetFileNameWithoutExtension(path) + "-25017.xml");
            File.WriteAllText(patchedPath, next, new UTF8Encoding(false));
            try
            {
                hmi.ScreenFolder.Screens.Import(new FileInfo(patchedPath), ImportOptions.Override);
                Console.WriteLine("  畫面改名 " + Path.GetFileNameWithoutExtension(path));
                patched++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  畫面匯入失敗 " + Path.GetFileName(path) + "：" + Flatten(ex));
            }
        }

        Console.WriteLine("已改畫面：" + patched);
        project.Save();
        Console.WriteLine("已存（TF 畫面 24137→25017）。");
        return CompileNamedHmi(project, "HMI_RT_1");
    }

    public static int ClearTfRecipeAndP4(TiaPortal portal, Project project)
    {
        Console.WriteLine("TF：不需要 Recipe — 清 P4 RecipeView 參照，並嘗試 Delete Recipe。");
        HmiTarget hmi = FindHmiTargetNamed(project, "HMI_RT_1", "25017_TF_HMI");
        if (hmi == null)
        {
            Console.WriteLine("找不到 HMI_RT_1。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "hmi-rewire", "tf-live", "abs-or-internal");
        Directory.CreateDirectory(dir);

        PatchTfP4RecipeButtons(hmi, dir);
        ClearTfUnusedRecipe(project, hmi, dir);
        // 不走 Internal 軟化：Basic Openness 刪不掉 Recipe，留 Internal 短名／24137 別名是廢物。
        // 官方手冊：Openness 可 Export/Import 清單不含 Recipes；Recipe view = No。
        project.Save();
        Console.WriteLine("已存（P4／Recipe clear）。");
        return CompileNamedHmi(project, "HMI_RT_1");
    }

    /// <summary>
    /// 清掉為 Recipe 編譯權宜建立的 Internal 廢物，並把 bobbin_limit_A/B 恢復 Absolute（%DB4.DBD4/8）。
    /// </summary>
    public static int CleanupTfRecipeJunkTags(TiaPortal portal, Project project)
    {
        Console.WriteLine("TF：刪除 Recipe 權宜 Internal 廢物 Tag，恢復 bobbin_limit Absolute。");
        HmiTarget hmi = FindHmiTargetNamed(project, "HMI_RT_1", "25017_TF_HMI");
        if (hmi == null)
        {
            Console.WriteLine("找不到 HMI_RT_1。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "hmi-rewire", "tf-live", "abs-or-internal");
        Directory.CreateDirectory(dir);

        TagTable table = hmi.TagFolder.TagTables.FirstOrDefault() ?? hmi.TagFolder.DefaultTagTable;
        if (table == null)
        {
            Console.WriteLine("沒有 Tag 表。");
            return 1;
        }

        string[] junk =
        {
            "24137_TF_PLC_PV_bobbin_side_A",
            "24137_TF_PLC_PV_bobbin_side_B"
        };
        foreach (string name in junk)
        {
            Tag tag = FindHmiTagFresh(table, name);
            if (tag == null)
            {
                Console.WriteLine("  無廢物 Tag：" + name);
                continue;
            }

            try
            {
                tag.Delete();
                Console.WriteLine("  已刪：" + name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  刪失敗 " + name + "：" + Flatten(ex));
            }
        }

        // 短名改回 Absolute（畫面／PLC 真綁）
        Dictionary<string, string> restore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "bobbin_limit_A", "%DB4.DBD4" },
            { "bobbin_limit_B", "%DB4.DBD8" }
        };
        foreach (KeyValuePair<string, string> pair in restore)
        {
            if (TrySetHmiTagAbsolute(table, pair.Key, pair.Value, "HMI_Connection_2", dir))
            {
                Console.WriteLine("  已恢復 Absolute：" + pair.Key + " → " + pair.Value);
            }
            else
            {
                Console.WriteLine("  恢復 Absolute 失敗：" + pair.Key);
            }
        }

        project.Save();
        return CompileNamedHmi(project, "HMI_RT_1");
    }

    public static int RepairTfHmi(TiaPortal portal, Project project)
    {
        Console.WriteLine("TF HMI：Absolute 真綁 PLC（Connection+%DB/%I…，不走 Internal／ControllerTag）。");
        Console.WriteLine("尋找 HMI_RT_1（裝置 25017_TF_HMI）…");
        HmiTarget hmi = FindHmiTargetNamed(project, "HMI_RT_1", "25017_TF_HMI");
        Console.WriteLine("尋找 PLC 25017_TF_PLC…");
        PlcSoftware plc = FindPlc(project, "25017_TF_PLC");
        if (hmi == null || plc == null)
        {
            Console.WriteLine("找不到 HMI_RT_1 或 25017_TF_PLC。");
            return 1;
        }

        Console.WriteLine("  已找到 PLC=" + plc.Name + "／HMI=" + hmi.Name + "（略過全專案連線 dump）");

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "hmi-rewire", "tf-live", "abs-or-internal");
        Directory.CreateDirectory(dir);

        Dictionary<string, string> plcTagAddrs = CollectPlcTagAddresses(plc);
        Console.WriteLine("  PLC Tag 位址：" + plcTagAddrs.Count);

        HashSet<string> rests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> tableNames = new List<string>();
        foreach (TagTable t in hmi.TagFolder.TagTables.ToList())
        {
            tableNames.Add(t.Name);
            foreach (Tag tag in t.Tags.ToList())
            {
                string rest;
                if (TryTfHmiTagRest(tag.Name, plc.Name, out rest))
                {
                    rests.Add(rest);
                }
            }
        }

        foreach (KeyValuePair<string, bool> alias in TfHmiAliasNames())
        {
            rests.Add(alias.Key);
        }

        EnsureTfHmiBlocksStandardAccess(plc, rests);
        CompilePlcSoftware(plc, "TF HMI Absolute 前");

        Dictionary<string, string> absByKey = BuildTfAbsoluteAddressMap(plc, plcTagAddrs, rests, dir);
        ApplyTfKnownAbsoluteOverrides(absByKey);
        Console.WriteLine("  Absolute 對照：" + absByKey.Count);

        int abs = 0;
        int keptInternal = 0;
        int aliases = 0;
        foreach (string tableName in tableNames)
        {
            TagTable table = hmi.TagFolder.TagTables.Find(tableName);
            if (table == null)
            {
                Console.WriteLine("  找不到表 " + tableName);
                continue;
            }

            int a;
            int k;
            int n;
            if (!TryAbsoluteTfHmiTagTable(hmi, table, plc.Name, absByKey, dir, out a, out k, out n))
            {
                return 1;
            }

            abs += a;
            keptInternal += k;
            aliases += n;
        }

        Console.WriteLine("已 Absolute：" + abs + "／保留 Internal：" + keptInternal + "／補短名：" + aliases);
        // TF P4 畫面空、不需要 Recipe：清按鈕 RecipeView 參照，並盡力刪掉 Recipe 定義。
        PatchTfP4RecipeButtons(hmi, dir);
        ClearTfUnusedRecipe(project, hmi, dir);
        project.Save();
        Console.WriteLine("已存（TF HMI Absolute／P4 清 RecipeView／清 Recipe）。");

        int compileRc = CompileNamedHmi(project, "HMI_RT_1");
        VerifyTfHmiAbsoluteSample(hmi, dir);
        return compileRc;
    }

    private static bool TryTfHmiTagRest(string tagName, string plcName, out string rest)
    {
        rest = null;
        if (string.IsNullOrEmpty(tagName))
        {
            return false;
        }

        string prefix = plcName + "_";
        if (tagName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = tagName.Substring(prefix.Length);
            return true;
        }

        if (string.Equals(tagName, "pageno", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        rest = tagName;
        return true;
    }

    private static void EnsureTfHmiBlocksStandardAccess(PlcSoftware plc, HashSet<string> rests)
    {
        HashSet<string> blockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlcBlock block in EnumerateBlocks(plc.BlockGroup))
        {
            string lang = block.ProgrammingLanguage.ToString();
            if (lang.IndexOf("DB", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            foreach (string rest in rests)
            {
                if (RestNeedsBlock(rest, block.Name))
                {
                    blockNames.Add(block.Name);
                    break;
                }
            }
        }

        // Latch instance DBs inherit FB optimized flag.
        blockNames.Add("Latch_v2");

        int changed = 0;
        foreach (PlcBlock block in EnumerateBlocks(plc.BlockGroup))
        {
            if (!blockNames.Contains(block.Name) &&
                !string.Equals(block.Name, "Latch_v2", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TrySetEngineeringAttribute(block, "OptimizedAccess", false) |
                TrySetEngineeringAttribute(block, "OptimizedBlockAccess", false) |
                TrySetEngineeringAttribute(block, "MemoryLayout", "Standard"))
            {
                changed++;
                Console.WriteLine("  標準存取：" + block.Name + " #" + block.Number);
            }
        }

        Console.WriteLine("  已設標準存取區塊：" + changed);
    }

    private static bool TrySetEngineeringAttribute(IEngineeringObject target, string name, object value)
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

    private static void CompilePlcSoftware(PlcSoftware plc, string label)
    {
        ICompilable compilable = plc.GetService<ICompilable>();
        if (compilable == null)
        {
            Console.WriteLine("  " + label + "：PLC 不支援編譯。");
            return;
        }

        CompilerResult result = compilable.Compile();
        Console.WriteLine("  " + label + " PLC 編譯：" + result.State +
            "／錯誤 " + result.ErrorCount + "／警告 " + result.WarningCount);
        if (result.ErrorCount > 0)
        {
            PrintCompileMessages(result.Messages, 2);
        }
    }

    private static Dictionary<string, string> BuildTfAbsoluteAddressMap(
        PlcSoftware plc,
        Dictionary<string, string> plcTagAddrs,
        HashSet<string> rests,
        string dir)
    {
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in plcTagAddrs)
        {
            if (LooksAbsolutePlcAddress(pair.Value))
            {
                map[pair.Key] = NormalizeAbsAddr(pair.Value);
            }
        }

        string plcDir = Path.Combine(dir, "abs-db", SafeFile(plc.Name));
        Directory.CreateDirectory(plcDir);

        foreach (PlcBlock block in EnumerateBlocks(plc.BlockGroup))
        {
            string lang = block.ProgrammingLanguage.ToString();
            if (lang.IndexOf("DB", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            bool needed = false;
            foreach (string rest in rests)
            {
                if (RestNeedsBlock(rest, block.Name))
                {
                    needed = true;
                    break;
                }
            }

            if (!needed)
            {
                continue;
            }

            string path = Path.Combine(plcDir, SafeFile(block.Name) + ".xml");
            if (!ExportBlockXmlQuiet(block, path))
            {
                continue;
            }

            try
            {
                if (File.Exists(path))
                {
                    // re-export with readonly for Offset
                    File.Delete(path);
                }

                block.Export(new FileInfo(path), ExportOptions.WithDefaults | ExportOptions.WithReadOnly);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  DB 匯出失敗 " + block.Name + "：" + Flatten(ex));
                continue;
            }

            int dbNumber = block.Number;
            XDocument doc = XDocument.Load(path);
            XElement numberEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Number");
            if (numberEl != null)
            {
                int parsed;
                if (int.TryParse(numberEl.Value, out parsed))
                {
                    dbNumber = parsed;
                }
            }

            int added = AddDbAbsoluteAddresses(doc.Root, block.Name, dbNumber, map);
            Console.WriteLine("  DB " + block.Name + " #" + dbNumber + " Absolute 成員：" + added);
        }

        // HMI array syntax LW{0} → key AErr_LW{0}
        List<string> keys = map.Keys.ToList();
        foreach (string key in keys)
        {
            int bracket = key.LastIndexOf('[');
            if (bracket <= 0 || !key.EndsWith("]", StringComparison.Ordinal))
            {
                continue;
            }

            string bare = key.Substring(0, bracket);
            string index = key.Substring(bracket + 1, key.Length - bracket - 2);
            string braceKey = bare + "{" + index + "}";
            if (!map.ContainsKey(braceKey))
            {
                map[braceKey] = map[key];
            }
        }

        return map;
    }

    private static int AddDbAbsoluteAddresses(
        XElement root,
        string blockName,
        int dbNumber,
        Dictionary<string, string> map)
    {
        XElement iface = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Interface");
        if (iface == null)
        {
            return 0;
        }

        XElement top = iface.Elements().FirstOrDefault(e => e.Name.LocalName == "Sections")
            ?? iface.Descendants().FirstOrDefault(e => e.Name.LocalName == "Sections");
        if (top == null)
        {
            return 0;
        }

        int before = map.Count;
        int bitCursor = 0;
        foreach (XElement section in top.Elements().Where(e => e.Name.LocalName == "Section"))
        {
            string sectionName = (string)section.Attribute("Name");
            if (string.Equals(sectionName, "Temp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sectionName, "Constant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Instance DB: Input/Output/InOut/Static are contiguous in absolute layout.
            WalkDbMembersForAbsolute(section, blockName, dbNumber, map, ref bitCursor);
        }

        return map.Count - before;
    }

    private static void WalkDbMembersForAbsolute(
        XElement parent,
        string prefix,
        int dbNumber,
        Dictionary<string, string> map,
        ref int bitCursor)
    {
        foreach (XElement member in parent.Elements().Where(e => e.Name.LocalName == "Member"))
        {
            string name = (string)member.Attribute("Name");
            string datatype = (string)member.Attribute("Datatype") ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            string path = string.IsNullOrEmpty(prefix) ? name : prefix + "." + name;
            string key = path.Replace('.', '_');

            int offsetBits;
            if (!TryReadMemberOffsetBits(member, out offsetBits))
            {
                AlignCursorForType(ref bitCursor, datatype);
                offsetBits = bitCursor;
            }

            string abs = FormatAbsoluteDbAddress(dbNumber, datatype, offsetBits);
            if (!string.IsNullOrEmpty(abs))
            {
                map[key] = abs;
            }

            if (IsArrayDatatype(datatype))
            {
                int lower;
                int upper;
                string elemType;
                if (TryParseArrayDatatype(datatype, out lower, out upper, out elemType))
                {
                    int elemBits = ElementaryBitSize(elemType);
                    if (elemBits <= 0)
                    {
                        elemBits = 16;
                    }

                    for (int i = lower; i <= upper; i++)
                    {
                        int elemOffset = offsetBits + (i - lower) * elemBits;
                        string elemAbs = FormatAbsoluteDbAddress(dbNumber, elemType, elemOffset);
                        if (string.IsNullOrEmpty(elemAbs))
                        {
                            continue;
                        }

                        string idxKey = key + "[" + i + "]";
                        map[idxKey] = elemAbs;
                        map[key + "{" + i + "}"] = elemAbs;
                    }

                    bitCursor = Math.Max(bitCursor, offsetBits + (upper - lower + 1) * elemBits);
                    continue;
                }
            }

            bool hasNested = member.Elements().Any(e =>
                e.Name.LocalName == "Sections" || e.Name.LocalName == "Section" || e.Name.LocalName == "Member");
            if (hasNested || datatype.IndexOf("Struct", StringComparison.OrdinalIgnoreCase) >= 0 ||
                datatype.StartsWith("\"", StringComparison.Ordinal))
            {
                int nestedCursor = offsetBits;
                foreach (XElement nested in member.Elements().Where(e =>
                    e.Name.LocalName == "Sections" || e.Name.LocalName == "Section" || e.Name.LocalName == "Member"))
                {
                    if (nested.Name.LocalName == "Member")
                    {
                        WalkDbMembersForAbsolute(
                            new XElement("Section", nested),
                            path,
                            dbNumber,
                            map,
                            ref nestedCursor);
                    }
                    else if (nested.Name.LocalName == "Section")
                    {
                        WalkDbMembersForAbsolute(nested, path, dbNumber, map, ref nestedCursor);
                    }
                    else
                    {
                        foreach (XElement section in nested.Elements().Where(e => e.Name.LocalName == "Section"))
                        {
                            WalkDbMembersForAbsolute(section, path, dbNumber, map, ref nestedCursor);
                        }
                    }
                }

                bitCursor = Math.Max(bitCursor, nestedCursor);
            }
            else
            {
                int size = ElementaryBitSize(datatype);
                if (size <= 0)
                {
                    size = 16;
                }

                bitCursor = Math.Max(bitCursor, offsetBits + size);
            }
        }

        foreach (XElement nested in parent.Elements().Where(e =>
            e.Name.LocalName == "Sections" || e.Name.LocalName == "Section"))
        {
            if (nested.Name.LocalName == "Section")
            {
                string sectionName = (string)nested.Attribute("Name");
                if (string.Equals(sectionName, "Temp", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(sectionName, "Constant", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            WalkDbMembersForAbsolute(nested, prefix, dbNumber, map, ref bitCursor);
        }
    }

    private static bool TryReadMemberOffsetBits(XElement member, out int offsetBits)
    {
        offsetBits = 0;
        foreach (XElement attr in member.Descendants().Where(e => e.Name.LocalName == "IntegerAttribute"))
        {
            string name = (string)attr.Attribute("Name");
            if (!string.Equals(name, "Offset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return int.TryParse(attr.Value, out offsetBits);
        }

        return false;
    }

    private static void AlignCursorForType(ref int bitCursor, string datatype)
    {
        int size = ElementaryBitSize(datatype);
        if (size >= 16)
        {
            int rem = bitCursor % 8;
            if (rem != 0)
            {
                bitCursor += 8 - rem;
            }
        }

        if (size >= 32)
        {
            int rem = bitCursor % 16;
            // Real/DInt prefer even-byte then 4-byte; simplify: align to 32-bit boundary in bits
            rem = bitCursor % 32;
            if (rem != 0)
            {
                bitCursor += 32 - rem;
            }
        }
        else if (size >= 16)
        {
            int rem = bitCursor % 16;
            if (rem != 0)
            {
                bitCursor += 16 - rem;
            }
        }
    }

    private static int ElementaryBitSize(string datatype)
    {
        if (string.IsNullOrEmpty(datatype))
        {
            return 0;
        }

        string t = datatype.Trim().Trim('"');
        if (t.Equals("Bool", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (t.Equals("Byte", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("USInt", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("SInt", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Char", StringComparison.OrdinalIgnoreCase))
        {
            return 8;
        }

        if (t.Equals("Int", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("UInt", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Word", StringComparison.OrdinalIgnoreCase))
        {
            return 16;
        }

        if (t.Equals("DInt", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("UDInt", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Real", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("DWord", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Time", StringComparison.OrdinalIgnoreCase))
        {
            return 32;
        }

        if (t.Equals("LReal", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("LInt", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("ULInt", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("LWord", StringComparison.OrdinalIgnoreCase))
        {
            return 64;
        }

        return 0;
    }

    private static bool IsArrayDatatype(string datatype)
    {
        return !string.IsNullOrEmpty(datatype) &&
               datatype.StartsWith("Array[", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseArrayDatatype(string datatype, out int lower, out int upper, out string elemType)
    {
        lower = 0;
        upper = 0;
        elemType = string.Empty;
        // Array[0..3] of Word
        Match m = Regex.Match(
            datatype,
            @"^Array\[(\d+)\.\.(\d+)\]\s+of\s+(.+)$",
            RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return false;
        }

        lower = int.Parse(m.Groups[1].Value);
        upper = int.Parse(m.Groups[2].Value);
        elemType = m.Groups[3].Value.Trim().Trim('"');
        return true;
    }

    private static string FormatAbsoluteDbAddress(int dbNumber, string datatype, int offsetBits)
    {
        string t = (datatype ?? string.Empty).Trim().Trim('"');
        if (IsArrayDatatype(t))
        {
            string elem;
            int lo;
            int hi;
            if (TryParseArrayDatatype(t, out lo, out hi, out elem))
            {
                t = elem;
            }
        }

        int size = ElementaryBitSize(t);
        if (size == 1)
        {
            int byteOffset = offsetBits / 8;
            int bit = offsetBits % 8;
            return "%DB" + dbNumber + ".DBX" + byteOffset + "." + bit;
        }

        if (size == 8)
        {
            return "%DB" + dbNumber + ".DBB" + (offsetBits / 8);
        }

        if (size == 16)
        {
            return "%DB" + dbNumber + ".DBW" + (offsetBits / 8);
        }

        if (size == 32)
        {
            return "%DB" + dbNumber + ".DBD" + (offsetBits / 8);
        }

        if (size == 64)
        {
            return "%DB" + dbNumber + ".DBD" + (offsetBits / 8);
        }

        // structs / UDT: expose base byte address as DBX for debugging only — skip empty
        return null;
    }

    private static bool TryAbsoluteTfHmiTagTable(
        HmiTarget hmi,
        TagTable table,
        string plcName,
        Dictionary<string, string> absByKey,
        string dir,
        out int absoluteCount,
        out int internalCount,
        out int aliases)
    {
        absoluteCount = 0;
        internalCount = 0;
        aliases = 0;
        string tableName = table.Name;
        string xml = Path.Combine(dir, "table-" + SafeFile(tableName) + ".xml");
        try
        {
            if (File.Exists(xml))
            {
                File.Delete(xml);
            }

            table.Export(new FileInfo(xml), ExportOptions.WithDefaults);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯出表失敗 " + tableName + "：" + Flatten(ex));
            return false;
        }

        XDocument doc = XDocument.Load(xml);
        XElement tableEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Tag.TagTable");
        XElement objectList = tableEl == null
            ? null
            : tableEl.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
        if (objectList == null)
        {
            Console.WriteLine("  表 XML 沒有 ObjectList：" + tableName);
            return false;
        }

        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        XElement boolSample = null;
        XElement realSample = null;
        foreach (XElement tagEl in objectList.Elements().Where(e => e.Name.LocalName == "Hmi.Tag.Tag").ToList())
        {
            string tagName = ReadHmiTagXmlName(tagEl);
            if (!string.IsNullOrEmpty(tagName))
            {
                names.Add(tagName);
            }

            string typeName = ReadHmiTagXmlDataType(tagEl);
            if (string.Equals(typeName, "Bool", StringComparison.OrdinalIgnoreCase) && boolSample == null)
            {
                boolSample = tagEl;
            }

            if (string.Equals(typeName, "Real", StringComparison.OrdinalIgnoreCase) && realSample == null)
            {
                realSample = tagEl;
            }

            string abs;
            if (TryResolveTfHmiAbsoluteFromMap(tagName, plcName, absByKey, out abs))
            {
                if (ApplyAbsoluteToHmiTagElement(tagEl, abs, "HMI_Connection_2"))
                {
                    absoluteCount++;
                    Console.WriteLine("  ABS " + tagName + " → " + abs);
                }
            }
            else if (string.Equals(tagName, "pageno", StringComparison.OrdinalIgnoreCase))
            {
                InternalizeHmiTagElement(tagEl);
                internalCount++;
            }
            else
            {
                Console.WriteLine("  無 Absolute，略過：" + tagName);
            }
        }

        int nextId = 9000;
        foreach (KeyValuePair<string, bool> alias in TfHmiAliasNames())
        {
            XElement sample = alias.Value ? realSample : boolSample;
            if (sample == null)
            {
                Console.WriteLine("  無樣板，略過 " + alias.Key);
                continue;
            }

            string abs;
            bool haveAbs = TryResolveTfHmiAbsoluteFromMap(alias.Key, plcName, absByKey, out abs);
            if (!haveAbs && alias.Key.StartsWith("bobbin_limit_A", StringComparison.OrdinalIgnoreCase))
            {
                haveAbs = TryResolveTfHmiAbsoluteFromMap(
                    "25017_TF_PLC_Traverser_Control_E_Gear_TF_DB_config_bobbin_limit_A",
                    plcName,
                    absByKey,
                    out abs);
            }
            else if (!haveAbs && alias.Key.StartsWith("bobbin_limit_B", StringComparison.OrdinalIgnoreCase))
            {
                haveAbs = TryResolveTfHmiAbsoluteFromMap(
                    "25017_TF_PLC_Traverser_Control_E_Gear_TF_DB_config_bobbin_limit_B",
                    plcName,
                    absByKey,
                    out abs);
            }

            if (!names.Contains(alias.Key))
            {
                XElement clone = new XElement(sample);
                RenumberXmlIds(clone, ref nextId);
                RenameHmiTagXml(clone, ReadHmiTagXmlName(sample), alias.Key);
                if (haveAbs)
                {
                    ApplyAbsoluteToHmiTagElement(clone, abs, "HMI_Connection_2");
                }
                else
                {
                    InternalizeHmiTagElement(clone);
                }

                objectList.Add(clone);
                aliases++;
                Console.WriteLine("  XML 補短名 " + alias.Key + (haveAbs ? (" → " + abs) : " (Internal)"));
            }
            else if (haveAbs)
            {
                XElement existing = objectList.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "Hmi.Tag.Tag" &&
                        string.Equals(ReadHmiTagXmlName(e), alias.Key, StringComparison.OrdinalIgnoreCase));
                if (existing != null && ApplyAbsoluteToHmiTagElement(existing, abs, "HMI_Connection_2"))
                {
                    absoluteCount++;
                    Console.WriteLine("  ABS 短名 " + alias.Key + " → " + abs);
                }
            }
        }

        string patched = Path.Combine(dir, "table-" + SafeFile(tableName) + "-absolute.xml");
        doc.Save(patched);
        try
        {
            hmi.TagFolder.TagTables.Import(new FileInfo(patched), ImportOptions.Override);
            Console.WriteLine("  匯入表 " + tableName + " Absolute=" + absoluteCount +
                " Internal=" + internalCount + " 短名=" + aliases);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯入表失敗 " + tableName + "：" + Flatten(ex));
            return false;
        }
    }

    private static bool TryResolveTfHmiAbsoluteFromMap(
        string tagName,
        string plcName,
        Dictionary<string, string> absByKey,
        out string address)
    {
        address = null;
        if (string.IsNullOrEmpty(tagName))
        {
            return false;
        }

        string rest;
        if (!TryTfHmiTagRest(tagName, plcName, out rest))
        {
            rest = tagName;
        }

        if (absByKey.TryGetValue(rest, out address))
        {
            return true;
        }

        if (absByKey.TryGetValue(tagName, out address))
        {
            return true;
        }

        // Traverser_Control_E_Gear_TF_DB_config_bobbin_limit_A → path key
        if (absByKey.TryGetValue(rest.Replace('{', '[').Replace('}', ']'), out address))
        {
            return true;
        }

        return false;
    }

    private static bool ApplyAbsoluteToHmiTagElement(XElement tagEl, string absoluteAddress, string connectionName)
    {
        if (tagEl == null || string.IsNullOrEmpty(absoluteAddress))
        {
            return false;
        }

        XElement attrs = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
        if (attrs == null)
        {
            return false;
        }

        SetOrAddAttr(attrs, "AddressAccessMode", "Absolute");
        SetOrAddAttr(attrs, "LogicalAddress", absoluteAddress);

        XElement linkList = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
        if (linkList == null)
        {
            linkList = new XElement("LinkList");
            tagEl.Add(linkList);
        }

        foreach (XElement old in linkList.Elements().Where(e => e.Name.LocalName == "ControllerTag").ToList())
        {
            old.Remove();
        }

        EnsureHmiConnectionLink(linkList, connectionName);

        // HMI DataType must stay HMI-side; keep DataType = HmiDataType when present
        XElement hmiType = linkList.Elements().FirstOrDefault(e => e.Name.LocalName == "HmiDataType");
        XElement dataType = linkList.Elements().FirstOrDefault(e => e.Name.LocalName == "DataType");
        XElement hmiTypeName = hmiType == null
            ? null
            : hmiType.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
        XElement dataTypeName = dataType == null
            ? null
            : dataType.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
        if (hmiTypeName != null && dataTypeName != null && !string.IsNullOrEmpty(hmiTypeName.Value))
        {
            // Absolute external tags use PLC-compatible DataType names already on HmiDataType
            dataTypeName.Value = hmiTypeName.Value;
        }

        return true;
    }

    private static void VerifyTfHmiAbsoluteSample(HmiTarget hmi, string dir)
    {
        TagTable table = hmi.TagFolder.TagTables.Find("Default tag table") ?? hmi.TagFolder.DefaultTagTable;
        if (table == null)
        {
            return;
        }

        string sampleName = "25017_TF_PLC_Motors_TF_Mtr_Info_RPM";
        Tag tag = FindHmiTagFresh(table, sampleName);
        if (tag == null)
        {
            Console.WriteLine("  驗證：找不到 " + sampleName);
            return;
        }

        string verify = Path.Combine(dir, "verify-" + SafeFile(sampleName) + ".xml");
        try
        {
            if (File.Exists(verify))
            {
                File.Delete(verify);
            }

            tag.Export(new FileInfo(verify), ExportOptions.WithDefaults | ExportOptions.WithReadOnly);
            string text = File.ReadAllText(verify, Encoding.UTF8);
            bool hasConn = text.IndexOf("HMI_Connection_2", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasAbs = text.IndexOf("%DB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          text.IndexOf("AddressAccessMode>Absolute", StringComparison.OrdinalIgnoreCase) >= 0;
            bool emptyLogical = Regex.IsMatch(text, @"<LogicalAddress\s*/>") ||
                                Regex.IsMatch(text, @"<LogicalAddress></LogicalAddress>");
            Console.WriteLine("  驗證 " + sampleName + "：Connection=" + hasConn +
                " Absolute=" + hasAbs + " LogicalEmpty=" + emptyLogical + " → " + verify);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  驗證失敗：" + Flatten(ex));
        }
    }

    private static bool TryInternalizeTfHmiTagTable(
        HmiTarget hmi,
        TagTable table,
        string dir,
        out int internalized,
        out int aliases)
    {
        internalized = 0;
        aliases = 0;
        string tableName = table.Name;
        string xml = Path.Combine(dir, "table-" + SafeFile(tableName) + ".xml");
        try
        {
            if (File.Exists(xml))
            {
                File.Delete(xml);
            }

            table.Export(new FileInfo(xml), ExportOptions.WithDefaults);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯出表失敗 " + tableName + "：" + Flatten(ex));
            return false;
        }

        XDocument doc = XDocument.Load(xml);
        XElement tableEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Tag.TagTable");
        XElement objectList = tableEl == null
            ? null
            : tableEl.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
        if (objectList == null)
        {
            Console.WriteLine("  表 XML 沒有 ObjectList：" + tableName);
            return false;
        }

        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        XElement boolSample = null;
        XElement realSample = null;
        foreach (XElement tagEl in objectList.Elements().Where(e => e.Name.LocalName == "Hmi.Tag.Tag").ToList())
        {
            XElement attrs = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            XElement nameEl = attrs == null
                ? null
                : attrs.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
            if (nameEl != null && !string.IsNullOrEmpty(nameEl.Value))
            {
                names.Add(nameEl.Value);
            }

            if (InternalizeHmiTagElement(tagEl))
            {
                internalized++;
            }

            string typeName = ReadHmiTagXmlDataType(tagEl);
            if (string.Equals(typeName, "Bool", StringComparison.OrdinalIgnoreCase) && boolSample == null)
            {
                boolSample = tagEl;
            }

            if (string.Equals(typeName, "Real", StringComparison.OrdinalIgnoreCase) && realSample == null)
            {
                realSample = tagEl;
            }
        }

        int nextId = 9000;
        foreach (KeyValuePair<string, bool> alias in TfHmiAliasNames())
        {
            if (names.Contains(alias.Key))
            {
                continue;
            }

            XElement sample = alias.Value ? realSample : boolSample;
            if (sample == null)
            {
                Console.WriteLine("  無樣板，略過 " + alias.Key);
                continue;
            }

            XElement clone = new XElement(sample);
            RenumberXmlIds(clone, ref nextId);
            RenameHmiTagXml(clone, ReadHmiTagXmlName(sample), alias.Key);
            InternalizeHmiTagElement(clone);
            objectList.Add(clone);
            aliases++;
            Console.WriteLine("  XML 補短名 " + alias.Key);
        }

        string patched = Path.Combine(dir, "table-" + SafeFile(tableName) + "-internal.xml");
        doc.Save(patched);
        try
        {
            hmi.TagFolder.TagTables.Import(new FileInfo(patched), ImportOptions.Override);
            Console.WriteLine("  匯入表 " + tableName + " Internal=" + internalized + " 短名=" + aliases);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯入表失敗 " + tableName + "：" + Flatten(ex));
            return false;
        }
    }

    private static void RenumberXmlIds(XElement root, ref int nextId)
    {
        foreach (XElement el in root.DescendantsAndSelf())
        {
            XAttribute id = el.Attribute("ID");
            if (id == null)
            {
                continue;
            }

            id.Value = nextId.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
            nextId++;
        }
    }

    private static Dictionary<string, bool> TfHmiAliasNames()
    {
        return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            { "bobbin_limit_A", true },
            { "bobbin_limit_B", true },
            { "AErr_MechanicalWidth_Max", false },
            { "AErr_EStop_PB_Act", false },
            { "AErr_Rail_Limit_A", false },
            { "AErr_Rail_Limit_B", false },
            { "QErr_LiftUp_Limit_A", false },
            { "QErr_LiftUp_Limit_B", false },
            { "QErr_LiftDown_Limit_A", false },
            { "QErr_LiftDown_Limit_B", false },
            { "QErr_Motor_Fan_OvrLd", false },
            { "NErr_Lift_Lock_Motor_OvrLd", false },
            { "EErr_TK_Motor_Overload", false },
            { "EErr_TK_Drv_Over_Heat", false },
            { "EErr_Bobbin_is_Not_Clamped", false },
            { "EErr_Trv_Drive_Fault", false },
            { "EErr_Takeup_Drive_Fault", false },
            { "EErr_EStop_Loop_Act", false }
        };
    }

    private static bool InternalizeHmiTagElement(XElement tagEl)
    {
        XElement attrs = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
        if (attrs != null)
        {
            SetOrAddAttr(attrs, "LogicalAddress", string.Empty);
        }

        XElement linkList = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
        if (linkList == null)
        {
            return false;
        }

        List<XElement> drop = linkList.Elements().Where(e =>
            e.Name.LocalName == "ControllerTag" || e.Name.LocalName == "Connection").ToList();
        if (drop.Count == 0)
        {
            return false;
        }

        foreach (XElement old in drop)
        {
            old.Remove();
        }

        XElement hmiType = linkList.Elements().FirstOrDefault(e => e.Name.LocalName == "HmiDataType");
        XElement dataType = linkList.Elements().FirstOrDefault(e => e.Name.LocalName == "DataType");
        XElement hmiTypeName = hmiType == null
            ? null
            : hmiType.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
        XElement dataTypeName = dataType == null
            ? null
            : dataType.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
        if (hmiTypeName != null && dataTypeName != null && !string.IsNullOrEmpty(hmiTypeName.Value))
        {
            dataTypeName.Value = hmiTypeName.Value;
        }

        return true;
    }

    private static string ReadHmiTagXmlName(XElement tagEl)
    {
        XElement attrs = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
        XElement nameEl = attrs == null
            ? null
            : attrs.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
        return nameEl == null ? string.Empty : nameEl.Value;
    }

    private static string ReadHmiTagXmlDataType(XElement tagEl)
    {
        XElement linkList = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
        if (linkList == null)
        {
            return string.Empty;
        }

        XElement dataType = linkList.Elements().FirstOrDefault(e => e.Name.LocalName == "DataType");
        XElement name = dataType == null
            ? null
            : dataType.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
        return name == null ? string.Empty : name.Value;
    }

    private static void RenameHmiTagXml(XElement tagEl, string oldName, string newName)
    {
        if (string.IsNullOrEmpty(oldName))
        {
            return;
        }

        foreach (XElement el in tagEl.Descendants().Where(e => e.Name.LocalName == "Name" && e.Value == oldName))
        {
            el.Value = newName;
        }
    }

    private static void PatchTfP4RecipeButtons(HmiTarget hmi, string dir)
    {
        Screen screen = hmi.ScreenFolder.Screens.Find("P4_Recipes");
        if (screen == null)
        {
            Console.WriteLine("  找不到畫面 P4_Recipes。");
            return;
        }

        string path = Path.Combine(dir, "screen-P4_Recipes-live.xml");
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            screen.Export(new FileInfo(path), ExportOptions.WithDefaults);
            XDocument doc = XDocument.Load(path);
            int removed = 0;
            // Classic HMI XML 節點名是 Hmi.Event.FunctionListEntry（LocalName 含點），不能只比 == "FunctionListEntry"。
            foreach (XElement entry in doc.Descendants()
                .Where(e => e.Name.LocalName == "FunctionListEntry" ||
                            e.Name.LocalName.EndsWith(".FunctionListEntry", StringComparison.Ordinal))
                .ToList())
            {
                string fn = ReadXmlAttrName(entry);
                bool drop = fn.StartsWith("RecipeView", StringComparison.OrdinalIgnoreCase);
                if (!drop)
                {
                    foreach (XElement linkName in entry.Descendants()
                        .Where(e => e.Name.LocalName == "Name" || e.Name.LocalName.EndsWith(".Name", StringComparison.Ordinal)))
                    {
                        if (string.Equals(linkName.Value, "Recipe view_1", StringComparison.OrdinalIgnoreCase) ||
                            linkName.Value.IndexOf("Recipe view", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            drop = true;
                            break;
                        }
                    }
                }

                if (drop)
                {
                    entry.Remove();
                    removed++;
                }
            }

            string patched = Path.Combine(dir, "screen-P4_Recipes-nopreview.xml");
            doc.Save(patched);
            hmi.ScreenFolder.Screens.Import(new FileInfo(patched), ImportOptions.Override);
            Console.WriteLine("  P4 拿掉 RecipeView／Recipe view 參照：" + removed);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  P4 修畫面失敗：" + Flatten(ex));
        }
    }

    private static string ReadXmlAttrName(XElement entry)
    {
        XElement attrs = entry.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "AttributeList" ||
                                 e.Name.LocalName.EndsWith(".AttributeList", StringComparison.Ordinal));
        if (attrs == null)
        {
            return string.Empty;
        }

        XElement nameEl = attrs.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Name" ||
                                 e.Name.LocalName.EndsWith(".Name", StringComparison.Ordinal));
        return nameEl == null ? string.Empty : nameEl.Value;
    }

    private static void ApplyTfKnownAbsoluteOverrides(Dictionary<string, string> absByKey)
    {
        // Traverser_Control_E_Gear_TF_DB #4：config.pitch_now Real@0，config.bobbin.limit_A@4，limit_B@8
        // 自動版面推算曾把 limit_A 誤算成與 pitch_now 同址。
        string[] pitchKeys =
        {
            "Traverser_Control_E_Gear_TF_DB_config_pitch_now",
            "25017_TF_PLC_Traverser_Control_E_Gear_TF_DB_config_pitch_now"
        };
        string[] limitAKeys =
        {
            "Traverser_Control_E_Gear_TF_DB_config_bobbin_limit_A",
            "25017_TF_PLC_Traverser_Control_E_Gear_TF_DB_config_bobbin_limit_A",
            "bobbin_limit_A"
        };
        string[] limitBKeys =
        {
            "Traverser_Control_E_Gear_TF_DB_config_bobbin_limit_B",
            "25017_TF_PLC_Traverser_Control_E_Gear_TF_DB_config_bobbin_limit_B",
            "bobbin_limit_B"
        };

        foreach (string key in pitchKeys)
        {
            absByKey[key] = "%DB4.DBD0";
        }

        foreach (string key in limitAKeys)
        {
            absByKey[key] = "%DB4.DBD4";
        }

        foreach (string key in limitBKeys)
        {
            absByKey[key] = "%DB4.DBD8";
        }

        Console.WriteLine("  覆寫 Traverser bobbin_limit_A/B：%DB4.DBD4 / %DB4.DBD8（PLC config.bobbin.limit_*）");
    }

    private static void ClearTfUnusedRecipe(Project project, HmiTarget hmi, string dir)
    {
        Console.WriteLine("  TF 不需要 Recipe（P4 畫面空）— 嘗試 Export／Delete。");
        int found = 0;
        int deleted = 0;
        int exported = 0;

        // 1) HmiTarget compositions（Basic 通常沒有 Recipe*）
        deleted += TryClearRecipeOnEngineering(hmi, dir, "HmiTarget", ref found, ref exported);

        // 2) HMI 裝置樹：有些面板 Recipe 掛在 DeviceItem／非 HmiTarget composition
        foreach (Device device in EnumerateDevices(project))
        {
            if (!string.Equals(device.Name, "25017_TF_HMI", StringComparison.OrdinalIgnoreCase) &&
                device.Name.IndexOf("TF_HMI", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            deleted += TryClearRecipeOnEngineering(device, dir, "Device:" + device.Name, ref found, ref exported);
            foreach (DeviceItem item in WalkItems(device.DeviceItems))
            {
                deleted += TryClearRecipeOnEngineering(item, dir, "DeviceItem:" + item.Name, ref found, ref exported);
                try
                {
                    SoftwareContainer container = item.GetService<SoftwareContainer>();
                    if (container != null && container.Software != null &&
                        !object.ReferenceEquals(container.Software, hmi))
                    {
                        deleted += TryClearRecipeOnEngineering(
                            container.Software,
                            dir,
                            "Software:" + container.Software.Name,
                            ref found,
                            ref exported);
                    }
                }
                catch
                {
                }
            }
        }

        Console.WriteLine(
            "  Recipe 掃描：命中物件 " + found + "／已 Export " + exported + "／已 Delete " + deleted);
        if (found == 0)
        {
            Console.WriteLine(
                "  Openness Basic 仍摸不到 Recipes composition；GUI 有 Export 但 API 無。請在 Portal 刪除 Recipe『Recipe』整筆以消 2 個編譯錯。");
        }
    }

    private static int TryClearRecipeOnEngineering(
        object targetObj,
        string dir,
        string where,
        ref int found,
        ref int exported)
    {
        IEngineeringObject target = targetObj as IEngineeringObject;
        if (target == null)
        {
            return 0;
        }

        int deleted = 0;
        EngineeringCompositionInfo[] infos;
        try
        {
            infos = target.GetCompositionInfos().ToArray();
        }
        catch
        {
            return 0;
        }

        foreach (EngineeringCompositionInfo info in infos)
        {
            if (info.Name.IndexOf("Recipe", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            Console.WriteLine("  在 " + where + " 找到 composition " + info.Name);
            object composition;
            try
            {
                composition = target.GetComposition(info.Name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  讀 composition 失敗：" + Flatten(ex));
                continue;
            }

            System.Collections.IEnumerable items = composition as System.Collections.IEnumerable;
            if (items == null)
            {
                continue;
            }

            List<object> snapshot = new List<object>();
            foreach (object item in items)
            {
                snapshot.Add(item);
            }

            foreach (object item in snapshot)
            {
                found++;
                IEngineeringObject engineering = item as IEngineeringObject;
                string itemName = engineering == null ? item.ToString() : ReadEngineeringName(engineering);
                string path = Path.Combine(dir, "recipe-clear-" + SafeFile(where + "-" + itemName) + ".xml");
                if (engineering != null && ExportHmiXml(engineering, path))
                {
                    exported++;
                    Console.WriteLine("  已 Export Recipe：" + path);
                }

                if (TryDeleteEngineering(item))
                {
                    deleted++;
                    Console.WriteLine("  已 Delete Recipe：" + itemName + " @" + where);
                }
                else
                {
                    Console.WriteLine("  無法 Delete Recipe：" + itemName + " @" + where);
                }
            }
        }

        return deleted;
    }

    private static bool TryDeleteEngineering(object item)
    {
        try
        {
            System.Reflection.MethodInfo delete = item.GetType().GetMethod("Delete", Type.EmptyTypes);
            if (delete == null)
            {
                return false;
            }

            delete.Invoke(item, null);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Delete 失敗：" + Flatten(ex));
            return false;
        }
    }

    private static void PatchTfRecipes(HmiTarget hmi, string dir)
    {
        // 保留舊進入點：改為「不需要 Recipe」清掉流程（僅 HmiTarget）。
        int found = 0;
        int exported = 0;
        TryClearRecipeOnEngineering(hmi, dir, "HmiTarget", ref found, ref exported);
    }

    private static bool TryResolveTfAbsoluteAddress(
        string hmiTagName,
        PlcSoftware plc,
        Dictionary<string, string> plcAddrs,
        out string address)
    {
        address = null;
        if (string.IsNullOrEmpty(hmiTagName))
        {
            return false;
        }

        string candidate = hmiTagName;
        string prefix = plc.Name + "_";
        if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate.Substring(prefix.Length);
        }

        string addr;
        if (plcAddrs.TryGetValue(candidate, out addr) && LooksAbsolutePlcAddress(addr))
        {
            address = NormalizeAbsAddr(addr);
            return true;
        }

        if (plcAddrs.TryGetValue(hmiTagName, out addr) && LooksAbsolutePlcAddress(addr))
        {
            address = NormalizeAbsAddr(addr);
            return true;
        }

        return false;
    }

    private static bool LooksAbsolutePlcAddress(string addr)
    {
        if (string.IsNullOrWhiteSpace(addr))
        {
            return false;
        }

        string t = addr.Trim();
        return t.StartsWith("%", StringComparison.Ordinal) ||
               t.StartsWith("I", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("Q", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("M", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("DB", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAbsAddr(string addr)
    {
        string t = addr.Trim();
        if (!t.StartsWith("%", StringComparison.Ordinal))
        {
            t = "%" + t;
        }

        return t;
    }

    private static Tag FindHmiTagFresh(TagTable table, string name)
    {
        if (table == null || string.IsNullOrEmpty(name))
        {
            return null;
        }

        try
        {
            return table.Tags.Find(name);
        }
        catch
        {
            return null;
        }
    }

    private static bool TagLooksExternal(TagTable table, string name, string dir)
    {
        Tag tag = FindHmiTagFresh(table, name);
        if (tag == null)
        {
            return false;
        }

        string probe = Path.Combine(dir, "probe-" + SafeFile(name) + ".xml");
        try
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }

            tag.Export(new FileInfo(probe), ExportOptions.WithDefaults);
            string text = File.ReadAllText(probe, Encoding.UTF8);
            return text.IndexOf("<Connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("ControllerTag", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool TryPatchOneTagAbsolute(TagTable table, string name, string absoluteAddress, string dir)
    {
        string xml = Path.Combine(dir, "abs-" + SafeFile(name) + ".xml");
        try
        {
            Tag tag = FindHmiTagFresh(table, name);
            if (tag == null)
            {
                return false;
            }

            if (File.Exists(xml))
            {
                File.Delete(xml);
            }

            tag.Export(new FileInfo(xml), ExportOptions.WithDefaults);
            XDocument doc = XDocument.Load(xml);
            XElement tagEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Tag.Tag");
            if (tagEl == null)
            {
                return false;
            }

            XElement attrs = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            if (attrs == null)
            {
                return false;
            }

            SetOrAddAttr(attrs, "AddressAccessMode", "Absolute");
            SetOrAddAttr(attrs, "LogicalAddress", absoluteAddress);

            XElement linkList = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
            if (linkList == null)
            {
                linkList = new XElement("LinkList");
                tagEl.Add(linkList);
            }

            foreach (XElement old in linkList.Elements().Where(e => e.Name.LocalName == "ControllerTag").ToList())
            {
                old.Remove();
            }

            EnsureHmiConnectionLink(linkList, "HMI_Connection_2");
            doc.Save(xml);
            table.Tags.Import(new FileInfo(xml), ImportOptions.Override);

            Tag after = FindHmiTagFresh(table, name);
            if (after == null)
            {
                return false;
            }

            string verify = Path.Combine(dir, "abs-after-" + SafeFile(name) + ".xml");
            if (File.Exists(verify))
            {
                File.Delete(verify);
            }

            after.Export(new FileInfo(verify), ExportOptions.WithDefaults | ExportOptions.WithReadOnly);
            string text = File.ReadAllText(verify, Encoding.UTF8);
            bool ok = text.IndexOf(absoluteAddress, StringComparison.OrdinalIgnoreCase) >= 0 &&
                      text.IndexOf("ControllerTag", StringComparison.OrdinalIgnoreCase) < 0;
            if (!ok)
            {
                Console.WriteLine("  Absolute 沒留下：" + name + " want=" + absoluteAddress);
            }

            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Absolute 失敗 " + name + "：" + Flatten(ex));
            return false;
        }
    }

    private static void SoftenTfRecipeElementTags(HmiTarget hmi, string dir)
    {
        // Basic Recipe 元素不接受 Absolute／失效舊名；API 又刪不掉 Recipe。
        // 把元素可能用到的 Tag 改成純 Internal，消編譯錯（畫面真 PLC 綁定用長名 Absolute）。
        TagTable table = hmi.TagFolder.TagTables.FirstOrDefault() ?? hmi.TagFolder.DefaultTagTable;
        if (table == null)
        {
            Console.WriteLine("  沒有 Tag 表，無法軟化 Recipe Tag。");
            return;
        }

        string[] recipeTags =
        {
            "bobbin_limit_A",
            "bobbin_limit_B",
            "24137_TF_PLC_PV_bobbin_side_A",
            "24137_TF_PLC_PV_bobbin_side_B"
        };

        Tag realSample = table.Tags.Find("25017_TF_PLC_Traverser_Control_E_Gear_TF_DB_config_bobbin_limit_A")
            ?? table.Tags.Find("bobbin_limit_A")
            ?? table.Tags.ToList().FirstOrDefault(t =>
                string.Equals(ReadTagDataType(t), "Real", StringComparison.OrdinalIgnoreCase));

        int ok = 0;
        foreach (string name in recipeTags)
        {
            Tag existing = FindHmiTagFresh(table, name);
            if (existing == null)
            {
                if (realSample == null)
                {
                    Console.WriteLine("  缺 sample，無法建立 Internal " + name);
                    continue;
                }

                if (CloneHmiTag(table, realSample, name, dir) <= 0 && FindHmiTagFresh(table, name) == null)
                {
                    Console.WriteLine("  無法建立 " + name);
                    continue;
                }

                Console.WriteLine("  已建立 Recipe 用 Tag：" + name);
            }

            if (TryPatchOneTagInternal(table, name, dir))
            {
                ok++;
                Console.WriteLine("  Recipe Tag → Internal：" + name);
            }
            else
            {
                Console.WriteLine("  Recipe Tag Internal 失敗：" + name);
            }
        }

        Console.WriteLine("  Recipe Tag Internal 完成：" + ok + "/" + recipeTags.Length);
    }

    private static bool TryPatchOneTagInternal(TagTable table, string name, string dir)
    {
        string xml = Path.Combine(dir, "int-" + SafeFile(name) + ".xml");
        try
        {
            Tag tag = FindHmiTagFresh(table, name);
            if (tag == null)
            {
                return false;
            }

            if (File.Exists(xml))
            {
                File.Delete(xml);
            }

            tag.Export(new FileInfo(xml), ExportOptions.WithDefaults);
            XDocument doc = XDocument.Load(xml);
            XElement tagEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Tag.Tag");
            if (tagEl == null)
            {
                return false;
            }

            XElement attrs = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            if (attrs != null)
            {
                // Internal：Symbolic + 空位址；拿掉 Connection。
                SetOrAddAttr(attrs, "AddressAccessMode", "Symbolic");
                SetOrAddAttr(attrs, "LogicalAddress", string.Empty);
            }

            XElement linkList = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
            if (linkList != null)
            {
                foreach (XElement old in linkList.Elements().Where(e =>
                    e.Name.LocalName == "ControllerTag" || e.Name.LocalName == "Connection").ToList())
                {
                    old.Remove();
                }

                XElement hmiType = linkList.Elements().FirstOrDefault(e => e.Name.LocalName == "HmiDataType");
                XElement dataType = linkList.Elements().FirstOrDefault(e => e.Name.LocalName == "DataType");
                XElement hmiTypeName = hmiType == null
                    ? null
                    : hmiType.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
                XElement dataTypeName = dataType == null
                    ? null
                    : dataType.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
                if (hmiTypeName != null && dataTypeName != null && !string.IsNullOrEmpty(hmiTypeName.Value))
                {
                    dataTypeName.Value = hmiTypeName.Value;
                }
            }

            doc.Save(xml);
            table.Tags.Import(new FileInfo(xml), ImportOptions.Override);
            return FindHmiTagFresh(table, name) != null;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Internal 失敗 " + name + "：" + Flatten(ex));
            return false;
        }
    }

    private static void SetOrAddAttr(XElement attrs, string name, string value)
    {
        XElement el = attrs.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        if (el == null)
        {
            attrs.Add(new XElement(name, value ?? string.Empty));
        }
        else
        {
            el.Value = value ?? string.Empty;
        }
    }

    private static int RecreateTfHmiAliasTagsAbsoluteOrInternal(
        HmiTarget hmi,
        PlcSoftware plc,
        Dictionary<string, string> plcAddrs,
        string dir)
    {
        TagTable table = hmi.TagFolder.TagTables.FirstOrDefault() ?? hmi.TagFolder.DefaultTagTable;
        if (table == null)
        {
            return 0;
        }

        Tag boolSample = table.Tags.ToList().FirstOrDefault(t =>
            string.Equals(ReadTagDataType(t), "Bool", StringComparison.OrdinalIgnoreCase));
        Tag realSample = table.Tags.Find("25017_TF_PLC_Traverser_Control_E_Gear_TF_DB_config_bobbin_limit_A")
            ?? table.Tags.ToList().FirstOrDefault(t =>
                string.Equals(ReadTagDataType(t), "Real", StringComparison.OrdinalIgnoreCase));

        Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "bobbin_limit_A", "Traverser_Control_E_Gear_TF_DB.config.bobbin.limit_A" },
            { "bobbin_limit_B", "Traverser_Control_E_Gear_TF_DB.config.bobbin.limit_B" },
            { "AErr_MechanicalWidth_Max", "AErr_MechanicalWidth_Max" },
            { "AErr_EStop_PB_Act", "AErr_EStop_PB_Act" },
            { "AErr_Rail_Limit_A", "AErr_Rail_Limit_A" },
            { "AErr_Rail_Limit_B", "AErr_Rail_Limit_B" },
            { "QErr_LiftUp_Limit_A", "QErr_LiftUp_Limit_A" },
            { "QErr_LiftUp_Limit_B", "QErr_LiftUp_Limit_B" },
            { "QErr_LiftDown_Limit_A", "QErr_LiftDown_Limit_A" },
            { "QErr_LiftDown_Limit_B", "QErr_LiftDown_Limit_B" },
            { "QErr_Motor_Fan_OvrLd", "QErr_Motor_Fan_OvrLd" },
            { "NErr_Lift_Lock_Motor_OvrLd", "NErr_Lift_Lock_Motor_OvrLd" },
            { "EErr_TK_Motor_Overload", "EErr_TK_Motor_Overload" },
            { "EErr_TK_Drv_Over_Heat", "EErr_TK_Drv_Over_Heat" },
            { "EErr_Bobbin_is_Not_Clamped", "EErr_Bobbin_is_Not_Clamped" },
            { "EErr_Trv_Drive_Fault", "EErr_Trv_Drive_Fault" },
            { "EErr_Takeup_Drive_Fault", "EErr_Takeup_Drive_Fault" },
            { "EErr_EStop_Loop_Act", "EErr_EStop_Loop_Act" }
        };

        int created = 0;
        foreach (KeyValuePair<string, string> pair in aliases)
        {
            if (table.Tags.Find(pair.Key) != null)
            {
                continue;
            }

            bool isReal = pair.Key.StartsWith("bobbin_limit", StringComparison.OrdinalIgnoreCase);
            Tag sample = isReal ? realSample : boolSample;
            if (sample == null)
            {
                Console.WriteLine("  無樣板，略過 " + pair.Key);
                continue;
            }

            string absAddr = null;
            string rawAddr = null;
            bool haveAbs = false;
            if (!isReal && plcAddrs.TryGetValue(pair.Value, out rawAddr) && LooksAbsolutePlcAddress(rawAddr))
            {
                absAddr = NormalizeAbsAddr(rawAddr);
                haveAbs = true;
            }

            if (CreateHmiTagAbsoluteOrInternal(table, sample, pair.Key, absAddr, dir))
            {
                created++;
                Console.WriteLine("  新建 " + pair.Key + (haveAbs ? (" ABS " + absAddr) : " INT"));
            }
        }

        return created;
    }

    private static bool TrySetHmiTagAbsolute(
        TagTable table,
        string name,
        string absoluteAddress,
        string connectionName,
        string dir)
    {
        string xml = Path.Combine(dir, "restore-abs-" + SafeFile(name) + ".xml");
        try
        {
            Tag tag = FindHmiTagFresh(table, name);
            if (tag == null)
            {
                return false;
            }

            if (File.Exists(xml))
            {
                File.Delete(xml);
            }

            tag.Export(new FileInfo(xml), ExportOptions.WithDefaults);
            XDocument doc = XDocument.Load(xml);
            XElement tagEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Tag.Tag");
            if (tagEl == null)
            {
                return false;
            }

            XElement attrs = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            XElement linkList = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
            if (attrs == null || linkList == null)
            {
                return false;
            }

            SetOrAddAttr(attrs, "AddressAccessMode", "Absolute");
            SetOrAddAttr(attrs, "LogicalAddress", absoluteAddress);
            foreach (XElement old in linkList.Elements().Where(e =>
                e.Name.LocalName == "ControllerTag" || e.Name.LocalName == "Connection").ToList())
            {
                old.Remove();
            }

            EnsureHmiConnectionLink(linkList, connectionName);
            doc.Save(xml);
            table.Tags.Import(new FileInfo(xml), ImportOptions.Override);
            return FindHmiTagFresh(table, name) != null;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Absolute 恢復失敗 " + name + "：" + Flatten(ex));
            return false;
        }
    }

    private static bool CreateHmiTagAbsoluteOrInternal(
        TagTable table,
        Tag sample,
        string newName,
        string absoluteAddressOrNull,
        string dir)
    {
        string template = Path.Combine(dir, "sample-" + SafeFile(sample.Name) + ".xml");
        try
        {
            if (File.Exists(template))
            {
                File.Delete(template);
            }

            sample.Export(new FileInfo(template), ExportOptions.WithDefaults);
            XDocument doc = XDocument.Load(template);
            foreach (XElement el in doc.Descendants().Where(e => e.Name.LocalName == "Name" && e.Value == sample.Name))
            {
                el.Value = newName;
            }

            XElement tagEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Tag.Tag");
            if (tagEl == null)
            {
                return false;
            }

            XElement attrs = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            XElement linkList = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
            if (linkList == null)
            {
                linkList = new XElement("LinkList");
                tagEl.Add(linkList);
            }

            foreach (XElement old in linkList.Elements().Where(e =>
                e.Name.LocalName == "ControllerTag" || e.Name.LocalName == "Connection").ToList())
            {
                old.Remove();
            }

            if (!string.IsNullOrEmpty(absoluteAddressOrNull) && attrs != null)
            {
                SetOrAddAttr(attrs, "AddressAccessMode", "Absolute");
                SetOrAddAttr(attrs, "LogicalAddress", absoluteAddressOrNull);
                EnsureHmiConnectionLink(linkList, "HMI_Connection_2");
            }
            else if (attrs != null)
            {
                SetOrAddAttr(attrs, "LogicalAddress", string.Empty);
            }

            string path = Path.Combine(dir, "new-" + SafeFile(newName) + ".xml");
            doc.Save(path);
            table.Tags.Import(new FileInfo(path), ImportOptions.None);
            return table.Tags.Find(newName) != null;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  新建失敗 " + newName + "：" + Flatten(ex));
            return false;
        }
    }

    private static int RewireTfHmiControllerTags(Project project, HmiTarget hmi, PlcSoftware plc)
    {
        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "hmi-rewire", "tf-live", "controller");
        Directory.CreateDirectory(dir);

        List<PlcSoftware> plcs = new List<PlcSoftware> { plc };
        List<HmiTagRef> refs = new List<HmiTagRef>();
        foreach (TagTable table in hmi.TagFolder.TagTables.ToList())
        {
            foreach (Tag tag in table.Tags.ToList())
            {
                PlcSoftware matched;
                string rest;
                if (!TrySplitHmiPlcName(tag.Name, plcs, out matched, out rest))
                {
                    continue;
                }

                refs.Add(new HmiTagRef { Hmi = hmi, Table = table, Tag = tag, Plc = matched, Rest = rest });
            }
        }

        HashSet<string> rests = new HashSet<string>(refs.Select(r => r.Rest), StringComparer.OrdinalIgnoreCase);
        PlcHmiCatalog catalog = BuildPlcHmiCatalog(plc, dir, rests);
        Console.WriteLine("  TF catalog Tag " + catalog.Tags.Count + "／路徑 " + catalog.Paths.Count);

        int wired = 0;
        foreach (HmiTagRef item in refs)
        {
            string path;
            if (!TryResolveCatalogPath(item, new Dictionary<string, PlcHmiCatalog>(StringComparer.OrdinalIgnoreCase)
                {
                    { plc.Name, catalog }
                }, out path))
            {
                Console.WriteLine("  對不到：" + item.Tag.Name + " rest=" + item.Rest);
                continue;
            }

            path = ApplyHmiArrayIndex(item.Rest, path);
            if (TryPatchOneTagControllerTag(item, path, dir))
            {
                wired++;
                Console.WriteLine("  OK " + item.Tag.Name + " → " + path);
            }
        }

        return wired;
    }

    private static int RecreateTfHmiAliasTags(Project project, HmiTarget hmi, PlcSoftware plc)
    {
        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "hmi-rewire", "tf-live", "aliases");
        Directory.CreateDirectory(dir);

        TagTable table = hmi.TagFolder.TagTables.FirstOrDefault() ?? hmi.TagFolder.DefaultTagTable;
        if (table == null)
        {
            return 0;
        }

        Tag boolSample = table.Tags.ToList().FirstOrDefault(t =>
            string.Equals(ReadTagDataType(t), "Bool", StringComparison.OrdinalIgnoreCase));
        Tag realSample = table.Tags.Find("25017_TF_PLC_Traverser_Control_E_Gear_TF_DB_config_bobbin_limit_A")
            ?? table.Tags.ToList().FirstOrDefault(t =>
                string.Equals(ReadTagDataType(t), "Real", StringComparison.OrdinalIgnoreCase));

        Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "bobbin_limit_A", "Traverser_Control_E_Gear_TF_DB.config.bobbin.limit_A" },
            { "bobbin_limit_B", "Traverser_Control_E_Gear_TF_DB.config.bobbin.limit_B" },
            { "AErr_MechanicalWidth_Max", "AErr_MechanicalWidth_Max" },
            { "AErr_EStop_PB_Act", "AErr_EStop_PB_Act" },
            { "AErr_Rail_Limit_A", "AErr_Rail_Limit_A" },
            { "AErr_Rail_Limit_B", "AErr_Rail_Limit_B" },
            { "QErr_LiftUp_Limit_A", "QErr_LiftUp_Limit_A" },
            { "QErr_LiftUp_Limit_B", "QErr_LiftUp_Limit_B" },
            { "QErr_LiftDown_Limit_A", "QErr_LiftDown_Limit_A" },
            { "QErr_LiftDown_Limit_B", "QErr_LiftDown_Limit_B" },
            { "QErr_Motor_Fan_OvrLd", "QErr_Motor_Fan_OvrLd" },
            { "NErr_Lift_Lock_Motor_OvrLd", "NErr_Lift_Lock_Motor_OvrLd" },
            { "EErr_TK_Motor_Overload", "EErr_TK_Motor_Overload" },
            { "EErr_TK_Drv_Over_Heat", "EErr_TK_Drv_Over_Heat" },
            { "EErr_Bobbin_is_Not_Clamped", "EErr_Bobbin_is_Not_Clamped" },
            { "EErr_Trv_Drive_Fault", "EErr_Trv_Drive_Fault" },
            { "EErr_Takeup_Drive_Fault", "EErr_Takeup_Drive_Fault" },
            { "EErr_EStop_Loop_Act", "EErr_EStop_Loop_Act" }
        };

        Dictionary<string, string> plcTags = CollectPlcTagAddresses(plc);
        int created = 0;
        foreach (KeyValuePair<string, string> pair in aliases)
        {
            if (table.Tags.Find(pair.Key) != null)
            {
                continue;
            }

            bool isReal = pair.Key.StartsWith("bobbin_limit", StringComparison.OrdinalIgnoreCase);
            Tag sample = isReal ? realSample : boolSample;
            if (sample == null)
            {
                Console.WriteLine("  無樣板，略過 " + pair.Key);
                continue;
            }

            if (!isReal && !plcTags.ContainsKey(pair.Value))
            {
                Console.WriteLine("  PLC 無 " + pair.Value + "，略過 " + pair.Key);
                continue;
            }

            if (CreateHmiTagWithController(table, sample, pair.Key, pair.Value, dir))
            {
                created++;
            }
        }

        return created;
    }

    private static string ReadTagDataType(Tag tag)
    {
        try
        {
            object value = tag.GetAttribute("DataType");
            if (value != null)
            {
                return value.ToString();
            }
        }
        catch
        {
        }

        try
        {
            object value = tag.GetAttribute("HmiDataType");
            return value == null ? string.Empty : value.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ApplyHmiArrayIndex(string rest, string path)
    {
        int brace = rest.IndexOf('{');
        if (brace <= 0 || !rest.EndsWith("}", StringComparison.Ordinal))
        {
            return path;
        }

        string index = rest.Substring(brace + 1, rest.Length - brace - 2);
        if (path.IndexOf('[') >= 0)
        {
            return path;
        }

        return path + "[" + index + "]";
    }

    private static bool TryPatchOneTagControllerTag(HmiTagRef item, string plcPath, string dir)
    {
        string xml = Path.Combine(dir, "ctl-" + SafeFile(item.Tag.Name) + ".xml");
        try
        {
            if (File.Exists(xml))
            {
                File.Delete(xml);
            }

            item.Tag.Export(new FileInfo(xml), ExportOptions.WithDefaults);
            XDocument doc = XDocument.Load(xml);
            XElement tagEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Tag.Tag");
            if (tagEl == null)
            {
                return false;
            }

            XElement linkList = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
            if (linkList == null)
            {
                linkList = new XElement("LinkList");
                tagEl.Add(linkList);
            }

            foreach (XElement old in linkList.Elements().Where(e => e.Name.LocalName == "ControllerTag").ToList())
            {
                old.Remove();
            }

            XElement controller = new XElement("ControllerTag");
            controller.SetAttributeValue("TargetID", "@OpenLink");
            controller.Add(new XElement("Name", QuotePlcPath(plcPath)));
            linkList.Add(controller);

            EnsureHmiConnectionLink(linkList, "HMI_Connection_2");
            doc.Save(xml);
            item.Table.Tags.Import(new FileInfo(xml), ImportOptions.Override);

            Tag after = item.Table.Tags.Find(item.Tag.Name);
            if (after == null)
            {
                return false;
            }

            string verify = Path.Combine(dir, "ctl-after-" + SafeFile(item.Tag.Name) + ".xml");
            if (File.Exists(verify))
            {
                File.Delete(verify);
            }

            after.Export(new FileInfo(verify), ExportOptions.WithDefaults | ExportOptions.WithReadOnly);
            string text = File.ReadAllText(verify, Encoding.UTF8);
            bool ok = text.IndexOf("ControllerTag", StringComparison.OrdinalIgnoreCase) >= 0 &&
                      text.IndexOf(QuotePlcPath(plcPath), StringComparison.Ordinal) >= 0;
            if (!ok)
            {
                Console.WriteLine("  ControllerTag 沒留下：" + item.Tag.Name);
            }

            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ControllerTag 失敗 " + item.Tag.Name + "：" + Flatten(ex));
            return false;
        }
    }

    private static bool CreateHmiTagWithController(
        TagTable table,
        Tag sample,
        string newName,
        string plcPath,
        string dir)
    {
        string template = Path.Combine(dir, "sample-" + SafeFile(sample.Name) + ".xml");
        try
        {
            if (File.Exists(template))
            {
                File.Delete(template);
            }

            sample.Export(new FileInfo(template), ExportOptions.WithDefaults);
            XDocument doc = XDocument.Load(template);
            foreach (XElement el in doc.Descendants().Where(e => e.Name.LocalName == "Name" && e.Value == sample.Name))
            {
                el.Value = newName;
            }

            XElement tagEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Tag.Tag");
            XElement linkList = tagEl == null
                ? null
                : tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
            if (linkList == null)
            {
                Console.WriteLine("  樣板無 LinkList：" + newName);
                return false;
            }

            foreach (XElement old in linkList.Elements().Where(e => e.Name.LocalName == "ControllerTag").ToList())
            {
                old.Remove();
            }

            XElement controller = new XElement("ControllerTag");
            controller.SetAttributeValue("TargetID", "@OpenLink");
            controller.Add(new XElement("Name", QuotePlcPath(plcPath)));
            linkList.Add(controller);
            EnsureHmiConnectionLink(linkList, "HMI_Connection_2");

            string path = Path.Combine(dir, "new-" + SafeFile(newName) + ".xml");
            doc.Save(path);
            table.Tags.Import(new FileInfo(path), ImportOptions.None);
            Console.WriteLine("  新建 " + newName + " → " + plcPath);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  新建失敗 " + newName + "：" + Flatten(ex));
            return false;
        }
    }

    private static void EnsureHmiConnectionLink(XElement linkList, string connectionName)
    {
        XElement connection = linkList.Elements().FirstOrDefault(e => e.Name.LocalName == "Connection");
        if (connection == null)
        {
            connection = new XElement("Connection");
            connection.SetAttributeValue("TargetID", "@OpenLink");
            connection.Add(new XElement("Name", connectionName));
            linkList.Add(connection);
            return;
        }

        XElement name = connection.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
        if (name == null)
        {
            connection.Add(new XElement("Name", connectionName));
        }
        else
        {
            name.Value = connectionName;
        }
    }

    public static int CompileTfHmi(TiaPortal portal, Project project)
    {
        return CompileNamedHmi(project, "HMI_RT_1");
    }

    public static int FixTfHmiNames(TiaPortal portal, Project project)
    {
        Console.WriteLine("TF HMI：缺的名用 PLC 同名補上。");
        HmiTarget hmi = EnumerateHmiTargets(project).FirstOrDefault(t => t.Name == "HMI_RT_1");
        PlcSoftware plc = FindPlc(project, "25017_TF_PLC");
        if (hmi == null || plc == null)
        {
            Console.WriteLine("找不到 HMI_RT_1 或 25017_TF_PLC。");
            return 1;
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "hmi-rewire", "tf-live", "names");
        Directory.CreateDirectory(dir);

        DumpTfHmiConnection(project, hmi);
        DumpTfHmiCompositions(hmi, dir);

        Dictionary<string, string> plcTags = CollectPlcTagAddresses(plc);
        Console.WriteLine("TF PLC Tag：" + plcTags.Count);

        TagTable table = hmi.TagFolder.TagTables.FirstOrDefault() ?? hmi.TagFolder.DefaultTagTable;
        if (table == null)
        {
            Console.WriteLine("找不到 TF Tag 表。");
            return 1;
        }

        HashSet<string> hmiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Tag tag in table.Tags)
        {
            hmiNames.Add(tag.Name);
        }

        string[] discrete =
        {
            "AErr_MechanicalWidth_Max",
            "AErr_EStop_PB_Act",
            "AErr_Rail_Limit_A",
            "AErr_Rail_Limit_B",
            "QErr_LiftUp_Limit_A",
            "QErr_LiftUp_Limit_B",
            "QErr_LiftDown_Limit_A",
            "QErr_LiftDown_Limit_B",
            "QErr_Motor_Fan_OvrLd",
            "NErr_Lift_Lock_Motor_OvrLd",
            "EErr_TK_Motor_Overload",
            "EErr_TK_Drv_Over_Heat",
            "EErr_Bobbin_is_Not_Clamped",
            "EErr_Trv_Drive_Fault",
            "EErr_Takeup_Drive_Fault",
            "EErr_EStop_Loop_Act"
        };

        List<string> mapLines = new List<string>();
        mapLines.Add("種類\tHMI／缺名\tPLC\t址");
        foreach (string name in discrete)
        {
            string addr;
            bool plcHave = plcTags.TryGetValue(name, out addr);
            bool hmiHave = hmiNames.Contains(name) || hmiNames.Contains("25017_TF_PLC_" + name);
            mapLines.Add((hmiHave ? "警報有" : "警報缺") + "\t" + name + "\t" +
                (plcHave ? name : "無") + "\t" + (addr ?? ""));
            Console.WriteLine("  警報 " + name + " PLC=" + (plcHave ? addr : "無") +
                " HMI=" + (hmiHave ? "有" : "缺"));
        }

        mapLines.Add("配方缺\tbobbin_limit_A\tTraverser_Control_E_Gear_TF_DB.config.bobbin.limit_A\t");
        mapLines.Add("配方缺\tbobbin_limit_B\tTraverser_Control_E_Gear_TF_DB.config.bobbin.limit_B\t");

        Tag boolSample = table.Tags.Find("25017_TF_PLC_Recipe_Ctrl_DB_preview_to_run")
            ?? table.Tags.Find("25017_TF_PLC_Traverser_Control_E_Gear_TF_DB_limit_set_key_A_btn");
        Tag realSampleA = table.Tags.Find("25017_TF_PLC_Traverser_Control_E_Gear_TF_DB_config_bobbin_limit_A");
        Tag realSampleB = table.Tags.Find("25017_TF_PLC_Traverser_Control_E_Gear_TF_DB_config_bobbin_limit_B");

        int created = 0;
        if (boolSample == null)
        {
            Console.WriteLine("沒有 Bool 樣板，警報 Tag 不建。");
        }
        else
        {
            foreach (string name in discrete)
            {
                created += CloneHmiTag(table, boolSample, name, dir);
            }
        }

        if (realSampleA != null)
        {
            created += CloneHmiTag(table, realSampleA, "bobbin_limit_A", dir);
        }

        if (realSampleB != null)
        {
            created += CloneHmiTag(table, realSampleB, "bobbin_limit_B", dir);
        }

        File.WriteAllLines(Path.Combine(dir, "tf-plc-match.txt"), mapLines, Encoding.UTF8);
        Console.WriteLine("新建 HMI Tag：" + created + " → " + Path.Combine(dir, "tf-plc-match.txt"));
        project.Save();
        Console.WriteLine("已存（TF 同名 Tag）。");
        return CompileNamedHmi(project, "HMI_RT_1");
    }

    private static void DumpTfHmiConnection(Project project, HmiTarget hmi)
    {
        Console.WriteLine("  軟體連線：" + hmi.Connections.Count);
        foreach (Connection connection in hmi.Connections)
        {
            Console.WriteLine("    " + connection.Name);
        }

        foreach (Device device in EnumerateDevices(project))
        {
            if (device.Name.IndexOf("HMI", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            bool mine = false;
            foreach (DeviceItem item in WalkItems(device.DeviceItems))
            {
                SoftwareContainer container = item.GetService<SoftwareContainer>();
                HmiTarget found = container == null ? null : container.Software as HmiTarget;
                if (found != null && string.Equals(found.Name, hmi.Name, StringComparison.Ordinal))
                {
                    mine = true;
                    break;
                }
            }

            if (!mine)
            {
                continue;
            }

            Console.WriteLine("  HMI 裝置：" + device.Name);
            foreach (DeviceItem item in WalkItems(device.DeviceItems))
            {
                CommunicationManagement management = item.GetService<CommunicationManagement>();
                if (management == null)
                {
                    continue;
                }

                Console.WriteLine("    HW 連線（" + item.Name + "）：" + management.Connections.Count);
                foreach (Siemens.Engineering.HW.CommunicationConnections.Connection connection in management.Connections)
                {
                    Siemens.Engineering.HW.CommunicationConnections.HmiConnection hmiConnection =
                        connection as Siemens.Engineering.HW.CommunicationConnections.HmiConnection;
                    string name = hmiConnection != null
                        ? hmiConnection.LocalConnectionName
                        : connection.GetType().Name;
                    Console.WriteLine("      " + connection.GetType().Name + " " + name +
                        " valid=" + connection.IsValid);
                }
            }
        }
    }

    private static void DumpTfHmiCompositions(HmiTarget hmi, string dir)
    {
        IEngineeringObject target = hmi;
        EngineeringCompositionInfo[] infos;
        try
        {
            infos = target.GetCompositionInfos().ToArray();
        }
        catch
        {
            infos = new EngineeringCompositionInfo[0];
        }

        foreach (EngineeringCompositionInfo info in infos)
        {
            string typeName = info.Type == null ? "?" : info.Type.FullName;
            Console.WriteLine("  composition " + info.Name + " / " + typeName);
            if (info.Name.IndexOf("Recipe", StringComparison.OrdinalIgnoreCase) < 0 &&
                info.Name.IndexOf("Alarm", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            try
            {
                object composition = target.GetComposition(info.Name);
                System.Collections.IEnumerable items = composition as System.Collections.IEnumerable;
                if (items == null)
                {
                    continue;
                }

                foreach (object item in items)
                {
                    IEngineeringObject engineering = item as IEngineeringObject;
                    string itemName = engineering == null ? item.ToString() : ReadEngineeringName(engineering);
                    Console.WriteLine("    " + info.Name + " " + itemName);
                    if (engineering != null)
                    {
                        ExportHmiXml(engineering, Path.Combine(dir, SafeFile(info.Name + "-" + itemName) + ".xml"));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("    " + info.Name + "：" + Flatten(ex));
            }
        }
    }

    private static string ReadEngineeringName(IEngineeringObject target)
    {
        try
        {
            object value = target.GetAttribute("Name");
            return value == null ? target.ToString() : value.ToString();
        }
        catch
        {
            return target.GetType().Name;
        }
    }

    private static Dictionary<string, string> CollectPlcTagAddresses(PlcSoftware plc)
    {
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            foreach (PlcTag tag in table.Tags)
            {
                string addr = string.Empty;
                try
                {
                    object value = tag.GetAttribute("LogicalAddress");
                    addr = value == null ? string.Empty : value.ToString();
                }
                catch
                {
                }

                map[tag.Name] = addr;
            }
        }

        return map;
    }

    private static int CloneHmiTag(TagTable table, Tag sample, string newName, string dir)
    {
        if (table.Tags.Find(newName) != null)
        {
            Console.WriteLine("  已有 " + newName);
            return 0;
        }

        string template = Path.Combine(dir, "clone-" + SafeFile(sample.Name) + ".xml");
        if (!File.Exists(template))
        {
            if (File.Exists(template))
            {
                File.Delete(template);
            }

            sample.Export(new FileInfo(template), ExportOptions.WithDefaults);
        }

        XDocument doc = XDocument.Load(template);
        foreach (XElement el in doc.Descendants().Where(e => e.Name.LocalName == "Name" && e.Value == sample.Name))
        {
            el.Value = newName;
        }

        string path = Path.Combine(dir, "new-" + SafeFile(newName) + ".xml");
        doc.Save(path);
        try
        {
            table.Tags.Import(new FileInfo(path), ImportOptions.None);
            Console.WriteLine("  新建 " + newName + " ← " + sample.Name);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  新建失敗 " + newName + "：" + Flatten(ex));
            return 0;
        }
    }

    private static bool ExportHmiXml(object item, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            System.Reflection.MethodInfo export = item.GetType().GetMethod(
                "Export",
                new[] { typeof(FileInfo), typeof(ExportOptions) });
            if (export == null)
            {
                Console.WriteLine("  沒有 Export：" + item.GetType().Name);
                return false;
            }

            export.Invoke(item, new object[] { new FileInfo(path), ExportOptions.WithDefaults });
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯出失敗 " + path + "：" + Flatten(ex));
            return false;
        }
    }

    private static void ReportTfTagExport(string path)
    {
        XDocument doc = XDocument.Load(path);
        int tags = doc.Descendants().Count(e => e.Name.LocalName == "Hmi.Tag.Tag");
        int plcTag = doc.Descendants().Count(e => e.Name.LocalName == "PlcTag");
        int oldNames = CountText(path, "24137_TF_PLC");
        int newNames = CountText(path, "25017_TF_PLC");
        HashSet<string> connections = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement el in doc.Descendants().Where(e => e.Name.LocalName == "Connection"))
        {
            XElement name = el.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
            if (name != null && !string.IsNullOrEmpty(name.Value))
            {
                connections.Add(name.Value);
            }
        }

        Console.WriteLine("    Tag " + tags + "／PlcTag 節點 " + plcTag +
            "／24137=" + oldNames + "／25017=" + newNames +
            "／連線 " + string.Join("、", connections.ToArray()));
    }

    private static void WriteTfNameMap(string dir)
    {
        HashSet<string> hmiTags = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> screenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(dir, "tags-*.xml"))
        {
            foreach (XElement name in XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "Name"))
            {
                if (name.Value != null && name.Value.IndexOf("_TF_PLC_", StringComparison.Ordinal) >= 0)
                {
                    hmiTags.Add(name.Value);
                }
            }
        }

        foreach (string file in Directory.GetFiles(dir, "screen-*.xml"))
        {
            if (file.IndexOf("-25017.xml", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue;
            }

            foreach (XElement name in XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "Name"))
            {
                if (name.Value != null && name.Value.IndexOf("_TF_PLC_", StringComparison.Ordinal) >= 0)
                {
                    screenNames.Add(name.Value);
                }
            }
        }

        List<string> lines = new List<string>();
        lines.Add("畫面引用／HMI Tag／對 25017 名／PLC 路徑");
        foreach (string raw in screenNames.OrderBy(s => s, StringComparer.Ordinal))
        {
            string modern = raw.Replace("24137_TF_PLC_", "25017_TF_PLC_");
            bool have = hmiTags.Contains(modern) || hmiTags.Contains(raw);
            string rest = modern.StartsWith("25017_TF_PLC_", StringComparison.Ordinal)
                ? modern.Substring("25017_TF_PLC_".Length)
                : modern;
            string path = rest.Replace('_', '.');
            lines.Add((have ? "有" : "缺") + "\t" + raw + "\t" + modern + "\t" + path);
        }

        File.WriteAllLines(Path.Combine(dir, "tf-names.txt"), lines, Encoding.UTF8);
        Console.WriteLine("  畫面引用 " + screenNames.Count + "／HMI Tag " + hmiTags.Count +
            " → " + Path.Combine(dir, "tf-names.txt"));
    }

    private static int CountText(string path, string token)
    {
        return File.ReadAllText(path, Encoding.UTF8).Split(new[] { token }, StringSplitOptions.None).Length - 1;
    }

    private static int CompileNamedHmi(Project project, string name)
    {
        foreach (HmiTarget hmi in EnumerateHmiTargets(project))
        {
            if (!string.Equals(hmi.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ICompilable compilable = hmi.GetService<ICompilable>();
            if (compilable == null)
            {
                Console.WriteLine(hmi.Name + "：不支援編譯。");
                return 1;
            }

            CompilerResult result = compilable.Compile();
            Console.WriteLine("編譯 " + hmi.Name + "：" + result.State +
                "／錯誤 " + result.ErrorCount + "／警告 " + result.WarningCount);
            PrintCompileMessages(result.Messages, 1);
            project.Save();
            Console.WriteLine("已存（TF HMI 編譯後）。");
            return result.ErrorCount == 0 ? 0 : 1;
        }

        Console.WriteLine("找不到 HMI " + name + "。");
        return 1;
    }

    public static int CompileAllHmi(TiaPortal portal, Project project)
    {
        Console.WriteLine("編譯全部 HMI。");
        int errors = 0;
        foreach (HmiTarget hmi in EnumerateHmiTargets(project))
        {
            ICompilable compilable = hmi.GetService<ICompilable>();
            if (compilable == null)
            {
                Console.WriteLine(hmi.Name + "：不支援編譯。");
                continue;
            }

            try
            {
                CompilerResult result = compilable.Compile();
                errors += result.ErrorCount;
                Console.WriteLine("編譯 " + hmi.Name + "：" + result.State +
                    "／錯誤 " + result.ErrorCount + "／警告 " + result.WarningCount);
                PrintCompileMessages(result.Messages, 1);
            }
            catch (Exception ex)
            {
                errors++;
                Console.WriteLine("編譯 HMI " + hmi.Name + "：" + Flatten(ex));
            }
        }

        project.Save();
        Console.WriteLine("已存（HMI 編譯後）。總錯誤：" + errors);
        return errors == 0 ? 0 : 1;
    }

    public static int RewireHmiPlcTags(TiaPortal portal, Project project)
    {
        Console.WriteLine("HMI Tag：對得到的 PLC 成員就重連，然後編譯 HMI。");
        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "hmi-rewire");
        Directory.CreateDirectory(dir);

        List<PlcSoftware> plcs = LadBlockTools.FindAllPlcSoftwares(project)
            .OrderByDescending(p => p.Name.Length)
            .ToList();
        foreach (HmiTarget hmi in EnumerateHmiTargets(project))
        {
            Console.WriteLine("  連線 " + hmi.Name + "：" + hmi.Connections.Count);
            foreach (Connection connection in hmi.Connections)
            {
                Console.WriteLine("    " + connection.Name);
            }
        }

        List<HmiTagRef> refs = CollectHmiTagRefs(project, plcs);
        Dictionary<string, HashSet<string>> needed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (HmiTagRef item in refs)
        {
            HashSet<string> set;
            if (!needed.TryGetValue(item.Plc.Name, out set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                needed[item.Plc.Name] = set;
            }

            set.Add(item.Rest);
        }

        Dictionary<string, PlcHmiCatalog> catalogs = new Dictionary<string, PlcHmiCatalog>(StringComparer.OrdinalIgnoreCase);
        foreach (PlcSoftware plc in plcs)
        {
            HashSet<string> rests;
            if (!needed.TryGetValue(plc.Name, out rests))
            {
                rests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            PlcHmiCatalog catalog = BuildPlcHmiCatalog(plc, dir, rests);
            catalogs[plc.Name] = catalog;
            Console.WriteLine("  " + plc.Name + " Tag " + catalog.Tags.Count + "／路徑 " + catalog.Paths.Count);
        }

        int wired = 0;
        int unknown = 0;
        List<string> unknownNames = new List<string>();
        List<string> wiredLog = new List<string>();
        Dictionary<string, string> resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (HmiTagRef item in refs)
        {
            string path;
            if (!TryResolveCatalogPath(item, catalogs, out path))
            {
                unknown++;
                unknownNames.Add(item.Hmi.Name + "\t" + item.Tag.Name);
                continue;
            }

            resolved[item.Tag.Name] = path;
        }

        if (refs.Count > 0)
        {
            HmiTagRef sample = refs[0];
            string samplePath;
            if (resolved.TryGetValue(sample.Tag.Name, out samplePath))
            {
                Console.WriteLine("先試一顆 LogicalAddress：" + sample.Tag.Name + " → " + samplePath);
                if (!TryPatchOneTagLogicalAddress(sample, samplePath, dir))
                {
                    Console.WriteLine("單顆 LogicalAddress 沒寫進去，整表不改。");
                    File.WriteAllLines(Path.Combine(dir, "unmatched.txt"), unknownNames, Encoding.UTF8);
                    File.WriteAllLines(Path.Combine(dir, "wired.txt"), wiredLog, Encoding.UTF8);
                    project.Save();
                    return CompileAllHmi(portal, project);
                }
            }
        }

        foreach (HmiTarget hmi in EnumerateHmiTargets(project))
        {
            foreach (TagTable table in hmi.TagFolder.TagTables.ToList())
            {
                int n = PatchHmiTablePlcTags(hmi, table, dir, resolved, wiredLog);
                wired += n;
                Console.WriteLine("  " + hmi.Name + "／" + table.Name + " XML 寫入：" + n);
            }
        }

        Console.WriteLine("已連 " + wired + "／對不到 " + unknown);
        foreach (string line in wiredLog.Take(40))
        {
            Console.WriteLine("  " + line);
        }

        foreach (string name in unknownNames.Take(40))
        {
            Console.WriteLine("  對不到：" + name);
        }

        File.WriteAllLines(Path.Combine(dir, "unmatched.txt"), unknownNames, Encoding.UTF8);
        File.WriteAllLines(Path.Combine(dir, "wired.txt"), wiredLog, Encoding.UTF8);
        project.Save();
        Console.WriteLine("已存（HMI 重連）。");
        return CompileAllHmi(portal, project);
    }

    private sealed class PlcHmiCatalog
    {
        public HashSet<string> Tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class HmiTagRef
    {
        public HmiTarget Hmi;
        public TagTable Table;
        public Tag Tag;
        public PlcSoftware Plc;
        public string Rest;
    }

    private static List<HmiTagRef> CollectHmiTagRefs(Project project, List<PlcSoftware> plcs)
    {
        List<HmiTagRef> refs = new List<HmiTagRef>();
        foreach (HmiTarget hmi in EnumerateHmiTargets(project))
        {
            foreach (TagTable table in hmi.TagFolder.TagTables.ToList())
            {
                foreach (Tag tag in table.Tags.ToList())
                {
                    PlcSoftware plc;
                    string rest;
                    if (!TrySplitHmiPlcName(tag.Name, plcs, out plc, out rest))
                    {
                        continue;
                    }

                    refs.Add(new HmiTagRef { Hmi = hmi, Table = table, Tag = tag, Plc = plc, Rest = rest });
                }
            }
        }

        return refs;
    }

    private static bool TrySplitHmiPlcName(
        string hmiName,
        List<PlcSoftware> plcs,
        out PlcSoftware plc,
        out string rest)
    {
        plc = null;
        rest = null;
        foreach (PlcSoftware candidate in plcs)
        {
            string prefix = candidate.Name + "_";
            if (!hmiName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            plc = candidate;
            rest = hmiName.Substring(prefix.Length);
            return !string.IsNullOrEmpty(rest);
        }

        return false;
    }

    private static PlcHmiCatalog BuildPlcHmiCatalog(PlcSoftware plc, string dir, HashSet<string> rests)
    {
        PlcHmiCatalog catalog = new PlcHmiCatalog();
        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            foreach (PlcTag tag in table.Tags)
            {
                catalog.Tags.Add(tag.Name);
            }
        }

        List<PlcBlock> blocks = EnumerateBlocks(plc.BlockGroup).ToList();
        HashSet<string> exportNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlcBlock block in blocks)
        {
            string lang = block.ProgrammingLanguage.ToString();
            if (lang.IndexOf("DB", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            foreach (string rest in rests)
            {
                if (RestNeedsBlock(rest, block.Name))
                {
                    exportNames.Add(block.Name);
                    break;
                }
            }
        }

        string plcDir = Path.Combine(dir, SafeFile(plc.Name));
        Directory.CreateDirectory(plcDir);
        Console.WriteLine("  匯出 " + plc.Name + " DB " + exportNames.Count);
        foreach (PlcBlock block in blocks)
        {
            if (!exportNames.Contains(block.Name))
            {
                continue;
            }

            string path = Path.Combine(plcDir, SafeFile(block.Name) + ".xml");
            if (!ExportBlockXmlQuiet(block, path))
            {
                continue;
            }

            AddDbMemberPaths(XDocument.Load(path).Root, block.Name, catalog.Paths);
        }

        return catalog;
    }

    private static bool RestNeedsBlock(string rest, string blockName)
    {
        if (string.Equals(rest, blockName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string key = blockName.Replace('.', '_');
        return rest.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddDbMemberPaths(XElement root, string blockName, Dictionary<string, string> paths)
    {
        XElement iface = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Interface");
        if (iface == null)
        {
            return;
        }

        XElement top = iface.Elements().FirstOrDefault(e => e.Name.LocalName == "Sections")
            ?? iface.Descendants().FirstOrDefault(e => e.Name.LocalName == "Sections");
        if (top == null)
        {
            return;
        }

        foreach (XElement section in top.Elements().Where(e => e.Name.LocalName == "Section"))
        {
            string name = (string)section.Attribute("Name");
            if (string.Equals(name, "Temp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Constant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CollectHmiMemberPaths(section, blockName, paths);
        }
    }

    private static void CollectHmiMemberPaths(XElement parent, string prefix, Dictionary<string, string> paths)
    {
        foreach (XElement member in parent.Elements().Where(e => e.Name.LocalName == "Member"))
        {
            string name = (string)member.Attribute("Name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            string path = string.IsNullOrEmpty(prefix) ? name : prefix + "." + name;
            string key = path.Replace('.', '_');
            if (!paths.ContainsKey(key))
            {
                paths[key] = path;
            }

            CollectHmiMemberPaths(member, path, paths);
        }

        foreach (XElement nested in parent.Elements().Where(e =>
            e.Name.LocalName == "Sections" || e.Name.LocalName == "Section"))
        {
            if (nested.Name.LocalName == "Section")
            {
                string name = (string)nested.Attribute("Name");
                if (string.Equals(name, "Temp", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Constant", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            CollectHmiMemberPaths(nested, prefix, paths);
        }
    }

    private static bool TryResolveCatalogPath(
        HmiTagRef item,
        Dictionary<string, PlcHmiCatalog> catalogs,
        out string path)
    {
        path = null;
        PlcHmiCatalog catalog;
        if (!catalogs.TryGetValue(item.Plc.Name, out catalog))
        {
            return false;
        }

        if (TryMatchCatalog(item.Rest, catalog, out path))
        {
            return true;
        }

        return item.Rest.IndexOf("18B", StringComparison.Ordinal) >= 0 &&
               TryMatchCatalog(item.Rest.Replace("18B", "20B"), catalog, out path);
    }

    private static void ProbeHmiPlcBind(HmiTagRef item, Dictionary<string, PlcHmiCatalog> catalogs, string dir)
    {
        Console.WriteLine("探針 " + item.Hmi.Name + " " + item.Tag.Name);
        try
        {
            object connection = item.Tag.GetAttribute("Connection");
            Console.WriteLine("  Connection=" + (connection == null ? "<null>" : connection.ToString()));
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Connection 讀不到：" + Flatten(ex));
        }

        try
        {
            foreach (EngineeringAttributeInfo info in item.Tag.GetAttributeInfos())
            {
                object value = null;
                try
                {
                    value = item.Tag.GetAttribute(info.Name);
                }
                catch
                {
                    value = "<讀取失敗>";
                }

                Console.WriteLine("  屬性 " + info.Name + " = " + (value == null ? "<null>" : value.ToString()));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  屬性列不出：" + Flatten(ex));
        }

        string path;
        if (TryResolveCatalogPath(item, catalogs, out path))
        {
            string quoted = QuotePlcPath(path);
            try
            {
                item.Tag.SetAttribute("PlcTag", quoted);
                Console.WriteLine("  SetAttribute PlcTag 成功：" + quoted);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  SetAttribute PlcTag 失敗：" + Flatten(ex));
            }
        }

        string xml = Path.Combine(dir, "probe-" + SafeFile(item.Tag.Name) + ".xml");
        try
        {
            if (File.Exists(xml))
            {
                File.Delete(xml);
            }

            item.Tag.Export(new FileInfo(xml), ExportOptions.WithDefaults);
            Console.WriteLine("  已匯出探針：" + xml);
            string xmlRo = Path.Combine(dir, "probe-ro-" + SafeFile(item.Tag.Name) + ".xml");
            if (File.Exists(xmlRo))
            {
                File.Delete(xmlRo);
            }

            item.Tag.Export(new FileInfo(xmlRo), ExportOptions.WithDefaults | ExportOptions.WithReadOnly);
            Console.WriteLine("  已匯出唯讀探針：" + xmlRo);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  探針匯出失敗：" + Flatten(ex));
        }
    }

    private static bool TryPatchOneTagLogicalAddress(HmiTagRef item, string plcPath, string dir)
    {
        string xml = Path.Combine(dir, "one-" + SafeFile(item.Tag.Name) + ".xml");
        try
        {
            if (File.Exists(xml))
            {
                File.Delete(xml);
            }

            item.Tag.Export(new FileInfo(xml), ExportOptions.WithDefaults);
            XDocument doc = XDocument.Load(xml);
            XElement attrs = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            if (attrs == null)
            {
                Console.WriteLine("  單顆沒有 AttributeList。");
                return false;
            }

            XElement logical = attrs.Elements().FirstOrDefault(e => e.Name.LocalName == "LogicalAddress");
            if (logical == null)
            {
                attrs.Add(new XElement("LogicalAddress", plcPath));
            }
            else
            {
                logical.Value = plcPath;
            }

            doc.Save(xml);
            item.Table.Tags.Import(new FileInfo(xml), ImportOptions.Override);
            string verify = Path.Combine(dir, "one-after-" + SafeFile(item.Tag.Name) + ".xml");
            if (File.Exists(verify))
            {
                File.Delete(verify);
            }

            Tag after = item.Table.Tags.Find(item.Tag.Name);
            if (after == null)
            {
                Console.WriteLine("  單顆匯入後找不到。");
                return false;
            }

            after.Export(new FileInfo(verify), ExportOptions.WithDefaults);
            XDocument afterDoc = XDocument.Load(verify);
            XElement afterLogical = afterDoc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "LogicalAddress");
            bool ok = afterLogical != null &&
                      string.Equals(afterLogical.Value, plcPath, StringComparison.Ordinal);
            Console.WriteLine("  單顆 LogicalAddress " + (ok ? "留下了" : "沒留下") + "：" +
                (afterLogical == null ? "<null>" : afterLogical.Value));
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  單顆 LogicalAddress 失敗：" + Flatten(ex));
            return false;
        }
    }

    private static int PatchHmiTablePlcTags(
        HmiTarget hmi,
        TagTable table,
        string dir,
        Dictionary<string, string> resolved,
        List<string> wiredLog)
    {
        string xml = Path.Combine(dir, SafeFile(hmi.Name + "-" + table.Name) + ".xml");
        try
        {
            if (File.Exists(xml))
            {
                File.Delete(xml);
            }

            table.Export(new FileInfo(xml), ExportOptions.WithDefaults);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯出 " + table.Name + " 失敗：" + Flatten(ex));
            return 0;
        }

        XDocument doc = XDocument.Load(xml);
        int patched = 0;
        foreach (XElement tagEl in doc.Descendants().Where(e => e.Name.LocalName == "Hmi.Tag.Tag"))
        {
            XElement attrs = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            if (attrs == null)
            {
                continue;
            }

            XElement nameEl = attrs.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
            if (nameEl == null || string.IsNullOrEmpty(nameEl.Value))
            {
                continue;
            }

            string plcPath;
            if (!resolved.TryGetValue(nameEl.Value, out plcPath))
            {
                continue;
            }

            XElement logical = attrs.Elements().FirstOrDefault(e => e.Name.LocalName == "LogicalAddress");
            if (logical == null)
            {
                attrs.Add(new XElement("LogicalAddress", plcPath));
            }
            else
            {
                logical.Value = plcPath;
            }

            patched++;
            wiredLog.Add(nameEl.Value + " → " + plcPath);
        }

        if (patched == 0)
        {
            return 0;
        }

        string patchedPath = Path.Combine(dir, SafeFile(hmi.Name + "-" + table.Name) + "-patched.xml");
        doc.Save(patchedPath);
        try
        {
            hmi.TagFolder.TagTables.Import(new FileInfo(patchedPath), ImportOptions.Override);
            return patched;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  匯入 " + table.Name + " 失敗：" + Flatten(ex));
            return 0;
        }
    }

    private static bool TryMatchCatalog(string rest, PlcHmiCatalog catalog, out string path)
    {
        path = null;
        if (string.IsNullOrEmpty(rest))
        {
            return false;
        }

        if (catalog.Tags.Contains(rest))
        {
            path = rest;
            return true;
        }

        if (catalog.Paths.TryGetValue(rest, out path))
        {
            return true;
        }

        int brace = rest.IndexOf('{');
        if (brace > 0 && rest.EndsWith("}", StringComparison.Ordinal))
        {
            string bare = rest.Substring(0, brace);
            if (catalog.Tags.Contains(bare))
            {
                path = bare;
                return true;
            }

            if (catalog.Paths.TryGetValue(bare, out path))
            {
                return true;
            }
        }

        int bracket = rest.LastIndexOf('[');
        if (bracket > 0 && rest.EndsWith("]", StringComparison.Ordinal))
        {
            string bare = rest.Substring(0, bracket);
            string index = rest.Substring(bracket);
            if (catalog.Tags.Contains(bare))
            {
                path = bare + index;
                return true;
            }

            if (catalog.Paths.TryGetValue(bare, out path))
            {
                path = path + index;
                return true;
            }
        }

        return false;
    }

    private static string QuotePlcPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        string[] parts = path.Split('.');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            string index = string.Empty;
            int bracket = part.IndexOf('[');
            if (bracket >= 0)
            {
                index = part.Substring(bracket);
                part = part.Substring(0, bracket);
            }

            if (!Regex.IsMatch(part, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                part = "\"" + part + "\"";
            }

            parts[i] = part + index;
        }

        return string.Join(".", parts);
    }

    private static bool TryBindHmiPlcTag(Tag tag, string path)
    {
        try
        {
            tag.SetAttribute("PlcTag", path);
            string current = ReadHmiPlcTag(tag);
            return string.Equals(current, path, StringComparison.Ordinal) ||
                   (!string.IsNullOrEmpty(current) &&
                    current.IndexOf(path.Replace("\"", string.Empty), StringComparison.OrdinalIgnoreCase) >= 0);
        }
        catch
        {
            return false;
        }
    }

    private static string ReadHmiPlcTag(Tag tag)
    {
        try
        {
            object value = tag.GetAttribute("PlcTag");
            return value == null ? string.Empty : value.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IEnumerable<HmiTarget> EnumerateHmiTargets(Project project)
    {
        foreach (Device device in EnumerateDevices(project))
        {
            foreach (DeviceItem item in WalkItems(device.DeviceItems))
            {
                SoftwareContainer container = item.GetService<SoftwareContainer>();
                HmiTarget hmi = container == null ? null : container.Software as HmiTarget;
                if (hmi != null)
                {
                    yield return hmi;
                }
            }
        }
    }

    private static int CountHmiZigbeeTags(HmiTarget hmi)
    {
        int n = 0;
        foreach (TagTable table in hmi.TagFolder.TagTables)
        {
            foreach (Tag tag in table.Tags)
            {
                if (tag.Name.IndexOf("zigbee", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    n++;
                }
            }
        }

        return n;
    }

    private static int StripLeftMainZigbeeUnits(XElement root)
    {
        int n = 0;
        foreach (XElement unit in root.Descendants().Where(IsCompileUnit).ToList())
        {
            if (CountMainZigbeeSymbols(unit) == 0)
            {
                continue;
            }

            Console.WriteLine("    刪網：" + CompileUnitTitle(unit));
            unit.Remove();
            n++;
        }

        return n;
    }

    private static List<string> CollectZigbeeNamedBlocks(PlcSoftware plc)
    {
        List<string> names = EnumerateBlocks(plc.BlockGroup)
            .Where(b => b.Name.IndexOf("zigbee", StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(b => b.Name)
            .ToList();
        return names
            .OrderByDescending(n => n.EndsWith("_DB", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(n => n.StartsWith("Modbus_Comm_", StringComparison.OrdinalIgnoreCase))
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    private static void TryDeleteNamedBlock(PlcSoftware plc, string name)
    {
        PlcBlock block = FindBlock(plc, name);
        if (block == null)
        {
            return;
        }

        try
        {
            block.Delete();
            Console.WriteLine("  已刪區塊 " + name);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  刪不掉區塊 " + name + "：" + Flatten(ex));
        }
    }

    private static void TryDeleteNamedType(PlcSoftware plc, string name)
    {
        PlcType type = FindType(plc, name);
        if (type == null)
        {
            return;
        }

        try
        {
            type.Delete();
            Console.WriteLine("  已刪型別 " + name);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  刪不掉型別 " + name + "：" + Flatten(ex));
        }
    }

    private static void ReportZigbeeLeft(Project project, PlcSoftware main, string dir)
    {
        Console.WriteLine("---- Zigbee 還留 ----");
        foreach (string name in CollectZigbeeNamedBlocks(main))
        {
            Console.WriteLine("  區塊 " + name);
        }

        PlcType type = FindType(main, "Modbus_PLC_48B");
        if (type != null)
        {
            Console.WriteLine("  型別 " + type.Name);
        }

        foreach (PlcTagTable table in EnumerateTagTables(main.TagTableGroup))
        {
            foreach (PlcTag tag in table.Tags)
            {
                if (tag.Name.IndexOf("zigbee", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Console.WriteLine("  PLC Tag " + tag.Name);
                }
            }
        }

        foreach (PlcSoftware plc in LadBlockTools.FindAllPlcSoftwares(project))
        {
            if (string.Equals(plc.Name, main.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (PlcBlock block in EnumerateBlocks(plc.BlockGroup))
            {
                if (block.Name.IndexOf("zigbee", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Console.WriteLine("  其他 PLC " + plc.Name + " 區塊 " + block.Name);
                }
            }
        }

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

                int n = 0;
                foreach (TagTable table in hmi.TagFolder.TagTables)
                {
                    foreach (Tag tag in table.Tags)
                    {
                        if (tag.Name.IndexOf("zigbee", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            n++;
                        }
                    }
                }

                Console.WriteLine("  HMI " + hmi.Name + " Zigbee Tag：" + n);
            }
        }

        foreach (PlcBlock block in EnumerateBlocks(main.BlockGroup))
        {
            if (block.Name.IndexOf("zigbee", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue;
            }

            string path = Path.Combine(dir, SafeFile(block.Name) + "-left.xml");
            if (!ExportBlockXmlQuiet(block, path))
            {
                continue;
            }

            if (CountMainZigbeeSymbols(XDocument.Load(path).Root) > 0)
            {
                ListRemainingMainZigbee(XDocument.Load(path).Root, block.Name);
            }
        }
    }

    private static int StripMainZigbeeDropRoot(XElement root, HashSet<string> dropEvents)
    {
        int n = 0;
        foreach (XElement unit in root.Descendants().Where(IsCompileUnit).ToList())
        {
            CollectDropEventNames(unit, dropEvents);
            string title = CompileUnitTitle(unit);
            if (IsMainZigbeeDropTitle(title))
            {
                Console.WriteLine("    刪網：" + title);
                unit.Remove();
                n++;
                continue;
            }

            if (StripFlgNetZigbeeDrop(unit) > 0)
            {
                Console.WriteLine("    拆 Event：" + title);
                n++;
            }

            if (StripSclIfBlocksMentioningZigbeeDrop(unit) > 0)
            {
                Console.WriteLine("    拆 SCL：" + title);
                n++;
            }
        }

        return n;
    }

    private static bool IsMainZigbeeDropTitle(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return false;
        }

        foreach (string key in MainZigbeeDropTitles)
        {
            if (title.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDropZigbeeMember(string member)
    {
        if (string.IsNullOrEmpty(member))
        {
            return false;
        }

        foreach (string name in MainZigbeeDropMembers)
        {
            if (string.Equals(name, member, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDropEventTag(string name)
    {
        return !string.IsNullOrEmpty(name) &&
            (name.IndexOf("_Wire_Broken", StringComparison.Ordinal) >= 0 ||
             name.IndexOf("_Tension_Drive_Fault", StringComparison.Ordinal) >= 0 ||
             name.IndexOf("_Pico_Comm_Error", StringComparison.Ordinal) >= 0 ||
             name.IndexOf("_Inside_PLC_step_timeout", StringComparison.Ordinal) >= 0);
    }

    private static void CollectDropEventNames(XElement unit, HashSet<string> dropEvents)
    {
        foreach (XElement comp in unit.Descendants().Where(e => e.Name.LocalName == "Component"))
        {
            string name = (string)comp.Attribute("Name");
            if (IsDropEventTag(name))
            {
                dropEvents.Add(name);
            }
        }
    }

    private static bool AccessLooksZigbeeDrop(XElement part)
    {
        foreach (XElement symbol in part.DescendantsAndSelf().Where(e => e.Name.LocalName == "Symbol"))
        {
            List<XElement> comps = SymbolComponents(symbol);
            if (comps.Count == 0)
            {
                continue;
            }

            string first = (string)comps[0].Attribute("Name");
            if (IsDropEventTag(first))
            {
                return true;
            }

            if (first != null && first.StartsWith("Modbus_Comm_Zigbee", StringComparison.Ordinal))
            {
                return true;
            }

            if (!IsMainZigbeeDb(first) || comps.Count < 3)
            {
                continue;
            }

            if (IsDropZigbeeMember((string)comps[2].Attribute("Name")))
            {
                return true;
            }
        }

        return false;
    }

    private static int StripFlgNetZigbeeDrop(XElement unit)
    {
        XElement flg = unit.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        if (flg == null)
        {
            return 0;
        }

        HashSet<string> dropUids = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement access in flg.Descendants().Where(e => e.Name.LocalName == "Access").ToList())
        {
            if (!AccessLooksZigbeeDrop(access))
            {
                continue;
            }

            string uid = (string)access.Attribute("UId");
            if (!string.IsNullOrEmpty(uid))
            {
                dropUids.Add(uid);
            }
        }

        if (dropUids.Count == 0)
        {
            return 0;
        }

        foreach (XElement wire in flg.Descendants().Where(e => e.Name.LocalName == "Wire"))
        {
            bool hitDropAccess = wire.Elements().Any(e =>
                e.Name.LocalName == "IdentCon" &&
                dropUids.Contains((string)e.Attribute("UId")));
            if (!hitDropAccess)
            {
                continue;
            }

            foreach (XElement con in wire.Elements().Where(e =>
                e.Name.LocalName == "NameCon" &&
                (string)e.Attribute("Name") == "operand"))
            {
                string uid = (string)con.Attribute("UId");
                if (!string.IsNullOrEmpty(uid))
                {
                    dropUids.Add(uid);
                }
            }
        }

        foreach (XElement part in flg.Descendants().Where(e =>
            e.Name.LocalName == "Access" ||
            e.Name.LocalName == "Part").ToList())
        {
            string uid = (string)part.Attribute("UId");
            if (string.IsNullOrEmpty(uid) || !dropUids.Contains(uid))
            {
                continue;
            }

            part.Remove();
        }

        foreach (XElement wire in flg.Descendants().Where(e => e.Name.LocalName == "Wire").ToList())
        {
            foreach (XElement con in wire.Elements().Where(e =>
                (e.Name.LocalName == "NameCon" || e.Name.LocalName == "IdentCon") &&
                dropUids.Contains((string)e.Attribute("UId"))).ToList())
            {
                con.Remove();
            }

            int ends = wire.Elements().Count(e =>
                e.Name.LocalName == "NameCon" ||
                e.Name.LocalName == "IdentCon" ||
                e.Name.LocalName == "Powerrail");
            if (ends < 2)
            {
                wire.Remove();
            }
        }

        HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement con in flg.Descendants().Where(e =>
            e.Name.LocalName == "NameCon" || e.Name.LocalName == "IdentCon"))
        {
            string uid = (string)con.Attribute("UId");
            if (!string.IsNullOrEmpty(uid))
            {
                used.Add(uid);
            }
        }

        foreach (XElement part in flg.Descendants().Where(e =>
            e.Name.LocalName == "Access" ||
            e.Name.LocalName == "Call" ||
            e.Name.LocalName == "Part").ToList())
        {
            string uid = (string)part.Attribute("UId");
            if (!string.IsNullOrEmpty(uid) && !used.Contains(uid))
            {
                part.Remove();
            }
        }

        return 1;
    }

    private static int StripSclIfBlocksMentioningZigbeeDrop(XElement unit)
    {
        XElement st = unit.Descendants().FirstOrDefault(e => e.Name.LocalName == "StructuredText");
        if (st == null)
        {
            return 0;
        }

        int changed = 0;
        bool again = true;
        while (again)
        {
            again = false;
            List<XElement> kids = st.Elements().ToList();
            for (int i = 0; i < kids.Count; i++)
            {
                if (!IsSclToken(kids[i], "IF"))
                {
                    continue;
                }

                int end = FindMatchingEndIf(kids, i);
                if (end < 0)
                {
                    continue;
                }

                int last = end;
                if (last + 1 < kids.Count && IsSclToken(kids[last + 1], ";"))
                {
                    last++;
                }

                bool hit = false;
                for (int j = i; j <= last; j++)
                {
                    if (AccessLooksZigbeeDrop(kids[j]))
                    {
                        hit = true;
                        break;
                    }
                }

                if (!hit)
                {
                    continue;
                }

                for (int j = i; j <= last; j++)
                {
                    kids[j].Remove();
                }

                changed = 1;
                again = true;
                break;
            }
        }

        return changed;
    }

    private static bool IsSclToken(XElement el, string text)
    {
        return el.Name.LocalName == "Token" && (string)el.Attribute("Text") == text;
    }

    private static int FindMatchingEndIf(List<XElement> kids, int ifIndex)
    {
        int depth = 0;
        for (int i = ifIndex; i < kids.Count; i++)
        {
            if (IsSclToken(kids[i], "IF"))
            {
                depth++;
                continue;
            }

            if (IsSclToken(kids[i], "END_IF"))
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static HashSet<string> ScanPlcForNames(PlcSoftware plc, string dir, HashSet<string> names)
    {
        HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
        if (names.Count == 0)
        {
            return used;
        }

        string scanDir = Path.Combine(dir, "scan-used");
        Directory.CreateDirectory(scanDir);
        foreach (PlcBlock block in EnumerateBlocks(plc.BlockGroup))
        {
            if (block.Name.StartsWith("Modbus_Zigbee_", StringComparison.Ordinal) ||
                block.Name.StartsWith("Modbus_Comm_Zigbee", StringComparison.Ordinal) ||
                string.Equals(block.ProgrammingLanguage.ToString(), "DB", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string path = Path.Combine(scanDir, SafeFile(block.Name) + ".xml");
            if (!ExportBlockXmlQuiet(block, path))
            {
                continue;
            }

            XElement root = XDocument.Load(path).Root;
            foreach (XElement comp in root.Descendants().Where(e => e.Name.LocalName == "Component"))
            {
                string name = (string)comp.Attribute("Name");
                if (name != null && names.Contains(name))
                {
                    used.Add(name);
                }
            }
        }

        return used;
    }

    private static readonly string[] ZigbeeRemapBlocks =
    {
        "Main_Logic",
        "Event_Control",
        "Main",
        "Bobbin_Loaded_Check",
        "Bobbin_Unloaded_Check",
        "Bobbin_solenoid valve_Ctrl"
    };

    private static readonly string[] ZigbeeDropBlocks =
    {
        "Modbus_DB_PLC_Zigbee"
    };

    private static readonly string[] ZigbeeDropTypes =
    {
        "Modbus_PLC_48B",
        "Modbus_Pico_Server",
        "Pico_Status_bit"
    };

    public static int StripInsideZigbee(TiaPortal portal, Project project)
    {
        Console.WriteLine("四台 Inside：Zigbee 資料／Tag／標題拿掉，對得到的改 for_dc12_plc。");

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "inside-rtu", "strip-zigbee");
        Directory.CreateDirectory(dir);

        int failed = 0;
        foreach (string plcName in InsidePlcNames)
        {
            Console.WriteLine("---- " + plcName + " ----");
            if (StripOneInsideZigbee(project, plcName, dir) != 0)
            {
                failed++;
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + "）。");
        }

        return failed == 0 ? 0 : 1;
    }

    private static int StripOneInsideZigbee(Project project, string plcName, string dir)
    {
        PlcSoftware plc = FindPlc(project, plcName);
        if (plc == null)
        {
            Console.WriteLine("找不到 " + plcName + "。");
            return 1;
        }

        if (FindBlock(plc, "for_dc12_plc") == null)
        {
            Console.WriteLine("找不到 for_dc12_plc。");
            return 1;
        }

        string plcDir = Path.Combine(dir, plcName.Replace("25017_", string.Empty));
        Directory.CreateDirectory(plcDir);
        RestoreZigbeeCommTagForExport(plc);
        if (CompilePlc(plc) != 0)
        {
            Console.WriteLine("改回 Tag 後編譯失敗，匯不出。");
            return 1;
        }

        List<string> patched = new List<string>();
        foreach (string name in ZigbeeRemapBlocks)
        {
            PlcBlock block = FindBlock(plc, name);
            if (block == null)
            {
                continue;
            }

            string before = Path.Combine(plcDir, name + "-before.xml");
            if (!ExportBlockXmlQuiet(block, before))
            {
                return 1;
            }

            XDocument doc = XDocument.Load(before);
            int changed = PatchZigbeeOutOfBlock(doc.Root);
            Console.WriteLine("  " + name + " 變更：" + changed);
            if (!VerifyNoZigbeeName(doc.Root))
            {
                Console.WriteLine("  " + name + " 還有 Zigbee，未匯入。");
                return 1;
            }

            string importPath = Path.Combine(plcDir, name + ".xml");
            doc.Save(importPath);
            patched.Add(name);
        }

        RenameZigbeeCommTag(plc);

        foreach (string name in patched)
        {
            TryImportBlockFile(plc, FindBlock(plc, name), Path.Combine(plcDir, name + ".xml"));
        }

        if (CompilePlc(plc) != 0)
        {
            return 1;
        }

        foreach (string name in patched)
        {
            PlcBlock imported = FindBlock(plc, name);
            string after = Path.Combine(plcDir, name + "-after.xml");
            if (imported == null || !ExportBlockXmlQuiet(imported, after) ||
                !VerifyNoZigbeeName(XDocument.Load(after).Root))
            {
                Console.WriteLine("  " + name + " 匯入後驗證失敗。");
                return 1;
            }
        }

        foreach (string name in ZigbeeDropBlocks)
        {
            PlcBlock block = FindBlock(plc, name);
            if (block == null)
            {
                Console.WriteLine("  沒有 " + name + "，略過。");
                continue;
            }

            try
            {
                block.Delete();
                Console.WriteLine("  已刪 " + name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  刪不掉 " + name + "：" + Flatten(ex));
                return 1;
            }
        }

        foreach (string name in ZigbeeDropTypes)
        {
            PlcType type = FindType(plc, name);
            if (type == null)
            {
                continue;
            }

            try
            {
                type.Delete();
                Console.WriteLine("  已刪型別 " + name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  型別暫不刪 " + name + "：" + Flatten(ex));
            }
        }

        if (FindLeftoverZigbee(plc, plcDir))
        {
            Console.WriteLine("還有 Zigbee 名稱沒清完。");
            return 1;
        }

        return CompilePlc(plc) == 0 ? 0 : 1;
    }

    private static void RestoreZigbeeCommTagForExport(PlcSoftware plc)
    {
        PlcTag renamed = FindTag(plc, "AErr_DC12_Comm_Error");
        if (renamed != null && FindTag(plc, "AErr_Zigbee_Comm_Error") == null)
        {
            renamed.Name = "AErr_Zigbee_Comm_Error";
            Console.WriteLine("  先把 AErr_DC12_Comm_Error 改回，才能匯出。");
        }
    }

    private static void RenameZigbeeCommTag(PlcSoftware plc)
    {
        PlcTag tag = FindTag(plc, "AErr_Zigbee_Comm_Error");
        if (tag == null)
        {
            return;
        }

        if (FindTag(plc, "AErr_DC12_Comm_Error") == null)
        {
            tag.Name = "AErr_DC12_Comm_Error";
            Console.WriteLine("  Tag AErr_Zigbee_Comm_Error → AErr_DC12_Comm_Error");
        }
    }

    private static int PatchZigbeeOutOfBlock(XElement root)
    {
        int n = 0;
        n += ReplaceAttrName(root, "AErr_Zigbee_Comm_Error", "AErr_DC12_Comm_Error");
        n += ReplaceAttrName(root, "watchdog_zigbee", "watchdog_dc12");
        n += ReplaceZigbeeText(root);
        n += WidenCtrlBits2(root);
        n += RemapZigbeeSymbols(root);
        n += DropUnitsStillZigbeeDb(root);
        return n > 0 ? 1 : 0;
    }

    private static int RemapZigbeeSymbols(XElement root)
    {
        int n = 0;
        foreach (XElement symbol in root.Descendants().Where(e => e.Name.LocalName == "Symbol"))
        {
            List<XElement> comps = SymbolComponents(symbol);
            if (comps.Count < 3 ||
                (string)comps[0].Attribute("Name") != "Modbus_DB_PLC_Zigbee")
            {
                continue;
            }

            string section = (string)comps[1].Attribute("Name");
            string member = (string)comps[2].Attribute("Name");
            string newSection;
            string newMember;
            if (!TryMapZigbeeMember(section, member, out newSection, out newMember))
            {
                continue;
            }

            comps[0].SetAttributeValue("Name", "for_dc12_plc");
            comps[1].SetAttributeValue("Name", newSection);
            comps[2].SetAttributeValue("Name", newMember);
            n++;
        }

        return n;
    }

    private static bool TryMapZigbeeMember(
        string section,
        string member,
        out string newSection,
        out string newMember)
    {
        newSection = null;
        newMember = null;
        if (section == "Write")
        {
            if (member == "rotor_section_id")
            {
                newSection = "Read";
                newMember = "rotor_section_id";
                return true;
            }

            if (member == "watch_dog")
            {
                newSection = "Read";
                newMember = "plc_WatchDog";
                return true;
            }

            if (member == "ctrl_bits_1" || member == "ctrl_bits_2")
            {
                newSection = "Read";
                newMember = "ctrl_bit";
                return true;
            }
        }

        if (section == "Read")
        {
            if (member == "rotor_section_id" ||
                member == "bobbin_ctrl_code" ||
                member == "safety_bar_ctrl_code")
            {
                newSection = "Write_From_DC12";
                newMember = member;
                return true;
            }

            if (member == "ctrl_bits_1" || member == "ctrl_bits_2")
            {
                newSection = "Write_From_DC12";
                newMember = "ctrl_bit";
                return true;
            }

            if (member == "bobbin_status_bits_1")
            {
                newSection = "Read";
                newMember = "safty_bar_snr";
                return true;
            }
        }

        return false;
    }

    private static int DropUnitsStillZigbeeDb(XElement root)
    {
        List<XElement> drop = new List<XElement>();
        foreach (XElement unit in root.Descendants().Where(IsCompileUnit).ToList())
        {
            if (UnitMentionsName(unit, "Modbus_DB_PLC_Zigbee"))
            {
                drop.Add(unit);
            }
        }

        foreach (XElement unit in drop)
        {
            Console.WriteLine("    刪還對不到的網：" + CompileUnitTitle(unit));
            unit.Remove();
        }

        return drop.Count;
    }

    private static int WidenCtrlBits2(XElement root)
    {
        int n = 0;
        foreach (XElement node in root.Descendants().Where(e =>
            (e.Name.LocalName == "Member" || e.Name.LocalName == "Parameter") &&
            (string)e.Attribute("Name") == "ctrl_bits_2"))
        {
            XAttribute dt = node.Attribute("Datatype") ?? node.Attribute("Type");
            if (dt == null || dt.Value.IndexOf("bobbin_total", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            dt.Value = "Array[0..63] of Bool";
            n++;
        }

        return n;
    }

    private static int ReplaceAttrName(XElement root, string from, string to)
    {
        int n = 0;
        foreach (XElement node in root.Descendants())
        {
            XAttribute name = node.Attribute("Name");
            if (name != null && name.Value == from)
            {
                name.Value = to;
                n++;
            }
        }

        return n;
    }

    private static int ReplaceZigbeeText(XElement root)
    {
        int n = 0;
        foreach (XElement text in root.Descendants().Where(e => e.Name.LocalName == "Text"))
        {
            string value = text.Value ?? string.Empty;
            if (value.IndexOf("zigbee", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            text.Value = value
                .Replace("via zigbee", "via DC12")
                .Replace("via Zigbee", "via DC12")
                .Replace("Zigbee slave", "DC12 slave")
                .Replace("zigbee", "DC12")
                .Replace("Zigbee", "DC12");
            n++;
        }

        return n;
    }

    private static bool VerifyNoZigbeeName(XElement root)
    {
        foreach (XElement node in root.Descendants())
        {
            string name = (string)node.Attribute("Name");
            if (!string.IsNullOrEmpty(name) &&
                name.IndexOf("zigbee", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            if (node.Name.LocalName == "Text" &&
                !string.IsNullOrEmpty(node.Value) &&
                node.Value.IndexOf("zigbee", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool FindLeftoverZigbee(PlcSoftware plc, string dir)
    {
        bool hit = false;
        foreach (PlcBlock block in EnumerateBlocks(plc.BlockGroup))
        {
            if (block.Name.IndexOf("zigbee", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Console.WriteLine("  還有區塊 " + block.Name);
                hit = true;
            }
        }

        foreach (PlcType type in EnumerateTypes(plc.TypeGroup))
        {
            if (type.Name.IndexOf("zigbee", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Console.WriteLine("  還有型別 " + type.Name);
                hit = true;
            }
        }

        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            foreach (PlcTag tag in table.Tags)
            {
                if (tag.Name.IndexOf("zigbee", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Console.WriteLine("  還有 Tag " + tag.Name);
                    hit = true;
                }
            }
        }

        string[] check = { "Main", "Main_Logic", "Event_Control" };
        foreach (string name in check)
        {
            PlcBlock block = FindBlock(plc, name);
            if (block == null)
            {
                continue;
            }

            string path = Path.Combine(dir, name + "-leftover.xml");
            if (!ExportBlockXmlQuiet(block, path))
            {
                continue;
            }

            if (!VerifyNoZigbeeName(XDocument.Load(path).Root))
            {
                Console.WriteLine("  " + name + " 本文還有 Zigbee");
                hit = true;
            }
        }

        return hit;
    }

    public static int StripInsidePico(TiaPortal portal, Project project)
    {
        Console.WriteLine("四台 Inside 拿掉 Pico。Modbus_DB_PLC_Zigbee 先留，列出還在用的網。");

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "inside-rtu", "strip-pico");
        Directory.CreateDirectory(dir);

        string[] plcNames =
        {
            "25017_6B_Inside_PLC",
            "25017_12B_Inside_PLC",
            "25017_20B_Inside_PLC",
            "25017_24B_Inside_PLC"
        };

        int failed = 0;
        foreach (string plcName in plcNames)
        {
            Console.WriteLine("---- " + plcName + " ----");
            if (StripOneInsidePico(project, plcName, dir) != 0)
            {
                failed++;
            }

            project.Save();
            Console.WriteLine("已存（" + plcName + "）。");
        }

        return failed == 0 ? 0 : 1;
    }

    private static readonly string[] PicoDropBlocks =
    {
        "Pico_UID_Ctrl",
        "Modbus_Comm_Pico_Server_DB",
        "Modbus_DB_Pico_Server",
        "Modbus_Comm_Pico_Server",
        "Bobbin_valve_shift",
        "Bobbin_snr_shift"
    };

    private static readonly string[] PicoDropTypes =
    {
        "Pico_UID_write"
    };

    private static int StripOneInsidePico(Project project, string plcName, string dir)
    {
        PlcSoftware plc = FindPlc(project, plcName);
        if (plc == null)
        {
            Console.WriteLine("找不到 " + plcName + "。");
            return 1;
        }

        string plcDir = Path.Combine(dir, plcName.Replace("25017_", string.Empty));
        Directory.CreateDirectory(plcDir);
        if (FindType(plc, "Pico_Status_bit") == null)
        {
            string typePath = Path.Combine(
                Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
                "HmiExport", "Templates", "26037_Cusor",
                "PLC_24137_6B_Inside_PLC", "Types", "Pico_Status_bit.xml");
            if (!ImportTypeFile(plc, null, typePath))
            {
                return 1;
            }
        }

        ListRemainingZigbeeUses(plc, plcDir);

        int mainChanged = PatchNamedBlock(
            plc,
            plcDir,
            "Main",
            root => RemovePicoCompileUnits(root, true),
            VerifyInsideMainRtu);
        Console.WriteLine("  Main：" + mainChanged);
        if (mainChanged < 0)
        {
            return 1;
        }

        int logicChanged = PatchNamedBlock(
            plc,
            plcDir,
            "Main_Logic",
            root => RemovePicoCompileUnits(root, false),
            root => !BlockMentionsPico(root));
        Console.WriteLine("  Main_Logic：" + logicChanged);
        if (logicChanged < 0)
        {
            return 1;
        }

        int eventChanged = PatchNamedBlock(
            plc,
            plcDir,
            "Event_Control",
            PatchEventControlDropPico,
            root => !BlockMentionsPico(root));
        Console.WriteLine("  Event_Control：" + eventChanged);
        if (eventChanged < 0)
        {
            return 1;
        }

        foreach (string name in PicoDropBlocks)
        {
            PlcBlock block = FindBlock(plc, name);
            if (block == null)
            {
                Console.WriteLine("  沒有 " + name + "，略過。");
                continue;
            }

            try
            {
                block.Delete();
                Console.WriteLine("  已刪 " + name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  刪不掉 " + name + "：" + Flatten(ex));
                return 1;
            }
        }

        foreach (string name in PicoDropTypes)
        {
            PlcType type = FindType(plc, name);
            if (type == null)
            {
                continue;
            }

            try
            {
                type.Delete();
                Console.WriteLine("  已刪型別 " + name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  型別暫不刪 " + name + "：" + Flatten(ex));
            }
        }

        if (FindBlock(plc, "Modbus_DB_PLC_Zigbee") == null)
        {
            Console.WriteLine("  Modbus_DB_PLC_Zigbee 不見了，不該這輪刪。");
            return 1;
        }

        int errors = CompilePlc(plc);
        return errors == 0 ? 0 : 1;
    }

    private static void ListRemainingZigbeeUses(PlcSoftware plc, string dir)
    {
        string[] names =
        {
            "Main", "Main_Logic", "Event_Control",
            "Bobbin_Loaded_Check", "Bobbin_Unloaded_Check"
        };
        List<string> lines = new List<string>();
        lines.Add(plc.Name);
        foreach (string name in names)
        {
            PlcBlock block = FindBlock(plc, name);
            if (block == null)
            {
                continue;
            }

            string path = Path.Combine(dir, name + "-uses.xml");
            if (!ExportBlockXmlQuiet(block, path))
            {
                continue;
            }

            XElement root = XDocument.Load(path).Root;
            int n = 0;
            foreach (XElement unit in root.Descendants().Where(IsCompileUnit))
            {
                n++;
                if (!UnitMentionsName(unit, "Modbus_DB_PLC_Zigbee") &&
                    !UnitMentionsName(unit, "Modbus_DB_Pico_Server") &&
                    !UnitMentionsName(unit, "Modbus_Comm_Pico_Server") &&
                    !UnitMentionsName(unit, "Pico_UID_Ctrl"))
                {
                    continue;
                }

                string title = CompileUnitTitle(unit);
                string row = "  " + name + " NW" + n + "  " + title;
                Console.WriteLine(row);
                lines.Add(row);
            }
        }

        File.WriteAllLines(Path.Combine(dir, "uses.txt"), lines, new UTF8Encoding(false));
    }

    private static int RemovePicoCompileUnits(XElement root, bool mainOb)
    {
        List<XElement> drop = new List<XElement>();
        foreach (XElement unit in root.Descendants().Where(IsCompileUnit).ToList())
        {
            if (IsPicoCompileUnit(unit, mainOb))
            {
                drop.Add(unit);
            }
        }

        foreach (XElement unit in drop)
        {
            unit.Remove();
        }

        return drop.Count > 0 ? 1 : 0;
    }

    private static bool IsPicoCompileUnit(XElement unit, bool mainOb)
    {
        if (HasCall(unit, "WatchDog_Int"))
        {
            return false;
        }

        if (UnitMentionsName(unit, "Modbus_Comm_Pico_Server") ||
            UnitMentionsName(unit, "Modbus_DB_Pico_Server") ||
            UnitMentionsName(unit, "Modbus_DB_Pico") ||
            UnitMentionsName(unit, "Pico_UID_Ctrl") ||
            UnitMentionsName(unit, "Modbus_Comm_Pico_Server_DB"))
        {
            return true;
        }

        if (mainOb)
        {
            return false;
        }

        string title = CompileUnitTitle(unit);
        return title.IndexOf("pico", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int PatchEventControlDropPico(XElement root)
    {
        int changed = RemovePicoCompileUnits(root, false);
        foreach (XElement unit in root.Descendants().Where(IsCompileUnit).ToList())
        {
            if (HasCall(unit, "WatchDog_Dint") || UnitMentionsName(unit, "Modbus_DB_Pico_Server"))
            {
                if (StripPicoParts(unit) > 0)
                {
                    changed = 1;
                }
            }
        }

        return BlockMentionsPico(root) ? -1 : (changed > 0 ? 1 : 0);
    }

    private static int StripPicoParts(XElement unit)
    {
        XElement flg = unit.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        if (flg == null)
        {
            return 0;
        }

        bool watchdog = HasCall(unit, "WatchDog_Int");
        HashSet<string> dropUids = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement part in flg.Descendants().Where(e =>
            e.Name.LocalName == "Access" ||
            e.Name.LocalName == "Call" ||
            e.Name.LocalName == "Part").ToList())
        {
            bool contactOrCoil = part.Name.LocalName == "Part" &&
                ((string)part.Attribute("Name") == "Contact" ||
                 (string)part.Attribute("Name") == "Coil");
            if (!PartLooksPico(part) && !(watchdog && contactOrCoil))
            {
                continue;
            }

            string uid = (string)part.Attribute("UId");
            if (!string.IsNullOrEmpty(uid))
            {
                dropUids.Add(uid);
            }

            part.Remove();
        }

        if (dropUids.Count == 0)
        {
            return 0;
        }

        foreach (XElement wire in flg.Descendants().Where(e => e.Name.LocalName == "Wire").ToList())
        {
            bool hit = wire.Descendants().Any(e =>
                (e.Name.LocalName == "NameCon" || e.Name.LocalName == "IdentCon") &&
                dropUids.Contains((string)e.Attribute("UId")));
            if (!hit)
            {
                continue;
            }

            foreach (XElement con in wire.Elements().Where(e =>
                (e.Name.LocalName == "NameCon" || e.Name.LocalName == "IdentCon") &&
                dropUids.Contains((string)e.Attribute("UId"))).ToList())
            {
                con.Remove();
            }

            int ends = wire.Elements().Count(e =>
                e.Name.LocalName == "NameCon" ||
                e.Name.LocalName == "IdentCon" ||
                e.Name.LocalName == "Powerrail");
            if (ends < 2)
            {
                wire.Remove();
            }
        }

        HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement con in flg.Descendants().Where(e =>
            e.Name.LocalName == "NameCon" || e.Name.LocalName == "IdentCon"))
        {
            string uid = (string)con.Attribute("UId");
            if (!string.IsNullOrEmpty(uid))
            {
                used.Add(uid);
            }
        }

        foreach (XElement part in flg.Descendants().Where(e =>
            e.Name.LocalName == "Access" ||
            e.Name.LocalName == "Call" ||
            e.Name.LocalName == "Part").ToList())
        {
            string uid = (string)part.Attribute("UId");
            if (!string.IsNullOrEmpty(uid) && !used.Contains(uid))
            {
                part.Remove();
            }
        }

        return 1;
    }

    private static bool PartLooksPico(XElement part)
    {
        if (part.DescendantsAndSelf().Any(e =>
            e.Name.LocalName == "CallInfo" &&
            (string)e.Attribute("Name") == "WatchDog_Dint"))
        {
            return true;
        }

        return part.DescendantsAndSelf().Any(e =>
        {
            string n = (string)e.Attribute("Name");
            return !string.IsNullOrEmpty(n) &&
                (n.IndexOf("Pico", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 n.IndexOf("Modbus_DB_Pico", StringComparison.OrdinalIgnoreCase) >= 0);
        });
    }

    private static bool BlockMentionsPico(XElement root)
    {
        return UnitMentionsName(root, "Modbus_Comm_Pico_Server") ||
            UnitMentionsName(root, "Modbus_DB_Pico_Server") ||
            UnitMentionsName(root, "Modbus_DB_Pico") ||
            UnitMentionsName(root, "Pico_UID_Ctrl") ||
            UnitMentionsName(root, "Modbus_Comm_Pico_Server_DB");
    }

    private static bool UnitMentionsName(XElement unit, string name)
    {
        return unit.DescendantsAndSelf().Any(e =>
            ((e.Name.LocalName == "Component" || e.Name.LocalName == "CallInfo") &&
             (string)e.Attribute("Name") == name) ||
            (e.Name.LocalName == "Constant" &&
             (string)e.Attribute("Name") != null &&
             ((string)e.Attribute("Name")).IndexOf("CB_1241_PICO", StringComparison.OrdinalIgnoreCase) >= 0 &&
             name.IndexOf("Pico", StringComparison.OrdinalIgnoreCase) >= 0));
    }

    private static string CompileUnitTitle(XElement unit)
    {
        XElement title = unit.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "MultilingualText" &&
            (string)e.Attribute("CompositionName") == "Title");
        if (title == null)
        {
            return string.Empty;
        }

        XElement en = title.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Culture" &&
            ((string)e == "en-US" || (e.Value == "en-US")));
        if (en != null)
        {
            XElement text = en.Parent.Elements().FirstOrDefault(e => e.Name.LocalName == "Text");
            if (text != null && !string.IsNullOrWhiteSpace(text.Value))
            {
                return text.Value.Trim();
            }
        }

        XElement any = title.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Text" && !string.IsNullOrWhiteSpace(e.Value));
        return any == null ? string.Empty : any.Value.Trim();
    }

    private static int WireOneInsideRtu(
        Project project,
        string plcName,
        string dir,
        string sclDir)
    {
        PlcSoftware plc = FindPlc(project, plcName);
        if (plc == null)
        {
            Console.WriteLine("找不到 " + plcName + "。");
            return 1;
        }

        foreach (string file in new[] { "for_dc12_plc.scl", "DC12_RTU_Config.scl" })
        {
            string path = Path.Combine(sclDir, file);
            if (!File.Exists(path))
            {
                Console.WriteLine("找不到 SCL：" + path);
                return 1;
            }

            string blockName = SclBlockName(path);
            if (blockName != null && FindBlock(plc, blockName) != null)
            {
                Console.WriteLine("已有 " + blockName + "，略過 SCL。");
                continue;
            }

            if (!ImportSclFile(plc, path))
            {
                return 1;
            }
        }

        if (FindBlock(plc, "DC12_RTU_Slave") != null)
        {
            Console.WriteLine("已有 DC12_RTU_Slave，略過 FB。");
        }
        else if (!ImportLadSpec(plc, dir, "DC12_RTU_Slave"))
        {
            return 1;
        }

        EnsureModbusInstanceDb(project, plc, "DC12_RTU_Slave_DB", "DC12_RTU_Slave");
        if (FindBlock(plc, "DC12_RTU_Slave_DB") == null)
        {
            Console.WriteLine("找不到 DC12_RTU_Slave_DB。");
            return 1;
        }

        string plcDir = Path.Combine(dir, plcName.Replace("25017_", string.Empty));
        Directory.CreateDirectory(plcDir);
        int mainChanged = PatchNamedBlock(
            plc,
            plcDir,
            "Main",
            root => PatchInsideMainToRtu(root, dir),
            VerifyInsideMainRtu);
        Console.WriteLine("  Main：" + mainChanged);
        if (mainChanged < 0)
        {
            return 1;
        }

        int errors = CompilePlc(plc);
        if (errors == 0)
        {
            MoveInsideRtuBlocks(plc, dir);
            errors = CompilePlc(plc);
            if (errors == 0)
            {
                MoveInsideRtuBlocks(plc, dir);
            }
        }

        return errors == 0 ? 0 : 1;
    }

    private static bool ImportLadSpec(PlcSoftware plc, string dir, string name)
    {
        string specPath = Path.Combine(dir, name + ".lad.xml");
        string generatedPath = Path.Combine(dir, name + ".generated.xml");
        if (!File.Exists(specPath))
        {
            Console.WriteLine("找不到 " + specPath);
            return false;
        }

        File.WriteAllText(generatedPath, LadWriter.Generate(specPath), new UTF8Encoding(false));
        TryImportBlockFile(plc, FindBlock(plc, name), generatedPath);
        if (FindBlock(plc, name) == null)
        {
            Console.WriteLine("匯入 " + name + " 之後找不到區塊。");
            return false;
        }

        Console.WriteLine("已匯入 " + name);
        return true;
    }

    private static int PatchInsideMainToRtu(XElement root, string dir)
    {
        if (VerifyInsideMainRtu(root))
        {
            return 0;
        }

        string specPath = Path.Combine(dir, "Call-DC12_RTU_Slave.lad.xml");
        if (!File.Exists(specPath))
        {
            Console.WriteLine("找不到 " + specPath);
            return -1;
        }

        string generatedPath = Path.Combine(dir, "Call-DC12_RTU_Slave.generated.xml");
        File.WriteAllText(generatedPath, LadWriter.Generate(specPath), new UTF8Encoding(false));
        XDocument generated = XDocument.Load(generatedPath);
        XElement newFlg = generated.Root.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        if (newFlg == null)
        {
            Console.WriteLine("Call-DC12_RTU_Slave 沒有 FlgNet。");
            return -1;
        }

        XElement unit = root.Descendants().FirstOrDefault(e =>
            IsCompileUnit(e) &&
            e.Descendants().Any(c =>
                c.Name.LocalName == "CallInfo" &&
                (string)c.Attribute("Name") == "Modbus_Comm_PLC_Zigbee"));
        if (unit == null)
        {
            unit = root.Descendants().FirstOrDefault(e =>
                IsCompileUnit(e) &&
                e.Descendants().Any(c =>
                    c.Name.LocalName == "CallInfo" &&
                    (string)c.Attribute("Name") == "DC12_RTU_Slave"));
        }

        if (unit == null)
        {
            Console.WriteLine("Main 找不到 Zigbee／RTU 通訊 CompileUnit。");
            return -1;
        }

        XElement networkSource = unit.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "NetworkSource");
        if (networkSource == null)
        {
            Console.WriteLine("Main CompileUnit 沒有 NetworkSource。");
            return -1;
        }

        networkSource.ReplaceAll(new XElement(newFlg));
        SetCompileUnitText(unit, "Title", "DC12 Modbus RTU slave");
        SetCompileUnitText(
            unit,
            "Comment",
            "Replace Zigbee slave. PORT=CM 1241, station 30, PDU 4100.");
        return VerifyInsideMainRtu(root) ? 1 : -1;
    }

    private static bool VerifyInsideMainRtu(XElement root)
    {
        bool slave = root.Descendants().Any(e =>
            e.Name.LocalName == "CallInfo" &&
            (string)e.Attribute("Name") == "DC12_RTU_Slave");
        bool cm = root.Descendants().Any(e =>
            e.Name.LocalName == "Constant" &&
            (string)e.Attribute("Name") == "Local~CM_1241_Main_PLC" &&
            e.Ancestors().Any(a =>
                a.Name.LocalName == "CallInfo" &&
                (string)a.Attribute("Name") == "DC12_RTU_Slave"));
        if (!cm)
        {
            cm = root.Descendants().Any(e =>
                e.Name.LocalName == "Constant" &&
                (string)e.Attribute("Name") == "Local~CM_1241_Main_PLC");
        }

        bool zigbee = root.Descendants().Any(e =>
            e.Name.LocalName == "CallInfo" &&
            (string)e.Attribute("Name") == "Modbus_Comm_PLC_Zigbee");
        bool pico = root.Descendants().Any(e =>
            e.Name.LocalName == "CallInfo" &&
            (string)e.Attribute("Name") == "Modbus_Comm_Pico_Server");
        return slave && cm && !zigbee && !pico;
    }

    private static void MoveInsideRtuBlocks(PlcSoftware plc, string dir)
    {
        PlcBlockGroup dest = EnsureUserBlockGroup(plc.BlockGroup, "2.Communication");
        List<BlockMove> pending = new List<BlockMove>();
        foreach (string name in new[]
        {
            "for_dc12_plc", "DC12_RTU_Config", "DC12_RTU_Slave", "DC12_RTU_Slave_DB"
        })
        {
            BlockMove item = PrepareBlockMove(plc, dest, "2.Communication", name, dir);
            if (item != null)
            {
                pending.Add(item);
            }
        }

        foreach (BlockMove item in pending)
        {
            CommitBlockMove(item);
        }
    }

    private static string SclBlockName(string path)
    {
        Match nameMatch = Regex.Match(
            File.ReadAllText(path),
            @"(?:FUNCTION(?:_BLOCK)?|DATA_BLOCK)\s+""([^""]+)""",
            RegexOptions.IgnoreCase);
        return nameMatch.Success ? nameMatch.Groups[1].Value : null;
    }

    private static bool EnsureMbClientDb(PlcSoftware plc, string dir)
    {
        PlcBlock existing = FindBlock(plc, "MB_CLIENT_DB");
        if (existing != null)
        {
            Console.WriteLine("  已有 MB_CLIENT_DB（DB" + existing.Number + "）");
            return true;
        }

        string source = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "tcp-from-dsp", "MB_CLIENT_DB.xml");
        if (!File.Exists(source))
        {
            Console.WriteLine("找不到 " + source);
            return false;
        }

        XDocument doc = XDocument.Load(source);
        XElement nameNode = doc.Root.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Name" &&
                e.Parent != null &&
                e.Parent.Name.LocalName == "AttributeList" &&
                e.Parent.Elements().Any(c => c.Name.LocalName == "InstanceOfName"));
        XElement numberNode = nameNode == null
            ? null
            : nameNode.Parent.Elements().FirstOrDefault(e => e.Name.LocalName == "Number");
        if (nameNode == null || numberNode == null)
        {
            Console.WriteLine("MB_CLIENT_DB XML 沒有 Name/Number。");
            return false;
        }

        numberNode.Value = NextFreeUserOrSystemDbNumber(plc).ToString();
        string dest = Path.Combine(dir, "MB_CLIENT_DB.xml");
        doc.Save(dest);
        TryImportBlockFile(plc, null, dest);
        existing = FindBlock(plc, "MB_CLIENT_DB");
        if (existing == null)
        {
            Console.WriteLine("匯入 MB_CLIENT_DB 失敗。");
            return false;
        }

        Console.WriteLine("  匯入 MB_CLIENT_DB（系統 MB_CLIENT 6.0／DB" + existing.Number + "）");
        return true;
    }

    private static bool ImportSclFile(PlcSoftware plc, string path)
    {
        string sourceName = Path.GetFileName(path);
        PlcExternalSource existingSource = plc.ExternalSourceGroup.ExternalSources.Find(sourceName);
        if (existingSource != null)
        {
            existingSource.Delete();
        }

        string sourceText = File.ReadAllText(path);
        Match nameMatch = Regex.Match(
            sourceText,
            @"(?:FUNCTION(?:_BLOCK)?|DATA_BLOCK)\s+""([^""]+)""",
            RegexOptions.IgnoreCase);
        if (nameMatch.Success)
        {
            PlcBlock existingBlock = FindBlock(plc, nameMatch.Groups[1].Value);
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

        try
        {
            plc.ExternalSourceGroup.ExternalSources
                .CreateFromFile(sourceName, path)
                .GenerateBlocksFromSource(option);
        }
        catch (Exception ex)
        {
            Console.WriteLine("SCL 匯入失敗：" + sourceName + "：" + Flatten(ex));
            return false;
        }

        if (nameMatch.Success && FindBlock(plc, nameMatch.Groups[1].Value) == null)
        {
            Console.WriteLine("SCL 產生後找不到區塊：" + nameMatch.Groups[1].Value);
            return false;
        }

        Console.WriteLine("已產生區塊：" + sourceName);
        return true;
    }

    private static bool HasTcpSeqCall(XElement root)
    {
        return root.Descendants().Any(e =>
            e.Name.LocalName == "CallInfo" &&
            (string)e.Attribute("Name") == "TCP_Seq") &&
            root.Descendants().Any(e =>
                e.Name.LocalName == "Part" &&
                (string)e.Attribute("Name") == "MB_CLIENT");
    }

    private static bool HasZigbeeMasterCall(XElement root)
    {
        return root.Descendants().Any(e =>
            e.Name.LocalName == "CallInfo" &&
            (string)e.Attribute("Name") == "Modbus_Comm_Zigbee");
    }

    private static int PatchMainZigbeeToTcp(XElement root, string dir)
    {
        if (HasTcpSeqCall(root) && !HasZigbeeMasterCall(root))
        {
            return 0;
        }

        string specPath = Path.Combine(dir, "Call-TCP_Seq.lad.xml");
        if (!File.Exists(specPath))
        {
            Console.WriteLine("找不到 " + specPath);
            return -1;
        }

        string generatedPath = Path.Combine(dir, "Call-TCP_Seq.generated.xml");
        File.WriteAllText(generatedPath, LadWriter.Generate(specPath), new UTF8Encoding(false));
        XDocument generated = XDocument.Load(generatedPath);
        XElement newFlg = generated.Root.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        if (newFlg == null)
        {
            Console.WriteLine("Call-TCP_Seq 沒有 FlgNet。");
            return -1;
        }

        XElement unit = root.Descendants().FirstOrDefault(e =>
            IsCompileUnit(e) &&
            e.Descendants().Any(c =>
                c.Name.LocalName == "CallInfo" &&
                (string)c.Attribute("Name") == "Modbus_Comm_Zigbee"));
        if (unit == null)
        {
            unit = root.Descendants().FirstOrDefault(IsCompileUnit);
        }

        if (unit == null)
        {
            Console.WriteLine("Main 找不到可換成 TCP 的 CompileUnit。");
            return -1;
        }

        XElement networkSource = unit.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "NetworkSource");
        if (networkSource == null)
        {
            Console.WriteLine("Main CompileUnit 沒有 NetworkSource。");
            return -1;
        }

        networkSource.ReplaceAll(new XElement(newFlg));
        SetCompileUnitText(unit, "Title", "Modbus TCP poll MB_CLIENT");
        SetCompileUnitText(
            unit,
            "Comment",
            "Replace Zigbee masters. TCP_Seq + one MB_CLIENT. Station 0-3 = 6B/12B/20B/24B.");
        return 1;
    }

    private static void SetCompileUnitText(XElement unit, string composition, string text)
    {
        foreach (XElement multi in unit.Descendants().Where(e =>
            e.Name.LocalName == "MultilingualText" &&
            (string)e.Attribute("CompositionName") == composition))
        {
            foreach (XElement item in multi.Descendants().Where(e => e.Name.LocalName == "Text"))
            {
                item.Value = text;
            }
        }
    }

    public static int WireRotorSync(TiaPortal portal, Project project)
    {
        Console.WriteLine("運轉角度同步照 22021：Rotor_Sync_Ctrl + PID → Cyclic。定位不改。");
        Console.WriteLine("enc_max 先 50000。Speed_Loop 補 pretwist_factor／ovrflow。");
        PlcSoftware main = FindPlc(project, "25017_Main_PLC");
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        if (FindBlock(main, "PID_Rotor_Sync") == null)
        {
            Console.WriteLine("找不到 PID_Rotor_Sync。");
            return 1;
        }

        PlcBlock cyclicFix = FindBlock(main, "Cyclic interrupt");
        if (cyclicFix != null)
        {
            SetCyclicInterval(cyclicFix);
        }

        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "rotor-sync");
        Directory.CreateDirectory(dir);

        int preCompileErrors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後再改）。");
        if (preCompileErrors != 0 && FindBlock(main, "Rotor_Sync_Ctrl") == null)
        {
            return 1;
        }

        if (SetRotorEncMax(main, dir, "50000.0") < 0)
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（enc_max=50000）。");

        EnsureModbusInstanceDb(project, main, "PID_Rotor_Sync_12B_DB", "PID_Rotor_Sync");
        EnsureModbusInstanceDb(project, main, "PID_Rotor_Sync_20B_DB", "PID_Rotor_Sync");
        CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（PID IDB）。");
        if (SetPidRotorDefaults(main, dir, "PID_Rotor_Sync_12B_DB") < 0 ||
            SetPidRotorDefaults(main, dir, "PID_Rotor_Sync_20B_DB") < 0)
        {
            return 1;
        }

        project.Save();
        Console.WriteLine("已存（PID IDB）。");

        if (!ImportGeneratedBlock(main, dir, "Rotor_Sync_Ctrl"))
        {
            return 1;
        }

        EnsureModbusInstanceDb(project, main, "Rotor_Sync_Ctrl_DB", "Rotor_Sync_Ctrl");

        project.Save();
        Console.WriteLine("已存（Rotor_Sync_Ctrl／PID IDB）。");

        int cyclicChanged = PatchNamedBlock(
            main,
            dir,
            "Cyclic interrupt",
            PatchCyclicRotorSync,
            VerifyCyclicRotorSync);
        Console.WriteLine("  Cyclic：" + cyclicChanged);
        if (cyclicChanged < 0)
        {
            PlcBlock cyclic = FindBlock(main, "Cyclic interrupt");
            if (cyclic == null || !SetCyclicInterval(cyclic) || CompilePlc(main) != 0)
            {
                return 1;
            }

            project.Save();
            Console.WriteLine("已存（CyclicTime 設回 10 ms）。");
            cyclicChanged = 1;
        }

        project.Save();
        Console.WriteLine("已存（Cyclic PID 速度）。");

        int speedChanged = PatchNamedBlock(
            main,
            dir,
            "Speed_Loop",
            PatchSpeedLoopPretwistFactor,
            VerifySpeedLoopPretwistFactor);
        Console.WriteLine("  Speed_Loop pretwist：" + speedChanged);
        if (speedChanged < 0)
        {
            return 1;
        }

        PlcBlock cyclicOb = FindBlock(main, "Cyclic interrupt");
        if (cyclicOb != null)
        {
            SetCyclicInterval(cyclicOb);
        }

        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。");
        return errors == 0 ? 0 : 1;
    }

    private static int SetRotorEncMax(PlcSoftware plc, string dir, string startValue)
    {
        string[] stations = { "6B", "12B", "20B", "24B" };
        int changed = 0;
        foreach (string station in stations)
        {
            string name = "Rotor_Angle_" + station + "_DB";
            PlcBlock block = FindBlock(plc, name);
            if (block == null)
            {
                Console.WriteLine("找不到 " + name + "。");
                return -1;
            }

            string path = Path.Combine(dir, name + ".xml");
            if (!ExportBlockXmlQuiet(block, path))
            {
                return -1;
            }

            XDocument doc = XDocument.Load(path);
            XElement encMax = doc.Descendants().FirstOrDefault(e =>
                e.Name.LocalName == "Member" &&
                (string)e.Attribute("Name") == "enc_max");
            if (encMax == null)
            {
                Console.WriteLine(name + " 沒有 enc_max。");
                return -1;
            }

            XElement start = encMax.Elements().FirstOrDefault(e => e.Name.LocalName == "StartValue");
            if (start != null && start.Value == startValue)
            {
                Console.WriteLine("  " + name + ".enc_max 已是 " + startValue);
                continue;
            }

            if (start == null)
            {
                encMax.Add(new XElement(encMax.Name.Namespace + "StartValue", startValue));
            }
            else
            {
                start.Value = startValue;
            }

            doc.Save(path);
            TryImportBlockFile(plc, block, path);
            Console.WriteLine("  " + name + ".enc_max = " + startValue);
            changed++;
        }

        return changed;
    }

    private static int SetPidRotorDefaults(PlcSoftware plc, string dir, string dbName)
    {
        PlcBlock block = FindBlock(plc, dbName);
        if (block == null)
        {
            Console.WriteLine("找不到 " + dbName + "。");
            return -1;
        }

        string path = Path.Combine(dir, dbName + ".xml");
        if (!ExportBlockXmlQuiet(block, path))
        {
            return -1;
        }

        XDocument doc = XDocument.Load(path);
        bool changed = false;
        changed |= SetMemberStartValue(doc.Root, "Kc", "0.15");
        changed |= SetMemberStartValue(doc.Root, "Ti", "2500.0");
        changed |= SetMemberStartValue(doc.Root, "Td", "0.0");
        changed |= SetMemberStartValue(doc.Root, "Kf", "1.0");
        if (!changed)
        {
            Console.WriteLine("  " + dbName + " PID 初值已是 22021＋Kf=1");
            return 0;
        }

        doc.Save(path);
        TryImportBlockFile(plc, block, path);
        Console.WriteLine("  " + dbName + " Kc=0.15 Ti=2500 Kf=1（關掉時 Out=FF，開環跟原來一樣）");
        return 1;
    }

    private static bool SetMemberStartValue(XElement root, string memberName, string startValue)
    {
        XElement member = root.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Member" &&
            (string)e.Attribute("Name") == memberName &&
            !e.Elements().Any(c => c.Name.LocalName == "Member"));
        if (member == null)
        {
            return false;
        }

        XElement start = member.Elements().FirstOrDefault(e => e.Name.LocalName == "StartValue");
        if (start != null && start.Value == startValue)
        {
            return false;
        }

        if (start == null)
        {
            member.Add(new XElement(member.Name.Namespace + "StartValue", startValue));
        }
        else
        {
            start.Value = startValue;
        }

        return true;
    }

    private static int PatchCyclicRotorSync(XElement root)
    {
        int added = 0;
        if (!root.Descendants().Any(e =>
            e.Name.LocalName == "CallInfo" &&
            (string)e.Attribute("Name") == "Rotor_Sync_Ctrl"))
        {
            string specPath = Path.Combine(
                Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
                "Practice", "io-merge", "rotor-sync", "Call-Rotor_Sync_Ctrl.lad.xml");
            if (!File.Exists(specPath))
            {
                Console.WriteLine("找不到 " + specPath);
                return -1;
            }

            XDocument generated = XDocument.Parse(LadWriter.Generate(specPath));
            XElement unit = generated.Root.Descendants().FirstOrDefault(IsCompileUnit);
            if (unit == null)
            {
                Console.WriteLine("Call-Rotor_Sync_Ctrl 沒有 CompileUnit。");
                return -1;
            }

            XElement after = root.Descendants().FirstOrDefault(e =>
                IsCompileUnit(e) &&
                e.Descendants().Any(c =>
                    c.Name.LocalName == "CallInfo" &&
                    (string)c.Attribute("Name") == "Sync_Ramp"));
            if (after == null)
            {
                Console.WriteLine("找不到 Sync_Ramp 網路。");
                return -1;
            }

            int nextId = MaxHexId(root) + 16;
            ShiftIds(unit, nextId - FirstHexId(unit));
            after.AddAfterSelf(unit);
            added++;
        }

        int pid12 = PatchOneRotorPidOutput(root, "CT_PO_12B", "PID_Rotor_Sync_12B_DB");
        int pid20 = PatchOneRotorPidOutput(root, "CT_PO_20B", "PID_Rotor_Sync_20B_DB");
        if (pid12 < 0 || pid20 < 0)
        {
            return -1;
        }

        return added + pid12 + pid20;
    }

    private static bool VerifyCyclicRotorSync(XElement root)
    {
        bool call = root.Descendants().Any(e =>
            e.Name.LocalName == "CallInfo" &&
            (string)e.Attribute("Name") == "Rotor_Sync_Ctrl");
        bool pid12 = root.Descendants().Any(e =>
            e.Name.LocalName == "Component" &&
            (string)e.Attribute("Name") == "PID_Rotor_Sync_12B_DB");
        bool pid20 = root.Descendants().Any(e =>
            e.Name.LocalName == "Component" &&
            (string)e.Attribute("Name") == "PID_Rotor_Sync_20B_DB");
        bool open6 = HasOpenLoopRotor(root, "CT_PO_6B", "6B");
        bool open24 = HasOpenLoopRotor(root, "CT_PO_24B", "24B");
        Console.WriteLine("  驗證 Cyclic：Call=" + call + " PID12=" + pid12 +
            " PID20=" + pid20 + " 6B開環=" + open6 + " 24B開環=" + open24);
        return call && pid12 && pid20 && open6 && open24;
    }

    private static bool HasOpenLoopRotor(XElement root, string ctPo, string ratioName)
    {
        foreach (XElement flg in root.Descendants().Where(e => e.Name.LocalName == "FlgNet"))
        {
            if (!flg.Descendants().Any(e =>
                e.Name.LocalName == "Component" &&
                (string)e.Attribute("Name") == ctPo))
            {
                continue;
            }

            bool ratio = flg.Descendants().Any(e =>
                e.Name.LocalName == "Component" &&
                (string)e.Attribute("Name") == ratioName &&
                e.Parent != null &&
                e.Parent.Elements().Any(c =>
                    c.Name.LocalName == "Component" &&
                    (string)c.Attribute("Name") == "ratio"));
            bool ramped = flg.Descendants().Any(e =>
                e.Name.LocalName == "Component" &&
                (string)e.Attribute("Name") == "ramped");
            return ratio && ramped;
        }

        return false;
    }

    private static int PatchOneRotorPidOutput(XElement root, string ctPoName, string pidDb)
    {
        XElement flg = root.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "FlgNet" &&
            e.Descendants().Any(c =>
                c.Name.LocalName == "Component" &&
                (string)c.Attribute("Name") == ctPoName) &&
            e.Descendants().Any(c =>
                c.Name.LocalName == "CallInfo" &&
                (string)c.Attribute("Name") == "CT_Linear_Output"));
        if (flg == null)
        {
            Console.WriteLine("找不到 " + ctPoName + " 的 CT_Linear。");
            return -1;
        }

        if (flg.Descendants().Any(e =>
            e.Name.LocalName == "Component" &&
            (string)e.Attribute("Name") == pidDb))
        {
            return 0;
        }

        XElement ctAccess = FindAccessWithComponents(flg, ctPoName, "Preset Reference 1");
        if (ctAccess == null)
        {
            Console.WriteLine("找不到 " + ctPoName + " Preset Reference 1。");
            return -1;
        }

        XElement call = FindCallWiredToAccess(flg, ctAccess, "cmd_ref");
        if (call == null)
        {
            Console.WriteLine("找不到接到 " + ctPoName + " 的 CT_Linear。");
            return -1;
        }

        XElement rampAccess = FindAccessWiredToCallPort(flg, call, "sync_ramp");
        XElement ratioAccess = FindAccessWiredToCallPort(flg, call, "ratio");
        if (rampAccess == null || ratioAccess == null)
        {
            Console.WriteLine(ctPoName + " 的 sync_ramp／ratio Access 找不到。");
            return -1;
        }

        RewriteAccessToGlobal(rampAccess, pidDb, "Out");
        RewriteAccessToLiteral(ratioAccess, "Real", "100.0");
        Console.WriteLine("  " + ctPoName + " ← " + pidDb + ".Out × 100");
        return 1;
    }

    private static XElement FindAccessWithComponents(XElement flg, params string[] names)
    {
        foreach (XElement access in flg.Descendants().Where(e => e.Name.LocalName == "Access"))
        {
            List<string> path = access.Descendants()
                .Where(e => e.Name.LocalName == "Component")
                .Select(e => (string)e.Attribute("Name"))
                .ToList();
            if (path.Count == names.Length && path.SequenceEqual(names))
            {
                return access;
            }
        }

        return null;
    }

    private static XElement FindCallWiredToAccess(XElement flg, XElement access, string port)
    {
        string accUid = (string)access.Attribute("UId");
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        XElement parts = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
        if (wires == null || parts == null)
        {
            return null;
        }

        foreach (XElement wire in wires.Elements().Where(e => e.Name.LocalName == "Wire"))
        {
            if (!wire.Elements().Any(e =>
                e.Name.LocalName == "IdentCon" &&
                (string)e.Attribute("UId") == accUid))
            {
                continue;
            }

            XElement nameCon = wire.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "NameCon" &&
                (string)e.Attribute("Name") == port);
            if (nameCon == null)
            {
                continue;
            }

            string callUid = (string)nameCon.Attribute("UId");
            return parts.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "Call" &&
                (string)e.Attribute("UId") == callUid);
        }

        return null;
    }

    private static XElement FindAccessWiredToCallPort(XElement flg, XElement call, string port)
    {
        string callUid = (string)call.Attribute("UId");
        XElement wires = flg.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
        if (wires == null)
        {
            return null;
        }

        foreach (XElement wire in wires.Elements().Where(e => e.Name.LocalName == "Wire"))
        {
            if (!wire.Elements().Any(e =>
                e.Name.LocalName == "NameCon" &&
                (string)e.Attribute("UId") == callUid &&
                (string)e.Attribute("Name") == port))
            {
                continue;
            }

            XElement ident = wire.Elements().FirstOrDefault(e => e.Name.LocalName == "IdentCon");
            if (ident == null)
            {
                continue;
            }

            string accUid = (string)ident.Attribute("UId");
            return flg.Descendants().FirstOrDefault(e =>
                e.Name.LocalName == "Access" &&
                (string)e.Attribute("UId") == accUid);
        }

        return null;
    }

    private static void RewriteAccessToGlobal(XElement access, params string[] path)
    {
        XNamespace ns = access.Name.Namespace;
        access.SetAttributeValue("Scope", "GlobalVariable");
        access.RemoveNodes();
        XElement symbol = new XElement(ns + "Symbol");
        foreach (string part in path)
        {
            symbol.Add(new XElement(ns + "Component", new XAttribute("Name", part)));
        }

        access.Add(symbol);
    }

    private static void RewriteAccessToLiteral(XElement access, string type, string value)
    {
        XNamespace ns = access.Name.Namespace;
        access.SetAttributeValue("Scope", "LiteralConstant");
        access.RemoveNodes();
        access.Add(new XElement(ns + "Constant",
            new XElement(ns + "ConstantType", type),
            new XElement(ns + "ConstantValue", value)));
    }

    private static int PatchSpeedLoopPretwistFactor(XElement root)
    {
        if (VerifySpeedLoopPretwistFactor(root))
        {
            return 0;
        }

        if (AddSpeedLoopPretwistMembers(root) < 0)
        {
            return -1;
        }

        if (root.Descendants().Any(e =>
            e.Name.LocalName == "Component" &&
            (string)e.Attribute("Name") == "pretwist_cmd_ovrflow"))
        {
            return 1;
        }

        string specPath = Path.Combine(
            Path.GetDirectoryName(typeof(StationSync).Assembly.Location),
            "Practice", "io-merge", "rotor-sync", "Speed_Loop-PretwistFactor.lad.xml");
        if (!File.Exists(specPath))
        {
            Console.WriteLine("找不到 " + specPath);
            return -1;
        }

        XDocument generated = XDocument.Parse(LadWriter.Generate(specPath));
        XElement unit = generated.Root.Descendants().FirstOrDefault(IsCompileUnit);
        if (unit == null)
        {
            Console.WriteLine("pretwist factor 規格沒有 CompileUnit。");
            return -1;
        }

        XElement last = root.Descendants().LastOrDefault(IsCompileUnit);
        if (last == null)
        {
            Console.WriteLine("Speed_Loop 沒有網路。");
            return -1;
        }

        int nextId = MaxHexId(root) + 16;
        ShiftIds(unit, nextId - FirstHexId(unit));
        last.AddAfterSelf(unit);
        return 1;
    }

    private static bool VerifySpeedLoopPretwistFactor(XElement root)
    {
        bool factor = HasInterfaceMember(root, "pretwist_factor");
        bool overflow = HasInterfaceMember(root, "pretwist_cmd_ovrflow");
        bool network = root.Descendants().Any(e =>
            e.Name.LocalName == "Component" &&
            (string)e.Attribute("Name") == "pretwist_cmd_ovrflow");
        return factor && overflow && network;
    }

    private static bool HasInterfaceMember(XElement root, string name)
    {
        XElement section = FindFbStaticSection(root);
        return section != null &&
            section.Elements().Any(e =>
                e.Name.LocalName == "Member" &&
                (string)e.Attribute("Name") == name);
    }

    private static XElement FindFbStaticSection(XElement root)
    {
        return root.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Section" &&
            (string)e.Attribute("Name") == "Static" &&
            e.Parent != null &&
            e.Parent.Name.LocalName == "Sections" &&
            e.Parent.Parent != null &&
            e.Parent.Parent.Name.LocalName == "Interface");
    }

    private static int AddSpeedLoopPretwistMembers(XElement root)
    {
        XElement section = FindFbStaticSection(root);
        if (section == null)
        {
            Console.WriteLine("Speed_Loop 找不到 Static。");
            return -1;
        }

        XNamespace ns = section.Name.Namespace;
        int added = 0;
        added += EnsureBuiltMember(section, ns, "pre_twist_threshold", "Real", "Retain", "0.8");
        added += EnsureBuiltMember(section, ns, "pretwist_factor", "Real", "Retain", "1.0");
        added += EnsureBuiltMember(section, ns, "pretwist_cmd_ovrflow", "Bool", "NonRetain", null);
        added += EnsureBuiltMember(section, ns, "pretwist_cmd", "Real", "NonRetain", null);
        return added;
    }

    private static int EnsureBuiltMember(
        XElement section,
        XNamespace ns,
        string name,
        string datatype,
        string remanence,
        string startValue)
    {
        if (section.Elements().Any(e =>
            e.Name.LocalName == "Member" &&
            (string)e.Attribute("Name") == name))
        {
            return 0;
        }

        XElement member = new XElement(ns + "Member",
            new XAttribute("Name", name),
            new XAttribute("Datatype", datatype),
            new XAttribute("Remanence", remanence),
            new XAttribute("Accessibility", "Public"));
        if (startValue != null)
        {
            member.Add(new XElement(ns + "StartValue", startValue));
        }

        section.Add(member);
        Console.WriteLine("  Speed_Loop 加 " + name);
        return 1;
    }

    private static string Flatten(Exception ex)
    {
        return ex.GetBaseException().Message;
    }
}

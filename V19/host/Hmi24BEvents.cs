using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
using Siemens.Engineering.Hmi.Communication;
using Siemens.Engineering.Hmi.TextGraphicList;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;

// 把 24B AErr 搬進 Latch／HMI Current Events 看得到的 M 區，並在 OP1/OP2 補 Discrete Alarm。
internal static class Hmi24BEvents
{
    private const int LatchStartByte = 200;
    private const int HmiLastByte = 229;
    private const int LatchLastByte = 239;
    private const int FirstPackBit = 218 * 8 + 7;
    private const string MainPlcName = "25017_Main_PLC";
    private const string HmiTagPrefix = "25017_Main_PLC_AErr_LW";

    public static int ShowOnOpHmi(TiaPortal portal, Project project)
    {
        Console.WriteLine("24B 事件要在 OP1／OP2 Current Events 顯示。");
        PlcSoftware main = FindPlc(project, MainPlcName);
        if (main == null)
        {
            Console.WriteLine("找不到 25017_Main_PLC。");
            return 1;
        }

        string staging = Path.Combine(
            Path.GetDirectoryName(typeof(Hmi24BEvents).Assembly.Location),
            "Practice", "io-merge", "station-sync", "hmi-24b");
        Directory.CreateDirectory(staging);

        List<HmiTarget> opHmis = FindOpHmis(project);
        Console.WriteLine("OP HMI：" + opHmis.Count + " 台。");
        foreach (HmiTarget hmi in opHmis)
        {
            ProbeHmi(hmi, staging);
        }

        List<PlcTag> targets = Collect24BAerr(main);
        Console.WriteLine("24B AErr Tag：" + targets.Count + " 個。");
        if (targets.Count == 0)
        {
            return 1;
        }

        HashSet<int> used = CollectUsedBits(main, targets);
        List<MovedAerr> moved = Move24BAerr(targets, used);
        WriteMoveLog(staging, moved);
        project.Save();
        Console.WriteLine("已存（24B AErr 改址 " + moved.Count(m => m.Changed) + "）。");

        int extraLw = HighestLwIndex(moved);
        if (extraLw > 14)
        {
            foreach (HmiTarget hmi in opHmis)
            {
                EnsureLwTags(hmi, extraLw, staging);
            }

            project.Save();
            Console.WriteLine("已存（HMI AErr_LW 補到 {" + extraLw + "}）。");
        }

        int alarms = 0;
        foreach (HmiTarget hmi in opHmis)
        {
            alarms += CloneOrCreateAlarms(hmi, moved, staging);
        }

        project.Save();
        Console.WriteLine("已存（HMI 警報）。");

        int errors = CompilePlc(main);
        project.Save();
        Console.WriteLine("已存（編譯後）。警報處理 " + alarms + " 條。");
        return errors == 0 ? 0 : 1;
    }

    public static int EnsureOpLwUpTo(TiaPortal portal, Project project, int extraLw)
    {
        Console.WriteLine("OP1／OP2 補 AErr_LW 到 {" + extraLw + "}。");
        string staging = Path.Combine(
            Path.GetDirectoryName(typeof(Hmi24BEvents).Assembly.Location),
            "Practice", "io-merge", "station-sync", "hmi-24b");
        Directory.CreateDirectory(staging);
        List<HmiTarget> opHmis = FindOpHmis(project);
        foreach (HmiTarget hmi in opHmis)
        {
            EnsureLwTags(hmi, extraLw, staging);
        }

        project.Save();
        Console.WriteLine("已存（HMI AErr_LW）。");
        return 0;
    }

    public static int ProbeAlarms(TiaPortal portal, Project project)
    {
        Console.WriteLine("探 OP HMI 警報／畫面物件／CAx。");
        string staging = Path.Combine(
            Path.GetDirectoryName(typeof(Hmi24BEvents).Assembly.Location),
            "Practice", "io-merge", "station-sync", "hmi-24b");
        Directory.CreateDirectory(staging);

        List<HmiTarget> opHmis = FindOpHmis(project);
        foreach (HmiTarget hmi in opHmis)
        {
            string dir = Path.Combine(staging, Sanitize(hmi.Name), "probe2");
            Directory.CreateDirectory(dir);
            Console.WriteLine("==== " + hmi.Name + " ====");
            Console.WriteLine("Connections：" + hmi.Connections.Count);
            foreach (Connection connection in hmi.Connections)
            {
                Console.WriteLine("Connection " + connection.Name);
                DumpObject("Connection", connection, 1);
                ExportIfPossible(
                    connection,
                    Path.Combine(dir, "conn-" + Sanitize(connection.Name) + ".xml"),
                    "Connection " + connection.Name);
                FindAlarmCollection(connection);
            }

            Console.WriteLine("Popups：" + hmi.ScreenPopupFolder.ScreenPopups.Count);
            foreach (ScreenPopup popup in hmi.ScreenPopupFolder.ScreenPopups)
            {
                Console.WriteLine("  popup " + popup.Name);
                ExportIfPossible(popup, Path.Combine(dir, "popup-" + Sanitize(popup.Name) + ".xml"), "popup " + popup.Name);
            }

            Console.WriteLine("Slideins：" + hmi.ScreenSlideinFolder.ScreenSlideins.Count);
            foreach (ScreenSlidein slide in hmi.ScreenSlideinFolder.ScreenSlideins)
            {
                Console.WriteLine("  slidein type=" + slide.SlideinType);
                ExportIfPossible(slide, Path.Combine(dir, "slide-" + slide.SlideinType + ".xml"), "slidein " + slide.SlideinType);
            }

            Screen current = hmi.ScreenFolder.Screens.Find("P20_Log_Current");
            if (current != null)
            {
                DumpObject("P20", current, 0);
                DumpScreenItems(current, 0);
            }

            break;
        }

        foreach (Device device in EnumerateDevices(project))
        {
            if (device.Name.IndexOf("Main_HMI", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            string aml = Path.Combine(staging, Sanitize(device.Name) + ".aml");
            Console.WriteLine("CAx 匯出 " + device.Name + " → " + aml);
            try
            {
                CaxProvider cax = project.GetService<CaxProvider>();
                if (cax == null)
                {
                    Console.WriteLine("CaxProvider = <null>");
                    continue;
                }

                TransferResult result = cax.Export(device, new FileInfo(aml));
                Console.WriteLine("CAx = " + (result == null ? "<null>" :
                    ("err=" + result.ErrorCount + " warn=" + result.WarningCount + " " + result.State)));
            }
            catch (Exception ex)
            {
                Console.WriteLine("CAx 失敗：" + Flatten(ex));
            }
        }

        return 0;
    }

    private static void DumpScreenItems(IEngineeringObject parent, int depth)
    {
        string pad = new string(' ', depth * 2);
        Console.WriteLine(pad + parent.GetType().Name + " " + TryGetAttr(parent, "Name"));
        foreach (EngineeringCompositionInfo info in SafeCompositionInfos(parent))
        {
            Console.WriteLine(pad + "  composition " + info.Name + " / " + V19Api.TypeName(info));
            object collection;
            try
            {
                collection = parent.GetComposition(info.Name);
            }
            catch (Exception ex)
            {
                Console.WriteLine(pad + "    GetComposition 失敗：" + Flatten(ex));
                continue;
            }

            IEnumerable enumerable = collection as IEnumerable;
            if (enumerable == null)
            {
                continue;
            }

            foreach (object item in enumerable)
            {
                IEngineeringObject child = item as IEngineeringObject;
                if (child == null)
                {
                    Console.WriteLine(pad + "  " + info.Name + " " + item);
                    continue;
                }

                DumpScreenItems(child, depth + 1);
            }
        }
    }


    private static void ProbeHmi(HmiTarget hmi, string staging)
    {
        string dir = Path.Combine(staging, Sanitize(hmi.Name));
        Directory.CreateDirectory(dir);
        Console.WriteLine("探 HMI " + hmi.Name + " " + hmi.GetType().FullName);
        DumpObject("HmiTarget", hmi, 0);
        ExportIfPossible(hmi.ScreenGlobalElements, Path.Combine(dir, "ScreenGlobalElements.xml"), "ScreenGlobalElements");
        foreach (ScreenTemplate template in hmi.ScreenTemplateFolder.ScreenTemplates)
        {
            ExportIfPossible(template, Path.Combine(dir, "template-" + Sanitize(template.Name) + ".xml"), "Template " + template.Name);
        }

        Console.WriteLine("  TextLists：" + hmi.TextLists.Count);
        foreach (TextList list in hmi.TextLists)
        {
            string path = Path.Combine(dir, "textlist-" + Sanitize(list.Name) + ".xml");
            ExportIfPossible(list, path, "TextList " + list.Name);
        }

        object alarms = FindAlarmCollection(hmi);
        if (alarms == null)
        {
            Console.WriteLine("  沒找到 DiscreteAlarms composition。");
            return;
        }

        DumpCollection("DiscreteAlarms", alarms, dir);
    }

    private static object FindAlarmCollection(IEngineeringObject target)
    {
        string[] names =
        {
            "DiscreteAlarms",
            "DiscreteAlarm",
            "Alarms",
            "AlarmFolder",
            "AnalogAlarms",
            "AlarmClasses",
            "ControllerAlarms"
        };

        foreach (EngineeringCompositionInfo info in SafeCompositionInfos(target))
        {
            Console.WriteLine("  composition " + info.Name + " / " + V19Api.TypeName(info));
            Type compositionType = V19Api.CompositionType(info);
            if (info.Name.IndexOf("Alarm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (compositionType != null && compositionType.FullName.IndexOf("Alarm", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                try
                {
                    return target.GetComposition(info.Name);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("    GetComposition 失敗：" + Flatten(ex));
                }
            }
        }

        foreach (string name in names)
        {
            try
            {
                object found = target.GetComposition(name);
                if (found != null)
                {
                    Console.WriteLine("  GetComposition(" + name + ") = " + found.GetType().FullName);
                    return found;
                }
            }
            catch
            {
            }
        }

        foreach (PropertyInfo property in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.Name.IndexOf("Alarm", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            try
            {
                object value = property.GetValue(target, null);
                Console.WriteLine("  property " + property.Name + " = " +
                    (value == null ? "<null>" : value.GetType().FullName));
                if (value != null)
                {
                    return value;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  property " + property.Name + "：" + Flatten(ex));
            }
        }

        IEngineeringServiceProvider provider = target as IEngineeringServiceProvider;
        if (provider != null)
        {
            try
            {
                foreach (EngineeringServiceInfo info in provider.GetServiceInfos())
                {
                    Console.WriteLine("  service " +
                        (info.Type == null ? "?" : info.Type.FullName));
                    if (info.Type != null &&
                        info.Type.FullName.IndexOf("Alarm", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try
                        {
                            MethodInfo getService = provider.GetType().GetMethod("GetService", new[] { typeof(Type) });
                            if (getService != null && info.Type != null)
                            {
                                object service = getService.Invoke(provider, new object[] { info.Type });
                                Console.WriteLine("    GetService = " +
                                    (service == null ? "<null>" : service.GetType().FullName));
                                if (service != null)
                                {
                                    return service;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("    GetService 失敗：" + Flatten(ex));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  GetServiceInfos：" + Flatten(ex));
            }
        }

        return null;
    }

    private static void DumpObject(string title, IEngineeringObject target, int depth)
    {
        string pad = new string(' ', depth * 2);
        Console.WriteLine(pad + title + " " + target.GetType().FullName);
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

                string text = value == null ? "<null>" : value.ToString();
                if (text.Length > 160)
                {
                    text = text.Substring(0, 160) + "…";
                }

                Console.WriteLine(pad + "  attr " + info.Name + " = " + text);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(pad + "  attrs：" + Flatten(ex));
        }

        try
        {
            foreach (EngineeringInvocationInfo info in target.GetInvocationInfos())
            {
                Console.WriteLine(pad + "  invoke " + info.Name);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(pad + "  invokes：" + Flatten(ex));
        }
    }

    private static void DumpCollection(string title, object collection, string dir)
    {
        Console.WriteLine("  " + title + " 型別 " + collection.GetType().FullName);
        IEngineeringObject asObj = collection as IEngineeringObject;
        if (asObj != null)
        {
            DumpObject(title, asObj, 1);
            try
            {
                foreach (EngineeringCreationInfo create in ((IEngineeringComposition)collection).GetCreationInfos())
                {
                    Console.WriteLine("    create " + create.Type.FullName);
                    foreach (EngineeringCreationParameterInfo parameter in create.ParameterInfos)
                    {
                        Console.WriteLine("      param " + parameter.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("    GetCreationInfos：" + Flatten(ex));
            }
        }

        int count = 0;
        IEnumerable enumerable = collection as IEnumerable;
        if (enumerable == null)
        {
            return;
        }

        foreach (object item in enumerable)
        {
            count++;
            IEngineeringObject alarm = item as IEngineeringObject;
            if (alarm == null)
            {
                Console.WriteLine("    [" + count + "] " + item);
                continue;
            }

            if (count <= 8)
            {
                DumpObject("alarm[" + count + "]", alarm, 2);
                TryExportAlarm(alarm, Path.Combine(dir, "alarm-sample-" + count + ".xml"));
            }
            else if (count <= 40)
            {
                object name = TryGetAttr(alarm, "Name");
                object text = TryGetAttr(alarm, "AlarmText") ?? TryGetAttr(alarm, "Text");
                Console.WriteLine("    [" + count + "] " + name + " / " + text);
            }
        }

        Console.WriteLine("  " + title + " Count=" + count);
    }

    private static int CloneOrCreateAlarms(HmiTarget hmi, List<MovedAerr> moved, string staging)
    {
        object collection = FindAlarmCollection(hmi);
        if (collection == null)
        {
            Console.WriteLine(hmi.Name + "：沒有 DiscreteAlarms，先靠 Latch 位址；警報文字下一刀再補。");
            return 0;
        }

        Dictionary<string, IEngineeringObject> existing = IndexAlarms(collection);
        Console.WriteLine(hmi.Name + " 現有警報 " + existing.Count + " 條。");
        int done = 0;
        string dir = Path.Combine(staging, Sanitize(hmi.Name), "new-alarms");
        Directory.CreateDirectory(dir);

        foreach (MovedAerr item in moved)
        {
            if (HasAlarmFor(existing, item.Tag.Name))
            {
                Console.WriteLine("  已有警報 " + item.Tag.Name);
                done++;
                continue;
            }

            IEngineeringObject template = FindTemplateAlarm(existing, item.Tag.Name);
            if (template != null && TryExportAlarm(template, Path.Combine(dir, "template.xml")))
            {
                string path = Path.Combine(dir, Sanitize(item.Tag.Name) + ".xml");
                if (WriteClonedAlarmXml(Path.Combine(dir, "template.xml"), path, item) &&
                    TryImportAlarm(collection, path))
                {
                    Console.WriteLine("  警報 " + item.Tag.Name + " " + item.NewAddress +
                        " LW{" + item.LwIndex + "}." + item.WordBit);
                    done++;
                    continue;
                }
            }

            if (TryCreateAlarm(collection, item, template))
            {
                Console.WriteLine("  建警報 " + item.Tag.Name);
                done++;
                continue;
            }

            Console.WriteLine("  警報失敗 " + item.Tag.Name);
        }

        return done;
    }

    private static Dictionary<string, IEngineeringObject> IndexAlarms(object collection)
    {
        Dictionary<string, IEngineeringObject> map = new Dictionary<string, IEngineeringObject>(StringComparer.OrdinalIgnoreCase);
        IEnumerable enumerable = collection as IEnumerable;
        if (enumerable == null)
        {
            return map;
        }

        foreach (object item in enumerable)
        {
            IEngineeringObject alarm = item as IEngineeringObject;
            if (alarm == null)
            {
                continue;
            }

            string name = Convert.ToString(TryGetAttr(alarm, "Name"));
            if (string.IsNullOrEmpty(name))
            {
                name = Convert.ToString(TryGetAttr(alarm, "AlarmText") ?? TryGetAttr(alarm, "Text"));
            }

            if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name))
            {
                map.Add(name, alarm);
            }
        }

        return map;
    }

    private static bool HasAlarmFor(Dictionary<string, IEngineeringObject> existing, string tagName)
    {
        if (existing.ContainsKey(tagName))
        {
            return true;
        }

        string needle = tagName.Replace("AErr_", string.Empty);
        foreach (string key in existing.Keys)
        {
            if (key.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                key.IndexOf(tagName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static IEngineeringObject FindTemplateAlarm(Dictionary<string, IEngineeringObject> existing, string tagName)
    {
        string twenty = tagName.Replace("24B", "20B");
        IEngineeringObject hit;
        if (existing.TryGetValue(twenty, out hit))
        {
            return hit;
        }

        foreach (KeyValuePair<string, IEngineeringObject> pair in existing)
        {
            if (pair.Key.IndexOf(twenty, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return pair.Value;
            }
        }

        string family18 = FamilyKey(tagName).Replace("#19", "#18").Replace("#20", "#18")
            .Replace("#21", "#18").Replace("#22", "#18").Replace("#23", "#18").Replace("#24", "#18");
        string twenty18 = family18.Replace("24B", "20B");
        if (existing.TryGetValue(twenty18, out hit))
        {
            return hit;
        }

        foreach (KeyValuePair<string, IEngineeringObject> pair in existing)
        {
            if (pair.Key.IndexOf("20B", StringComparison.OrdinalIgnoreCase) >= 0 &&
                pair.Key.IndexOf("Wire", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return pair.Value;
            }
        }

        return existing.Values.FirstOrDefault();
    }

    private static string FamilyKey(string tagName)
    {
        return tagName;
    }

    private static bool WriteClonedAlarmXml(string templatePath, string path, MovedAerr item)
    {
        try
        {
            XDocument doc = XDocument.Load(templatePath);
            foreach (XElement name in doc.Descendants().Where(e => e.Name.LocalName == "Name"))
            {
                if (name.Value != null && name.Value.IndexOf("20B", StringComparison.Ordinal) >= 0)
                {
                    name.Value = name.Value.Replace("20B", "24B").Replace("18B", "24B");
                }
            }

            foreach (XElement text in doc.Descendants().Where(e => e.Name.LocalName == "Text"))
            {
                if (string.IsNullOrEmpty(text.Value))
                {
                    continue;
                }

                text.Value = ReplaceStationText(text.Value, item.Tag.Name);
            }

            foreach (XElement el in doc.Descendants())
            {
                string local = el.Name.LocalName;
                if (local == "TriggerTag" || local == "Tag" || local == "FieldTag")
                {
                    XElement inner = el.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
                    if (inner != null)
                    {
                        inner.Value = HmiTagPrefix + "{" + item.LwIndex + "}";
                    }
                }

                if (local == "BitNumber" || local == "FieldBit" || local == "TriggerBit" || local == "Bit")
                {
                    el.Value = item.WordBit.ToString();
                }
            }

            doc.Save(path);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("    改警報 XML 失敗：" + Flatten(ex));
            return false;
        }
    }

    private static string ReplaceStationText(string text, string tagName)
    {
        string next = text.Replace("20B", "24B").Replace("18B", "24B");
        int hash = tagName.LastIndexOf('#');
        if (hash >= 0)
        {
            string number = tagName.Substring(hash);
            next = System.Text.RegularExpressions.Regex.Replace(next, @"#\d+", number);
        }

        if (next.IndexOf("24B", StringComparison.Ordinal) < 0)
        {
            next = "24B " + next;
        }

        return next;
    }

    private static bool TryImportAlarm(object collection, string path)
    {
        try
        {
            MethodInfo import = collection.GetType().GetMethod(
                "Import",
                new[] { typeof(FileInfo), typeof(ImportOptions) });
            if (import == null)
            {
                return false;
            }

            import.Invoke(collection, new object[] { new FileInfo(path), ImportOptions.None });
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("    Import 警報失敗：" + Flatten(ex));
            return false;
        }
    }

    private static bool TryCreateAlarm(object collection, MovedAerr item, IEngineeringObject template)
    {
        try
        {
            MethodInfo create = collection.GetType().GetMethod("Create", new[] { typeof(string) });
            object created = null;
            if (create != null)
            {
                created = create.Invoke(collection, new object[] { item.Tag.Name });
            }

            if (created == null)
            {
                IEngineeringObject host = collection as IEngineeringObject;
                IEngineeringComposition composition = collection as IEngineeringComposition;
                if (host != null && composition != null)
                {
                    foreach (EngineeringCreationInfo info in composition.GetCreationInfos())
                    {
                        created = host.Create(V19Api.TypeName(info), V19Api.CompositionType(info), null);
                        break;
                    }
                }
            }

            IEngineeringObject alarm = created as IEngineeringObject;
            if (alarm == null)
            {
                return false;
            }

            TrySetAttr(alarm, "Name", item.Tag.Name);
            TrySetAttr(alarm, "BitNumber", item.WordBit);
            TrySetAttr(alarm, "FieldBit", item.WordBit);
            TrySetAttr(alarm, "TriggerBit", item.WordBit);
            TrySetAttr(alarm, "TriggerTag", HmiTagPrefix + "{" + item.LwIndex + "}");
            TrySetAttr(alarm, "AlarmText", AlarmTextFromTag(item.Tag.Name));
            if (template != null)
            {
                object cls = TryGetAttr(template, "AlarmClass") ?? TryGetAttr(template, "Class");
                if (cls != null)
                {
                    TrySetAttr(alarm, "AlarmClass", cls);
                    TrySetAttr(alarm, "Class", cls);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("    Create 警報失敗：" + Flatten(ex));
            return false;
        }
    }

    private static string AlarmTextFromTag(string tagName)
    {
        string text = tagName;
        if (text.StartsWith("AErr_", StringComparison.Ordinal))
        {
            text = text.Substring(5);
        }

        return text.Replace('_', ' ');
    }

    private static List<MovedAerr> Move24BAerr(List<PlcTag> targets, HashSet<int> used)
    {
        List<MovedAerr> moved = new List<MovedAerr>();
        int bit = FirstPackBit;
        int lastHmi = (HmiLastByte * 8) + 7;
        int lastLatch = (LatchLastByte * 8) + 7;

        foreach (PlcTag tag in targets.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            int current = ParseMemoryBit(tag.LogicalAddress);
            MovedAerr row = new MovedAerr { Tag = tag, OldAddress = tag.LogicalAddress };
            if (current >= LatchStartByte * 8 && current <= lastHmi)
            {
                FillWord(row, current);
                row.NewAddress = tag.LogicalAddress;
                row.Changed = false;
                moved.Add(row);
                Console.WriteLine("  已在窗內 " + tag.Name + " " + tag.LogicalAddress);
                continue;
            }

            while ((used.Contains(bit) || bit < FirstPackBit) && bit <= lastLatch)
            {
                bit++;
            }

            if (bit > lastLatch)
            {
                Console.WriteLine("  沒有空 bit：" + tag.Name);
                return moved;
            }

            string address = "%M" + (bit / 8) + "." + (bit % 8);
            try
            {
                tag.LogicalAddress = address;
                FillWord(row, bit);
                row.NewAddress = address;
                row.Changed = true;
                used.Add(bit);
                Console.WriteLine("  " + tag.Name + " " + row.OldAddress + " -> " + address +
                    "  LW{" + row.LwIndex + "}." + row.WordBit);
                moved.Add(row);
                bit++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  改址失敗 " + tag.Name + "：" + Flatten(ex));
            }
        }

        return moved;
    }

    private static void FillWord(MovedAerr row, int bit)
    {
        int byteNo = bit / 8;
        int bitNo = bit % 8;
        row.LwIndex = (byteNo - LatchStartByte) / 2;
        row.WordBit = ((byteNo - LatchStartByte) % 2) * 8 + bitNo;
    }

    private static int HighestLwIndex(List<MovedAerr> moved)
    {
        int max = 14;
        foreach (MovedAerr item in moved)
        {
            if (item.LwIndex > max)
            {
                max = item.LwIndex;
            }
        }

        return max;
    }

    private static void EnsureLwTags(HmiTarget hmi, int extraLw, string staging)
    {
        TagTable table = hmi.TagFolder.TagTables.FirstOrDefault() ?? hmi.TagFolder.DefaultTagTable;
        if (table == null)
        {
            Console.WriteLine(hmi.Name + " 沒有 Tag 表。");
            return;
        }

        Tag sample = table.Tags.Find(HmiTagPrefix + "{14}");
        if (sample == null)
        {
            Console.WriteLine(hmi.Name + " 找不到 " + HmiTagPrefix + "{14}。");
            return;
        }

        string dir = Path.Combine(staging, Sanitize(hmi.Name));
        Directory.CreateDirectory(dir);
        string template = Path.Combine(dir, "lw14.xml");
        sample.Export(new FileInfo(template), ExportOptions.WithDefaults);
        for (int i = 15; i <= extraLw; i++)
        {
            string name = HmiTagPrefix + "{" + i + "}";
            if (table.Tags.Find(name) != null)
            {
                continue;
            }

            XDocument doc = XDocument.Load(template);
            foreach (XElement el in doc.Descendants().Where(e => e.Name.LocalName == "Name" && e.Value != null))
            {
                if (el.Value.IndexOf(HmiTagPrefix + "{14}", StringComparison.Ordinal) >= 0)
                {
                    el.Value = el.Value.Replace(HmiTagPrefix + "{14}", name);
                }
            }

            string path = Path.Combine(dir, "lw" + i + ".xml");
            doc.Save(path);
            try
            {
                table.Tags.Import(new FileInfo(path), ImportOptions.None);
                Console.WriteLine("  HMI Tag " + name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  HMI Tag 失敗 " + name + "：" + Flatten(ex));
            }
        }
    }

    private static List<PlcTag> Collect24BAerr(PlcSoftware plc)
    {
        List<PlcTag> list = new List<PlcTag>();
        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            foreach (PlcTag tag in table.Tags)
            {
                if (tag.Name != null &&
                    tag.Name.StartsWith("AErr_", StringComparison.Ordinal) &&
                    tag.Name.IndexOf("24B", StringComparison.Ordinal) >= 0)
                {
                    list.Add(tag);
                }
            }
        }

        return list;
    }

    private static HashSet<int> CollectUsedBits(PlcSoftware plc, List<PlcTag> moving)
    {
        HashSet<string> skip = new HashSet<string>(moving.Select(t => t.Name), StringComparer.Ordinal);
        HashSet<int> used = new HashSet<int>();
        foreach (PlcTagTable table in EnumerateTagTables(plc.TagTableGroup))
        {
            foreach (PlcTag tag in table.Tags)
            {
                if (skip.Contains(tag.Name))
                {
                    continue;
                }

                int bit = ParseMemoryBit(tag.LogicalAddress);
                if (bit >= 0)
                {
                    used.Add(bit);
                }
            }
        }

        return used;
    }

    private static void WriteMoveLog(string staging, List<MovedAerr> moved)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("tag,old,new,lw,bit,changed");
        foreach (MovedAerr item in moved)
        {
            sb.Append(item.Tag.Name).Append(',')
                .Append(item.OldAddress).Append(',')
                .Append(item.NewAddress).Append(',')
                .Append(item.LwIndex).Append(',')
                .Append(item.WordBit).Append(',')
                .Append(item.Changed).AppendLine();
        }

        File.WriteAllText(Path.Combine(staging, "moved-aerr.csv"), sb.ToString(), new UTF8Encoding(false));
    }

    private static List<HmiTarget> FindOpHmis(Project project)
    {
        List<HmiTarget> list = new List<HmiTarget>();
        foreach (Device device in EnumerateDevices(project))
        {
            if (device.Name.IndexOf("Main_HMI", StringComparison.OrdinalIgnoreCase) < 0 &&
                device.Name.IndexOf("OP2_HMI", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            HmiTarget hmi = FindHmiTarget(device);
            if (hmi != null)
            {
                Console.WriteLine("  " + device.Name + " / " + hmi.Name);
                list.Add(hmi);
            }
        }

        return list;
    }

    private static HmiTarget FindHmiTarget(Device device)
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

        return null;
    }

    private static IEnumerable<EngineeringCompositionInfo> SafeCompositionInfos(IEngineeringObject target)
    {
        try
        {
            return target.GetCompositionInfos();
        }
        catch
        {
            return new EngineeringCompositionInfo[0];
        }
    }

    private static object TryGetAttr(IEngineeringObject target, string name)
    {
        try
        {
            return target.GetAttribute(name);
        }
        catch
        {
            return null;
        }
    }

    private static void TrySetAttr(IEngineeringObject target, string name, object value)
    {
        try
        {
            target.SetAttribute(name, value);
        }
        catch
        {
        }
    }

    private static bool TryExportAlarm(IEngineeringObject alarm, string path)
    {
        return ExportIfPossible(alarm, path, null);
    }

    private static bool ExportIfPossible(object item, string path, string label)
    {
        try
        {
            MethodInfo export = item.GetType().GetMethod(
                "Export",
                new[] { typeof(FileInfo), typeof(ExportOptions) });
            if (export == null)
            {
                if (label != null)
                {
                    Console.WriteLine("  略過 " + label + "（沒有 Export）");
                }

                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            export.Invoke(item, new object[] { new FileInfo(path), ExportOptions.WithDefaults });
            if (label != null)
            {
                Console.WriteLine("  已匯出 " + label);
            }

            return true;
        }
        catch (Exception ex)
        {
            if (label != null)
            {
                Console.WriteLine("  匯出失敗 " + label + "：" + Flatten(ex));
            }

            return false;
        }
    }

    private static int ParseMemoryBit(string address)
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

        int start = i;
        while (i < raw.Length && char.IsDigit(raw[i]))
        {
            i++;
        }

        if (start == i || i >= raw.Length || raw[i] != '.')
        {
            return -1;
        }

        int number;
        int bit;
        if (!int.TryParse(raw.Substring(start, i - start), out number) ||
            !int.TryParse(raw.Substring(i + 1), out bit))
        {
            return -1;
        }

        return number * 8 + bit;
    }

    private static PlcSoftware FindPlc(Project project, string name)
    {
        foreach (PlcSoftware plc in LadBlockTools.FindAllPlcSoftwares(project))
        {
            if (string.Equals(plc.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return plc;
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
        return result.ErrorCount;
    }

    private static void CompileHmi(HmiTarget hmi)
    {
        ICompilable compilable = hmi.GetService<ICompilable>();
        if (compilable == null)
        {
            return;
        }

        try
        {
            CompilerResult result = compilable.Compile();
            Console.WriteLine("編譯 " + hmi.Name + "：" + result.State +
                "／錯誤 " + result.ErrorCount + "／警告 " + result.WarningCount);
        }
        catch (Exception ex)
        {
            Console.WriteLine("編譯 HMI " + hmi.Name + "：" + Flatten(ex));
        }
    }

    public static int WirePitch24B(TiaPortal portal, Project project)
    {
        Console.WriteLine("OP1／OP2 接 24B 節距（opt.wire_pitch.24B）。不 Compile HMI。");
        string staging = Path.Combine(
            Path.GetDirectoryName(typeof(Hmi24BEvents).Assembly.Location),
            "Practice", "io-merge", "rotor-sync", "hmi-pitch-24b");
        Directory.CreateDirectory(staging);

        List<HmiTarget> opHmis = FindOpHmis(project);
        if (opHmis.Count == 0)
        {
            Console.WriteLine("找不到 OP HMI。");
            return 1;
        }

        int tags = 0;
        int fields = 0;
        foreach (HmiTarget hmi in opHmis)
        {
            try
            {
                tags += EnsurePitch24BTag(hmi, staging);
                fields += EnsurePitch24BField(hmi, staging);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  " + hmi.Name + " 畫面失敗：" + Flatten(ex) + "（Tag 仍在）");
            }
        }

        project.Save();
        Console.WriteLine("已存（HMI 24B 節距 Tag " + tags + "、畫面欄 " + fields + "）。沒有 Compile HMI。");
        return 0;
    }

    private static int EnsurePitch24BTag(HmiTarget hmi, string staging)
    {
        TagTable table = hmi.TagFolder.TagTables.FirstOrDefault() ?? hmi.TagFolder.DefaultTagTable;
        if (table == null)
        {
            Console.WriteLine(hmi.Name + " 沒有 Tag 表。");
            return 0;
        }

        string dest = "25017_Main_PLC_opt_wire_pitch_24B";
        Tag existing = table.Tags.Find(dest);
        if (existing != null)
        {
            TrySetHmiPlcTag(existing, "opt.wire_pitch.24B");
            Console.WriteLine("  " + hmi.Name + " 已有 " + dest);
            return 0;
        }

        Tag sample = table.Tags.Find("25017_Main_PLC_opt_wire_pitch_20B")
            ?? table.Tags.Find("25017_Main_PLC_opt_wire_pitch_18B")
            ?? table.Tags.Find("25017_Main_PLC_opt_wire_pitch_12B");
        if (sample == null)
        {
            Console.WriteLine(hmi.Name + " 找不到 12／18／20B pitch Tag 當樣板。");
            return 0;
        }

        string dir = Path.Combine(staging, Sanitize(hmi.Name));
        Directory.CreateDirectory(dir);
        string template = Path.Combine(dir, "pitch-sample.xml");
        if (File.Exists(template))
        {
            File.Delete(template);
        }

        sample.Export(new FileInfo(template), ExportOptions.WithDefaults);
        XDocument doc = XDocument.Load(template);
        foreach (XElement el in doc.Descendants().Where(e => e.Name.LocalName == "Name" && e.Value != null))
        {
            el.Value = el.Value
                .Replace("wire_pitch_20B", "wire_pitch_24B")
                .Replace("wire_pitch_18B", "wire_pitch_24B")
                .Replace("wire_pitch_12B", "wire_pitch_24B");
        }

        foreach (XElement el in doc.Descendants().Where(e =>
            e.Name.LocalName == "PlcTag" || e.Name.LocalName == "LogicalAddress"))
        {
            if (el.Value != null && el.Value.IndexOf("wire_pitch", StringComparison.Ordinal) >= 0)
            {
                el.Value = "opt.wire_pitch.24B";
            }
        }

        string path = Path.Combine(dir, "pitch-24B.xml");
        doc.Save(path);
        try
        {
            table.Tags.Import(new FileInfo(path), ImportOptions.None);
            Tag created = table.Tags.Find(dest);
            TrySetHmiPlcTag(created, "opt.wire_pitch.24B");
            Console.WriteLine("  HMI Tag " + dest + " ← " + sample.Name);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  HMI Tag 失敗 " + dest + "：" + Flatten(ex));
            return 0;
        }
    }

    private static void TrySetHmiPlcTag(Tag tag, string plcTag)
    {
        if (tag == null)
        {
            return;
        }

        try
        {
            tag.SetAttribute("PlcTag", plcTag);
        }
        catch
        {
        }
    }

    private static int EnsurePitch24BField(HmiTarget hmi, string staging)
    {
        string screenName = "P02_Stranding";
        Screen screen = hmi.ScreenFolder.Screens.Find(screenName);
        if (screen == null)
        {
            Console.WriteLine(hmi.Name + " 找不到 " + screenName + "。");
            return 0;
        }

        string dir = Path.Combine(staging, Sanitize(hmi.Name));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Sanitize(screenName) + ".xml");
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
            Console.WriteLine("  畫面匯出失敗 " + screenName + "：" + Flatten(ex));
            return 0;
        }

        XDocument doc = XDocument.Load(path);
        if (doc.Descendants().Any(e =>
            e.Name.LocalName == "Name" &&
            e.Value != null &&
            e.Value.IndexOf("wire_pitch_24B", StringComparison.Ordinal) >= 0))
        {
            Console.WriteLine("  " + hmi.Name + " / " + screenName + " 已有 24B 節距欄。");
            return 0;
        }

        XElement sample = doc.Descendants().FirstOrDefault(e =>
            (e.Name.LocalName == "IOField" || e.Name.LocalName.EndsWith(".IOField")) &&
            e.Descendants().Any(n =>
                n.Name.LocalName == "Name" &&
                n.Value != null &&
                (n.Value.IndexOf("wire_pitch_20B", StringComparison.Ordinal) >= 0 ||
                 n.Value.IndexOf("wire_pitch_18B", StringComparison.Ordinal) >= 0)));
        if (sample == null)
        {
            Console.WriteLine("  " + screenName + " 找不到 20B／18B 節距 IO 欄。");
            return 0;
        }

        XElement clone = new XElement(sample);
        int nextId = 0;
        foreach (XElement el in doc.Root.DescendantsAndSelf())
        {
            XAttribute id = el.Attribute("ID");
            int n;
            if (id != null &&
                int.TryParse(id.Value, System.Globalization.NumberStyles.HexNumber, null, out n) &&
                n > nextId)
            {
                nextId = n;
            }
        }

        int first = 0;
        XAttribute cloneId = clone.Attribute("ID");
        if (cloneId != null)
        {
            int.TryParse(cloneId.Value, System.Globalization.NumberStyles.HexNumber, null, out first);
        }

        int delta = nextId + 16 - first;
        foreach (XElement el in clone.DescendantsAndSelf())
        {
            XAttribute id = el.Attribute("ID");
            int n;
            if (id != null &&
                int.TryParse(id.Value, System.Globalization.NumberStyles.HexNumber, null, out n))
            {
                id.Value = (n + delta).ToString("X");
            }
        }

        foreach (XElement el in clone.Descendants().Where(e => e.Name.LocalName == "Name"))
        {
            if (el.Value == null)
            {
                continue;
            }

            if (el.Value.IndexOf("wire_pitch_20B", StringComparison.Ordinal) >= 0 ||
                el.Value.IndexOf("wire_pitch_18B", StringComparison.Ordinal) >= 0)
            {
                el.Value = "25017_Main_PLC_opt_wire_pitch_24B";
            }
        }

        XElement left = clone.Descendants().FirstOrDefault(e => e.Name.LocalName == "Left");
        XElement top = clone.Descendants().FirstOrDefault(e => e.Name.LocalName == "Top");
        XElement width = clone.Descendants().FirstOrDefault(e => e.Name.LocalName == "Width");
        int leftVal;
        int topVal;
        int widthVal;
        if (left != null && top != null &&
            int.TryParse(left.Value, out leftVal) &&
            int.TryParse(top.Value, out topVal))
        {
            widthVal = 147;
            if (width != null)
            {
                int.TryParse(width.Value, out widthVal);
            }

            if (leftVal + 220 + widthVal <= 1280)
            {
                left.Value = (leftVal + 220).ToString();
            }
            else
            {
                top.Value = (topVal + 56).ToString();
            }
        }

        XElement objectName = clone.Descendants().FirstOrDefault(e => e.Name.LocalName == "ObjectName");
        if (objectName != null)
        {
            objectName.Value = "I/O field_24B_pitch";
        }

        sample.AddAfterSelf(clone);
        doc.Save(path);

        try
        {
            hmi.ScreenFolder.Screens.Import(new FileInfo(path), ImportOptions.Override);
            Console.WriteLine("  畫面 " + screenName + " 加 24B 節距欄。");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  畫面匯入失敗 " + screenName + "：" + Flatten(ex) + "（Tag 仍在）");
            return 0;
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

    private static string Flatten(Exception ex)
    {
        return ex.GetBaseException().Message;
    }

    private sealed class MovedAerr
    {
        public PlcTag Tag;
        public string OldAddress;
        public string NewAddress;
        public int LwIndex;
        public int WordBit;
        public bool Changed;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.TechnologicalObjects;

// Several TO parameters (FollowingError.*, Modulo.*, Sensor[n].Parameter.Resolution,
// PositioningMonitoring.Window, StandstillSignal.*) expose Value as read-only through
// Openness because TIA derives them from the position unit. The only way to force them
// is to export the technology object, edit the <StartValue> nodes and import it back.
internal static class TechXmlPatcher
{
    private static readonly XNamespace Iface =
        "http://www.siemens.com/automation/Openness/SW/Interface/v5";

    public static void Apply(
        Project project,
        List<PlcSoftware> plcSoftwares,
        string configPath,
        string onlyPlc,
        string workFolder)
    {
        if (!File.Exists(configPath))
        {
            Console.WriteLine("找不到設定檔：" + configPath);
            return;
        }

        Directory.CreateDirectory(workFolder);
        Dictionary<string, Dictionary<string, string>> sections = LoadConfig(configPath);
        int touched = 0;

        // Export refuses to run on inconsistent software, so each PLC is compiled once up front.
        HashSet<string> compiled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, Dictionary<string, string>> section in sections)
        {
            string[] parts = section.Key.Split(new[] { '/' }, 2);
            if (parts.Length != 2)
            {
                continue;
            }

            string plcName = parts[0];
            string objectName = parts[1];
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

            TechnologicalInstanceDB target = Find(plc.TechnologicalObjectGroup, objectName);
            if (target == null)
            {
                Console.WriteLine("找不到工藝對象：" + section.Key);
                continue;
            }

            Console.WriteLine(new string('-', 40));
            Console.WriteLine("XML 套用：" + section.Key);

            if (compiled.Add(plc.Name))
            {
                Compile(plc);
            }

            string path = Path.Combine(workFolder, Sanitize(plcName + "_" + objectName) + ".technology.xml");
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                target.Export(new FileInfo(path), ExportOptions.WithDefaults);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  匯出失敗：" + Flatten(ex));
                continue;
            }

            XDocument doc = XDocument.Load(path);
            int changed = 0;
            List<string> missed = new List<string>();

            foreach (KeyValuePair<string, string> entry in section.Value)
            {
                int result = SetValue(doc, entry.Key, entry.Value);
                if (result > 0)
                {
                    changed += result;
                }
                else if (result < 0)
                {
                    missed.Add(entry.Key);
                }
            }

            if (changed == 0)
            {
                Console.WriteLine("  沒有需要改的值");
                continue;
            }

            doc.Save(path);
            Console.WriteLine("  改寫 " + changed + " 個 StartValue" +
                (missed.Count == 0 ? string.Empty : "（XML 找不到 " + missed.Count + " 個路徑）"));

            try
            {
                plc.TechnologicalObjectGroup.TechnologicalObjects.Import(
                    new FileInfo(path),
                    ImportOptions.Override);
                touched++;
                Console.WriteLine("  已匯入");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  匯入失敗：" + Flatten(ex));
            }
        }

        if (touched > 0)
        {
            project.Save();
            Console.WriteLine("已存檔，共更新 " + touched + " 個工藝對象");
        }
    }

    private static void Compile(PlcSoftware plc)
    {
        ICompilable compilable = plc.GetService<ICompilable>();
        if (compilable == null)
        {
            return;
        }

        try
        {
            CompilerResult result = compilable.Compile();
            Console.WriteLine("  先編譯 " + plc.Name + "：" + result.State +
                "／錯誤 " + result.ErrorCount);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  編譯失敗：" + Flatten(ex));
        }
    }

    // Returns >0 when values were written, 0 when already equal, -1 when the path is absent.
    private static int SetValue(XDocument doc, string path, string value)
    {
        string[] tokens = path.Split('.');
        if (tokens.Length == 0)
        {
            return -1;
        }

        List<XElement> scope = new List<XElement> { doc.Root };
        string index = null;

        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            string name = token;
            int bracket = token.IndexOf('[');
            if (bracket > 0 && token.EndsWith("]", StringComparison.Ordinal))
            {
                name = token.Substring(0, bracket);
                index = token.Substring(bracket + 1, token.Length - bracket - 2);
            }

            List<XElement> next = new List<XElement>();
            foreach (XElement element in scope)
            {
                next.AddRange(element
                    .Descendants(Iface + "Member")
                    .Where(member => string.Equals(
                        (string)member.Attribute("Name"), name, StringComparison.Ordinal)));
            }

            if (next.Count == 0)
            {
                return -1;
            }

            scope = next;
        }

        int written = 0;
        foreach (XElement leaf in scope)
        {
            XElement holder = leaf;
            if (index != null)
            {
                XElement subelement = leaf
                    .Elements(Iface + "Subelement")
                    .FirstOrDefault(item => string.Equals(
                        (string)item.Attribute("Path"), index, StringComparison.Ordinal));

                if (subelement == null)
                {
                    subelement = new XElement(Iface + "Subelement", new XAttribute("Path", index));
                    leaf.Add(subelement);
                }

                holder = subelement;
            }

            string formatted = Format(value, (string)leaf.Attribute("Datatype"));
            XElement startValue = holder.Element(Iface + "StartValue");
            if (startValue == null)
            {
                holder.Add(new XElement(Iface + "StartValue", formatted));
                written++;
            }
            else if (!string.Equals(startValue.Value, formatted, StringComparison.Ordinal))
            {
                startValue.Value = formatted;
                written++;
            }
        }

        return written;
    }

    private static string Format(string value, string datatype)
    {
        if (string.Equals(datatype, "Bool", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(value, "True", StringComparison.OrdinalIgnoreCase) ||
                value == "1" ? "true" : "false";
        }

        if (string.Equals(datatype, "Real", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(datatype, "LReal", StringComparison.OrdinalIgnoreCase))
        {
            double number;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                string text = number.ToString("R", CultureInfo.InvariantCulture);
                if (text.IndexOf('.') < 0 && text.IndexOf('E') < 0 && text.IndexOf('e') < 0)
                {
                    text += ".0";
                }

                return text;
            }
        }

        return value;
    }

    private static TechnologicalInstanceDB Find(TechnologicalInstanceDBGroup group, string name)
    {
        foreach (TechnologicalInstanceDB item in group.TechnologicalObjects)
        {
            if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        foreach (TechnologicalInstanceDBUserGroup child in group.Groups)
        {
            TechnologicalInstanceDB found = Find(child, name);
            if (found != null)
            {
                return found;
            }
        }

        return null;
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
                if (!sections.TryGetValue(key, out current))
                {
                    current = new Dictionary<string, string>(StringComparer.Ordinal);
                    sections[key] = current;
                }

                continue;
            }

            if (current == null)
            {
                continue;
            }

            int split = line.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            current[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
        }

        return sections;
    }

    private static string Sanitize(string name)
    {
        StringBuilder builder = new StringBuilder(name.Length);
        foreach (char item in name)
        {
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), item) >= 0 ? '_' : item);
        }

        return builder.ToString();
    }

    private static string Flatten(Exception ex)
    {
        List<string> lines = new List<string>();
        for (Exception current = ex; current != null; current = current.InnerException)
        {
            lines.Add(current.Message);
        }

        return string.Join(" | ", lines);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

internal static class PracticeAnnotator
{
    private static readonly Dictionary<string, string> MemberHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "enable", "使能" },
        { "pause", "暫停" },
        { "reset", "復位" },
        { "ack", "確認" },
        { "clear", "清除" },
        { "SP", "設定值 Setpoint" },
        { "PV", "程序值 Process Value" },
        { "err", "偏差" },
        { "out", "輸出" },
        { "Kp", "比例增益" },
        { "Ti", "積分時間常數（秒）" },
        { "Td", "微分時間常數（秒）" },
        { "Ts", "掃描週期（秒）" },
        { "feed_fwd", "前饋量" },
        { "Kf", "前饋增益" },
        { "ctrl_inv", "反向控制（err = PV - SP）" },
        { "s_in", "閃爍使能輸入" },
        { "s_out", "閃爍輸出" },
        { "tm_on", "導通時間" },
        { "tm_off", "關斷時間" },
        { "WM_lo", "低水位" },
        { "WM_hi", "高水位" },
        { "charge", "充水/泵運轉輸出" },
        { "charge_tm", "低水位延時" },
        { "run", "運轉" },
        { "stop", "停止" },
        { "estop", "急停" },
        { "fault", "故障" },
        { "ready", "就緒" },
        { "valid", "資料有效" },
        { "diff", "差值" },
        { "accu", "累加值" },
        { "prev_cnt", "上次計數" },
        { "cur_cnt", "目前計數" },
        { "bipolar", "雙向計數" },
        { "linespeed", "線速度" },
        { "finish_length", "完成長度" },
        { "ramped_ref", "斜坡後參考（%）" },
        { "ramped", "斜坡後參考" },
        { "ramp_dec", "減速斜坡" },
        { "ramp_NStop", "停止斜坡" },
        { "finishing_ref", "收尾參考（%）" },
        { "Slow_at", "開始減速位置" },
        { "Stop_at", "停止位置" },
        { "pb", "按鈕" },
        { "pb_edge", "按鈕上次狀態（邊緣偵測）" },
        { "sw", "切換狀態" },
        { "windup", "積分飽和（輸出到限幅）" },
        { "at_limit", "輸出已達上下限" },
        { "is_run", "運轉中" },
        { "in_action", "動作進行中" },
    };

    public static void Run(string projectFolder)
    {
        string ladDir = Path.Combine(projectFolder, "Practice");
        string sclDir = Path.Combine(ladDir, "Scl");
        string dumpDir = Path.Combine(ladDir, "SclDump");

        int ladUpdated = 0;
        int ladSkipped = 0;
        foreach (string path in Directory.GetFiles(ladDir, "*.lad.xml"))
        {
            if (AnnotateLad(path))
            {
                ladUpdated++;
                Console.WriteLine("LAD updated: " + Path.GetFileName(path));
            }
            else
            {
                ladSkipped++;
                Console.WriteLine("LAD skip: " + Path.GetFileName(path));
            }
        }

        int sclUpdated = 0;
        int sclSkipped = 0;
        foreach (string path in Directory.GetFiles(sclDir, "*.scl"))
        {
            if (AnnotateScl(path, dumpDir))
            {
                sclUpdated++;
                Console.WriteLine("SCL updated: " + Path.GetFileName(path));
            }
            else
            {
                sclSkipped++;
                Console.WriteLine("SCL skip: " + Path.GetFileName(path));
            }
        }

        Console.WriteLine("LAD: updated " + ladUpdated + ", skip " + ladSkipped);
        Console.WriteLine("SCL: updated " + sclUpdated + ", skip " + sclSkipped);
    }

    private static bool AnnotateLad(string path)
    {
        string raw = File.ReadAllText(path, Encoding.UTF8);
        if (Regex.IsMatch(raw, @"<Lad\b[^>]*\bcomment="))
        {
            return false;
        }

        string blockComment = null;
        Match header = Regex.Match(raw, @"(?s)<!--\s*(.*?)\s*-->\s*<Lad");
        if (header.Success)
        {
            blockComment = Regex.Replace(header.Groups[1].Value, @"\s+", " ").Trim();
        }

        if (string.IsNullOrWhiteSpace(blockComment))
        {
            string baseName = Path.GetFileNameWithoutExtension(path);
            if (baseName.EndsWith(".lad", StringComparison.OrdinalIgnoreCase))
            {
                baseName = baseName.Substring(0, baseName.Length - 4);
            }

            blockComment = "練習區塊 " + baseName + "（LAD）。可攜腳位，不依賴廠端全域標籤。";
        }

        raw = Regex.Replace(raw, @"<Lad\b([^>]*)>", m =>
        {
            if (m.Groups[1].Value.IndexOf("comment=", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return m.Value;
            }

            return "<Lad" + m.Groups[1].Value + " comment=\"" + EscapeXmlAttr(blockComment) + "\">";
        });

        string section = "Input";
        raw = Regex.Replace(raw, @"<Section name=""([^""]+)"">", m =>
        {
            section = m.Groups[1].Value;
            return m.Value;
        });

        raw = Regex.Replace(raw, @"<Member\b([^>]*)/>", m =>
        {
            string attrs = m.Groups[1].Value;
            if (attrs.IndexOf("comment=", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return m.Value;
            }

            Match nameMatch = Regex.Match(attrs, @"\bname=""([^""]+)""");
            Match typeMatch = Regex.Match(attrs, @"\btype=""([^""]+)""");
            if (!nameMatch.Success || !typeMatch.Success)
            {
                return m.Value;
            }

            string note = EscapeXmlAttr(DescribeMember(section, nameMatch.Groups[1].Value, typeMatch.Groups[1].Value));
            return "<Member" + attrs + " comment=\"" + note + "\" />";
        });

        int netIdx = 0;
        raw = Regex.Replace(raw, @"<Network\b([^>]*)>", m =>
        {
            netIdx++;
            string attrs = m.Groups[1].Value;
            if (attrs.IndexOf("comment=", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return m.Value;
            }

            string title = string.Empty;
            Match titleMatch = Regex.Match(attrs, @"\btitle=""([^""]+)""");
            if (titleMatch.Success)
            {
                title = titleMatch.Groups[1].Value;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = "Network " + netIdx;
            }

            string note = EscapeXmlAttr(netIdx + ". " + title);
            if (titleMatch.Success)
            {
                return "<Network" + attrs + " comment=\"" + note + "\">";
            }

            return "<Network title=\"" + EscapeXmlAttr(title) + "\" comment=\"" + note + "\">";
        });

        File.WriteAllText(path, raw, new UTF8Encoding(false));
        return true;
    }

    private static bool AnnotateScl(string path, string dumpDir)
    {
        string raw = File.ReadAllText(path, Encoding.UTF8);
        if (Regex.IsMatch(raw, @"(?m)^// ====="))
        {
            return false;
        }

        string blockName = null;
        string kind = "FC";
        Match fb = Regex.Match(raw, @"FUNCTION_BLOCK\s+""([^""]+)""", RegexOptions.IgnoreCase);
        Match fc = Regex.Match(raw, @"FUNCTION\s+""([^""]+)""", RegexOptions.IgnoreCase);
        if (fb.Success)
        {
            kind = "FB";
            blockName = fb.Groups[1].Value;
        }
        else if (fc.Success)
        {
            blockName = fc.Groups[1].Value;
        }
        else
        {
            throw new InvalidOperationException("Not an SCL block: " + path);
        }

        string summary = GetSclDumpSummary(dumpDir, blockName);
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = "練習用 SCL 區塊 " + blockName + "。可攜腳位。";
        }

        string header =
            "// =============================================================================" + Environment.NewLine +
            "// 區塊：" + blockName + "（" + kind + "）" + Environment.NewLine +
            "// " + summary + Environment.NewLine +
            "// =============================================================================" + Environment.NewLine +
            Environment.NewLine;

        raw = Regex.Replace(raw, @"(FUNCTION(?:_BLOCK)?\s+""[^""]+""[^\r\n]*)", header + "$1", RegexOptions.IgnoreCase);

        const string varComment = "// --- 介面腳位 ---";
        if (raw.IndexOf(varComment, StringComparison.Ordinal) < 0)
        {
            raw = Regex.Replace(raw, @"(VAR_(?:INPUT|OUTPUT|IN_OUT|TEMP|CONSTANT)\s*)", varComment + Environment.NewLine + "$1");
            raw = Regex.Replace(raw, "(" + Regex.Escape(varComment) + @"\r?\n){2,}", varComment + Environment.NewLine);
        }

        string beginComment =
            Environment.NewLine +
            "// --- 主要邏輯 ---" + Environment.NewLine +
            "// 依 23019 原版算法改寫；細節見 Practice\\SclDump\\" + blockName + ".txt" + Environment.NewLine;

        raw = Regex.Replace(raw, @"\r?\nBEGIN\r?\n", beginComment + "BEGIN" + Environment.NewLine);

        File.WriteAllText(path, raw, new UTF8Encoding(false));
        return true;
    }

    private static string GetSclDumpSummary(string dumpDir, string blockName)
    {
        string dump = Path.Combine(dumpDir, blockName + ".txt");
        if (!File.Exists(dump))
        {
            return null;
        }

        int nets = 0;
        foreach (string line in File.ReadAllLines(dump, Encoding.UTF8))
        {
            if (line.StartsWith("=== NETWORK", StringComparison.Ordinal))
            {
                nets++;
            }
        }

        if (nets > 0)
        {
            return "23019 參考：" + nets + " 個邏輯段。詳見 Practice\\SclDump\\" + blockName + ".txt";
        }

        return "23019 SCL 原版邏輯；可攜腳位練習版。";
    }

    private static string DescribeMember(string section, string name, string type)
    {
        string hint;
        if (MemberHints.TryGetValue(name, out hint))
        {
            return hint;
        }

        string sec;
        switch (section)
        {
            case "Input": sec = "輸入"; break;
            case "Output": sec = "輸出"; break;
            case "InOut": sec = "InOut"; break;
            case "Static": sec = "靜態"; break;
            case "Temp": sec = "暫存"; break;
            case "Constant": sec = "常數"; break;
            default: sec = section; break;
        }

        return sec + "：" + name + "（" + type + "）";
    }

    private static string EscapeXmlAttr(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace("\r\n", "&#10;")
            .Replace("\n", "&#10;");
    }
}

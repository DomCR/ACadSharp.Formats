using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Formats.Pdf;

/// <summary>
/// PDF 内嵌 CJK TrueType 字体（Type0 + Identity-H + CIDFontType2 + ToUnicode）。
/// 解析字体 cmap 表（format 4/12）得到 Unicode→GID；文字写 2 字节 GID（大端）。
/// 注意：本机 C:\Windows\Fonts 下的 CJK 字体其度量表（head.unitsPerEm/head bbox/OS/2 win 度量/hmtx）
/// 存在损坏（字形 glyf 与 cmap 完好，Windows 渲染靠驱动容错仍正常），但 PDF 查看器会严格按
/// 内嵌字体的 head.unitsPerEm 缩放字形、按 hmtx 取字宽。故嵌入前必须：
///   1) 经验法确定正确的 unitsPerEm（.notdef/全角 CJK 字形的 advance 应等于 1em）；
///   2) 修补 head 表（unitsPerEm + bbox）与 OS/2 表（usWinAscent/usWinDescent）；
///   3) 在 CIDFont 字典中用 /W 数组显式给出所用 GID 的字宽（1/1000 em），
///      避免查看器使用损坏的 hmtx 导致文字重叠。
/// </summary>
internal sealed class CjkFont
{
    public string FontName { get; private set; }
    public byte[] TtfBytes { get; private set; }
    public string SourcePath { get; private set; }
    private readonly Dictionary<int, int> _unicodeToGid = new();
    /// <summary>已使用的 GID → Unicode（用于生成 ToUnicode CMap 与 /W 字宽数组）</summary>
    public readonly Dictionary<int, int> UsedGidToUnicode = new();
    // 字体表目录与 unitsPerEm（供 /W 从 hmtx 读真实字宽）
    private Dictionary<string, (int off, int len)> _tables;
    private int _upem = 2048;

    /// <summary>默认 CJK 字体候选（按优先级）。</summary>
    public static readonly string[] DefaultCandidates = { "simhei.ttf", "msyh.ttf", "simkai.ttf", "simfang.ttf", "Deng.ttf" };

    /// <summary>
    /// 跨平台查找 TTF 字体文件：
    ///   1) 环境变量 D2X_CJK_FONT：直接指定 .ttf 文件路径（最高优先级）；
    ///   2) 环境变量 D2X_FONT_DIR：字体目录（封闭字体集——设置后只在该目录内查找，
    ///      不再回落到 OS 系统字体目录；后端宿主据此与浏览器 wasm 宿主保持逐字节一致，
    ///      浏览器宿主的字体集天然封闭于 VFS 镜像，不允许后端泄漏 C:\Windows\Fonts 等系统字体）；
    ///   3) Windows：C:\Windows\Fonts；
    ///   4) 非 Windows：/usr/share/fonts、/usr/local/share/fonts、~/.fonts、~/.local/share/fonts；
    ///   5) 本地：Support\fonts、fonts（CWD 与 exe 目录）。
    /// 先各目录直接匹配（快路径），再对系统/用户字体目录递归匹配。
    /// </summary>
    public static string? FindFontFile(string[] candidateNames)
    {
        if (candidateNames == null || candidateNames.Length == 0) return null;

        string envFile = Environment.GetEnvironmentVariable("D2X_CJK_FONT") ?? "";
        if (File.Exists(envFile)) return envFile;

        string envDir = Environment.GetEnvironmentVariable("D2X_FONT_DIR") ?? "";
        if (envDir.Length > 0 && Directory.Exists(envDir))
        {
            // 封闭字体集：只查该目录（快路径 + 递归），未命中直接返回 null（不回落到系统目录）。
            foreach (string name in candidateNames)
            {
                string p = Path.Combine(envDir, name);
                if (File.Exists(p)) return p;
                try
                {
                    foreach (string hit in Directory.EnumerateFiles(envDir, name, SearchOption.AllDirectories))
                        return hit;
                }
                catch { /* 容错：权限/IO 异常跳过 */ }
            }
            return null;
        }

        var dirs = new List<string>();
        string winFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        if (!string.IsNullOrEmpty(winFonts) && Directory.Exists(winFonts)) dirs.Add(winFonts);
        bool isWin = OperatingSystem.IsWindows();
        if (!isWin)
        {
            dirs.Add("/usr/share/fonts");
            dirs.Add("/usr/local/share/fonts");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                dirs.Add(Path.Combine(home, ".fonts"));
                dirs.Add(Path.Combine(home, ".local", "share", "fonts"));
            }
        }
        string baseDir = AppContext.BaseDirectory;
        string cwd = Directory.GetCurrentDirectory();
        foreach (string d in new[] { Path.Combine(cwd, "Support", "fonts"), Path.Combine(cwd, "fonts"), Path.Combine(baseDir, "Support", "fonts"), Path.Combine(baseDir, "fonts") })
            if (Directory.Exists(d) && !dirs.Contains(d)) dirs.Add(d);

        // 去重（保持优先级顺序）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = dirs.Where(d => seen.Add(d)).ToList();

        // 快路径：目录直接匹配
        foreach (string dir in ordered)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string name in candidateNames)
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        }

        // 慢路径：递归匹配（系统/用户字体目录 + 本地目录，目录规模小，容错跳过权限异常）
        foreach (string dir in ordered)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string name in candidateNames)
            {
                try
                {
                    foreach (string hit in Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories))
                        return hit;
                }
                catch { /* 权限/IO 异常：忽略继续 */ }
            }
        }
        return null;
    }

    // 加载默认 CJK 字体（simhei 优先，回退 msyh/simkai/simfang/Deng；跨平台目录搜索）。
    public static CjkFont TryLoad(Action<string> log)
    {
        string? path = FindFontFile(DefaultCandidates);
        if (path != null) return TryLoadFile(path, log);
        log?.Invoke("⚠️  未找到 CJK TTF 字体（simhei.ttf 等），PDF 中文文字将显示为 ?（可用环境变量 D2X_CJK_FONT/D2X_FONT_DIR 或 Support/fonts 目录指定）");
        return null;
    }

    // 加载指定 TTF 文件（供多字体按需内嵌）。
    public static CjkFont TryLoadFile(string path, Action<string> log)
    {
        try
        {
            var f = new CjkFont();
            f.SourcePath = path;
            f.TtfBytes = File.ReadAllBytes(path);
            string baseName = Path.GetFileNameWithoutExtension(path);
            f.FontName = new string(baseName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            if (!TryParseCmap(f.TtfBytes, f._unicodeToGid, out var tables, out int upem))
            {
                log?.Invoke($"⚠️  字体 cmap 表解析失败：{path}，PDF 中文文字将显示为 ?");
                return null;
            }
            f._tables = tables;
            f._upem = upem;
            f.PatchFontMetrics(tables, upem);
            log?.Invoke($"  PDF 中文字体：{Path.GetFileName(path)}（{f.TtfBytes.Length / 1024 / 1024} MB，{f._unicodeToGid.Count} 个字形映射，unitsPerEm={upem}）");
            return f;
        }
        catch (Exception ex)
        {
            log?.Invoke($"⚠️  加载 CJK 字体失败 {path}：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 修补内嵌字体的度量表（原地修改 TtfBytes）：
    /// head.unitsPerEm(+18)、head bbox(xMin@36/yMin@38/xMax@40/yMax@42)、OS/2 usWinAscent(+74)/usWinDescent(+76)。
    /// 字形轮廓（glyf/loca）与 cmap 完好，无需处理。
    /// </summary>
    private void PatchFontMetrics(Dictionary<string, (int off, int len)> tables, int upem)
    {
        byte[] t = TtfBytes;
        if (tables.TryGetValue("head", out var hd))
        {
            WriteU16(t, hd.off + 18, upem); // head.unitsPerEm 位于 +18（+16 是 flags，旧版误写会破坏 flags）
            // bbox 用安全范围（-0.3em .. 1.3em / -0.3em .. 1.2em）
            WriteI16(t, hd.off + 36, (int)(-0.3 * upem));
            WriteI16(t, hd.off + 38, (int)(-0.3 * upem));
            WriteI16(t, hd.off + 40, (int)(1.3 * upem));
            WriteI16(t, hd.off + 42, (int)(1.2 * upem));
        }
        if (tables.TryGetValue("OS/2", out var o2) && o2.len >= 78)
        {
            int asc = (int)(0.88 * upem), desc = (int)(0.12 * upem);
            if (tables.TryGetValue("hhea", out var hh))
            {
                int a = ReadI16(t, hh.off + 4), d = ReadI16(t, hh.off + 6);
                if (a > 0 && a < upem * 2 && d < 0 && d > -upem) { asc = a; desc = -d; }
            }
            WriteU16(t, o2.off + 74, asc);  // OS/2.usWinAscent
            WriteU16(t, o2.off + 76, desc); // OS/2.usWinDescent
        }
    }

    /// <summary>把文本编码为 2 字节 GID 序列（Identity-H）；记录用到的 GID 供 ToUnicode 使用。</summary>
    public byte[] Encode(string text)
    {
        var buf = new List<byte>(text.Length * 2);
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            int code;
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                code = char.ConvertToUtf32(ch, text[i + 1]);
                i += 2;
            }
            else
            {
                code = ch;
                i++;
            }
            if (code < 32)
            {
                if (code == 9) code = 32; // Tab → 空格
                else continue;            // 换行/其它控制符：跳过（i 已前进）
            }
            if (_unicodeToGid.TryGetValue(code, out int gid) && gid > 0 && gid < 0x10000)
            {
                buf.Add((byte)(gid >> 8));
                buf.Add((byte)(gid & 0xFF));
                if (!UsedGidToUnicode.ContainsKey(gid)) UsedGidToUnicode[gid] = code;
            }
            // 字体中不存在的字符：跳过（不写 .notdef，避免渲染出空白方块）
        }
        return buf.ToArray();
    }

    /// <summary>FontDescriptor 字典（1/1000 em 单位）。
    /// 使用安全的 CJK 通用值——本机字体文件的度量表不可信（见类注释），不从中取值。</summary>
    public string DescriptorDict(int fontFile2Obj)
    {
        return "<< /Type /FontDescriptor /FontName /" + FontName +
               " /Flags 32" +
               " /FontBBox [-300 -300 1300 1150]" +
               " /ItalicAngle 0" +
               " /Ascent 860" +
               " /Descent -140" +
               " /CapHeight 740" +
               " /StemV 80" +
               " /FontFile2 " + fontFile2Obj + " 0 R >>";
    }

    /// <summary>按码位类别估算字宽（1/1000 em）：CJK/全角=1em，拉丁/数字/半角≈0.5em。</summary>
    public static int WidthOf(int code)
    {
        if (code == 0x20) return 500;
        if (code >= 0x1100 && code <= 0x115F) return 1000; // 韩文 Jamo
        if (code >= 0x2E80 && code <= 0xA4CF) return 1000;  // CJK 部首/汉字/假名等
        if (code >= 0xA960 && code <= 0xA97F) return 1000;  // 彝文
        if (code >= 0xAC00 && code <= 0xD7A3) return 1000;  // 韩文音节
        if (code >= 0xF900 && code <= 0xFAFF) return 1000;  // CJK 兼容汉字
        if (code >= 0xFF00 && code <= 0xFF60) return 1000;  // 全角形式
        if (code >= 0x3000 && code <= 0x303F) return 1000;  // CJK 标点（、。《》等全角）
        if (code >= 0x20000 && code <= 0x2FA1F) return 1000; // CJK 扩展
        return 500; // 拉丁字母/数字/半角标点
    }

    /// <summary>生成 /W 字宽数组（仅含实际用到的 GID，1/1000 em）。
    /// 优先从内嵌字体 hmtx 读真实 advance（比例拉丁字距正确，如 arial T=611/i=222），
    /// 无效（缺失/越界）时回退按码位类别估算（CJK=1em、其余≈0.5em）。</summary>
    public string WidthArray()
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var kv in UsedGidToUnicode.OrderBy(k => k.Key, Comparer<int>.Default))
        {
            if (!first) sb.Append(' ');
            sb.Append(kv.Key).Append(' ').Append(kv.Key).Append(' ').Append(AdvanceOfGid(kv.Key, kv.Value));
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>单个 GID 的字宽（1/1000 em）：优先 hmtx 真实值，越界/缺失回退类别估算。</summary>
    private int AdvanceOfGid(int gid, int unicode)
    {
        try
        {
            if (_tables != null && TtfBytes != null &&
                _tables.TryGetValue("hmtx", out var hm) && _tables.TryGetValue("hhea", out var hh))
            {
                byte[] t = TtfBytes;
                int numH = ReadU16(t, hh.off + 34); // hhea.numberOfHMetrics
                if (numH > 0)
                {
                    int idx = Math.Min(gid, numH - 1);
                    int adv = ReadU16(t, hm.off + idx * 4);
                    if (adv > 0 && adv <= 2 * _upem)
                        return (int)Math.Round(adv * 1000.0 / _upem);
                }
            }
        }
        catch { /* 度量表异常：回退估算 */ }
        return WidthOf(unicode);
    }

    /// <summary>生成 ToUnicode CMap 流（GID → Unicode）。</summary>
    public static byte[] BuildCMapStream(Dictionary<int, int> gidToUnicode)
    {
        var sb = new StringBuilder();
        sb.AppendLine("/CIDInit /ProcSet findresource begin");
        sb.AppendLine("12 dict begin");
        sb.AppendLine("begincmap");
        sb.AppendLine("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def");
        sb.AppendLine("/CMapName /Adobe-Identity-UCS def");
        sb.AppendLine("/CMapType 2 def");
        sb.AppendLine("1 begincodespacerange");
        sb.AppendLine("<0000> <FFFF>");
        sb.AppendLine("endcodespacerange");
        sb.AppendLine(gidToUnicode.Count + " beginbfchar");
        foreach (var kv in gidToUnicode)
        {
            string dst = kv.Value >= 0x10000 ? kv.Value.ToString("X8") : kv.Value.ToString("X4");
            sb.AppendLine("<" + kv.Key.ToString("X4") + "> <" + dst + ">");
        }
        sb.AppendLine("endbfchar");
        sb.AppendLine("endcmap");
        sb.AppendLine("CMapName currentdict /CMap defineresource pop");
        sb.AppendLine("end");
        sb.AppendLine("end");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ===== TTF cmap 解析 =====
    // 解析 cmap（format 12 优先，回退 format 4），返回表目录与经验法确定的 unitsPerEm。
    // unitsPerEm 判定（head 表可能损坏，不能盲信）：
    //   1) head.unitsPerEm 在 [16..8192] 合理范围内则采用；
    //   2) 否则取 hmtx 中 .notdef（gid 0）的 advance（CJK 字体通常为 1em）；
    //   3) 否则取 U+4E00（一，全角）的 advance；
    //   4) 仍无有效值则默认 2048。
    private static bool TryParseCmap(byte[] t, Dictionary<int, int> map,
        out Dictionary<string, (int off, int len)> tables, out int unitsPerEm)
    {
        tables = null;
        unitsPerEm = 2048;
        if (t.Length < 14) return false;
        int numTables = ReadU16(t, 4);
        tables = new Dictionary<string, (int off, int len)>();
        for (int i = 0; i < numTables; i++)
        {
            int p = 12 + i * 16;
            if (p + 16 > t.Length) break;
            string tag = Encoding.ASCII.GetString(t, p, 4);
            tables[tag] = (ReadU32(t, p + 8), ReadU32(t, p + 12));
        }
        if (!tables.ContainsKey("cmap")) return false;

        var (cmapOff, _) = tables["cmap"];
        int nSub = ReadU16(t, cmapOff + 2);
        int fmt12Off = -1, fmt4Off = -1;
        for (int i = 0; i < nSub; i++)
        {
            int p = cmapOff + 4 + i * 8;
            if (p + 8 > t.Length) break;
            int plat = ReadU16(t, p), enc = ReadU16(t, p + 2), off = ReadU32(t, p + 4);
            int abs = cmapOff + off;
            if (abs + 4 > t.Length) continue;
            int fmt = ReadU16(t, abs);
            if (plat != 0 && plat != 3) continue;
            if (fmt == 12 && fmt12Off < 0) fmt12Off = abs;
            else if (fmt == 4 && fmt4Off < 0) fmt4Off = abs;
        }
        if (fmt12Off >= 0) ParseFmt12(t, fmt12Off, map);
        if (map.Count == 0 && fmt4Off >= 0) ParseFmt4(t, fmt4Off, map);
        if (map.Count == 0) return false;

        int upem = 0;
        if (tables.TryGetValue("head", out var head))
        {
            int h = ReadU16(t, head.off + 18); // head.unitsPerEm 位于 +18（+16 是 flags）
            if (h >= 16 && h <= 8192) upem = h;
        }
        if (upem <= 0 && tables.TryGetValue("hmtx", out var hm) && hm.off + 2 <= t.Length)
        {
            int a0 = ReadU16(t, hm.off);
            if (a0 >= 100 && a0 <= 8192) upem = a0;
        }
        if (upem <= 0 && tables.TryGetValue("hmtx", out var hm2)
            && map.TryGetValue(0x4E00, out int gid4E00) && hm2.off + gid4E00 * 2 + 2 <= t.Length)
        {
            int a1 = ReadU16(t, hm2.off + gid4E00 * 2);
            if (a1 >= 100 && a1 <= 8192) upem = a1;
        }
        if (upem <= 0) upem = 2048;
        unitsPerEm = upem;
        return true;
    }

    // cmap format 12（32 位码位，完整 Unicode）
    private static void ParseFmt12(byte[] t, int p, Dictionary<int, int> map)
    {
        int numGroups = ReadU32(t, p + 12);
        int g = p + 16;
        for (int i = 0; i < numGroups; i++)
        {
            if (g + 12 > t.Length) break;
            int start = ReadU32(t, g);
            int end = ReadU32(t, g + 4);
            int startGid = ReadU32(t, g + 8);
            g += 12;
            for (int c = start; c <= end; c++)
            {
                int gid = startGid + (c - start);
                if (gid > 0 && gid < 0x10000) map[c] = gid;
            }
        }
    }

    // cmap format 4（16 位 BMP）
    private static void ParseFmt4(byte[] t, int p, Dictionary<int, int> map)
    {
        int segCountX2 = ReadU16(t, p + 6);
        int segCount = segCountX2 / 2;
        int endOff = p + 14;
        int startOff = endOff + segCountX2 + 2; // 跳过 reservedRange
        int deltaOff = startOff + segCountX2;
        int rangeOff = deltaOff + segCountX2;
        int glyphBase = rangeOff + segCountX2;
        var endCodes = new int[segCount];
        for (int i = 0; i < segCount; i++) endCodes[i] = ReadU16(t, endOff + i * 2);
        for (int i = 0; i < segCount; i++)
        {
            int start = ReadU16(t, startOff + i * 2);
            int delta = ReadI16(t, deltaOff + i * 2);
            int rangeOffVal = ReadU16(t, rangeOff + i * 2);
            for (int c = start; c <= endCodes[i]; c++)
            {
                int gid;
                if (rangeOffVal == 0)
                {
                    gid = (c + delta) & 0xFFFF;
                }
                else
                {
                    int gi = rangeOffVal / 2 + (c - start);
                    if (glyphBase + gi * 2 + 1 >= t.Length) continue;
                    int g = ReadU16(t, glyphBase + gi * 2);
                    gid = g == 0 ? 0 : (g + delta) & 0xFFFF;
                }
                if (gid > 0) map[c] = gid;
            }
        }
    }

    private static int ReadU16(byte[] t, int p) => (t[p] << 8) | t[p + 1];
    private static int ReadI16(byte[] t, int p) => (short)ReadU16(t, p);
    private static int ReadU32(byte[] t, int p) => (t[p] << 24) | (t[p + 1] << 16) | (t[p + 2] << 8) | t[p + 3];
    private static void WriteU16(byte[] t, int p, int v) { t[p] = (byte)(v >> 8); t[p + 1] = (byte)v; }
    private static void WriteI16(byte[] t, int p, int v) => WriteU16(t, p, v & 0xFFFF);
}

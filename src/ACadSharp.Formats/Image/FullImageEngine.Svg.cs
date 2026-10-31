using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ACadSharp.Formats.Pdf;
using CSMath;
// SHX 形状/字体解析现由核心 ACadSharp.Text 自行实现（零外部依赖，替代原 Fonts.Lib）。
using ACadSharp.Text;

namespace ACadSharp.Formats.Image;

/// <summary>图像引擎：DxfImageExporter（netDxf 输入版）SVG+PDF 侧的 ACadSharp 移植（partial，SVG 部分在此文件、PDF 部分在 FullImageEngine.Pdf.cs）。</summary>
internal partial class ImageEngine
{
    private readonly CadDocument _doc;
    private readonly ImageConfiguration _opt;
    private readonly Action<string> _log;
    // INSERT 展开的祖先块集合（环防护，双保险）：数据带自引用/循环块（如 *Txx 匿名块被错连
    // ModelSpace）时截断该层展开，防渲染无限递归爆栈；正常嵌套块随展开入栈、返回出栈。
    // partial 类单处声明（SVG/PDF/PNG 三端共用）。
    private readonly HashSet<BlockRecord> _insertAncestors = new();
    private readonly bool _whiteBg;
    private readonly bool _transparentBg; // 仅 PNG 光栅端使用：true=画布不画底色（全透明 alpha）
    private bool _wipeoutDisplayImageFrame; // $WIPEOUTFRAME（WipeoutVariables 组码70）：有图 WIPEOUT 是否显示边框
    private bool _useRealTextWidth;       // PNG 光栅端置 true：文字对齐锚点用真实字体度量算宽（修正居中/右对正漂移）
    private double _ltScale = 1.0;
    private double _scale = 1.0;
    private short _pdMode;                 // $PDMODE 点样式：位0-2=0点/1无/2+/3X/4竖线；位5(32)=圆框；位6(64)=方框
    private double _pdSize;                // $PDSIZE 点尺寸：0=视高5%；>0=绝对单位；<0=视高百分比(|v|%)
    private bool _lwDisplay;               // $LWDISPLAY 线宽显示开关：开=应用全局线宽缩放($LWEIGHTSCALE)；关=按实际(未缩放)线宽渲染
    private double _lwScale = 1.0;         // $LWEIGHTSCALE 全局线宽缩放系数(1=名义不缩放)：仅 _lwDisplay 开时作用于"显示线宽"(组码370)的描边；多段线几何宽(40/43)走填充多边形、不受控
    private double _minX = double.MaxValue, _minY = double.MaxValue, _maxX = double.MinValue, _maxY = double.MinValue;
    private double _margin = 2.0, _w = 1.0, _h = 1.0;

    private StringBuilder _svgDefs;
    private int _hatchGradSeq;
    private readonly Dictionary<string, int> _skippedCounts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, double[]> _ltypeDash;
    private Dictionary<string, LtpPattern> _ltypePattern;
    private readonly Dictionary<string, ShxFont?> _shxFonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GlyphRec?> _glyphCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _shxAscentCache = new(StringComparer.OrdinalIgnoreCase); // 实测字形最大 Y（BigFont CJK 缩放用）

    public ImageEngine(CadDocument doc, ImageConfiguration opt)
    {
        _doc = doc;
        _opt = opt;
        _log = opt.Log ?? (_ => { });
        _whiteBg = opt.WhiteBackground;
        _transparentBg = opt.TransparentBackground;
        // $WIPEOUTFRAME：有图 WIPEOUT 的边框显示开关（遗留无图 WIPEOUT 不受影响→仍为擦除器，无边框）
        try { _wipeoutDisplayImageFrame = doc.GetCadObjects<ACadSharp.Objects.WipeoutVariables>().FirstOrDefault()?.DisplayImageFrame ?? false; } catch { }
        try
        {
            double v = doc.Header.LineTypeScale;
            if (double.IsFinite(v) && v > 0) _ltScale = v;
        }
        catch { }
        try
        {
            _pdMode = doc.Header.PointDisplayMode;
            _pdSize = doc.Header.PointDisplaySize;
        }
        catch { _pdMode = 0; _pdSize = 0.0; }
        // 全局线宽：$LWDISPLAY(开=应用全局缩放 / 关=按实际未缩放线宽) + $LWEIGHTSCALE(缩放系数)。
        // 只作用于"显示线宽"(组码370)的描边路径(StrokeWidth)；多段线几何宽(40/43/ConstantWidth)
        // 走 EmitTaperedPolyline 填充多边形、不经过本方法，故其自有的全局宽度不受全局缩放控制。
        try
        {
            _lwDisplay = doc.Header.DisplayLineWeight;
            double s = doc.Header.LineweightScale;
            if (double.IsFinite(s) && s > 0) _lwScale = s;
        }
        catch { _lwDisplay = false; _lwScale = 1.0; }
    }

    #region ===== 颜色：AutoCAD 标准 ACI 调色板 =====
    // 使用 ACadSharp fork 内置官方 ACI 色表（Color.GetIndexRGB，1-255 精确值；
    // 已程序化验证与 netDxf AciColor.IndexRgb 逐字节一致，ACI 颜色 parity 成立）。
    private static readonly string[] AciHex = BuildAciHex();

    private static string[] BuildAciHex()
    {
        var t = new string[256];
        for (int i = 0; i < 256; i++) t[i] = "#FFFFFF";
        for (int i = 1; i <= 255; i++)
        {
            ReadOnlySpan<byte> rgb = Color.GetIndexRGB((byte)i);
            t[i] = "#" + rgb[0].ToString("X2") + rgb[1].ToString("X2") + rgb[2].ToString("X2");
        }
        return t;
    }

    // ACI 索引 → 十六进制颜色。ACI 7 是 AutoCAD 的"白/黑"自适应色：
    // 黑底显示白色；白底（--white）或透明底（--transparent，叠加到未知/通常浅色页面）显示黑色，
    // 与 AutoCAD 模型空间切换布局背景的行为一致。此前仅白底映射黑、透明底仍映射白 →
    // 透明底 PNG/SVG 上 ACI7 图元（图层 0/Defpoints 的线、ByBlock 文字等）不可见。
    private string AciColor(int idx)
    {
        if (idx <= 0 || idx >= 256) idx = 7;
        if (idx == 7 && (_whiteBg || _transparentBg)) return "#000000";
        return AciHex[idx];
    }

    // netDxf 一致：AciColor 对真色（420）存储最近 ACI 索引（FromTrueColor → RgbToAci），
    // 而 fork Color.Index 对真色为 -1 —— 故普通实体按 Index 取色前必须把真色换算为最近 ACI
    // （netDxf RgbToAci：RGB 空间暴力最近邻，精确匹配优先）。
    private static short TrueColorToAci(byte r, byte g, byte b)
    {
        int prevDist = int.MaxValue;
        short index = 0;
        for (int i = 1; i < 256; i++)
        {
            ReadOnlySpan<byte> color = Color.GetIndexRGB((byte)i);
            int red = r - color[0];
            int green = g - color[1];
            int blue = b - color[2];
            int dist = red * red + green * green + blue * blue;
            if (dist == 0) return (short)i; // RGB 恰好是某索引色
            if (dist < prevDist) { prevDist = dist; index = (short)i; }
        }
        return index;
    }

    // 等价 netDxf EntityObject.Color.Index：真色 → 最近 ACI (1..255)；否则原索引（0=ByBlock / 256=ByLayer）
    private static short EntityAciIndex(Entity ent)
    {
        var c = ent.Color;
        if (c.IsTrueColor) return TrueColorToAci(c.R, c.G, c.B);
        return c.Index;
    }

    private string ColorOf(short aci, short layerAci, short byBlockAci)
    {
        int idx;
        if (aci >= 1 && aci <= 255) idx = aci;   // 独立颜色
        else if (aci == 0) idx = byBlockAci;      // 随块 -> INSERT 颜色
        else idx = layerAci;                      // 随图层 (256) / 非法 -> 图层色
        return AciColor(idx);
    }

    // 等价 netDxf Layer.Color.Index（真色图层同样 = 最近 ACI）；层缺失 → 7
    private static short LayerAciIndex(Layer layer)
    {
        if (layer == null) return 7;
        var lc = layer.Color;
        return lc.IsTrueColor ? TrueColorToAci(lc.R, lc.G, lc.B) : lc.Index;
    }

    // 实体的有效 ACI 颜色（用于把 INSERT 颜色作为其块内容的 ByBlock 颜色传递下去）：
    //   独立颜色 (1-255) -> 该颜色；ByLayer (256) -> 自身图层色；ByBlock (0) -> 继承上层 ByBlock 色 fallback。
    private static short EffectiveAci(Entity ent, short fallback)
    {
        short aci;
        try { aci = EntityAciIndex(ent); } catch { return fallback; }
        if (aci >= 1 && aci <= 255) return aci;
        if (aci == 256)
        {
            try
            {
                var layer = ent.Layer;
                if (layer == null) return fallback;
                var lc = layer.Color;
                return lc.IsTrueColor ? TrueColorToAci(lc.R, lc.G, lc.B) : lc.Index;
            }
            catch { return fallback; }
        }
        return fallback; // ByBlock (0) -> 上层 ByBlock 色
    }

    #endregion

    #region ===== 线型：虚线/点划线模式 =====
    // 从文档 LTYPE 表构建"线型名 -> dasharray"缓存。
    // DXF 段长语义：正=实线段(dash)、负=空隙(gap)、0=点(dot)。
    // 简单线型（无 SHX Shape 段）：合并连续同类型段后输出交替的 [dash, gap, ...]。
    // 复杂线型（含 SHX Shape 段）：另构建 LtpPattern 模式段序列，导出时沿实体路径逐周期走查。
    private void BuildLinetypeDash()
    {
        _ltypeDash = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        _ltypePattern = new Dictionary<string, LtpPattern>(StringComparer.OrdinalIgnoreCase);
        if (_doc?.LineTypes == null) return;
        const double dot = 0.1;
        const double shapeUnit = 4.0; // SHX 字形经验基准尺寸：点 ≈ 缩放 × 4
        foreach (var lt in _doc.LineTypes)
        {
            if (lt == null || string.IsNullOrEmpty(lt.Name)) continue;
            var seglist0 = lt.Segments?.ToList() ?? new List<LineType.Segment>();
            if (seglist0.Count == 0)
            {
                _ltypeDash[lt.Name] = null; // Continuous 等无段线型 = 实线
                continue;
            }

            // [T4.8] 与 netDxf（DxfReader 后处理 L883-895）逐条一致：shape 段的 NAME 按【编号】
            // 从 SHX 库解析（Style.ShapeName(number)，不是编号字符串——SHX 名表键是形状名，
            // "149" 这类数字串查不到 → 回退短划）；库中无此形状（或字体文件缺失）时
            // netDxf 把该段从线型中【删除】，再用剩余段重建图案。
            List<LineType.Segment> seglist;
            string[] resolvedShapeNames = null; // 与 seglist 同序；仅 shape 段非空
            if (lt.HasShapes)
            {
                seglist = new List<LineType.Segment>(seglist0.Count);
                resolvedShapeNames = new string[seglist0.Count];
                int w = 0;
                for (int j = 0; j < seglist0.Count; j++)
                {
                    var s0 = seglist0[j];
                    if (s0.IsShape)
                    {
                        int shapeNum = (int)s0.ShapeNumber;
                        if (shapeNum < 0) shapeNum += 65536; // short 负值 = 高位形状号
                        var font = GetShapeFont(s0.Style?.Filename ?? "");
                        string sname = font != null ? font.GetShapeName(shapeNum) : "";
                        if (string.IsNullOrEmpty(sname)) continue; // 库中无此形状 → 删除该段（netDxf 语义）
                        resolvedShapeNames[w] = sname;
                    }
                    seglist.Add(s0);
                    w++;
                }
            }
            else
            {
                seglist = seglist0;
            }

            // 复杂线型（含 SHX Shape 段 / 文字段）：构建"模式段序列"供沿路径真实渲染（严格遵循 LTYPE 定义）。
            // 触发条件须含文字段（----GAS---- 等纯文字线型 HasShapes=false，旧实现被当实线、文字段丢失）。
            bool hasComplex = seglist0.Any(s => s != null && (s.IsShape || s.IsText));
            if (hasComplex)
            {
                int n0 = seglist.Count;
                var pat = new LtpPattern();
                for (int j = 0; j < n0; j++)
                {
                    var s0 = seglist[j];
                    double l = s0?.Length ?? 0;
                    if (s0.IsShape)
                    {
                        // 旋转类型与 netDxf 同位判定（DXF 74 bit0：1=absolute，0=relative；upright 不可达）
                        int rotType = s0.Flags.HasFlag(LineTypeShapeFlags.RotationIsAbsolute) ? 2 : 1;
                        pat.Segs.Add(new LtpSeg
                        {
                            Kind = LtpSeg.Shape,
                            Len = Math.Abs(l),
                            InGap = l <= 1e-9,
                            ShapeName = resolvedShapeNames[j],
                            ShapeFile = s0.Style?.Filename ?? "",
                            ShapeScale = s0.Scale > 0 ? s0.Scale : 1.0,
                            RotType = rotType,
                            ShapeRot = s0.Rotation * 180.0 / Math.PI, // fork 存弧度 → 转度（与参考实现单位一致）
                            OX = s0.Offset.X,
                            OY = s0.Offset.Y,
                        });
                    }
                    else if (s0.IsText)
                    {
                        // 文字段：线在文字处断开，并在段中点沿切向渲染文字（如 ----GAS---- 的 GAS）。
                        // 旧实现只当空隙、不画文字 → GAS 文字线型渲染成实线（#55）。
                        string tf = s0.Style?.Filename ?? "";
                        // 字体族名：TTF 去扩展名；SHX 直接用文件名（渲染端 ResolveFontFamily 兜底）
                        int dotI = tf.LastIndexOf('.');
                        string fam = dotI > 0 ? tf.Substring(0, dotI) : tf;
                        pat.Segs.Add(new LtpSeg
                        {
                            Kind = LtpSeg.Text,
                            Len = Math.Abs(l),
                            InGap = l <= 1e-9,
                            TextContent = s0.Text,
                            TextScale = s0.Scale > 0 ? s0.Scale : 1.0,
                            TextRot = s0.Rotation * 180.0 / Math.PI,
                            TextFont = fam,
                            OX = s0.Offset.X,   // 组码 44（数据单位；参考实测不施加，存档备用）
                            OY = s0.Offset.Y,   // 组码 45（数据单位；参考施加为基线垂直偏移，经 pat.Scaled 乘图案比例）
                        });
                    }
                    else if (l > 1e-9) pat.Segs.Add(new LtpSeg { Kind = LtpSeg.Dash, Len = l });
                    else if (l < -1e-9) pat.Segs.Add(new LtpSeg { Kind = LtpSeg.Gap, Len = -l });
                    else
                    {
                        // 零长简单段 = 点；相邻形状段已有字形 → 跳过（避免双点）
                        var pv = seglist[(j - 1 + n0) % n0];
                        var nx2 = seglist[(j + 1) % n0];
                        if (!pv.IsShape && !nx2.IsShape)
                            pat.Segs.Add(new LtpSeg { Kind = LtpSeg.Dash, Len = dot });
                    }
                }
                // 合并连续同类型段（Shape/Text 段永不合并——每个都是独立放置）
                var pm = new List<LtpSeg>();
                foreach (var s0 in pat.Segs)
                {
                    if (pm.Count > 0 && s0.Kind != LtpSeg.Shape && s0.Kind != LtpSeg.Text && pm[pm.Count - 1].Kind == s0.Kind)
                        pm[pm.Count - 1].Len += s0.Len;
                    else pm.Add(s0);
                }
                pat.Segs.Clear();
                pat.Segs.AddRange(pm);
                // Shape/Text 放置位置（相对段起点）：
                //  - Shape：按 DXF 规范字形原点落在【自身段跨度末端】（正跨度与间隙型一致）。
                //    旧实现的"段中点/总空隙中点"经参考 PDF 实测证伪：'444' 的刻度/圆与 '10422' 的
                //    刻度/靶心位置全部 = 段跨度末端（4.5/9.5 与 0.75/2.25），误差 <0.001u。
                //  - Text：仍居中于自身段跨度（线在文字处断开，文字置于跨度中点；间隙型用总空隙中点）。
                int pn = pat.Segs.Count;
                for (int j = 0; j < pn; j++)
                {
                    var s0 = pat.Segs[j];
                    if (s0.Kind == LtpSeg.Shape) { s0.CenterOffset = s0.Len; continue; }
                    if (s0.Kind != LtpSeg.Text) continue;
                    if (!s0.InGap) { s0.CenterOffset = s0.Len / 2; continue; }
                    double gapBefore = pat.Segs[(j - 1 + pn) % pn].Kind == LtpSeg.Gap ? pat.Segs[(j - 1 + pn) % pn].Len : 0;
                    double gapAfter = pat.Segs[(j + 1) % pn].Kind == LtpSeg.Gap ? pat.Segs[(j + 1) % pn].Len : 0;
                    s0.CenterOffset = (s0.Len + gapAfter - gapBefore) / 2;
                }
                double per = 0;
                foreach (var s0 in pat.Segs) per += s0.Len;
                if (per > 1e-9)
                {
                    pat.Period = per;
                    _ltypePattern[lt.Name] = pat;
                }
            }

            int n = seglist.Count;
            var segs = new List<(bool IsDash, double Len)>();
            double pendingShrink = 0;   // 待施加到"下一个 gap 段"的缩减（零长形状点跨边界吃掉的 d/2）
            double deferredShrink = 0;  // 前面没有 gap 可吃时，延后施加到图案尾部 gap 的缩减
            void AddGap(double g)
            {
                g -= pendingShrink;
                pendingShrink = 0;
                if (g > 1e-6) segs.Add((false, g));
            }
            void AddDash(double d)
            {
                d += pendingShrink; // 跨边界的点与紧邻笔落段视觉融合（加长）
                pendingShrink = 0;
                if (d > 1e-6) segs.Add((true, d));
            }
            void ShrinkPrevGap(double v)
            {
                for (int m = segs.Count - 1; m >= 0; m--)
                {
                    if (!segs[m].IsDash)
                    {
                        var g = segs[m].Len - v;
                        if (g > 1e-6) segs[m] = (false, g); else segs.RemoveAt(m);
                        return;
                    }
                }
                deferredShrink += v; // 尚无 gap，延后到尾部
            }
            for (int i = 0; i < n; i++)
            {
                var seg = seglist[i];
                double len = seg?.Length ?? 0;
                var prev = seglist[(i - 1 + n) % n];
                var next = seglist[(i + 1) % n];
                if (seg.IsShape)
                {
                    double s = seg.Scale > 0 ? seg.Scale : 1.0;
                    if (len > 1e-9 || len < -1e-9)
                    {
                        // 形状占用明确段长（笔落段或空隙段）：点居中于该段
                        double span = Math.Abs(len);
                        double d = Math.Min(s * shapeUnit, 0.9 * span);
                        if (d < 0.05) d = Math.Min(0.05, span);
                        double side = (span - d) / 2;
                        if (side > 1e-9) AddGap(side);
                        AddDash(d);
                        if (side > 1e-9) AddGap(side);
                    }
                    else
                    {
                        // 零长形状（符号位于图案边界）：点跨边界，前后空隙各吃 d/2
                        double prevGap = prev != null && prev.Length < -1e-9 ? -prev.Length : 0;
                        double nextGap = next != null && next.Length < -1e-9 ? -next.Length : 0;
                        double d = Math.Min(s * shapeUnit, 0.85 * (prevGap + nextGap));
                        if (d < 0.05) d = 0.05;
                        AddDash(d);
                        pendingShrink += d / 2;
                        ShrinkPrevGap(d / 2);
                    }
                    continue;
                }
                if (seg.IsText)
                {
                    // 文字段：线在文字处断开 → 空隙
                    if (Math.Abs(len) > 1e-9) AddGap(Math.Abs(len));
                    else AddDash(dot);
                    continue;
                }
                if (len > 1e-9) AddDash(len);
                else if (len < -1e-9) AddGap(-len);
                else
                {
                    // 零长简单段 = 点；相邻形状段的点已代表该位置 → 跳过（避免多余小点）
                    bool nearShape = prev.IsShape || next.IsShape;
                    if (!nearShape) AddDash(dot);
                }
            }
            if (pendingShrink > 0) ShrinkPrevGap(pendingShrink); // 图案末尾仍待施加 → 落到尾部 gap
            if (deferredShrink > 0) ShrinkPrevGap(deferredShrink);

            // 合并连续同类型
            var merged = new List<(bool IsDash, double Len)>();
            foreach (var s in segs)
            {
                if (merged.Count > 0 && merged[merged.Count - 1].IsDash == s.IsDash)
                    merged[merged.Count - 1] = (s.IsDash, merged[merged.Count - 1].Len + s.Len);
                else merged.Add(s);
            }
            // 保证以 dash 开头（若以 gap 开头，把首个 gap 移到末尾，形成闭环等效模式）
            if (merged.Count > 0 && !merged[0].IsDash)
            {
                var first = merged[0];
                merged.RemoveAt(0);
                merged.Add(first);
            }
            if (merged.Count == 0)
            {
                _ltypeDash[lt.Name] = null; // 极端退化（全被吃掉）→ 实线兜底
                continue;
            }
            bool anyDash = merged.Any(m => m.IsDash);
            var arr = anyDash
                ? merged.Select(m => m.Len).ToArray()
                : new double[] { 1.0, 1.0 };
            _ltypeDash[lt.Name] = arr;
        }
    }

    // 实体的有效虚线模式（已乘有效比例 = $LTSCALE × 实体线型比例48 × 块插入比例）；
    // null = 实线。
    private double[] ResolveDash(Entity ent, double[] byBlockDash, double ltMul)
    {
        if (ent == null) return null;
        string name = null;
        try { name = ent.LineType?.Name; } catch { }
        if (string.IsNullOrEmpty(name) || name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase))
        { try { name = ent.Layer?.LineType?.Name; } catch { } }
        if (string.IsNullOrEmpty(name) || name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase))
            return byBlockDash; // 随块 -> 继承上层
        if (name.Equals("Continuous", StringComparison.OrdinalIgnoreCase)) return null;
        if (_ltypeDash == null || !_ltypeDash.TryGetValue(name, out var dash) || dash == null) return null;
        double scale = (double.IsFinite(ltMul) && ltMul > 0) ? ltMul : 1.0;
        try { var s = ent.LineTypeScale; if (double.IsFinite(s) && s > 0) scale *= s; } catch { }
        if (Math.Abs(scale - 1.0) < 1e-9) return dash;
        var scaled = new double[dash.Length];
        for (int i = 0; i < dash.Length; i++) scaled[i] = dash[i] * scale;
        return scaled;
    }

    private static string DashAttr(double[] dash)
    {
        if (dash == null || dash.Length == 0) return "";
        return string.Join(" ", dash.Select(v => F(v)));
    }

    // 解析实体的有效复杂线型模式（已乘有效比例）。null = 非复杂线型（走 dasharray/实线）。
    private LtpPattern ResolvePattern(Entity ent, LtpPattern byBlockPattern, double ltMul)
    {
        if (ent == null) return null;
        string name = null;
        try { name = ent.LineType?.Name; } catch { }
        if (string.IsNullOrEmpty(name) || name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase))
        { try { name = ent.Layer?.LineType?.Name; } catch { } }
        if (string.IsNullOrEmpty(name) || name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase))
            return byBlockPattern; // 随块 -> 继承上层
        if (name.Equals("Continuous", StringComparison.OrdinalIgnoreCase)) return null;
        if (_ltypePattern == null || !_ltypePattern.TryGetValue(name, out var pat) || pat == null) return null;
        double scale = (double.IsFinite(ltMul) && ltMul > 0) ? ltMul : 1.0;
        try { var s = ent.LineTypeScale; if (double.IsFinite(s) && s > 0) scale *= s; } catch { }
        return Math.Abs(scale - 1.0) < 1e-9 ? pat : pat.Scaled(scale);
    }

    // 块插入的线型比例贡献（CAD 语义：块内实体图案比例乘块插入的 X 比例）。
    private static double BlockScaleOf(Insert ins)
    {
        try
        {
            double s = ins.XScale;
            if (double.IsFinite(s) && s > 0) return s;
        }
        catch { }
        return 1.0;
    }

    // 经 Fonts.Lib 加载 SHX 形状库（AutoCAD-86 shapes 格式），按文件名缓存。
    // 文件路径由 ImageConfiguration.FileProvider 解析；失败返回 null（调用方回退"点"）。
    private ShxFont? GetShapeFont(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        if (_shxFonts.TryGetValue(fileName, out var cached)) return cached;
        ShxFont? font = null;
        try
        {
            string path = "";
            try { path = _opt.FileProvider?.Invoke(fileName) ?? ""; } catch { }
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                _log($"⚠️  SHX 形状库未找到：{fileName}（复杂线型形状回退为「点」渲染）");
            else
                font = ShxFont.LoadFromFile(path);
        }
        catch (Exception ex)
        {
            _log($"⚠️  SHX 形状库加载失败 {fileName}：{ex.Message}");
        }
        _shxFonts[fileName] = font;
        return font;
    }

    // 取形状字形（按"文件名|shape编号"缓存）。SHX shapes 库中 code 即 shape 编号。
    private GlyphRec? GetGlyph(ShxFont font, string fileName, int num)
    {
        string key = fileName + "|" + num;
        if (_glyphCache.TryGetValue(key, out var g)) return g;
        GlyphRec? rec = null;
        try
        {
            var r = new GlyphRec();
            if (font.TryGetGlyph(num, r) && !r.Empty) rec = r;
        }
        catch { }
        _glyphCache[key] = rec;
        return rec;
    }

    // 累计弧长表（数据单位）
    private static double[] BuildCum(List<(double X, double Y)> pts)
    {
        var cum = new double[pts.Count];
        for (int i = 1; i < pts.Count; i++)
        {
            double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            cum[i] = cum[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }
        return cum;
    }

    // 取路径上弧长 s 处的点与切向单位向量（s 钳制到 [0, L]）
    private static void PathPointAt(List<(double X, double Y)> pts, double[] cum, double s,
        out double x, out double y, out double tx, out double ty)
    {
        int n = pts.Count;
        double L = cum[n - 1];
        if (L <= 1e-12)
        {
            x = pts[0].X; y = pts[0].Y; tx = 1; ty = 0; return;
        }
        if (s < 0) s = 0; else if (s > L) s = L;
        int lo = 0, hi = n - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (cum[mid] >= s) hi = mid; else lo = mid + 1;
        }
        int i = Math.Max(1, lo);
        double segLen = cum[i] - cum[i - 1];
        double t = segLen > 1e-12 ? (s - cum[i - 1]) / segLen : 0;
        x = pts[i - 1].X + (pts[i].X - pts[i - 1].X) * t;
        y = pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * t;
        tx = pts[i].X - pts[i - 1].X;
        ty = pts[i].Y - pts[i - 1].Y;
        double tl = Math.Sqrt(tx * tx + ty * ty);
        if (tl < 1e-12) { tx = 1; ty = 0; } else { tx /= tl; ty /= tl; }
    }

    // 把弧长区间 [s0, s1] 的子路径追加到 d（端点精确插值 + 区间内原始采样顶点）。
    // pdf=false 为 SVG（M/L），true 为 PDF（m/l）；fx/fy=数据坐标→输出坐标映射。
    private static void AppendSpanPath(StringBuilder d, List<(double X, double Y)> pts, double[] cum,
        double s0, double s1, Func<double, double> fx, Func<double, double> fy, bool pdf)
    {
        if (s1 - s0 < 1e-9) return;
        int n = pts.Count;
        double L = cum[n - 1];
        if (s0 < 0) s0 = 0;
        if (s1 > L) s1 = L;
        if (s1 - s0 < 1e-9) return;

        PathPointAt(pts, cum, s0, out double ax, out double ay, out _, out _);
        double m0x = fx(ax), m0y = fy(ay);
        PathPointAt(pts, cum, s1, out double bx, out double by, out _, out _);
        double m1x = fx(bx), m1y = fy(by);

        if (pdf) d.Append(F(m0x)).Append(' ').Append(F(m0y)).Append(" m ");
        else d.Append("M").Append(F(m0x)).Append(',').Append(F(m0y));
        for (int i = 1; i < n; i++)
        {
            double c = cum[i];
            if (c <= s0 + 1e-9 || c >= s1 - 1e-9) continue;
            double mx = fx(pts[i].X), my = fy(pts[i].Y);
            if (pdf) d.Append(F(mx)).Append(' ').Append(F(my)).Append(" l ");
            else d.Append("L").Append(F(mx)).Append(',').Append(F(my));
        }
        double dx = m1x - m0x, dy = m1y - m0y;
        if (dx * dx + dy * dy > 1e-18)
        {
            if (pdf) d.Append(F(m1x)).Append(' ').Append(F(m1y)).Append(" l ");
            else d.Append("L").Append(F(m1x)).Append(',').Append(F(m1y));
        }
    }

    // 图案起始相位（路径起点处的图案坐标）。
    // DXF 实体未记录 code42（起始偏移），参考（AutoCAD）的布局满足不变式：
    //   线两端关于【首字形图案位置 T】对称，即 q(起点)+q(终点) ≡ T (mod P)。
    // 设线长 L，则 q(终点) ≡ q(起点)+L (mod P) ⇒ q(起点) = (T − (L mod P))/2 (mod P)。
    // 已对参考 PDF 两条线型数值验证：'444'(P=10,T=4.5) 刻度/圆/间隙 与 '10422'(P=3,T=0.75)
    // 刻度/靶心 全部落在预测位置，误差 <0.001u（L=31.43u/34.03u，约 24 个字形）。
    // 纯文本图案（无 Shape 段）：锚点取首个文字段【起点】T（参考 PDF 实测同一对称不变式：
    //   'GAS_LINE' σ=1.5 时 T=19.05（首 dash 末=文字段起点），φ=(T−(L mod P))/2 mod P = 29.3018，
    //   与实测 29.297±0.005u 吻合）。既有 Shape 锚点规则（'444'/'10422'）不受影响。
    // 既无 Shape 也无 Text 段的线型保持 φ=0（自路径起点走查，与旧行为一致）。
    private static double PatternPhase(LtpPattern pat, double L)
    {
        double P = pat.Period;
        if (P <= 1e-9) return 0;
        double T = pat.PhaseAnchor();
        if (T < 0) return 0;
        double phi = (T - L % P) / 2.0;
        phi %= P;
        if (phi < 0) phi += P;
        return phi;
    }

    // 沿路径走查模式（SVG/PDF 共用）：
    //   dash 段→线子路径；正跨度 shape 段→线子路径 + 段末字形；间隙型 shape→段末字形（不画线）；
    //   gap 段→留空；text 段→跨度中点文字（线断开）。
    // 起始相位见 PatternPhase：从 φ 处切入图案（定位所在段），首末段按路径裁剪。
    // 字形/文字位置 = 其段跨度末端/中点的图案坐标 − φ（转路径坐标），越界不输出。
    private void WalkPattern(StringBuilder d, List<(double X, double Y)> pts, double[] cum, LtpPattern pat,
        Func<double, double> fx, Func<double, double> fy, bool pdf, List<LtpTextPlace> textOut = null)
    {
        double L = cum[cum.Length - 1];
        var segs = pat.Segs;
        int n = segs.Count;
        if (L <= 1e-9 || n == 0) return;
        // 图案坐标累计跨度：cumpat[j] = 第 j 段起点在图案中的位置
        var cumpat = new double[n + 1];
        for (int j = 0; j < n; j++) cumpat[j + 1] = cumpat[j] + segs[j].Len;
        double phi = PatternPhase(pat, L);
        bool _dbg = System.Environment.GetEnvironmentVariable("GDB2CAD_LTP_DEBUG") != null
            && pat.Period > 10.0 && pat.Period < 11.0 && segs.Count == 4;
        var _dbgGlyphs = new System.Collections.Generic.List<string>();
        if (_dbg)
            System.Console.Error.WriteLine(string.Format("[LTPDBG] L={0:F6} P={1:F6} phi={2:F6} T={3:F6}", L, pat.Period, phi, pat.PhaseAnchor()));
        // 起点所在段：cumpat[i] <= phi < cumpat[i+1]（φ 恰在边界时归后一段，into=0）
        int i = 0;
        while (i < n - 1 && cumpat[i + 1] <= phi) i++;
        double pos = phi;                 // 当前图案空间位置
        double into = phi - cumpat[i];    // 段内偏移
        double firstStart = double.MaxValue, lastEnd = -1.0; // 已画实线跨度的首/末（供端部空段填充）
        int guard = 0;
        while (pos < L + phi - 1e-9 && guard++ < 50000000)
        {
            var seg = segs[i % n];
            double span = seg.Len;
            double take = Math.Max(0, Math.Min(span - into, (L + phi) - pos));
            if (take > 1e-12)
            {
                double p0 = pos - phi;    // 本切片路径坐标
                double p1 = p0 + take;
                if (seg.Kind == LtpSeg.Dash || (seg.Kind == LtpSeg.Shape && !seg.InGap))
                {
                    AppendSpanPath(d, pts, cum, p0, p1, fx, fy, pdf);
                    if (p0 < firstStart) firstStart = p0;
                    if (p1 > lastEnd) lastEnd = p1;
                }
            }
            if (seg.Kind == LtpSeg.Shape)
            {
                // 字形置于段跨度末端（图案绝对坐标含周期偏移：pos 为段起点的图案绝对坐标，
                // 末端 = pos + (span−into)；若用 cumpat[(i%n)+1] 则丢失周期数，字形冻结在第一周期，
                // 且 φ 大于段内坐标时首字形位置为负被误裁剪；含零长间隙型段——take=0 也需输出）
                double glyphAt = pos + (span - into) - phi;
                // 对齐 AutoCAD 参考（参考模型 PDF 实测）：仅当段末之后紧接的下一段也完整落在线内时
                // 才输出该段末字形——如线端附近的 tick 段完整画出、但下一 shape 段溢出线端时，
                // 该段末的 tick 字形被省略（线跨度本身仍画）。仅用 glyphAt<=L 会多画这类端部字形。
                double nextLen = Math.Abs(segs[(i + 1) % n].Len);
                // 头端规则（用户指令：头尾伸出不足一个循环的按属性保持对称、直接渲染为直线）：
                // 首实线跨度出现前的图案区（头端填充区 [0,firstStart]）内不输出字形——
                // 如 BATTING 弹簧首切片落 InGap 段中、段末 ∪ 字形落在头端填充区内被省略，
                // 头端保持干净直线引入（AutoCAD 参考头端无 ∪，仅被截断的 ∩ 左腿触轴）。
                bool _emit = glyphAt >= -1e-9 && glyphAt + nextLen <= L + 1e-9 && firstStart != double.MaxValue;
                if (_dbg)
                    _dbgGlyphs.Add(string.Format("i={0} span={1:F3} into={2:F3} rotType={3} rotdeg={4:F0} OX={5:F3} OY={6:F3} InGap={7} glyphAt={8:F3} nextLen={9:F3} emit={10}",
                        i, span, into, seg.RotType, seg.ShapeRot, seg.OX, seg.OY, seg.InGap, glyphAt, nextLen, _emit));
                if (_emit)
                {
                    double at = Math.Max(0.0, Math.Min(L, glyphAt));
                    PathPointAt(pts, cum, at, out double px, out double py, out double tx, out double ty);
                    EmitShapeGlyph(d, pts, cum, at, px, py, tx, ty, seg, fx, fy, pdf);
                }
            }
            else if (seg.Kind == LtpSeg.Text && textOut != null && !string.IsNullOrEmpty(seg.TextContent))
            {
                // 文字段：基线起点置于段跨度中点（参考 PDF 实测：3 个 "GAS" 的 Tm 原点全部落在
                // 文字段跨度中点 29.7532/65.9482/102.1432，误差 <0.003u；字符串沿切向【正向】延伸，
                // 而非以段中点为中心）。旋转=切向角+绝对旋转（数据系，CCW 正）。
                double textAt = pos + (span - into) - seg.Len / 2.0 - phi;
                // 端部规则（参考 PDF 实测）：文字段【整体】完整落在线内才输出——第 4 段
                // [134.528,142.148] 末越出 L=141.421 即被省略（其起点虽在线内、且该区域被
                // 端部填充画成实线）。比 shape 字形的"下一段也须完整"规则更松。
                if (textAt - seg.Len / 2.0 >= -1e-9 && textAt + seg.Len / 2.0 <= L + 1e-9)
                {
                    double at = Math.Max(0.0, Math.Min(L, textAt));
                    PathPointAt(pts, cum, at, out double cx, out double cy, out double tx, out double ty);
                    textOut.Add(BuildTextPlace(cx, cy, tx, ty, seg));
                }
            }
            pos += take;
            i++;
            into = 0;
        }
        // 端部空段填充（参考 AutoCAD 模型 PDF 流实测）：实体端落在 gap/文字段时，线端的不完整
        // 空段画成【实心】。'GAS_LINE' 实测：线端空段 [0,6.8932]∪[134.5282,141.4214] 均为实线
        // （流中仅 4 条显式线段、无 dash-array，排除虚线数组解释）。两端恰好落在实线段中时
        // firstStart=0/lastEnd=L，本步为无操作（RED/CYAN 线型不受影响）。
        if (lastEnd < 0.0)
            AppendSpanPath(d, pts, cum, 0.0, L, fx, fy, pdf);      // 全程无实线跨度（全 gap/文字）→ 整线实心
        else
        {
            if (firstStart > 1e-9) AppendSpanPath(d, pts, cum, 0.0, firstStart, fx, fy, pdf);
            if (L - lastEnd > 1e-9) AppendSpanPath(d, pts, cum, lastEnd, L, fx, fy, pdf);
        }
        if (_dbg)
        {
            System.Console.Error.WriteLine(string.Format("[LTPDBG] firstStart={0:F3} lastEnd={1:F3} headFill=[0,{2:F3}] tailFill=[{3:F3},{4:F3}]",
                firstStart, lastEnd, firstStart, lastEnd, L));
            foreach (var g in _dbgGlyphs)
                System.Console.Error.WriteLine("[LTPDBG] " + g);
        }
    }

    // 文字段放置（参考 AutoCAD 模型 PDF 实测，Tm 矩阵逐字反解）：
    //   · 基线【起点】（首字形基线原点）= 段跨度中点，字符串沿切向正向延伸（不居中回退）；
    //   · 垂直偏移 = 组码 45(OY)·图案比例（GAS：OY=−1.27→−1.905u，实测 −1.907±0.002u；
    //     负值=行进方向右侧），组码 44(OX) 参考实测【不施加】（存档备用）；
    //   · 字号 em = 组码 46·图案比例 ÷ capHeight 比（AutoCAD 把 46 当 cap 高：参考 'GAS'
    //     Tf=937.36 user-u = 5.3224 模型u = 1.3970×(46·σ=3.81u)，与 Arial upem/cap=2048/1467
    //     =1.3961 相差 0.06%，在 PDF 舍入误差内）。
    // 旋转 = 切向角 + 绝对旋转（数据系 CCW 正；水平线切向 0 → 文字水平）。
    private static LtpTextPlace BuildTextPlace(double cx, double cy, double tx, double ty, LtpSeg seg)
    {
        double h = seg.TextScale > 1e-9 ? seg.TextScale : 1.0;
        double tanAng = Math.Atan2(ty, tx) * 180.0 / Math.PI; // 数据系切向角（度）
        double rot = tanAng + seg.TextRot;
        double rad = rot * Math.PI / 180.0;
        double ca = Math.Cos(rad), sa = Math.Sin(rad);
        // 基线起点 = 段中点（cx,cy），无切向回退（参考：Tm 原点即段中点，字符串正向延伸）。
        double bx = cx, by = cy;
        // 垂直偏移：基线沿局部 -y（数据系 = (sa,−ca)，行进方向右侧）平移 −OY（OY 已含图案比例）。
        // 参考实测 GAS 基线在段中点右侧 1.905u（= −OY·σ）；旧 0.35h 视觉居中不符参考，废弃。
        double vShift = -seg.OY;
        bx += vShift * sa;
        by -= vShift * ca;
        // 字号：46 是 cap 高 → em = h×(upem/cap)。TTF 用字体自身指标（TtfCapScale）；
        // SHX/无指标字体回退参考实测值 1.397（见方法头注释）。
        double cap = TtfCapScale(seg.TextFont);
        if (cap <= 1.0) cap = 1.397;
        return new LtpTextPlace { X = bx, Y = by, RotDeg = rot, Height = h * cap, Text = seg.TextContent, Font = seg.TextFont, WidthFactor = 1.0 };
    }

    // 在路径点 (px,py) 绘制真实 SHX 字形（追加到 d）：字形包围盒中心对齐路径点，
    // X 轴沿切向（upright=0° / relative=切向 / absolute=指定角），缩放=ShapeScale。
    // SHX 库/形状不可用时回退为沿切向居中的"点"（短实线），保持图案节奏。
    private void EmitShapeGlyph(StringBuilder d, List<(double X, double Y)> pts, double[] cum, double at,
                                double px, double py, double tx, double ty, LtpSeg seg,
                                Func<double, double> fx, Func<double, double> fy, bool pdf)
    {
        GlyphRec? rec = null;
        if (!string.IsNullOrEmpty(seg.ShapeFile))
        {
            var font = GetShapeFont(seg.ShapeFile);
            if (font != null && font.TryGetShapeNumber(seg.ShapeName, out int num))
                rec = GetGlyph(font, seg.ShapeFile, num);
        }
        if (rec == null)
        {
            double dd = seg.Len > 1e-9 ? Math.Min(seg.ShapeScale * 4.0, 0.9 * seg.Len) : 0.1;
            if (dd < 0.05) dd = 0.05;
            AppendSpanPath(d, pts, cum, at - dd / 2, at + dd / 2, fx, fy, pdf);
            return;
        }
        // shape 缩放 = 组码46 标称缩放（已含 $LTSCALE 全局线型缩放，见 GetActiveLineType→pat.Scaled）。
        // 不额外硬编码 0.5——该缩放由图纸 header $LTSCALE 决定，链路已正确应用。
        double S = seg.ShapeScale > 1e-9 ? seg.ShapeScale : 1.0;
        double ang;
        switch (seg.RotType)
        {
            // relative（74 bit0=0）：字形角 = 路径切向角 + 组码50 偏移（Rotation）。
            // 旧实现漏加 ShapeRot → BATTING 等"180° 交替"线型两个 shape 同向（∩∩∩）画成锯齿，
            // 正确应 ∩(切向) 与 ∪(切向+180°) 交替成弹簧。
            case 1: ang = Math.Atan2(ty, tx) + seg.ShapeRot * Math.PI / 180.0; break;
            case 2: ang = seg.ShapeRot * Math.PI / 180.0; break; // absolute（74 bit0=1）
            default: ang = 0; break;                   // upright
        }
        double ca = Math.Cos(ang), sa = Math.Sin(ang);
        // offset（DXF 44/45）= 数据单位偏移：字形原点（插入点）= 路径点 + OX·切向 + OY·法向(-ty,tx)，
        // AutoCAD 语义（BATTING 参考实测：44=−2.54 的字形原点落在段末 glyphAt 之后 2.54 处、
        // 拱心恰在 7.195@+侧；44=+2.54 的 180° 字形拱心在 12.275@−侧）。
        // 旧实现切向分量取负（-44·切向）→ 原点沿路径错位 2×44=P/2，交替 ∩∪ 拱整体平移半周期，
        // 视觉上与 CAD 的螺旋方向相反（实为半周期位置差，非镜像）。法向分量保持 OY
        // （局部 +Y = 左法向；444/10422 的 45≠0 字形当前渲染正确，已验证）。不随字形旋转、不乘字形缩放 S。
        double oxw = seg.OX * tx - seg.OY * ty;   // 切向*OX + 法向*OY → 世界 x
        double oyw = seg.OX * ty + seg.OY * tx;   // → 世界 y
        // 【关键】SHX 字形按【原点(0,0)】锚定到"路径点+offset"（DXF 线型 shape 的插入点语义），
        // 不是按包围盒中心。BATTING 教训（Drawing1.dxf，CAD ground truth=弹簧状相切椭圆环）：
        // 若按包围盒中心锚定，BAT 字形弧心(1,2)=包围盒中心 → 弧心必然落在线型基线上，0°/180° 两个
        // 交替字形只能拼成"同心整圆→相邻圆相切"；而按原点锚定，弧心相对原点偏移 (CX,CY)·S，0° 与
        // 180° 交替使两弧分居基线上/下两侧 → 形成真正的螺旋弹簧（相邻环重叠、环内有对角弦），与参考一致。
        void Xf(float gx, float gy, out double mx, out double my)
        {
            double lx = gx * S;   // 原点锚定：字形 (0,0) → offset 点（不是包围盒中心）
            double ly = gy * S;
            double wx = px + oxw + lx * ca - ly * sa;
            double wy = py + oyw + lx * sa + ly * ca;
            mx = fx(wx); my = fy(wy);
        }
        double curX = 0, curY = 0; bool haveCur = false;
        for (int k = 0; k < rec.Type.Count; k++)
        {
            var p = rec.Pts[k];
            switch (rec.Type[k])
            {
                case 0:
                {
                    Xf(p[0], p[1], out double mx, out double my);
                    if (pdf) d.Append(F(mx)).Append(' ').Append(F(my)).Append(" m ");
                    else d.Append("M").Append(F(mx)).Append(',').Append(F(my));
                    curX = mx; curY = my; haveCur = true;
                    break;
                }
                case 1:
                {
                    Xf(p[0], p[1], out double mx, out double my);
                    if (pdf) d.Append(F(mx)).Append(' ').Append(F(my)).Append(" l ");
                    else d.Append("L").Append(F(mx)).Append(',').Append(F(my));
                    curX = mx; curY = my; haveCur = true;
                    break;
                }
                case 2:
                {
                    Xf(p[0], p[1], out double qx, out double qy);
                    Xf(p[2], p[3], out double ex, out double ey);
                    if (pdf)
                    {
                        // PDF 无二次贝塞尔 → 转三次：c1=p+2/3(c-p)、c2=e+2/3(c-e)
                        double p0x = haveCur ? curX : qx, p0y = haveCur ? curY : qy;
                        double c1x = p0x + 2.0 / 3.0 * (qx - p0x), c1y = p0y + 2.0 / 3.0 * (qy - p0y);
                        double c2x = ex + 2.0 / 3.0 * (qx - ex), c2y = ey + 2.0 / 3.0 * (qy - ey);
                        d.Append(F(c1x)).Append(' ').Append(F(c1y)).Append(' ').Append(F(c2x)).Append(' ')
                         .Append(F(c2y)).Append(' ').Append(F(ex)).Append(' ').Append(F(ey)).Append(" c ");
                    }
                    else d.Append("Q").Append(F(qx)).Append(',').Append(F(qy)).Append(' ').Append(F(ex)).Append(',').Append(F(ey));
                    curX = ex; curY = ey; haveCur = true;
                    break;
                }
                case 3:
                {
                    Xf(p[0], p[1], out double c1x, out double c1y);
                    Xf(p[2], p[3], out double c2x, out double c2y);
                    Xf(p[4], p[5], out double ex, out double ey);
                    if (pdf) d.Append(F(c1x)).Append(' ').Append(F(c1y)).Append(' ').Append(F(c2x)).Append(' ')
                        .Append(F(c2y)).Append(' ').Append(F(ex)).Append(' ').Append(F(ey)).Append(" c ");
                    else d.Append("C").Append(F(c1x)).Append(',').Append(F(c1y)).Append(' ')
                        .Append(F(c2x)).Append(',').Append(F(c2y)).Append(' ').Append(F(ex)).Append(',').Append(F(ey));
                    curX = ex; curY = ey; haveCur = true;
                    break;
                }
            }
        }
    }

    // 实体几何 → 折线点列（复杂线型走查用；返回 false = 无法折线化，退回 dasharray）。
    // 与参考实现 DxfImageExporter.TryGetPathPoints 一致（采样密度按 0.5 数据单位弧长）。
    // 注意：fork 中 Arc : Circle（netDxf 中两者为兄弟类），故 Arc 判断必须先于 Circle。
    private static bool TryGetPathPoints(Entity ent, Transform2D tr, out List<(double X, double Y)> pts)
    {
        pts = null;
        void Apply(double x, double y, out double rx, out double ry)
        {
            if (tr == null) { rx = x; ry = y; }
            else { var a = tr.Apply(x, y); rx = a.Item1; ry = a.Item2; }
        }
        if (ent is Line ln)
        {
            Apply(ln.StartPoint.X, ln.StartPoint.Y, out double x1, out double y1);
            Apply(ln.EndPoint.X, ln.EndPoint.Y, out double x2, out double y2);
            pts = new List<(double X, double Y)> { (x1, y1), (x2, y2) };
            return true;
        }
        if (ent is LwPolyline p2)
        {
            // 变宽填充折线回退（ConstantWidth = DWG 往返后的等宽形式，语义相同）
            if ((p2.Vertices.FirstOrDefault()?.StartWidth ?? 0) > 0 || p2.ConstantWidth > 0) return false;
            // 含 bulge 弧段：必须用 LwPolylineSamplePoints 采样（与主路径一致）。
            // 旧实现只取 v.Location（丢 Bulge）→ 带凸度的圆环/弧在复杂线型下被沿"控制点连线"
            // （顶点折线）走查 = 多绘制控制线（#63）。LwPolylineSamplePoints 已含闭合段（首尾回到 v0）。
            pts = LwPolylineSamplePoints(p2, tr);
            return pts.Count >= 2;
        }
        if (ent is Polyline3D p3)
        {
            var l3 = new List<(double X, double Y)>(p3.Vertices.Count);
            foreach (var v in p3.Vertices)
            {
                if (!IsDisplayVertex(v)) continue; // 跳过样条框架控制点（不显示）
                Apply(v.Location.X, v.Location.Y, out double x, out double y); l3.Add((x, y));
            }
            if (p3.IsClosed && l3.Count >= 3) l3.Add(l3[0]);
            pts = l3;
            return l3.Count >= 2;
        }
        if (ent is Arc a) // 必须先于 Circle（fork 继承关系）
        {
            Apply(a.Center.X, a.Center.Y, out double cx, out double cy);
            double r = a.Radius * (tr?.Scale ?? 1.0);
            if (r <= 0) return false;
            double s = a.StartAngle; // fork 弧度（参考实现度→弧度）
            double e = a.EndAngle;
            double span = e - s;
            while (span < 0) span += 2 * Math.PI;
            while (span >= 2 * Math.PI) span -= 2 * Math.PI;
            if (span < 1e-6) span = 2 * Math.PI;
            int m = (int)Math.Clamp(Math.Ceiling(r * span / 0.5), 32, 100000);
            var la = new List<(double X, double Y)>(m + 1);
            for (int i = 0; i <= m; i++)
            {
                double ang = s + span * i / m;
                la.Add((cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)));
            }
            pts = la;
            return true;
        }
        if (ent is Circle c)
        {
            Apply(c.Center.X, c.Center.Y, out double cx, out double cy);
            double r = c.Radius * (tr?.Scale ?? 1.0);
            if (r <= 0) return false;
            int m = (int)Math.Clamp(Math.Ceiling(2 * Math.PI * r / 0.5), 64, 100000);
            var lc = new List<(double X, double Y)>(m + 1);
            for (int i = 0; i <= m; i++)
            {
                double ang = 2 * Math.PI * i / m; // CAD 线型从 0°（3 点钟方向）逆时针起
                lc.Add((cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)));
            }
            pts = lc;
            return true;
        }
        if (ent is Ellipse el)
        {
            Apply(el.Center.X, el.Center.Y, out double cx, out double cy);
            // fork MajorAxis/MinorAxis 为主/短轴全长(2a/2b)，采样点用半轴 → 各除以 2（与 Png.cs L944 一致）
            double rx = el.MajorAxis / 2.0 * (tr?.Scale ?? 1.0), ry = el.MinorAxis / 2.0 * (tr?.Scale ?? 1.0);
            if (rx <= 0 || ry <= 0) return false;
            double rot = el.Rotation * 180.0 / Math.PI + (tr?.Rotation ?? 0.0); // fork 弧度 → 度（与参考实现单位一致）
            double ca = Math.Cos(rot), sa = Math.Sin(rot);
            int m = (int)Math.Clamp(Math.Ceiling(2 * Math.PI * Math.Max(rx, ry) / 0.5), 64, 100000);
            var le = new List<(double X, double Y)>(m + 1);
            for (int i = 0; i <= m; i++)
            {
                double th = 2 * Math.PI * i / m; // 从长轴端点起（与 CAD 图案起点一致）
                double ex = rx * Math.Cos(th), ey = ry * Math.Sin(th);
                le.Add((cx + ex * ca - ey * sa, cy + ex * sa + ey * ca));
            }
            pts = le;
            return true;
        }
        if (ent is Spline sp)
        {
            var sp2 = SplineSample(sp, tr, 16);
            if (sp2.Count < 2) return false;
            var ls = new List<(double X, double Y)>(sp2.Count);
            foreach (var p in sp2) ls.Add((p.X, p.Y));
            pts = ls;
            return true;
        }
        return false;
    }

    // 复杂线型（SVG）：沿路径走查模式生成单个 <path>（dash 子路径 + 字形子路径）。
    // 返回 false = 不支持/空（调用方回退旧 dasharray 逻辑）。
    private bool TryEmitComplexSvg(StringBuilder sb, Entity ent, Transform2D tr, LtpPattern pat, string col, double w)
    {
        if (pat == null) return false;
        if (!TryGetPathPoints(ent, tr, out var pts) || pts.Count < 2) return false;
        var cum = BuildCum(pts);
        if (cum[cum.Length - 1] <= 1e-9) return false;
        var d = new StringBuilder();
        var texts = new List<LtpTextPlace>();
        WalkPattern(d, pts, cum, pat, Sx, Sy, false, texts);
        if (d.Length == 0 && texts.Count == 0) return false;
        if (d.Length > 0)
            sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{col}\" stroke-width=\"{F(w)}\" stroke-linejoin=\"round\"/>");
        // 文字段：用原生 <text> 实心渲染（旋转传 -数据系角，因 SVG y 向下）
        foreach (var p in texts)
            EmitText(sb, p.X, p.Y, p.Text, p.Height, col, -p.RotDeg, SvgFontFamily(ResolveFontFamily(p.Font)), p.WidthFactor);
        return true;
    }

    // 复杂线型模式段（Dash/Gap/Shape/Text 四态；Shape/Text 段独立放置，永不合并）
    private class LtpSeg
    {
        public const int Dash = 0, Gap = 1, Shape = 2, Text = 3;
        public int Kind;
        public double Len;        // 段长（数据单位，正）
        public bool InGap;        // Shape：位于空隙内（间隙型）
        public double CenterOffset; // Shape/Text：字形中心相对段起点的偏移
        public string ShapeName;
        public string ShapeFile;
        public double ShapeScale;
        public int RotType;       // 0=upright 1=relative 2=absolute
        public double ShapeRot;   // 绝对旋转（度）
        public double OX, OY;     // 组码 44/45 偏移：Shape 段=字体单位；Text 段=数据单位（参考施加 OY 为基线垂直偏移，OX 不施加）
        public string TextContent; // Text：文字内容（组码 9）
        public double TextScale;   // Text：字高（组码 46，数据单位）
        public double TextRot;     // Text：绝对旋转（度，组码 50；0=沿切向）
        public string TextFont;    // Text：字体族名（Style 解析）
    }

    // 文字段放置（WalkPattern 收集，各引擎用原生文字 API 渲染）：
    // 基线左端 (X,Y) 数据坐标、数据系旋转角 RotDeg、字高 Height、内容/字体族/宽因子。
    private struct LtpTextPlace
    {
        public double X, Y;
        public double RotDeg;   // 数据系（CAD 约定，CCW 正）
        public double Height;   // 数据单位
        public string Text;
        public string Font;
        public double WidthFactor;
    }

    private class LtpPattern
    {
        public List<LtpSeg> Segs = new();
        public double Period;
        public LtpPattern Scaled(double s)
        {
            var p = new LtpPattern();
            foreach (var seg in this.Segs)
            {
                    var c = new LtpSeg
                    {
                        Kind = seg.Kind, Len = seg.Len * s, InGap = seg.InGap,
                        CenterOffset = seg.CenterOffset * s, ShapeName = seg.ShapeName,
                        ShapeFile = seg.ShapeFile, ShapeScale = seg.ShapeScale * s,
                        RotType = seg.RotType, ShapeRot = seg.ShapeRot, OX = seg.OX * s, OY = seg.OY * s,
                        TextContent = seg.TextContent, TextScale = seg.TextScale * s,
                        TextRot = seg.TextRot, TextFont = seg.TextFont,
                    };
                p.Segs.Add(c);
            }
            p.Period = this.Period * s;
            return p;
        }

        // 首个字形（Shape 段）的图案位置 = 累计跨度至该段末（含段长）；无 Shape 段返回 -1。
        // 供 PatternPhase 计算起始相位：参考把图案布局为实体两端关于首字形图案位置对称。
        public double FirstShapePos()
        {
            double c = 0;
            foreach (var s in Segs)
            {
                c += s.Len;
                if (s.Kind == LtpSeg.Shape) return c;
            }
            return -1;
        }

        // 首个文字（Text 段）的图案位置 = 累计跨度至该段【起点】；无 Text 段返回 -1。
        // 纯文本线型（无 Shape 段）供 PatternPhase 用作锚点：参考 PDF 实测同一"两端关于
        // 锚点对称"不变式成立，且锚点为首文字段起点（GAS：T=19.05，φ=29.3018 命中实测 29.297）。
        public double FirstTextPos()
        {
            double c = 0;
            foreach (var s in Segs)
            {
                if (s.Kind == LtpSeg.Text) return c;
                c += s.Len;
            }
            return -1;
        }

        // 相位锚点 T（线两端实线伸出关于该图案位置对称），按首特征段【属性】判定（Shape 优先于 Text）：
        //   正跨度（带实线）首字形段 → 段末（=字形位置；'444' T=4.5 / '10422' T=0.75 实测对称）；
        //   间隙型 InGap 首字形段（段内无实线，如 BATTING 弹簧点线）→ 段【起点】（=前一段实线末；
        //     AutoCAD 实测 BATTING 头尾伸出对称要求 T=0.00254=点线末=InGap 段起点，旧取段末 2.54254
        //     致头 3.383/尾 5.923 不对称）；
        //   无 Shape 段 → 首文字段起点（GAS T=19.05@σ1.5 实测命中）。
        // 注意：返回【未缩放】锚点——调用方先 pat.Scaled(σ) 再取，与 FirstShapePos/FirstTextPos 一致。
        public double PhaseAnchor()
        {
            double c = 0;
            foreach (var s in Segs)
            {
                if (s.Kind == LtpSeg.Shape) return s.InGap ? c : c + s.Len;
                c += s.Len;
            }
            c = 0;
            foreach (var s in Segs)
            {
                if (s.Kind == LtpSeg.Text) return c;
                c += s.Len;
            }
            return -1;
        }
    }

    // 记录 SHX 字形的笔路径（M/L/Q/C 指令 + 包围盒，SHX 字体单位；基线 y=0）
    private sealed class GlyphRec : IGlyphSink
    {
        public readonly List<int> Type = new();     // 0=M 1=L 2=Q 3=C
        public readonly List<float[]> Pts = new();
        public float MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue;
        public bool Empty { get { return Pts.Count == 0; } }
        public float CX { get { return (MinX + MaxX) / 2; } }
        public float CY { get { return (MinY + MaxY) / 2; } }
        private void Pt(float x, float y)
        {
            if (x < MinX) MinX = x; if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y; if (y > MaxY) MaxY = y;
        }
        public void MoveTo(float x, float y) { Type.Add(0); Pts.Add(new[] { x, y }); Pt(x, y); }
        public void LineTo(float x, float y) { Type.Add(1); Pts.Add(new[] { x, y }); Pt(x, y); }
        public void QuadTo(float cx, float cy, float x, float y) { Type.Add(2); Pts.Add(new[] { cx, cy, x, y }); Pt(cx, cy); Pt(x, y); }
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) { Type.Add(3); Pts.Add(new[] { c1x, c1y, c2x, c2y, x, y }); Pt(c1x, c1y); Pt(c2x, c2y); Pt(x, y); }
        public void Close() { } // SHX 笔路径为开放路径，无闭合轮廓
    }

    #endregion

    #region ===== 坐标映射与范围 =====

    // SVG：y 向下坐标系（原点在左上角），需 Y 翻转
    private double Sx(double x) => _margin + (x - _minX) * _scale;
    private double Sy(double y) => _h - (_margin + (y - _minY) * _scale); // Y 翻转
    // PDF：y 向上坐标系（原点在左下角），与 CAD 同向，不翻转
    private double Py(double y) => _margin + (y - _minY) * _scale;

    // XLine/Ray 裁剪：把无限线（XLine）/射线（Ray）在"画布矩形"（数据坐标，含边距）内取可见段。
    // 入参均为世界系（调用点先用 tr 把起点/方向转到世界系）：p0 起点、(dx,dy) 方向。
    // 参数 t 与画布边界求交 → t∈[tmin,tmax]。XLine：t∈[-∞,+∞]；Ray：t∈[0,+∞]（仅起点之后）。
    // 返回世界系两端点；与画布不相交返回 false（不发射）。
    private bool ClipInfiniteLineToCanvas(double p0x, double p0y, double dx, double dy, bool isXLine,
        out double x1, out double y1, out double x2, out double y2)
    {
        x1 = y1 = x2 = y2 = 0;
        double cxmin = _minX - _margin, cxmax = _maxX + _margin;
        double cymin = _minY - _margin, cymax = _maxY + _margin;
        double tmin = isXLine ? double.NegativeInfinity : 0.0;
        double tmax = double.PositiveInfinity;
        bool ok = true;
        // x 边界
        if (Math.Abs(dx) > 1e-12)
        {
            double ta = (cxmin - p0x) / dx, tb = (cxmax - p0x) / dx;
            if (ta > tb) { var tmp = ta; ta = tb; tb = tmp; }
            if (ta > tmin) tmin = ta;
            if (tb < tmax) tmax = tb;
        }
        else if (p0x < cxmin || p0x > cxmax) { ok = false; } // 垂直线但起点在画布 x 外
        // y 边界
        if (ok && Math.Abs(dy) > 1e-12)
        {
            double ta = (cymin - p0y) / dy, tb = (cymax - p0y) / dy;
            if (ta > tb) { var tmp = ta; ta = tb; tb = tmp; }
            if (ta > tmin) tmin = ta;
            if (tb < tmax) tmax = tb;
        }
        else if (p0y < cymin || p0y > cymax) { ok = false; } // 水平线但起点在画布 y 外
        if (!ok || !(tmax > tmin)) return false;
        x1 = p0x + dx * tmin; y1 = p0y + dy * tmin;
        x2 = p0x + dx * tmax; y2 = p0y + dy * tmax;
        return true;
    }

    // XLine/Ray 的方向向量经块变换（只旋转+缩放、不平移）：取原点与原点+方向两点之差。
    private static (double dx, double dy) TransformDirection(double dx, double dy, Transform2D tr)
    {
        if (tr == null) return (dx, dy);
        var a = tr.Apply(0, 0);
        var b = tr.Apply(dx, dy);
        return (b.Item1 - a.Item1, b.Item2 - a.Item2);
    }

    // MLine 单个子线元素（一个 element 在世界系的一条子线）
    private readonly struct MLineElement
    {
        public readonly List<(double X, double Y)> Center; // 中心线（offset 已沿 miter/法向偏移，世界系）
        public readonly double[] Width;                    // 逐顶点线宽（世界单位，可 0 或渐变）
        public readonly short Aci;
        public MLineElement(List<(double X, double Y)> center, double[] width, short aci)
        { Center = center; Width = width; Aci = aci; }
    }

    // MLine 渲染几何（三引擎共享）：按每个 vertex.Segments[e].Parameters 把各子线展开到世界系。
    // Parameters 语义（DXF MLINE）：2 参数 [offset, width]（固定宽），4 参数 [off0,w0,off1,w1]
    // （沿段渐变）；offset 为子线到中心线的法向偏移、width 为子线线宽，均为**绝对世界单位**
    // （已含 MLine ScaleFactor，ACadSharp ApplyTransform 已对块内 MLine 的 Parameters 按块缩放缩放）。
    // 拐角 miter 用 AutoCAD 预计算的 vertex.Miter 向量（角平分方向，尖接对齐 AutoCAD）；
    // 端点沿该端唯一线段法向偏移。样式无定义时退化为画中心线。
    private static List<MLineElement> MLineElements(MLine ml, Transform2D tr)
    {
        var result = new List<MLineElement>();
        if (ml?.Vertices == null) return result;
        int n = ml.Vertices.Count;
        if (n < 2) return result;
        // 顶点世界化（Position 经 tr；块内 MLine 的 Position/Segments 已被 ACadSharp ApplyTransform 世界化）
        var wp = new (double X, double Y)[n];
        for (int i = 0; i < n; i++)
        {
            var v = ml.Vertices[i];
            wp[i] = tr == null ? (v.Position.X, v.Position.Y) : tr.Apply(v.Position.X, v.Position.Y);
        }
        // 各段单位方向与法向（段 k：V_k→V_{k+1}），法向取左法向（与 offset 正方向一致）
        var d = new (double X, double Y)[n - 1];
        var nm = new (double X, double Y)[n - 1];
        for (int k = 0; k < n - 1; k++)
        {
            double dx = wp[k + 1].X - wp[k].X, dy = wp[k + 1].Y - wp[k].Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-12) { d[k] = (0, 0); nm[k] = (0, 0); }
            else { d[k] = (dx / len, dy / len); nm[k] = (-dy / len, dx / len); }
        }
        // miter 方向世界化：块插入带旋转时同步旋转（tr 旋转部分 Ca/Sa，不含缩放/平移）
        double ca = tr?.Ca ?? 1.0, sa = tr?.Sa ?? 0.0;
        (double X, double Y) WorldMiter(XYZ m)
        {
            double mx = m.X, my = m.Y;
            if (tr != null) { double rx = mx * ca - my * sa, ry = mx * sa + my * ca; mx = rx; my = ry; }
            double l = Math.Sqrt(mx * mx + my * my);
            return l < 1e-12 ? (0, 0) : (mx / l, my / l);
        }
        // element 数 = 各 vertex Segments 最大数（无定义 → 画中心线）
        int eMax = 0;
        for (int i = 0; i < n; i++) eMax = Math.Max(eMax, ml.Vertices[i].Segments.Count);
        var styleElems = ml.Style?.Elements?.ToList() ?? new List<ACadSharp.Objects.MLineStyle.Element>();
        for (int e = 0; e < eMax; e++)
        {
            var c = new List<(double X, double Y)>(n);
            var w = new double[n];
            for (int i = 0; i < n; i++)
            {
                var seg = ml.Vertices[i].Segments.Count > e ? ml.Vertices[i].Segments[e] : null;
                double off = (seg != null && seg.Parameters.Count > 0) ? seg.Parameters[0] : 0;
                w[i] = (seg != null && seg.Parameters.Count > 1) ? seg.Parameters[1] : 0;
                // 偏移方向：中间点用 AutoCAD 预计算的 vertex.Miter（角平分，世界化）；端点用该端段法向
                (double X, double Y) dir;
                if (i == 0) dir = nm[0];
                else if (i == n - 1) dir = nm[n - 2];
                else { dir = WorldMiter(ml.Vertices[i].Miter); if (dir.X == 0 && dir.Y == 0) dir = nm[i - 1]; }
                c.Add((wp[i].X + dir.X * off, wp[i].Y + dir.Y * off));
            }
            // 颜色：MLineStyle.Elements[e].Color（BYLAYER 256 由调用方 ColorOf 解析），缺省实体色
            short aci = EntityAciIndex(ml);
            if (e < styleElems.Count)
            {
                var col = styleElems[e].Color;
                try { aci = col.IsTrueColor ? TrueColorToAci(col.R, col.G, col.B) : col.Index; } catch { }
            }
            result.Add(new MLineElement(c, w, aci));
        }
        if (result.Count == 0)
        {
            // 无子线定义 → 画中心线（无偏移、宽度 0）
            result.Add(new MLineElement(wp.ToList(), new double[n], EntityAciIndex(ml)));
        }
        return result;
    }

    // MLine 按正式定义（DXF 41 参数化）提取各子线的实际画线段。
    // Parameters 语义（DXF MLINE 规范 + cadcodec 参考）：
    //   p[0] = offset（子线法向偏移，世界单位，已含 ScaleFactor）
    //   p[1] = width（子线线宽）
    //   p[2..] = 成对 (start, end) 累计位置（沿路径从 MLine 起点 V0 起的距离），
    //            定义画线区间。2 参数 → 画全段；4+ 参数 → 只画指定子段。
    // 返回：每个 element 的画线段列表（每段 = (X1, Y1, X2, Y2) 扁平 4 元组）。
    private readonly struct MLineDrawnElement
    {
        public readonly short Aci;
        public readonly List<(double X1, double Y1, double X2, double Y2)> Segs;
        public MLineDrawnElement(short aci, List<(double X1, double Y1, double X2, double Y2)> segs)
        { Aci = aci; Segs = segs; }
    }

    private static List<MLineDrawnElement> MLineDrawnElements(MLine ml, Transform2D tr)
    {
        var result = new List<MLineDrawnElement>();
        if (ml?.Vertices == null) return result;
        int n = ml.Vertices.Count;
        if (n < 2) return result;

        // 顶点世界化
        var wp = new (double X, double Y)[n];
        for (int i = 0; i < n; i++)
        {
            var v = ml.Vertices[i];
            wp[i] = tr == null ? (v.Position.X, v.Position.Y) : tr.Apply(v.Position.X, v.Position.Y);
        }
        // 各段单位法向（段 k：V_k→V_{k+1}）
        var nm = new (double X, double Y)[n - 1];
        double[] segLen = new double[n - 1];
        for (int k = 0; k < n - 1; k++)
        {
            double dx = wp[k + 1].X - wp[k].X, dy = wp[k + 1].Y - wp[k].Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            segLen[k] = len;
            if (len < 1e-12) nm[k] = (0, 0);
            else nm[k] = (-dy / len, dx / len);
        }
        // miter 世界化
        double ca = tr?.Ca ?? 1.0, sa = tr?.Sa ?? 0.0;
        (double X, double Y) WorldMiter(XYZ m)
        {
            double mx = m.X, my = m.Y;
            if (tr != null) { double rx = mx * ca - my * sa, ry = mx * sa + my * ca; mx = rx; my = ry; }
            double l = Math.Sqrt(mx * mx + my * my);
            return l < 1e-12 ? (0, 0) : (mx / l, my / l);
        }
        // 累计距离（从 V0 起）
        double[] cumDist = new double[n];
        cumDist[0] = 0;
        for (int k = 0; k < n - 1; k++) cumDist[k + 1] = cumDist[k] + segLen[k];
        double totalLen = cumDist[n - 1];
        if (totalLen < 1e-12) return result;

        // 沿偏移中心线取点：给定累计距离 t，返回世界坐标。
        // t 可越界：t<0 沿首段方向外延、t>totalLen 沿末段方向外延（用于端帽外延 p[2]）。
        (double X, double Y) PointAt((double X, double Y)[] center, double t)
        {
            if (t <= 0)
            {
                // 起点外延：沿首段方向（center[0]->center[1]）反向外推 -t
                if (n < 2) return center[0];
                double sl = segLen[0];
                if (sl < 1e-12) return center[0];
                double f = -t / sl;
                return (center[0].X + (center[1].X - center[0].X) * f,
                        center[0].Y + (center[1].Y - center[0].Y) * f);
            }
            if (t >= totalLen)
            {
                // 终点外延：沿末段方向（center[n-2]->center[n-1]）外推 (t-totalLen)
                if (n < 2) return center[n - 1];
                double sl = segLen[n - 2];
                if (sl < 1e-12) return center[n - 1];
                double f = 1.0 + (t - totalLen) / sl;
                return (center[n - 2].X + (center[n - 1].X - center[n - 2].X) * f,
                        center[n - 2].Y + (center[n - 1].Y - center[n - 2].Y) * f);
            }
            // 找所在段
            int k = n - 2;
            for (int i = 1; i < n; i++) { if (t <= cumDist[i]) { k = i - 1; break; } }
            double local = t - cumDist[k];
            double frac = segLen[k] > 1e-12 ? local / segLen[k] : 0;
            return (center[k].X + (center[k + 1].X - center[k].X) * frac,
                    center[k].Y + (center[k + 1].Y - center[k].Y) * frac);
        }

        // 端帽语义（AutoCAD 实测校准：test/sample-mline.pdf 矢量 ground truth，
        // 经 PDF 仿射反解到数据坐标系逐点比对，残差 < 0.06，见 Tools/mline_pdf_verify.py）：
        //   DXF 71 组码 NoStartCaps(bit4)/NoEndCaps(bit8) 抑制的是 MLine 端的【横向端帽描边】
        //   （闭合封口线段），与 41 组码 p[1] 的端点缩短（缩进）【无关】——p[1] 始终生效。
        //   实测：chevron（71=5 含 NoStartCaps）子线仍从加帽点画起（短缺 12.13/13.29）；
        //         连接线（71=13 含 NoStartCaps|NoEndCaps）起点同样加帽（短缺 20.0）。
        //   历史教训：67c2aba 曾按 71 标志抑制 p[1]，导致交汇点 chevron 越界 12~13 单位、
        //   "缩进"丢失（本提交回退该做法）。

        // element 数
        int eMax = 0;
        for (int i = 0; i < n; i++) eMax = Math.Max(eMax, ml.Vertices[i].Segments.Count);
        var styleElems = ml.Style?.Elements?.ToList() ?? new List<ACadSharp.Objects.MLineStyle.Element>();

        for (int e = 0; e < eMax; e++)
        {
            // 计算偏移中心线
            var c = new (double X, double Y)[n];
            for (int i = 0; i < n; i++)
            {
                var seg = ml.Vertices[i].Segments.Count > e ? ml.Vertices[i].Segments[e] : null;
                double off = (seg != null && seg.Parameters.Count > 0) ? seg.Parameters[0] : 0;
                (double X, double Y) dir;
                if (i == 0) dir = nm[0];
                else if (i == n - 1) dir = nm[n - 2];
                else { dir = WorldMiter(ml.Vertices[i].Miter); if (dir.X == 0 && dir.Y == 0) dir = nm[i - 1]; }
                c[i] = (wp[i].X + dir.X * off, wp[i].Y + dir.Y * off);
            }

            // 提取画线段：按 DXF 41 正式定义（AutoCAD DXF Reference）+ AutoCAD 实测。
            // 官方 41 定义："第 3 个值=从子线起点到第一个 break/cut 的距离；后续值继续列出 start/stop"。
            // 结合实测，41 列表按【参数个数 count】分两种语义（成对配对模型）：
            //   count==2 : [offset, cap]                       → 仅偏移 + 端帽内缩
            //   count==3 : [offset, cap, ext]                  → p[2] 单独（无配对）= 画线终点
            //             **AutoCAD 实测**：MLine#2 末端 V1 的 p[2]=9.5334/22.8132，子线从顶点偏移点
            //             沿 MLine 方向延长该距离（965.49,401.37→974.58,404.24 / 970.02,387.07→991.77,393.95），
            //             即末端顶点【外延】（ext>0 越过顶点），使相连多线在交汇点相接。
            //             起点 V0 的 p[2] 同理（反方向外延）。
            //   count>=4 : [offset, cap, b1s, b1e, b2s, b2e…]  → p[2..] 成对 (start,end) = 断口 gap（不画）
            //             相对段起点 V0（segStart）绝对定位。如 3A8 V0 element1 [−20,0,32.5,47.5]
            //             → 段中间 [32.5,47.5] 处断开 15 单位的口。
            //   p[0] = offset（沿 miter 的偏移，已用于定位中心线 c）
            //   p[1] = 端帽长度（内缩 cap）：段起点 V_k 的 p[1]=头部 startCap；段终点 V_{k+1} 的 p[1]=尾端 endCap。
            //          始终生效；71 组码 NoStartCaps/NoEndCaps 只抑制横向端帽描边，不影响此缩短量（见上方校准注释）。
            // 画线范围 = [segStart + startCap − startExt, segEnd − endCap + endExt]，再减去断口 gap 即得画线段。
            // 首尾世界点沿【段方向】从偏移点精确计算（pDrawStart/pDrawEnd），断口边界按距离域取点。
            var segs = new List<(double X1, double Y1, double X2, double Y2)>();
            for (int k = 0; k < n - 1; k++)
            {
                double segStart = cumDist[k];
                double segEnd = cumDist[k + 1];
                if (segEnd - segStart < 1e-12) continue;
                var startSeg = ml.Vertices[k].Segments.Count > e ? ml.Vertices[k].Segments[e] : null;
                var endSeg = ml.Vertices[k + 1].Segments.Count > e ? ml.Vertices[k + 1].Segments[e] : null;
                var pars = startSeg?.Parameters;

                // 头部/尾端：
                //   p[1] = 内缩（端帽 cap），始终生效（71 组码标志只抑制横向端帽描边，不影响缩短量）。
                //   p[2] = count==3 时【单独】出现 → 末端顶点外延（延长）；count>=4 时为断口起点（见下）。
                //          外延仅作用于 MLine 端点顶点（起点 V0 / 终点 V_{n-1}），使子线越过顶点延长，
                //          用于相连多线在交汇点相接。
                bool isMlineStart = (k == 0);
                bool isMlineEnd = (k + 1 == n - 1);
                // p[1] = 端帽内缩（缩进），始终生效（71 组码标志只抑制横向端帽描边，不影响缩短量）
                double startCap = 0;
                if (startSeg != null && startSeg.Parameters.Count >= 2)
                    startCap = startSeg.Parameters[1];
                double endCap = 0;
                if (endSeg != null && endSeg.Parameters.Count >= 2)
                    endCap = endSeg.Parameters[1];
                // 外延：仅当 p[2] 【单独】出现（count==3，无配对 p[3]）时，
                // 它是"从子线起点到第一个断口的距离"且断口开放（无终点）→ 表现为末端顶点外延：
                //   起点段(V0) p[2] 向后延、终点段(V_{n-1}) p[2] 向前延（AutoCAD 实测 MLine#2 确认）。
                // count>=4 时 p[2] 是断口【起点】（有配对 p[3]），不作外延（见下方断口逻辑）。
                double startExt = 0;
                if (isMlineStart && startSeg != null && startSeg.Parameters.Count == 3)
                    startExt = startSeg.Parameters[2];
                double endExt = 0;
                if (isMlineEnd && endSeg != null && endSeg.Parameters.Count == 3)
                    endExt = endSeg.Parameters[2];
                double drawStart = segStart + startCap - startExt; // 画线起点（距离域，头部）
                double drawEnd = segEnd - endCap + endExt;        // 画线终点（距离域，尾端）
                if (drawEnd < drawStart) drawEnd = drawStart;
                // 端帽/外延的精确世界点：沿【段方向】从偏移点 c[k]/c[k+1] 取。
                // 不能按偏移折线弧长参数化（PointAt）——偏移量沿线变化时（如 chevron e1：-20→-24.51），
                // 弧长参数化与段方向法偏差可达 3.7 单位；AutoCAD 实测端帽点 = 偏移点 + cap·段方向。
                double ux = segLen[k] > 1e-12 ? (wp[k + 1].X - wp[k].X) / segLen[k] : 0.0;
                double uy = segLen[k] > 1e-12 ? (wp[k + 1].Y - wp[k].Y) / segLen[k] : 0.0;
                (double X, double Y) pDrawStart = (c[k].X + ux * (startCap - startExt), c[k].Y + uy * (startCap - startExt));
                (double X, double Y) pDrawEnd = (c[k + 1].X - ux * (endCap - endExt), c[k + 1].Y - uy * (endCap - endExt));

                // 中间断口：p[2..] 成对 (start, end)，相对段起点 V0（segStart）绝对定位，夹到画线范围。
                //   count>=4 时 [p[2],p[3]] 为断口 gap；count>=6 时 [p[2],p[3]],[p[4],p[5]] 多断口。
                //   count==3 时 p[2] 为外延终点（上方已处理），不进入断口。
                var breaks = new List<(double a, double b)>();
                if (pars != null && pars.Count >= 4)
                {
                    int pc = pars.Count;
                    for (int pi = 2; pi + 1 < pc; pi += 2)
                    {
                        double ba = segStart + pars[pi];
                        double bb = segStart + pars[pi + 1];
                        if (ba < drawStart) ba = drawStart;
                        if (ba > drawEnd) ba = drawEnd;
                        if (bb < drawStart) bb = drawStart;
                        if (bb > drawEnd) bb = drawEnd;
                        if (bb - ba > 1e-9) breaks.Add((ba, bb));
                    }
                }

                // 在 [drawStart, drawEnd] 内按断口切出画线段（断口之外画）
                double cur = drawStart;
                foreach (var br in breaks)
                {
                    if (br.a > cur + 1e-9)
                    {
                        // 首画线段起点用精确端帽点 pDrawStart；断口边界仍按距离域取点
                        var p1 = PointAt(c, cur);
                        if (Math.Abs(cur - drawStart) < 1e-9) p1 = pDrawStart;
                        var p2 = PointAt(c, br.a);
                        segs.Add((p1.X, p1.Y, p2.X, p2.Y));
                    }
                    if (br.b > cur) cur = br.b;
                }
                if (drawEnd - cur > 1e-9)
                {
                    var p1 = PointAt(c, cur);
                    if (Math.Abs(cur - drawStart) < 1e-9) p1 = pDrawStart;
                    var p2 = pDrawEnd; // 尾端用精确端帽/外延点
                    segs.Add((p1.X, p1.Y, p2.X, p2.Y));
                }
            }

            // 颜色
            short aci = EntityAciIndex(ml);
            if (e < styleElems.Count)
            {
                var col = styleElems[e].Color;
                try { aci = col.IsTrueColor ? TrueColorToAci(col.R, col.G, col.B) : col.Index; } catch { }
            }
            result.Add(new MLineDrawnElement(aci, segs));
        }

        if (result.Count == 0)
        {
            // 无子线定义 → 画中心线全段
            var segs = new List<(double X1, double Y1, double X2, double Y2)>();
            for (int k = 0; k < n - 1; k++) segs.Add((wp[k].X, wp[k].Y, wp[k + 1].X, wp[k + 1].Y));
            result.Add(new MLineDrawnElement(EntityAciIndex(ml), segs));
        }
        return result;
    }

    // PolyfaceMesh 线框（三引擎共享）：旧式 3D 多面体网格。按 DXF POLYFACE_MESH 面引用语义：
    //   • Index1-4 为 1-based 顶点引用，0 = 面结束（其后引用忽略）；
    //   • 负引用 = 该顶点仍参与成面，但【该顶点到下一顶点】的边不显示（抑制出边）；
    //   • 面是闭合多边形（末点回首点），三角 3 边、四边形 4 边；
    //   • 全局按无向边去重（相邻面共享边只画一次），避免双线/重叠。
    // 顶点（VertexFaceMesh.Location）取 X/Y（丢弃 Z，投影到当前显示平面）经 tr 世界化，
    // 返回世界系线段列表 (a, b)。
    private static List<((double AX, double AY), (double BX, double BY))> PolyfaceMeshWire(PolyfaceMesh pf, Transform2D tr)
    {
        var segs = new List<((double AX, double AY), (double BX, double BY))>();
        var verts = pf?.Vertices;
        var faces = pf?.Faces;
        if (verts == null || verts.Count == 0 || faces == null) return segs;
        // 顶点投影到显示平面（X/Y，Z 丢弃）并世界化
        var wp = new (double X, double Y)[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            var p = tr == null ? (verts[i].Location.X, verts[i].Location.Y) : tr.Apply(verts[i].Location.X, verts[i].Location.Y);
            wp[i] = p;
        }
        // 全局无向边去重（key = 较小索引 <<32 | 较大索引）
        var drawn = new HashSet<long>();
        foreach (var f in faces)
        {
            short[] idx = { f.Index1, f.Index2, f.Index3, f.Index4 };
            var fverts = new List<int>();   // 0-based 顶点索引（按面内顺序）
            var sup = new List<bool>();     // 该顶点出边（指向下一顶点，末点回首点）是否被抑制
            foreach (var r in idx)
            {
                if (r == 0) break;                                  // 0 = 面结束
                int vi = (int)(r < 0 ? -r : r) - 1;                 // 1-based → 0-based
                if (vi < 0 || vi >= wp.Length) continue;            // 越界引用 → 跳过该引用
                fverts.Add(vi);
                sup.Add(r < 0);                                     // 负引用 = 抑制出边
            }
            int n = fverts.Count;
            if (n < 3) continue;                                    // 少于 3 顶点不成面
            for (int i = 0; i < n; i++)
            {
                if (sup[i]) continue;                               // 该出边被抑制
                int a = fverts[i], b = fverts[(i + 1) % n];         // 闭合：末点回首点
                if (a == b) continue;
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!drawn.Add(key)) continue;                      // 共享边去重
                segs.Add(((wp[a].X, wp[a].Y), (wp[b].X, wp[b].Y)));
            }
        }
        return segs;
    }

    // PolygonMesh 线框（三引擎共享）：M×N 多边形网格。
    // 【节点投影到显示坐标面】所有网格节点只取 Location.X/Y（丢弃 Z 高度）再经 tr 映射，
    // 使带 Z 起伏的 3D 网格得到平整的平面栅格（而不是 3D 斜视/顺序折线锯齿）。
    // 网格索引优先级：
    //   1) 控制点网格：MVertexCount × NVertexCount == 顶点数（无样条拟合点）；
    //   2) 拟合点网格：MSmoothSurfaceDensity × NSmoothSurfaceDensity == 拟合点数
    //      （样条拟合后的真实显示网格；行优先 M 外层、N 内层，index = iM*N + iN）；
    //   3) 退化：按顺序顶点折线。
    // 返回世界系线段列表（顶点已投影并 tr 世界化）。
    private static List<((double AX, double AY), (double BX, double BY))> PolygonMeshWire(PolygonMesh pm, Transform2D tr)
    {
        var segs = new List<((double AX, double AY), (double BX, double BY))>();
        var verts = pm?.Vertices;
        if (verts == null || verts.Count == 0) return segs;

        // 节点投影到当前显示坐标面：只取 X/Y（Z 丢弃）
        var wp = new (double X, double Y)[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            var l = verts[i].Location;
            var p = tr == null ? (l.X, l.Y) : tr.Apply(l.X, l.Y);
            wp[i] = p;
        }

        // 按 rows×cols（行优先，index = r*cols + c）画行线 + 列线
        void AddGrid(int rows, int cols, IReadOnlyList<int> idx)
        {
            if (rows < 2 || cols < 2 || idx.Count < rows * cols) return;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c + 1 < cols; c++)
                { int i1 = idx[r * cols + c], i2 = idx[r * cols + c + 1]; segs.Add(((wp[i1].X, wp[i1].Y), (wp[i2].X, wp[i2].Y))); }
            for (int r = 0; r + 1 < rows; r++)
                for (int c = 0; c < cols; c++)
                { int i1 = idx[r * cols + c], i2 = idx[(r + 1) * cols + c]; segs.Add(((wp[i1].X, wp[i1].Y), (wp[i2].X, wp[i2].Y))); }
        }

        int total = verts.Count;
        int m = pm.MVertexCount, n = pm.NVertexCount;
        int mD = pm.MSmoothSurfaceDensity, nD = pm.NSmoothSurfaceDensity;

        // 1) 控制点网格（无拟合点）
        if (m > 1 && n > 1 && m * n == total)
        {
            var all = new int[total];
            for (int i = 0; i < total; i++) all[i] = i;
            AddGrid(m, n, all);
            return segs;
        }

        // 2) 拟合点网格（样条拟合后的真实显示网格）
        if (mD > 1 && nD > 1)
        {
            if (total == mD * nD)
            {
                var all = new int[total];
                for (int i = 0; i < total; i++) all[i] = i;
                AddGrid(mD, nD, all);
                return segs;
            }
            // 混合（控制点 + 拟合点）：只取拟合点，按文件顺序（行优先 M 外层、N 内层）
            var fit = new List<int>();
            for (int i = 0; i < total; i++)
                if ((verts[i].Flags & VertexFlags.SplineVertexFromSplineFitting) != 0) fit.Add(i);
            if (fit.Count == mD * nD)
            {
                AddGrid(mD, nD, fit);
                return segs;
            }
        }

        // 3) 退化：按顺序顶点折线
        for (int i = 0; i + 1 < total; i++)
            segs.Add(((wp[i].X, wp[i].Y), (wp[i + 1].X, wp[i + 1].Y)));
        return segs;
    }

    // Wipeout 世界坐标多边形（三引擎共享）：擦除多边形。
    // 有 310 世界 blob → 直接采用（权威世界几何）。
    // 无 blob（DWG 不存该 blob）或损坏 → 用局部顶点重建：
    //   world = InsertPoint + (x+0.5)·UVector + (0.5−y)·VVector
    // 局部顶点是图像归一化坐标（原点在图像中心、y 向下），不是“相对 insert 的每像素偏移”：
    // 标准式 origin+x·U+y·V 会 y 镜像错位（三个样本 310 逐点验证：ALT 式 d=0.000，STD 式偏移 51–123 单位）。
    // tr 非空时 InsertPoint 整体世界化、U/V 只取线性部分（旋转+缩放、不平移）。
    // 顶点 <2 或全退化返回空列表。
    private static List<(double X, double Y)> WipeoutWorldPoly(Wipeout wo, Transform2D tr)
    {
        var pts = new List<(double X, double Y)>();
        if (wo == null) return pts;

        // [PATCH] 优先消费 DXF 92/310 世界裁剪预览 blob（TryDecodeWorldClip 校验
        // int32[0]==总长、int32[4]==顶点数）：它就是权威世界几何。部分文件的 14/24 局部顶点
        // 是该 blob 的镜像（WO1 同边长 Y 镜像、WO2 鞋带符号相反），只用局部顶点会把擦除形状
        // 画成错位/翻转的。blob 缺失（DWG 不存该 blob）或损坏时，用图像归一化坐标式重建：
        // world = origin + (x+0.5)·U + (0.5−y)·V（见方法头注释）。
        if (wo.WorldClipPreview != null && CadWipeoutBase.TryDecodeWorldClip(wo.WorldClipPreview, out var wv))
        {
            foreach (var v in wv)
            {
                if (tr == null)
                    pts.Add((v.X, v.Y));
                else
                {
                    var p = tr.Apply(v.X, v.Y);
                    pts.Add((p.Item1, p.Item2));
                }
            }
            return pts;
        }

        var clip = wo.ClipBoundaryVertices;
        if (clip == null || clip.Count < 2) return pts;
        double ix, iy, ux, uy, vx, vy;
        if (tr == null)
        {
            ix = wo.InsertPoint.X; iy = wo.InsertPoint.Y;
            ux = wo.UVector.X; uy = wo.UVector.Y;
            vx = wo.VVector.X; vy = wo.VVector.Y;
        }
        else
        {
            var i = tr.Apply(wo.InsertPoint.X, wo.InsertPoint.Y);
            ix = i.Item1; iy = i.Item2;
            (ux, uy) = TransformDirection(wo.UVector.X, wo.UVector.Y, tr);
            (vx, vy) = TransformDirection(wo.VVector.X, wo.VVector.Y, tr);
        }
        foreach (var c in clip)
        {
            // 局部顶点 = 图像归一化坐标（居中、y 向下）→ world = insert + (x+0.5)·U + (0.5−y)·V
            double wx = ix + (c.X + 0.5) * ux + (0.5 - c.Y) * vx;
            double wy = iy + (c.X + 0.5) * uy + (0.5 - c.Y) * vy;
            pts.Add((wx, wy));
        }
        return pts;
    }

    // ===== Wipeout 属性渲染（三引擎共享，参考 DXF WIPEOUT 组码规格）=====
    // 现代(AC1027+) WIPEOUT 组码：41=边框宽(0/缺省=无边框)、42=填充色(0=背景/71=黑/72=白/73=灰)、43=填充透明(0..1)。
    // 遗留(R13,无41/42) WIPEOUT：无图(340=0)时为"背景擦除器"→填充=背景色(模拟 AutoCAD 擦除底层)。
    // 边框(多段线)显示由文件全局 $WIPEOUTFRAME(WIPEOUTVARIABLES 组码70 DisplayImageFrame)控制：
    //   70=1 → 显示裁剪边界多段线（无论有无图）；70=0 → 不显示。实体41>0 时也显示（显式边框宽）。
    // 色=图元/图层色，宽=多段线标准(StrokeWidth，与多段线同一函数)。本测试文件：base 70=1 显示、off 70=0 不显示、D 70=1 显示。
    private static string WipeoutFillHex(double fillKind, bool whiteBg)
    {
        switch ((int)fillKind)
        {
            case 71: return "#000000";
            case 72: return "#FFFFFF";
            case 73: return "#808080";
            default: return whiteBg ? "#FFFFFF" : "#000000"; // 0/非法 = 背景擦除
        }
    }

    // 边框决策：返回 (是否画框, 边框色hex, 边框宽)。
    // 触发条件：文件全局 DisplayImageFrame(WIPEOUTVARIABLES 70)=1 或 实体边框宽41>0。
    // 无图(340=0)WIPEOUT 同样按 70 显示裁剪边界多段线（AutoCAD 行为，非仅"有图"才显示）。
    private (bool Show, string Col, double W) WipeoutFrame(Wipeout wo, short layerAci, short byBlockAci, double scaleMul)
    {
        if (wo.BorderWidth > 0 || _wipeoutDisplayImageFrame)
            return (true, ColorOf(EntityAciIndex(wo), layerAci, byBlockAci), StrokeWidth(wo, scaleMul));
        return (false, null, 0);
    }

    // Leader 实心箭头两翼（三引擎共享）：尖端 pts[0]（指向目标点），两翼沿 pts[0]→pts[1] 方向张开 ±30°，
    // 与尖端构成实心三角（尖端在 pts[0]、底边在两翼之间）。返回 (左翼, 右翼)；顶点不足或退化返回 false。
    // 箭头长 arrowLen = TextHeight（>0 时）否则线宽倍数。底边中心（折线止点）= 尖端 + (pts[0]→pts[1])方向 * arrowLen*cos30°。
    private static bool LeaderArrowWings(Leader ld, List<(double X, double Y)> pts, double arrowLen,
        out double w1x, out double w1y, out double w2x, out double w2y)
    {
        w1x = w1y = w2x = w2y = 0;
        if (pts == null || pts.Count < 2) return false;
        double ax = pts[0].X, ay = pts[0].Y;
        double dx = ax - pts[1].X, dy = ay - pts[1].Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return false;
        double baseAng = Math.Atan2(dy, dx);
        double a1 = baseAng + 0.5236; // +30°
        double a2 = baseAng - 0.5236; // -30°
        w1x = ax - arrowLen * Math.Cos(a1); w1y = ay - arrowLen * Math.Sin(a1);
        w2x = ax - arrowLen * Math.Cos(a2); w2y = ay - arrowLen * Math.Sin(a2);
        return true;
    }

    // ===== Leader 箭头块（三引擎共享）=====
    // 引线箭头由两个属性决定：
    //   ① 箭头形状 = DimensionStyle.LeaderArrow（DXF 组码 341 / DIMLDRBLK）——指向一个箭头块
    //      （_Open/_ArchTick/_Dot/_BoxFilled/_Integral/_Origin 等标准块）；为 null = 默认实心闭合箭头(_Closed)。
    //   ② 箭头缩放 = DimensionStyle.ArrowSize（DXF 组码 41 / DIMASZ）——标准箭头块长度归一为 1.0，
    //      故 DIMASZ 即箭头长度（闭合箭头长宽比 6:1，与 AutoCAD 一致）。
    // 解析出 (箭头块, 缩放)；块无法解析时 Block=null，调用方回退旧实心三角。缩放取 DIMASZ，<=0 时回退 fallbackScale。
    // 引线级 DSTYLE 覆盖 XData（组码 1001 ACAD / 1000 DSTYLE / 1002 { … }）解析：
    //   1070 341 + 1005 <句柄>  → DIMLDRBLK 箭头块（引用 BLOCK_RECORD 句柄）
    //   1070 147 + 1040 <实数>  → DIMASZ   箭头尺寸
    // 仅当该引线实体上带覆盖时返回非 null；否则由调用方回退到引线样式默认。
    // 说明：AutoCAD 把"与样式不同的引线属性"以 DSTYLE XData 形式挂在引线实体上；
    //       自定义箭头块（_ClosedBlank/_Dot 等）正是通过 341 句柄覆盖指定，尺寸通过 147 覆盖。
    private static (BlockRecord Block, double? Size) ParseLeaderXDataArrow(Leader ld, CadDocument doc)
    {
        BlockRecord block = null;
        double? size = null;
        try
        {
            if (ld?.ExtendedData == null || doc == null) return (null, null);
            if (!ld.ExtendedData.TryGet(AppId.DefaultName, out var edata) || edata?.Records == null)
                return (null, null);
            var header = edata.Records.FirstOrDefault() as XData.ExtendedDataString;
            if (header == null || header.Value != DimensionStyle.StyleOverrideEntryName)
                return (null, null);
            // 取 { … } 之间的 (code, value) 记录序列
            var vals = edata.Records
                .SkipWhile(c => c is not XData.ExtendedDataControlString)
                .Skip(1)
                .TakeWhile(c => c is not XData.ExtendedDataControlString)
                .ToList();
            for (int i = 0; i + 1 < vals.Count; i += 2)
            {
                if (vals[i] is not XData.ExtendedDataInteger16 code) continue;
                var val = vals[i + 1];
                if (code.Value == 341 && val is XData.ExtendedDataHandle h)
                    block = doc.GetCadObject<BlockRecord>(h.Value);
                else if (code.Value == 147 && val is XData.ExtendedDataReal r)
                    size = r.Value;
            }
        }
        catch { }
        return (block, size);
    }

    // 引线箭头块 + 尺寸解析（块忠实渲染）。优先级：
    //   块：引线 DSTYLE XData 341（自定义箭头块，如 _ClosedBlank/_Dot）
    //       > 引线样式 LeaderArrow（DIMLDRBLK）
    //       > 文档内 _Closed 块（AutoCAD 默认实心闭合箭头）
    //       > null（调用方退回 V 形启发式）
    //   尺寸：XData 147（DIMASZ 覆盖）> 样式 ArrowSize > fallbackScale
    private static (BlockRecord Block, double Scale, bool NoArrow) LeaderArrowResolve(Leader ld, CadDocument doc, double fallbackScale)
    {
        var (xdBlock, xdSize) = ParseLeaderXDataArrow(ld, doc);

        // 箭头尺寸 = 样式 ArrowSize（DIMSTYLE DIMASZ，组码 41，如 2.5）。
        // XData 147 与 DIMSTYLE 42/147 同值（如 0.625），是 DIMEXO 类偏移量，不是 DIMASZ，
        // 不用于箭头尺寸（GT 实测箭头≈2.5，取 XData 147 会画成 ~0.6 的小箭头）。
        double asz = 0;
        try { asz = ld.Style?.ArrowSize ?? 0; } catch { }
        double scale = asz > 1e-9 ? asz : fallbackScale;

        // XData 341 存在但指向空块（如 _None，0 实体）= 显式"无箭头"。
        // 此时不得回退样式/_Closed/默认实心三角（GT 行23 _None 为纯引线无箭头）。
        if (xdBlock != null)
        {
            if (xdBlock.Entities == null || xdBlock.Entities.Count == 0)
                return (null, scale, true);
            return (xdBlock, scale, false);
        }

        BlockRecord br = null;
        try { br = ld.Style?.LeaderArrow; } catch { }
        bool ok = br != null && br.Entities != null && br.Entities.Count > 0;
        if (!ok)
        {
            // 默认（LeaderArrow=null）= 实心闭合箭头。优先用文档内的 _Closed 块（像素级还原其几何）。
            try
            {
                foreach (var b in doc?.BlockRecords)
                    if (b.Name == "_Closed" && b.Entities != null && b.Entities.Count > 0) { br = b; ok = true; break; }
            }
            catch { }
        }
        return ok ? (br, scale, false) : (null, scale, false);
    }

    // 是否为"实心闭合"箭头（仅 _Closed）：渲染为**填充**三角。
    // _ClosedBlank 块内是空心 chevron 轮廓线（(0,0)→(-1,±1/6)），GT 实测为**空心**三角（仅描边不填充），
    // 故与其余标准块一样按块几何如实绘制（LeaderArrowGeom 描边）。
    // 无块默认（341 缺失）= 系统默认实心闭合箭头，走调用方的填充三角分支。
    private static bool IsClosedFilledArrow(BlockRecord br)
    {
        try
        {
            if (br == null) return false;
            string n = br.Name;
            return n == "_Closed";
        }
        catch { return false; }
    }

    // 箭头放置变换：块局部「尖端=(0,0)、主体朝 -X」→ 尖端对齐 pts[0]（指向目标），主体沿 pts[0]→pts[1]
    // （离开目标方向）展开，整体缩放 ascale（世界单位）。返回 null = 顶点退化。
    private static Transform2D LeaderArrowTransform(List<(double X, double Y)> pts, double ascale)
    {
        if (pts == null || pts.Count < 2 || ascale <= 1e-9) return null;
        double dx = pts[1].X - pts[0].X, dy = pts[1].Y - pts[0].Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return null;
        double ux = dx / len, uy = dy / len;
        // 块 -X 轴 → (ux,uy)  ⇒  旋转矩阵列 (Ca,Sa) = (-ux,-uy)
        return new Transform2D { Ca = -ux, Sa = -uy, Sx = ascale, Sy = ascale, Tx = pts[0].X, Ty = pts[0].Y };
    }

    // 圆弧采样为折线点（世界坐标，含旋转）。startDeg/endDeg 为**世界**角度（度）。
    private static List<(double X, double Y)> SampleArcWorld(double cx, double cy, double r, double startDeg, double endDeg)
    {
        var pts = new List<(double X, double Y)>(33);
        double s = startDeg * Math.PI / 180.0, e = endDeg * Math.PI / 180.0;
        double span = e - s;
        while (span < 0) span += 2 * Math.PI;
        if (span < 1e-6) span = 2 * Math.PI; // 整圆
        int n = Math.Max(8, (int)Math.Ceiling(span / (Math.PI / 24.0))); // 每段 ≤7.5°
        for (int i = 0; i <= n; i++)
        {
            double a = s + span * i / n;
            pts.Add((cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
        return pts;
    }

    // 圆采样为闭合折线（世界坐标）。
    private static List<(double X, double Y)> SampleCircleWorld(double cx, double cy, double r)
        => SampleArcWorld(cx, cy, r, 0, 360);

    // 提取箭头块几何 → 世界坐标的"待描边折线"(strokes) 与"待填充多边形"(fills)。三引擎共享：
    //   各引擎只需对 strokes 描边、对 fills 填充，无需各自理解块内实体类型，也规避了
    //   EmitEntity 对"旋转块内圆弧"不叠加旋转角的缺陷（此处弧/圆直接采样为折线，旋转已含在 trA 内）。
    // 实心闭合箭头(_Closed)不在此处理（由调用方生成单个填充三角）。
    private static void LeaderArrowGeom(BlockRecord br, Transform2D trA,
        List<List<(double X, double Y)>> strokes, List<List<(double X, double Y)>> fills)
    {
        if (br == null || trA == null || br.Entities == null) return;
        foreach (var e in br.Entities)
        {
            switch (e)
            {
                case Line ln:
                {
                    var p1 = trA.Apply(ln.StartPoint.X, ln.StartPoint.Y);
                    var p2 = trA.Apply(ln.EndPoint.X, ln.EndPoint.Y);
                    strokes.Add(new List<(double X, double Y)> { p1, p2 });
                    break;
                }
                case LwPolyline pl:
                {
                    var pp = LwPolylineSamplePoints(pl, trA); // 含闭合段（首尾回 v0）
                    double hw = pl.ConstantWidth * 0.5 * trA.Scale;
                    if (pl.ConstantWidth > 1e-9 && hw > 1e-9 && pp.Count >= 2)
                    {
                        // 定宽多段线：AutoCAD 按"定宽带"填充为实心形，而非细线轮廓。
                        //   闭合（如 _Dot 圆点）→ 外扩半宽得实心形；开放（如 _ArchTick 拱形刻线）→ 中线两侧外扩得实心带。
                        if (pl.IsClosed && pp.Count >= 3) fills.Add(ThickClosedPolygon(pp, hw));
                        else fills.Add(ThickOpenPolygon(pp, hw));
                    }
                    else if (pp.Count >= 2) strokes.Add(pp);
                    break;
                }
                case Arc a: // 必须先于 Circle（fork 继承关系）
                {
                    var ctr = trA.Apply(a.Center.X, a.Center.Y);
                    double rot = trA.Rotation; // 块内弧须叠加放置旋转角（EmitEntity 的 Arc 分支缺失此项，此处显式补上）
                    strokes.Add(SampleArcWorld(ctr.X, ctr.Y, a.Radius * trA.Scale,
                        a.StartAngle * 180.0 / Math.PI + rot, a.EndAngle * 180.0 / Math.PI + rot));
                    break;
                }
                case Circle c:
                {
                    var ctr = trA.Apply(c.Center.X, c.Center.Y);
                    strokes.Add(SampleCircleWorld(ctr.X, ctr.Y, c.Radius * trA.Scale));
                    break;
                }
                case Solid so:
                {
                    fills.Add(SolidQuadPts(so.FirstCorner, so.SecondCorner, so.ThirdCorner, so.FourthCorner, trA));
                    break;
                }
                case Ellipse el:
                {
                    // 箭头块不含椭圆；兜底按外接圆近似（保持不崩溃）。
                    var ctr = trA.Apply(el.Center.X, el.Center.Y);
                    strokes.Add(SampleCircleWorld(ctr.X, ctr.Y, Math.Max(el.MajorAxis, el.MinorAxis) / 2.0 * trA.Scale));
                    break;
                }
            }
        }
    }

    // 箭头"底边"距离（三引擎共享）：从尖端 pts[0] 沿引线方向（pts[0]→pts[1]，即块的 −X 轴方向）出发，
    // 求射线与箭头边界（描边 strokes + 填充 fills 的外轮廓）在"朝向末端一侧"的最远交点距离。
    // 引线应在此处截断（线停于箭头底边，箭头叠在其上），避免线穿入空心箭头内部 / 伸入块箭头。
    //   · 形（三角/方/圆点 + 可选茎）→ 最远交点 = 底边（含茎时取茎端，AutoCAD 行为：茎属箭头块，线接茎端）。
    //   · 刻线 _ArchTick/_Oblique（斜线过尖端）→ 射线恰在尖端 t≈0 相交（t≤0 被排除）→ 返回 0（不截断，
    //     线接到斜线中点=尖端），正是旧"截到块底边"实现丢尖端的陷阱所在。
    // 与 AutoCAD 一致：线自末端侧进入箭头，首先碰到的是底边（最远交点），故取 max(t)。
    private static double LeaderTrimDistance(List<(double X, double Y)> pts,
        List<List<(double X, double Y)>> strokes, List<List<(double X, double Y)>> fills)
    {
        if (pts == null || pts.Count < 2) return 0;
        double ux = pts[1].X - pts[0].X, uy = pts[1].Y - pts[0].Y;
        double len = Math.Sqrt(ux * ux + uy * uy);
        if (len < 1e-12) return 0;
        ux /= len; uy /= len;
        var T = pts[0];
        double best = 0;
        void DoPolyline(List<(double X, double Y)> poly, bool closed)
        {
            int n = poly.Count;
            if (n < 2) return;
            int last = closed ? n : n - 1;
            for (int i = 0; i < last; i++)
            {
                var A = poly[i];
                var B = poly[(i + 1) % n];
                double ex = B.X - A.X, ey = B.Y - A.Y;
                double det = ex * uy - ey * ux; // cross(e, d)
                if (Math.Abs(det) < 1e-12)
                {
                    // 共线/平行段（如沿射线方向的茎/中心线）：叉积交点退化，改用端点投影——
                    // 取落在射线（±eps 内）的端点投影中最大 t≥0（茎端 = AutoCAD 线接点）。
                    // 平行但偏离射线的段（两端点偏移均 > eps）不计入。
                    double offA = Math.Abs(ux * (A.Y - T.Y) - uy * (A.X - T.X));
                    double offB = Math.Abs(ux * (B.Y - T.Y) - uy * (B.X - T.X));
                    const double epsC = 1e-8;
                    if (offA < epsC && offB < epsC)
                    {
                        double tA = (A.X - T.X) * ux + (A.Y - T.Y) * uy;
                        double tB = (B.X - T.X) * ux + (B.Y - T.Y) * uy;
                        double tm = Math.Max(tA, tB);
                        if (tm > best) best = tm > 0 ? tm : 0;
                    }
                    continue;
                }
                double ax = A.X - T.X, ay = A.Y - T.Y;
                double t = (ex * ay - ey * ax) / det; // cross(e, A-T)/det
                if (t <= 1e-9) continue;            // 须朝末端一侧（t>0）
                double s = (ux * ay - uy * ax) / det; // cross(d, A-T)/det
                if (s < -1e-9 || s > 1 + 1e-9) continue;
                if (t > best) best = t;
            }
        }
        if (strokes != null) foreach (var s in strokes) DoPolyline(s, false);
        if (fills != null) foreach (var f in fills) DoPolyline(f, true);
        return best;
    }

    // 沿折线从起点 pts[0] 向末端走 distance 长度，返回该点之后的子折线（用于把引线起点截到箭头底边）。
    // distance≤0 或 ≥ 折线总长时原样返回（不截断）。不修改入参。
    private static List<(double X, double Y)> TrimPolylineStart(List<(double X, double Y)> pts, double distance)
    {
        if (pts == null || pts.Count < 2 || distance <= 1e-9) return pts;
        double remaining = distance;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double dx = pts[i + 1].X - pts[i].X, dy = pts[i + 1].Y - pts[i].Y;
            double seg = Math.Sqrt(dx * dx + dy * dy);
            if (seg <= 1e-12) continue;
            if (remaining <= seg)
            {
                double f = remaining / seg;
                var result = new List<(double X, double Y)>(pts.Count - i) { (pts[i].X + dx * f, pts[i].Y + dy * f) };
                for (int j = i + 1; j < pts.Count; j++) result.Add(pts[j]);
                return result;
            }
            remaining -= seg;
        }
        return pts; // distance ≥ 总长，不截断
    }

    // 闭合定宽多段线的填充外边界：以采样点形心为基准，把每个点沿"形心→点"方向外扩 off。
    // 对凸形（圆/凸多边形，如 _Dot 圆点）精确；凹形为近似（标准箭头块均为凸形）。
    private static List<(double X, double Y)> ThickClosedPolygon(List<(double X, double Y)> pp, double off)
    {
        double cx = 0, cy = 0;
        foreach (var p in pp) { cx += p.X; cy += p.Y; }
        cx /= pp.Count; cy /= pp.Count;
        var res = new List<(double X, double Y)>(pp.Count);
        foreach (var p in pp)
        {
            double dx = p.X - cx, dy = p.Y - cy;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-9) { res.Add((p.X + off, p.Y)); continue; }
            double s = (d + off) / d;
            res.Add((cx + dx * s, cy + dy * s));
        }
        return res;
    }

    // 开放定宽多段线的实心带：沿中线两侧各外扩 hw（平头端帽）。用于 _ArchTick 等开放定宽刻线——
    // AutoCAD 把开放定宽多段线渲染为"实心带"（填充），而非细描边。两侧偏移用平滑法线（相邻切线均值），
    // 端点取单侧切线法线，保证直段为矩形带、折角处平滑过渡。
    private static List<(double X, double Y)> ThickOpenPolygon(List<(double X, double Y)> pp, double hw)
    {
        int n = pp.Count;
        var left = new List<(double X, double Y)>(n);
        var right = new List<(double X, double Y)>(n);
        for (int i = 0; i < n; i++)
        {
            double tx, ty;
            if (i == 0) { tx = pp[1].X - pp[0].X; ty = pp[1].Y - pp[0].Y; }
            else if (i == n - 1) { tx = pp[n - 1].X - pp[n - 2].X; ty = pp[n - 1].Y - pp[n - 2].Y; }
            else
            {
                double a1x = pp[i].X - pp[i - 1].X, a1y = pp[i].Y - pp[i - 1].Y;
                double a2x = pp[i + 1].X - pp[i].X, a2y = pp[i + 1].Y - pp[i].Y;
                double l1 = Math.Sqrt(a1x * a1x + a1y * a1y), l2 = Math.Sqrt(a2x * a2x + a2y * a2y);
                if (l1 < 1e-9) l1 = 1e-9; if (l2 < 1e-9) l2 = 1e-9;
                tx = a1x / l1 + a2x / l2; ty = a1y / l1 + a2y / l2;
            }
            double tl = Math.Sqrt(tx * tx + ty * ty);
            if (tl < 1e-9) tl = 1e-9;
            tx /= tl; ty /= tl;
            left.Add((pp[i].X - ty * hw, pp[i].Y + tx * hw));
            right.Add((pp[i].X + ty * hw, pp[i].Y - tx * hw));
        }
        var res = new List<(double X, double Y)>(n * 2);
        res.AddRange(left);
        for (int i = n - 1; i >= 0; i--) res.Add(right[i]);
        return res;
    }

    // 直线引线显示路径（世界坐标）——三引擎（Svg/Png/Pdf）共享。
    // 背景：ACadSharp 的 Leader.HasHookline 是只读计算属性（无 setter），文件里存储的 75（钩线）
    // 标志在 DXF/DWG 读回时丢失，读回的只是"末段是否水平"的计算值。但 AutoCAD 对带钩线的引线
    // 始终绘制 角点 + 水平着陆段，其形状与端点由几何与关联标注共同决定（已逐条与 GT 核对）：
    //   · DXF 4 顶点形式（末段已水平存储）：角点 = 第 n-1 个顶点；水平段 = 存储的末段。
    //   · DWG 3 顶点形式（末段为斜线、角点被读取器丢弃）：角点 = (末顶点.x − 箭头长, 末顶点.y)；
    //     水平段自角点沿 +X 画到端点。
    //     核对：#2 角点 2703.307−2.5=2700.807、#1 角点 2701.624−2.5=2699.124 —— 恰为 DXF 存储的角点。
    // 水平段端点 endX（标注控制）：若引线末端严格位于 MTEXT 左缘左侧（last.x < textLeft），
    //   AutoCAD 把基线延长到文字右缘 textRight；否则保持存储末端。
    //   核对：#1 末点 2701.62 < 2A6 左缘 2702.25 → 延长到 2730.98（=2702.25+28.73，文字右缘）；
    //         #2/#4 末点 2703.31 == 2B1 左缘 → 不延长（保持 2703.31）。
    // 文字宽度优先取 MText.RectangleWidth（DWG 中为 0），回退 Leader.TextWidth（组码 41，恒有存储）。
    // 返回 null：样条引线（由 LeaderSplineFlatten 处理）或无钩线信号的直线引线（保持原顶点折线）。
    private static List<(double X, double Y)> LeaderDisplayPath(Leader ld, CadDocument doc, double arrowSize)
    {
        if (ld?.Vertices == null) return null;
        if (ld.PathType == LeaderPathType.Spline) return null;
        var V = ld.Vertices;
        int n = V.Count;
        if (n < 3) return null;

        var prev = V[n - 2];
        var last = V[n - 1];
        // 末段是否已水平（∥ HorizontalDirection；叉积 ≤ 1e-6·弦长 ≈ 1e-6 rad 容差）
        double hx = 1, hy = 0;
        try
        {
            var hd = ld.HorizontalDirection;
            if (hd != null && (hd.X != 0 || hd.Y != 0)) { hx = hd.X; hy = hd.Y; }
        }
        catch { }
        double ddx = last.X - prev.X, ddy = last.Y - prev.Y;
        double dl = Math.Sqrt(ddx * ddx + ddy * ddy);
        bool lastHorizontal = dl > 1e-9 && Math.Abs(ddx * hy - ddy * hx) <= 1e-6 * dl;

        // 关联 MTEXT（73=0 表示带文字标注；读取器已把 340 句柄解析为实体）
        MText mt = null;
        if (ld.CreationType == LeaderCreationType.CreatedWithTextAnnotation)
            try { mt = ld.AssociatedAnnotation as MText; } catch { }

        if (!lastHorizontal && mt == null) return null;   // 无钩线信号：保持原折线

        // 角点：4 顶点形式取存储的第 n-1 顶点；3 顶点形式 = 末顶点左移一个箭头长
        double cornerX, cornerY;
        if (lastHorizontal) { cornerX = prev.X; cornerY = prev.Y; }
        else { cornerX = last.X - (arrowSize > 1e-9 ? arrowSize : 0); cornerY = last.Y; }

        // 水平段端点：标注存在且引线末端严格短于文字左缘 → 延长到文字右缘
        double endX = last.X;
        if (mt != null)
        {
            double tx = mt.InsertPoint.X;
            double tw = mt.RectangleWidth > 1e-9 ? mt.RectangleWidth : (ld.TextWidth > 1e-9 ? ld.TextWidth : 0);
            if (tw > 1e-9)
            {
                double textLeft;
                switch (mt.AttachmentPoint)
                {
                    case AttachmentPointType.TopCenter:
                    case AttachmentPointType.MiddleCenter:
                    case AttachmentPointType.BottomCenter:
                        textLeft = tx - tw / 2; break;
                    case AttachmentPointType.TopRight:
                    case AttachmentPointType.MiddleRight:
                    case AttachmentPointType.BottomRight:
                        textLeft = tx - tw; break;
                    default: // Left 家族（TopLeft/MiddleLeft/BottomLeft）
                        textLeft = tx; break;
                }
                if (last.X < textLeft) endX = textLeft + tw;
            }
        }

        var path = new List<(double X, double Y)>(n + 1);
        for (int i = 0; i < n - 1; i++) path.Add((V[i].X, V[i].Y));
        if (!lastHorizontal) path.Add((cornerX, cornerY));
        path.Add((endX, last.Y));
        return path;
    }

    // 样条引线（LEADER 组码 72=1，PathType=Spline）的平滑曲线：AutoCAD 对"样条"引线类型
    // 用一条穿过全部顶点的三次样条（拟合点=顶点）渲染，而非折线。
    // 前 n-1 段用 Catmull-Rom（端点钳制）→ 三次贝塞尔逐段密集采样；
    // 末段强制水平切向（AutoCAD 样条引线总是水平着陆，GT #3/#5 末段恰为水平）——用 Hermite：
    //   起切向 (P[n-1]−P[n-3])/2（与 Catmull-Rom 在 P[n-2] 的切向一致，保 C1 连续），
    //   终切向 = 水平（方向 = 末段水平行进方向 sign(P[n-1].x−P[n-2].x)，幅值 = 末段弦长，m2scale=1.0）。
    // 注：74(HookLineDirection)/HorizontalDirection 数据驱动符号不可用——本文件未存 211/221/231
    // （HorizontalDirection 默认 +X），74=Opposite ⇒ −X 与 GT 的 +X 终切向相矛盾；
    // 行进方向（direction of travel）与 GT 一致（#3/#5 均 +X）。
    // 起点切向 = (P1−P0)，与直线引线的箭头朝向一致，故箭头几何（LeaderArrowTransform 等）无需改动。
    // 非样条引线不应调用本函数（直接保留原顶点折线）。
    private static List<(double X, double Y)> LeaderSplineFlatten(List<(double X, double Y)> pts, int steps = 24)
    {
        var outp = new List<(double X, double Y)>();
        int n = pts?.Count ?? 0;
        if (n == 0) return outp;
        if (n == 1) { outp.Add(pts[0]); return outp; }
        if (n == 2) { outp.Add(pts[0]); outp.Add(pts[1]); return outp; }
        for (int i = 0; i < n - 1; i++)
        {
            var p1 = pts[i];
            var p2 = pts[i + 1];
            double b1x, b1y, b2x, b2y;
            if (i == n - 2)
            {
                // 末段：Hermite，终切向强制水平（GT：样条引线末段恰为水平）
                var p0 = pts[i - 1];
                double m1x = (p2.X - p0.X) / 2.0, m1y = (p2.Y - p0.Y) / 2.0;
                double chord = Math.Sqrt((p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y));
                double sgn = (p2.X - p1.X) >= 0 ? 1.0 : -1.0;
                double m2x = sgn * chord, m2y = 0.0;
                b1x = p1.X + m1x / 3.0; b1y = p1.Y + m1y / 3.0;
                b2x = p2.X - m2x / 3.0; b2y = p2.Y - m2y / 3.0;
            }
            else
            {
                var p0 = pts[i > 0 ? i - 1 : 0];
                var p3 = pts[i + 2 < n ? i + 2 : n - 1];
                b1x = p1.X + (p2.X - p0.X) / 6.0; b1y = p1.Y + (p2.Y - p0.Y) / 6.0;
                b2x = p2.X - (p3.X - p1.X) / 6.0; b2y = p2.Y - (p3.Y - p1.Y) / 6.0;
            }
            for (int s = 0; s < steps; s++)
            {
                double t = s / (double)steps, mt = 1.0 - t;
                double a = mt * mt * mt, b = 3.0 * mt * mt * t, c = 3.0 * mt * t * t, d = t * t * t;
                outp.Add((a * p1.X + b * b1x + c * b2x + d * p2.X,
                          a * p1.Y + b * b1y + c * b2y + d * p2.Y));
            }
        }
        outp.Add(pts[n - 1]);
        return outp;
    }

    // spline-fit（样条拟合）多段线（POLYLINE 组码 70 bit2=curved）的顶点混合两类：
    //   SplineVertexFromSplineFitting —— 样条拟合点，AutoCAD 实际渲染的显示路径；
    //   SplineFrameControlPoint      —— 样条框架控制点，AutoCAD 不显示（仅定义拟合曲线）。
    // 渲染/范围计算只取拟合点，否则控制点之间的连线会画出超长直线（"多段线多渲染控制线"）。
    // 普通（非拟合）多段线顶点不带该标志，不受影响。
    private static bool IsDisplayVertex(ACadSharp.Entities.Vertex v)
        => v != null && !v.Flags.HasFlag(ACadSharp.Entities.VertexFlags.SplineFrameControlPoint);

    // 引线范围贡献（ComputeExtents[SVG/PDF] 与 Png 剔除共用，保证画布不裁掉样条过冲与箭头底边）：
    //  1) 引线全路径点（样条用 LeaderSplineFlatten 输出，可能超出顶点凸包）；
    //  2) 箭头世界坐标 bbox：块箭头取块实体局部 bbox → LeaderArrowTransform 四角（旋转+等比+平移，
    //     角点给出保守外接 AABB）；无块默认取实心三角局部 bbox x:[-1,0] y:[±1/6]。
    //  旧实现仅展开 ld.Vertices，箭头块（尤其 _Dot/_BoxFilled/五边形等）与样条过冲被裁出画布
    //  （leaderTest 行24 五边形下缘 y≈1438.7 < 旧 minY≈1439.38，整条底部缺失）。
    private static void ExpandLeaderExtents(Leader ld, CadDocument doc, Action<double, double> ex)
    {
        if (ld?.Vertices == null || ld.Vertices.Count < 2) return;
        var (arrowBr, asz, noArrow) = LeaderArrowResolve(ld, doc, 0);
        var pts = new List<(double X, double Y)>();
        foreach (var v in ld.Vertices) pts.Add((v.X, v.Y));
        // 显示路径：直线引线含钩线合成（角点 + 水平着陆段，端点按标注控制延长，见 LeaderDisplayPath）；
        // 样条引线平滑（LeaderSplineFlatten，末段强制水平着陆）。与 emit 路径一致，保证包围盒不被裁切。
        List<(double X, double Y)> path;
        if (ld.PathType == LeaderPathType.Spline) path = LeaderSplineFlatten(pts, 24);
        else path = LeaderDisplayPath(ld, doc, asz) ?? pts;
        foreach (var p in path) ex(p.X, p.Y);

        if (!ld.ArrowHeadEnabled || noArrow || asz <= 1e-9) return;
        var trA = LeaderArrowTransform(pts, asz);

        double mnx = double.MaxValue, mny = double.MaxValue, mxx = double.MinValue, mxy = double.MinValue;
        bool any = false;
        void LEx(double x, double y)
        {
            if (!any) { mnx = mxx = x; mny = mxy = y; any = true; }
            else { if (x < mnx) mnx = x; if (x > mxx) mxx = x; if (y < mny) mny = y; if (y > mxy) mxy = y; }
        }
        void LExCircle(double cx, double cy, double r) { LEx(cx - r, cy - r); LEx(cx + r, cy + r); }

        if (arrowBr == null || IsClosedFilledArrow(arrowBr))
        {
            // 默认实心三角局部 bbox：尖端 (0,0)，底边 x=-1、y=±1/6
            LEx(0, 0); LEx(-1, 1.0 / 6.0); LEx(-1, -1.0 / 6.0);
        }
        else if (arrowBr.Entities != null)
        {
            foreach (var e in arrowBr.Entities)
            {
                switch (e)
                {
                    case Line l: LEx(l.StartPoint.X, l.StartPoint.Y); LEx(l.EndPoint.X, l.EndPoint.Y); break;
                    case LwPolyline pl:
                        foreach (var p in LwPolylineSamplePoints(pl, null)) LEx(p.X, p.Y);
                        if (pl.ConstantWidth > 1e-9 && any)
                        { double hw = pl.ConstantWidth * 0.5; mnx -= hw; mny -= hw; mxx += hw; mxy += hw; }
                        break;
                    case Arc a: LExCircle(a.Center.X, a.Center.Y, a.Radius); break; // 必须先于 Circle（fork 继承）
                    case Circle c: LExCircle(c.Center.X, c.Center.Y, c.Radius); break;
                    case Ellipse el: LExCircle(el.Center.X, el.Center.Y, Math.Max(el.MajorAxis, el.MinorAxis) / 2.0); break;
                    case Solid sd:
                        LEx(sd.FirstCorner.X, sd.FirstCorner.Y); LEx(sd.SecondCorner.X, sd.SecondCorner.Y);
                        LEx(sd.ThirdCorner.X, sd.ThirdCorner.Y); LEx(sd.FourthCorner.X, sd.FourthCorner.Y); break;
                    case Spline sp: foreach (var p in SplineSample(sp, null, 4)) LEx(p.X, p.Y); break;
                    case Point pt: LEx(pt.Location.X, pt.Location.Y); break;
                }
            }
        }
        if (!any) return;
        foreach (var c in new[] { (mnx, mny), (mxx, mny), (mnx, mxy), (mxx, mxy) })
        {
            var q = trA.Apply(c.Item1, c.Item2);
            ex(q.X, q.Y);
        }
    }

    // $SORTENTS/SORTENTSTABLE 显示顺序缓存（首次迭代 AllEntities 时计算，见 ApplySortentsOrder）
    private List<Entity> _sortOrderCache;

    private IEnumerable<Entity> AllEntities()
    {
        if (_sortOrderCache == null)
        {
            if (_streaming)
                _sortOrderCache = ApplySortentsOrder(_streamEnts); // P5 流式：用管线流序记录的实体列表（图框 insert 在流末=最顶层），不再读 ModelSpace
            else
                _sortOrderCache = ApplySortentsOrder(_doc.ModelSpace.Entities.ToList());
        }
        foreach (var e in _sortOrderCache)
            yield return e;
    }

    // 显示顺序 = $SORTENTS（头码 280，EntitySortingFlags）+ SORTENTSTABLE 对象：
    // 启用时按表的显示顺序渲染（SortHandle 升序 = 底层→顶层；不在表中的实体以自身 Handle 作排序键，
    // 与 ACadSharp SortEntitiesTable.GetSorterHandle 语义一致）；禁用(0)或无表则退回文档顺序。
    // 2026-10-26 修复：sampleWIPEOUT.dxf $SORTENTS=127(All，含 Plotting/Postscript) → AutoCAD 的 PDF 导出
    // 用 sortents 显示顺序；引擎此前按文档顺序（右侧 LINE 在文档末尾=最顶层）→ 线画在五角形 WIPEOUT
    // 擦除区之上，与 ref.pdf 不符（该线在表中排最底，应被五角形填充擦掉）。
    private List<Entity> ApplySortentsOrder(List<Entity> docOrder)
    {
        try
        {
            if (_doc?.Header?.EntitySortingFlags == ACadSharp.Header.ObjectSortingFlags.Disabled) return docOrder;
            var table = _doc.GetCadObjects<ACadSharp.Objects.SortEntitiesTable>()
                .FirstOrDefault(t => t.BlockOwner != null && t.BlockOwner.Name == BlockRecord.ModelSpaceName);
            if (table == null) return docOrder;
            var keys = new Dictionary<Entity, ulong>();
            foreach (var s in table) keys[s.Entity] = s.SortHandle;
            if (keys.Count == 0) return docOrder;
            // OrderBy 为稳定排序：排序键相同（如多个实体不在表内且 Handle 相同——实际不会）保持文档顺序
            return docOrder
                .OrderBy(e => keys.TryGetValue(e, out var k) ? k : e.Handle)
                .ToList();
        }
        catch
        {
            return docOrder;
        }
    }

    // 计算数据范围（含 Insert 展开后的块内实体）
    // P5 流式：范围已在管线 Render 期间逐实体累积（_st* 字段），此处只回灌到当前视图字段，
    // 不重扫、不重置 → 多次 Write*（多级别/多格式复用同一引擎）各自独立还原同一基础范围。
    // 完整模式：每次全量重扫（可重入）。
    private bool ComputeExtents()
    {
        if (_streaming)
        {
            _minX = _stMinX; _minY = _stMinY; _maxX = _stMaxX; _maxY = _stMaxY;
            return _stAny;
        }
        _extAny = false;
        _minX = _minY = double.MaxValue;
        _maxX = _maxY = double.MinValue;

        if (_doc == null || _doc.ModelSpace?.Entities == null) return false;

        foreach (var ent in AllEntities())
            ExpandEntityExtents(ent, false);
        return _extAny;
    }

    // 单实体范围扩展（完整模式 ComputeExtents 与流式 Render 共用，P5 自 ComputeExtents 抽出）：
    // per-entity switch 语义与原版逐条一致（Insert 块展开、文字盒、引线过冲、OLE2 两角等），
    // 唯一差别是扩展目标——流式累积进 _st*，完整模式写当前视图 _minX..。
    private void ExpandEntityExtents(Entity ent, bool streaming)
    {
        void Expand(double x, double y)
        {
            if (streaming)
            {
                if (x < _stMinX) _stMinX = x; if (y < _stMinY) _stMinY = y;
                if (x > _stMaxX) _stMaxX = x; if (y > _stMaxY) _stMaxY = y;
                _stAny = true;
            }
            else
            {
                if (x < _minX) _minX = x; if (y < _minY) _minY = y;
                if (x > _maxX) _maxX = x; if (y > _maxY) _maxY = y;
                _extAny = true;
            }
        }

        void ExpandCircle(double cx, double cy, double r)
        { Expand(cx - r, cy - r); Expand(cx + r, cy + r); }

        switch (ent)
        {
            case Point pt: Expand(pt.Location.X, pt.Location.Y); break;
            case Line ln: Expand(ln.StartPoint.X, ln.StartPoint.Y); Expand(ln.EndPoint.X, ln.EndPoint.Y); break;
            case MLine ml:
                // 多线：用展开后的各子线中心线扩展 bbox（含 offset 偏移与宽度，避免漏算）
                foreach (var el in MLineElements(ml, null))
                    foreach (var p in el.Center) Expand(p.X, p.Y);
                break;
            case LwPolyline p2: foreach (var p in LwPolylineSamplePoints(p2, null)) Expand(p.X, p.Y); break; // 含 bulge 弧段范围
            case Polyline3D p3: foreach (var v in p3.Vertices) if (IsDisplayVertex(v)) Expand(v.Location.X, v.Location.Y); break;
            case Polyline2D p2d: foreach (var v in p2d.Vertices) if (IsDisplayVertex(v)) Expand(v.Location.X, v.Location.Y); break;
            case Mesh mesh: if (mesh.Vertices != null) foreach (var v in mesh.Vertices) Expand(v.X, v.Y); break;
            case PolyfaceMesh pf: if (pf.Vertices != null) foreach (var v in pf.Vertices) Expand(v.Location.X, v.Location.Y); break;
            case PolygonMesh pm: if (pm.Vertices != null) foreach (var v in pm.Vertices) Expand(v.Location.X, v.Location.Y); break;
            case Wipeout wo: foreach (var p in WipeoutWorldPoly(wo, null)) Expand(p.X, p.Y); break;
            case Ole2Frame olef: // OLE2 嵌入对象（310 内嵌图片）：UL/LR 两角计入图幅，否则图片区域撑大画布被裁
                Expand(Math.Min(olef.UpperLeftCorner.X, olef.LowerRightCorner.X), Math.Min(olef.UpperLeftCorner.Y, olef.LowerRightCorner.Y));
                Expand(Math.Max(olef.UpperLeftCorner.X, olef.LowerRightCorner.X), Math.Max(olef.UpperLeftCorner.Y, olef.LowerRightCorner.Y));
                break;
            case Arc a: ExpandCircle(a.Center.X, a.Center.Y, a.Radius); break; // 必须先于 Circle（fork 继承关系）
            case Circle c: ExpandCircle(c.Center.X, c.Center.Y, c.Radius); break;
            case Ellipse el: ExpandCircle(el.Center.X, el.Center.Y, Math.Max(el.MajorAxis, el.MinorAxis) / 2.0); break;
            case TextEntity t: ExpandTextBox(t.InsertPoint.X, t.InsertPoint.Y, TextMetrics(t), Expand); break;
            case MText mt: ExpandTextBox(mt.InsertPoint.X, mt.InsertPoint.Y, TextMetrics(mt), Expand); break;
            case Solid sd:
                Expand(sd.FirstCorner.X, sd.FirstCorner.Y); Expand(sd.SecondCorner.X, sd.SecondCorner.Y);
                Expand(sd.ThirdCorner.X, sd.ThirdCorner.Y); Expand(sd.FourthCorner.X, sd.FourthCorner.Y); break;
            case Spline sp:
                foreach (var p in SplineSample(sp, null, 4)) Expand(p.X, p.Y); break;
            case Hatch ht:
                foreach (var loop in BuildHatchLoops(ht, null))
                    foreach (var p in loop) Expand(p.X, p.Y); break;
            case Insert ins: ExpandInsert(ins, Expand); break;
            case Leader ld: // 引线全路径（样条含过冲）+ 箭头 bbox 计入图框，否则箭头/样条过冲被裁出画布
                ExpandLeaderExtents(ld, _doc, Expand); break;
            default: break;
        }
    }

    // 文字包围盒估算：TEXT/MText 若只展开基线插入点，会把 ascender/descender 裁出画布
    // （典型：顶部文字只剩基线附近下半、上环丢失，如 sample-hatch 的 "81,96"）。
    // 按字高/行数/宽度因子估算文字盒，再绕锚点旋转取轴对齐 AABB（不乘旋转的盒装不下旋转文字，
    // 如 61° 的 TEXT 需要 94×sin61°≈83 的竖向半高，未旋转盒只有 0.85H≈13）。
    //
    // MText 必须按**真实排版**（解析富文本 → 按组码41换行后的行宽/行数/行高）估算：
    // 旧版直接用原始富文本字符数（含 \f/\H/\W/\P 等格式码）算宽——最长"行"200+ 字符 →
    // 文字盒 ±900 单位 → 画布被撑大 9 倍、文字缩成蚂蚁（sample2004_textorMtext 占位 bug，
    // 用户反馈"读取文字大小占位有bug"）。且拉丁 0.6em/字对 CJK（1.0em）低估，
    // 现统一走 EstimateTextWidth（CJK 感知）+ 真实布局（\H 行高/\W 宽度/换行全部生效）。
    private (double Hw, double Asc, double Desc) TextMetrics(Entity e)
    {
        if (e is TextEntity t)
        {
            double H = t.Height > 0 ? t.Height : 1.0;
            double wf = t.WidthFactor > 0 ? t.WidthFactor : 1.0;
            double w = EstimateTextWidth(t.Value ?? "", H) * wf;
            // 局部盒：左右各扩整条文字宽（覆盖左/中/右对齐），上 0.85H 下 0.25H
            return RotatedBoxAabb(Math.Max(w, H), 0.85 * H, -0.25 * H, t.Rotation);
        }
        if (e is MText mt)
        {
            var layout = WrapMText(ParseMText(mt.Value ?? "", mt.Height, GetStyleFont(mt.Style), GetStyleBigFont(mt.Style), MTextCacheParaProps(mt)), mt.RectangleWidth, GetStyleFont(mt.Style), GetStyleBigFont(mt.Style));
            int n = layout.Lines.Count;
            if (n == 0) return (1.0, 1.0, 1.0);
            double maxH = 0.1;
            foreach (var ln in layout.Lines) maxH = Math.Max(maxH, ln.MaxHeight);
            double gap = MTextFontLineRatio(GetStyleFont(mt.Style), GetStyleBigFont(mt.Style)) * maxH; // 与 MTextLocalLayout 一致的字体定义行距
            double top = 0.8 * layout.Lines[0].MaxHeight;                  // 首行 ascender
            double bottom = -((n - 1) * gap + 0.2 * layout.Lines[n - 1].MaxHeight); // 末行 descender
            return RotatedBoxAabb(Math.Max(layout.MaxLineWidth, maxH), top, bottom, mt.Rotation);
        }
        return (1.0, 1.0, 1.0);
    }

    // 局部文字盒（左右 ±halfW，竖向 [yBot,yTop]）绕锚点旋转 rotRad 后的轴对齐 AABB 半高
    private static (double Hw, double Asc, double Desc) RotatedBoxAabb(double halfW, double yTop, double yBot, double rotRad)
    {
        double c = Math.Cos(rotRad), s = Math.Sin(rotRad);
        double maxX = 0, minX = 0, maxY = 0, minY = 0;
        bool first = true;
        foreach (double x in new[] { -halfW, halfW })
            foreach (double y in new[] { yBot, yTop })
            {
                double X = x * c - y * s, Y = x * s + y * c;
                if (first) { maxX = minX = X; maxY = minY = Y; first = false; }
                else
                {
                    if (X > maxX) maxX = X; if (X < minX) minX = X;
                    if (Y > maxY) maxY = Y; if (Y < minY) minY = Y;
                }
            }
        return (Math.Max(maxX, -minX), maxY, -minY);
    }

    // 以 (cx,cy) 为基线锚点的轴对齐文字盒 4 角（局部坐标，供 EntityLocalPoints yield）。
    private static IEnumerable<XY> TextBoxCorners(double cx, double cy, (double Hw, double Asc, double Desc) m)
    {
        yield return new XY(cx - m.Hw, cy - m.Desc);
        yield return new XY(cx + m.Hw, cy - m.Desc);
        yield return new XY(cx + m.Hw, cy + m.Asc);
        yield return new XY(cx - m.Hw, cy + m.Asc);
    }

    // 以 (cx,cy) 为基线锚点的轴对齐文字盒 4 角，逐角 expand（模型空间）。
    private static void ExpandTextBox(double cx, double cy, (double Hw, double Asc, double Desc) m, Action<double, double> expand)
    {
        foreach (var c in TextBoxCorners(cx, cy, m)) expand(c.X, c.Y);
    }

    // 展开 Insert（含嵌套），用于计算范围
    private void ExpandInsert(Insert ins, Action<double, double> expand)
    {
        if (ins?.Block == null) return;
        double sx = ins.XScale, sy = ins.YScale, rot = ins.Rotation; // fork 弧度（参考实现：度→弧度）
        double ca = Math.Cos(rot), sa = Math.Sin(rot);
        double px = ins.InsertPoint.X, py = ins.InsertPoint.Y;
        foreach (var e in ins.Block.Entities)
        {
            if (e is Insert nested)
            {
                // 嵌套块：把嵌套块位置先变换到本块坐标，再递归（近似：仅展开一层位置，深层用其自身展开）
                ExpandInsertTransformed(nested, ca, sa, sx, sy, px, py, expand);
                continue;
            }
            foreach (var p in EntityLocalPoints(e))
            {
                double lx = p.X * sx, ly = p.Y * sy;
                double wx = px + lx * ca - ly * sa;
                double wy = py + lx * sa + ly * ca;
                expand(wx, wy);
            }
        }
        // 【属性文字范围】顶层 INSERT 的 ATTRIB（ins.Attributes）为世界坐标（parent=null，
        // AutoCAD 存档时已把块定义 ATDEF 的位置/字高预乘插入比例），不套本 INSERT 的 tr。
        // 直接按字高估算文字盒扩展范围——否则插入块属性（如 Drawing2 的 "3.94"，位于圆圈上方
        // ~16 单位处）被裁出画布，出图只见块几何、不见属性文字。
        // 升档两处（针对 SHX 字体，如 rs.shx）：
        //   ① asc 取 1.05H——SHX 帽高=H（scale=H/Above，数字帽高恰为 H），TextMetrics 的 0.85H
        //      会裁掉 SHX 字形顶部（"3.94" 数字上缘恰在基线+H）。
        //   ② 宽度取 max(估算, 0.9em/字)——EstimateTextWidth 的 0.55em/字 对 SHX 偏窄
        //      （rs.shx 数字实测 ~0.79em/字@wf=1），纯估算会裁掉右端字符（如 "3.94" 的 "4"）。
        if (ins.Attributes != null)
            foreach (var att in ins.Attributes)
            {
                double aH = att.Height > 0 ? att.Height : 1.0;
                double aWf = att.WidthFactor > 0 ? att.WidthFactor : 1.0;
                int aLen = (att.Value ?? "").Length;
                double aW = Math.Max(EstimateTextWidth(att.Value ?? "", aH) * aWf, 0.9 * aLen * aH * aWf);
                aW = Math.Max(aW, aH);
                double asc = 1.05 * aH, desc = 0.35 * aH;
                // 基线左对齐盒（渲染端以 InsertPoint 为基线左端点、文字向右延伸，实测 Drawing2
                // "3.94" 左缘恰在 InsertPoint）：局部 4 角 (0,-desc)/(aW,-desc)/(aW,asc)/(0,asc)
                // 绕 (cx,cy) 旋转 att.Rotation 后逐点扩展。居中海箱会向 InsertPoint 左侧空扩
                // 半个字宽（Drawing2 左侧多出 ~52 单位空白）。
                double ar = att.Rotation, cc = Math.Cos(ar), ss = Math.Sin(ar);
                double cx = att.InsertPoint.X, cy = att.InsertPoint.Y;
                double[] ox = { 0, aW, aW, 0 }, oy = { -desc, -desc, asc, asc };
                for (int i = 0; i < 4; i++)
                    expand(cx + ox[i] * cc - oy[i] * ss, cy + ox[i] * ss + oy[i] * cc);
            }
    }

    private void ExpandInsertTransformed(Insert nested, double ca, double sa, double sx, double sy, double px, double py, Action<double, double> expand)
    {
        if (nested?.Block == null) return;
        double nsx = sx * nested.XScale, nsy = sy * nested.YScale;
        double nrot = (nested.Rotation * 180.0 / Math.PI) + Math.Atan2(sa, ca) * 180 / Math.PI; // fork：嵌套旋转弧度→度（参考实现：度）
        double nca = Math.Cos(nrot * Math.PI / 180), nsa = Math.Sin(nrot * Math.PI / 180);
        double bx = px + (nested.InsertPoint.X * sx) * ca - (nested.InsertPoint.Y * sy) * sa;
        double by = py + (nested.InsertPoint.X * sx) * sa + (nested.InsertPoint.Y * sy) * ca;
        foreach (var e in nested.Block.Entities)
        {
            foreach (var p in EntityLocalPoints(e))
            {
                double lx = p.X * nsx, ly = p.Y * nsy;
                expand(bx + lx * nca - ly * nsa, by + lx * nsa + ly * nca);
            }
        }
        // 【属性文字范围】嵌套 INSERT 的 ATTRIB 位于父块（本 INSERT 的 Block）局部坐标
        // （渲染端以 parent=父变换 直渲），按父变换（ca/sa/sx/sy/px/py）变换到世界再扩展范围，
        // 不套嵌套自身变换；升档规则同顶层（SHX 帽高=H、每字 ≥0.9em）。
        if (nested.Attributes != null)
            foreach (var att in nested.Attributes)
            {
                double aH = att.Height > 0 ? att.Height : 1.0;
                double aWf = att.WidthFactor > 0 ? att.WidthFactor : 1.0;
                int aLen = (att.Value ?? "").Length;
                double aW = Math.Max(EstimateTextWidth(att.Value ?? "", aH) * aWf, 0.9 * aLen * aH * aWf);
                aW = Math.Max(aW, aH);
                double asc = 1.05 * aH, desc = 0.35 * aH;
                double ar = att.Rotation, cc = Math.Cos(ar), ss = Math.Sin(ar);
                double[] ox = { 0, aW, aW, 0 }, oy = { -desc, -desc, asc, asc };
                for (int i = 0; i < 4; i++)
                {
                    // 局部旋转 → 父块局部坐标 → 套父变换（sx,sy,ca,sa,px,py）到世界
                    double rx = ox[i] * cc - oy[i] * ss, ry = ox[i] * ss + oy[i] * cc;
                    double lx = (att.InsertPoint.X + rx) * sx, ly = (att.InsertPoint.Y + ry) * sy;
                    expand(px + lx * ca - ly * sa, py + lx * sa + ly * ca);
                }
            }
    }

    // 取实体在局部坐标系下的关键点（用于范围/插入展开；MText 走真实排版占位，须为实例方法）
    private IEnumerable<XY> EntityLocalPoints(Entity e)
    {
        if (e is Point pt) { yield return new XY(pt.Location.X, pt.Location.Y); }
        else if (e is Line ln) { yield return new XY(ln.StartPoint.X, ln.StartPoint.Y); yield return new XY(ln.EndPoint.X, ln.EndPoint.Y); }
        // ⚠️ LwPolyline 须用 bulge 感知采样（LwPolylineSamplePoints），与 EntityWorldAabb 一致：
        // 仅取顶点会漏掉 bulge 弧（弧伸出顶点 AABB 之外）→ Insert 内容范围偏小 → 精灵画布裁掉 bulge 弧（Bug2）。
        else if (e is LwPolyline p2) { foreach (var p in LwPolylineSamplePoints(p2, null)) yield return new XY(p.X, p.Y); }
        else if (e is Polyline3D p3) { foreach (var v in p3.Vertices) if (IsDisplayVertex(v)) yield return new XY(v.Location.X, v.Location.Y); }
        // ⚠️ 以下类型此前遗漏 → ExpandInsert 取不到点 → Insert 内容 AABB 为空 → 走直渲（Insert 60s 瓶颈真根因）。
        // 建筑平面图块大量用闭合 Polyline2D（带宽度）/MLine/面域，须与 EntityWorldAabb 覆盖一致（含其 IsDisplayVertex 守卫）。
        // 循环变量名须唯一（if-else 链内 pattern var 与 foreach var 共享作用域，重名触发 CS0136）。
        else if (e is Polyline2D p2d) { foreach (var vp2d in p2d.Vertices) if (IsDisplayVertex(vp2d)) yield return new XY(vp2d.Location.X, vp2d.Location.Y); }
        else if (e is MLine ml) { foreach (var mli in MLineElements(ml, null)) foreach (var mlp in mli.Center) yield return new XY(mlp.X, mlp.Y); }
        else if (e is Mesh me) { if (me.Vertices != null) foreach (var vmesh in me.Vertices) yield return new XY(vmesh.X, vmesh.Y); }
        else if (e is PolyfaceMesh pf) { if (pf.Vertices != null) foreach (var vpf in pf.Vertices) yield return new XY(vpf.Location.X, vpf.Location.Y); }
        else if (e is PolygonMesh pm) { if (pm.Vertices != null) foreach (var vpm in pm.Vertices) yield return new XY(vpm.Location.X, vpm.Location.Y); }
        else if (e is Wipeout wo) { foreach (var wop in WipeoutWorldPoly(wo, null)) yield return new XY(wop.X, wop.Y); }
        else if (e is Face3D f3d)
        {
            yield return new XY(f3d.FirstCorner.X, f3d.FirstCorner.Y);
            yield return new XY(f3d.SecondCorner.X, f3d.SecondCorner.Y);
            yield return new XY(f3d.ThirdCorner.X, f3d.ThirdCorner.Y);
            yield return new XY(f3d.FourthCorner.X, f3d.FourthCorner.Y);
        }
        else if (e is RasterImage ri)
        {
            yield return new XY(ri.InsertPoint.X, ri.InsertPoint.Y);
            yield return new XY(ri.InsertPoint.X + ri.UVector.X * ri.Size.X, ri.InsertPoint.Y + ri.UVector.Y * ri.Size.Y);
            yield return new XY(ri.InsertPoint.X + ri.VVector.X * ri.Size.Y, ri.InsertPoint.Y + ri.VVector.Y * ri.Size.Y);
        }
        else if (e is Ole2Frame olef2) // OLE2 嵌入对象：UL/LR 两角（与 ComputeExtents 一致，防块内图片撑大内容盒）
        {
            yield return new XY(olef2.UpperLeftCorner.X, olef2.UpperLeftCorner.Y);
            yield return new XY(olef2.LowerRightCorner.X, olef2.LowerRightCorner.Y);
        }
        else if (e is Circle c) { yield return new XY(c.Center.X - c.Radius, c.Center.Y - c.Radius); yield return new XY(c.Center.X + c.Radius, c.Center.Y + c.Radius); }
        else if (e is Arc a) { yield return new XY(a.Center.X - a.Radius, a.Center.Y - a.Radius); yield return new XY(a.Center.X + a.Radius, a.Center.Y + a.Radius); }
        else if (e is Ellipse el) { double r = Math.Max(el.MajorAxis, el.MinorAxis) / 2.0; yield return new XY(el.Center.X - r, el.Center.Y - r); yield return new XY(el.Center.X + r, el.Center.Y + r); } // fork MajorAxis=全长(2a)，与 Circle/Arc 的半半径一致
        else if (e is TextEntity t) { var tm = TextMetrics(t); foreach (var cp in TextBoxCorners(t.InsertPoint.X, t.InsertPoint.Y, tm)) yield return cp; }
        else if (e is MText mt) { var tm = TextMetrics(mt); foreach (var cp in TextBoxCorners(mt.InsertPoint.X, mt.InsertPoint.Y, tm)) yield return cp; }
        else if (e is Solid so)
        {
            yield return new XY(so.FirstCorner.X, so.FirstCorner.Y);
            yield return new XY(so.SecondCorner.X, so.SecondCorner.Y);
            yield return new XY(so.ThirdCorner.X, so.ThirdCorner.Y);
            yield return new XY(so.FourthCorner.X, so.FourthCorner.Y);
        }
        else if (e is Spline sp2)
            { foreach (var p in SplineSample(sp2, null, 4)) yield return new XY(p.X, p.Y); }
        else if (e is Hatch ht2)
            { foreach (var loop in BuildHatchLoops(ht2, null))
                foreach (var p in loop) yield return new XY(p.X, p.Y); }
    }

    #endregion

    #region ===== 导出 SVG =====
    public ImageExportResult WriteSvg(string outPath)
    {
        if (!ComputeExtents())
        {
            _log("⚠️  SVG 无可用实体，跳过");
            return new ImageExportResult { Success = false };
        }

        _scale = 1.0; // 1:1 实际尺寸
        double dataW = _maxX - _minX;
        double dataH = _maxY - _minY;
        _margin = Math.Max(dataW, dataH) * 0.02 + 1.0; // 少量边距（实际尺寸）
        if (!double.IsFinite(_margin) || _margin <= 0) _margin = 2.0;
        _w = dataW + 2 * _margin;
        _h = dataH + 2 * _margin;
        ExpandViewForPointGlyphs(); // 点样式字形（r 可达视高 5%）四边扩界，避免边缘点被裁

        BuildLinetypeDash();

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        // 画布 = 1:1 内容尺寸 × OutputZoom（viewBox 保持 1:1 坐标，浏览器统一放大）
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(_w * _opt.OutputZoom)}\" height=\"{F(_h * _opt.OutputZoom)}\" viewBox=\"0 0 {F(_w)} {F(_h)}\">");
        // 背景（默认黑色；WhiteBackground=true 时白色；TransparentBackground=true 时省略背景矩形=透明）
        if (!_transparentBg)
            sb.AppendLine($"  <rect x=\"0\" y=\"0\" width=\"{F(_w)}\" height=\"{F(_h)}\" fill=\"{(_whiteBg ? "#FFFFFF" : "#000000")}\"/>");

        _svgDefs = new StringBuilder();
        _hatchGradSeq = 0;
        _skippedCounts.Clear();
        foreach (var ent in AllEntities())
            EmitEntitySvg(sb, ent, null, 1.0, 7, null, null, _ltScale); // 顶层无 INSERT 上下文，ByBlock 兜底白/实线

        if (_svgDefs.Length > 0)
        {
            sb.AppendLine("  <defs>");
            sb.Append(_svgDefs);
            sb.AppendLine("  </defs>");
        }
        sb.AppendLine("</svg>");
        var svgDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(svgDir) && !Directory.Exists(svgDir))
            Directory.CreateDirectory(svgDir);
        File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
        _log($"✅  SVG 已生成：{outPath}（画板 {F(_w * _opt.OutputZoom)}×{F(_h * _opt.OutputZoom)}，1:1 内容放大 {F(_opt.OutputZoom)} 倍）");
        LogSkipped("SVG");
        return new ImageExportResult
        {
            Success = true,
            CanvasPixelsW = (int)Math.Ceiling(_w * _opt.OutputZoom),
            CanvasPixelsH = (int)Math.Ceiling(_h * _opt.OutputZoom),
            PixelsPerUnit = _opt.OutputZoom,
            DataX0 = _minX - _margin,
            DataX1 = _maxX + _margin,
            DataY0 = _minY - _margin,
            DataY1 = _maxY + _margin,
        };
    }

    // 发射单个实体（含 Insert 展开）。tr 为累积变换（用于嵌套块），scaleMul 为线宽/文字高度倍率，
    // byBlockAci 为"随块"实体应继承的颜色（= 当前 INSERT 的有效颜色，顶层为 7），
    // byBlockDash 为"随块"实体应继承的虚线模式（= 当前 INSERT 的有效线型，顶层为 null=实线），
    // ltMul 为线型比例乘数（= $LTSCALE × 外层块插入比例之积；顶层 = _ltScale），
    // byLayerOverride 为"随图层"实体应继承的图层色（= 当前 INSERT 所在图层色；顶层为 null=用实体自身图层）。
    private void EmitEntitySvg(StringBuilder sb, Entity ent, Transform2D tr, double scaleMul, short byBlockAci, double[] byBlockDash, LtpPattern byBlockPat, double ltMul, short? byLayerOverride = null)
    {
        // 块内 ByLayer 图元按 CAD 语义随"插入点块所在图层"（byLayerOverride），
        // 顶层实体则用自身图层色。
        short layerAci;
        if (byLayerOverride.HasValue) layerAci = byLayerOverride.Value;
        else { layerAci = 7; try { layerAci = LayerAciIndex(ent.Layer); } catch { } }
        double[] dash = ResolveDash(ent, byBlockDash, ltMul);
        LtpPattern pat = ResolvePattern(ent, byBlockPat, ltMul); // 复杂线型（含 SHX 段）→ 沿路径真实渲染

        switch (ent)
        {
            case Insert ins: EmitInsertSvg(sb, ins, tr, byBlockAci, dash, pat, ltMul); break;

            case Line ln:
                {
                    var (x1, y1) = tr == null ? (ln.StartPoint.X, ln.StartPoint.Y) : tr.Apply(ln.StartPoint.X, ln.StartPoint.Y);
                    var (x2, y2) = tr == null ? (ln.EndPoint.X, ln.EndPoint.Y) : tr.Apply(ln.EndPoint.X, ln.EndPoint.Y);
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    if (!TryEmitComplexSvg(sb, ent, tr, pat, col, w))
                        EmitLine(sb, x1, y1, x2, y2, col, w, dash);
                }
                break;

            case LwPolyline pl:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    // 普通描边路径采样世界折线点（含 bulge 弧段）；变宽路径走 EmitTaperedPolyline（自有 bulge 逻辑）
                    var pts = LwPolylineSamplePoints(pl, tr);
                    // 几何宽度（组码43 常数宽 / 40 逐顶点起宽）：>0 时按 CAD 语义渲染为"填充多边形"（变宽）；否则按线宽(370)描边。
                    // 关键：判断"是否变宽"须看**任意**顶点 StartWidth(40) 或 ConstantWidth(43)，不能只看首顶点——
                    // 首顶点恰为窄端（StartWidth=0）的变宽多段线（如闭合纺锤形 0→5→0→5）会被漏判走描边，变宽丢失
                    // （用户反馈"两点间变宽没渲染"）。EndWidth(41) 不单独触发变宽（闭合圆环全 sw=0/ew=常数 应走
                    // 描边+bulge 弧，否则变宽路径会退化成 0 宽消失）。
                    // 常数宽(43)：DWG 往返常把全顶点等宽归一为 ConstantWidth（逐顶点置 0）→ 物化为逐顶点
                    // [PATCH] Option B：仅当 ConstantWidthSpecified（源文件确有 43）才物化。GDB2CAD 合成
                    // 站点只设 ConstantWidth 不设 Specified，DWG/DXF writer 也仅在 Specified 时写 43 →
                    // 读回侧无 43、按细线渲染，live 侧必须同口径，否则 live 与读回的 SVG/PNG 字节不一致。
                    if (pl.ConstantWidthSpecified && pl.ConstantWidth > 0 && !pl.Vertices.Any(v => v.StartWidth > 0))
                        foreach (var v in pl.Vertices) { v.StartWidth = pl.ConstantWidth; v.EndWidth = pl.ConstantWidth; }
                    // 变宽：任意顶点 起宽(40)/终宽(41) 非零，或 常数宽(43) 非零 → 走填充多边形（变宽）路径；
                    // 全 0 才按默认线宽(370)细线描边。逐顶点有效宽度由 TaperedVertexWidths 计算
                    // （顶点宽=本顶点起宽40，起宽为0时继承入段终宽41，正确处理"每节起点/终点线宽不等"）。
                    bool hasTaper = pl.Vertices.Any(v => v.StartWidth > 0 || v.EndWidth > 0) || (pl.ConstantWidthSpecified && pl.ConstantWidth > 0);
                    if (hasTaper && pts.Count >= 2)
                    {
                        EmitTaperedPolyline(sb, pl, tr, col, scaleMul, dash);
                        break;
                    }
                    double w = StrokeWidth(ent, scaleMul);
                    EmitPolyline(sb, pts, col, w, pl.IsClosed, dash);
                }
                break;

            case Polyline3D p3:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var pts = p3.Vertices.Where(v => IsDisplayVertex(v)) // 跳过样条框架控制点（不显示）
                        .Select(v => tr == null ? (v.Location.X, v.Location.Y) : tr.Apply(v.Location.X, v.Location.Y)).ToList();
                    // 闭合 3D 折线需补闭合段（旧版硬编码 false 会漏最后一段）
                    if (pts.Count >= 2) EmitPolyline(sb, pts, col, StrokeWidth(ent, scaleMul), p3.IsClosed, dash);
                }
                break;

            case Polyline2D p2d:
                {
                    // 2D 多段线（老式 POLYLINE+VERTEX）：顶点折线折线（bulge 弧段暂按直线弦，后续可采样为弧）
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var pts = new List<(double X, double Y)>(p2d.Vertices.Count);
                    foreach (var v in p2d.Vertices)
                    {
                        if (!IsDisplayVertex(v)) continue; // 跳过样条框架控制点（不显示）
                        var p = tr == null ? (v.Location.X, v.Location.Y) : tr.Apply(v.Location.X, v.Location.Y);
                        pts.Add(p);
                    }
                    if (pts.Count >= 2) EmitPolyline(sb, pts, col, StrokeWidth(ent, scaleMul), p2d.IsClosed, dash);
                }
                break;

            case Mesh mesh:
                {
                    // 3D 网格（现代 MESH）：等轴测 3D 着色实体渲染（Bug4 DOME）
                    // 有面 → 等轴测投影 + 背面剔除 + 法线灰度着色 + 画家算法排序 + 可见边实线/隐藏边虚线
                    // 无面 → 退化为顶视线框
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    var isoRes = MeshIsoRender(mesh);
                    if (isoRes.Faces.Count > 0)
                    {
                        // 先画面（远→近，画家算法），再画边
                        for (int fi = 0; fi < isoRes.Faces.Count; fi++)
                        {
                            int g = isoRes.Grays[fi];
                            EmitQuadFill(sb, isoRes.Faces[fi], $"rgb({g},{g},{g})");
                        }
                        double ew = Math.Max(0.4, w);
                        foreach (var ed in isoRes.Vis)
                            EmitLine(sb, ed.X1, ed.Y1, ed.X2, ed.Y2, "#000000", ew, null);
                        double[] hidDash = { 1.2, 0.8 };
                        foreach (var ed in isoRes.Hid)
                            EmitLine(sb, ed.X1, ed.Y1, ed.X2, ed.Y2, "#666666", Math.Max(0.25, ew * 0.6), hidDash);
                    }
                    else if (mesh.Edges != null && mesh.Vertices != null && mesh.Vertices.Count > 0)
                    {
                        foreach (var e in mesh.Edges)
                        {
                            if (e.Start < 0 || e.End < 0 || e.Start >= mesh.Vertices.Count || e.End >= mesh.Vertices.Count) continue;
                            var va = mesh.Vertices[e.Start];
                            var vb = mesh.Vertices[e.End];
                            var (mx1, my1) = tr == null ? (va.X, va.Y) : tr.Apply(va.X, va.Y);
                            var (mx2, my2) = tr == null ? (vb.X, vb.Y) : tr.Apply(vb.X, vb.Y);
                            EmitLine(sb, mx1, my1, mx2, my2, col, w, null);
                        }
                    }
                }
                break;

            case PolyfaceMesh pf:
                {
                    // 旧式 3D 多面体网格：按 Face 索引连线成线框（三角/四边形）
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, w)) break;
                    foreach (var (a, b) in PolyfaceMeshWire(pf, tr))
                        EmitLine(sb, a.AX, a.AY, b.BX, b.BY, col, w, null);
                }
                break;

            case PolygonMesh pm:
                {
                    // M×N 多边形网格：网格线框（行线+列线），参数无效退化为顶点折线
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, w)) break;
                    foreach (var (a, b) in PolygonMeshWire(pm, tr))
                        EmitLine(sb, a.AX, a.AY, b.BX, b.BY, col, w, null);
                }
                break;

            case Wipeout wo:
                {
                    // WIPEOUT 属性渲染（三引擎共享 WipeoutFillHex/WipeoutFrame）：
                    //  填充=42 属性(0/缺省=背景擦除,71=黑,72=白,73=灰)；透明底不画填充(叠加到未知底色时背景色不可知)；
                    //  边框=文件全局 DisplayImageFrame(WIPEOUTVARIABLES 组码70)=1 或 41>0，色=图元/图层色（有图/无图均适用）。
                    // 边框为真实颜色（图元/图层色），透明底也绘制；仅填充(背景擦除)在透明底跳过。
                    var wpoly = WipeoutWorldPoly(wo, tr);
                    if (wpoly.Count >= 3)
                    {
                        if (!_transparentBg)
                        {
                            string fill = WipeoutFillHex(wo.FillColor, _whiteBg);
                            EmitQuadFill(sb, wpoly, fill);
                        }
                        var frame = WipeoutFrame(wo, layerAci, byBlockAci, scaleMul);
                        if (frame.Show)
                        {
                            var fd = new StringBuilder();
                            fd.Append("M");
                            for (int i = 0; i < wpoly.Count; i++)
                                fd.Append($"{(i == 0 ? "" : "L")}{F(Sx(wpoly[i].X))},{F(Sy(wpoly[i].Y))}");
                            fd.Append("Z");
                            sb.AppendLine($"  <path d=\"{fd}\" fill=\"none\" stroke=\"{frame.Col}\" stroke-width=\"{F(Math.Max(0.1, frame.W))}\"/>");
                        }
                    }
                }
                break;

            case RasterImage ri:
                EmitRasterImageSvg(sb, ri, tr);
                break;

            case Ole2Frame olef3:
                EmitOle2FrameSvg(sb, olef3, tr);
                break;

            case Arc a: // 必须先于 Circle（fork 继承关系）
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (a.Center.X, a.Center.Y) : tr.Apply(a.Center.X, a.Center.Y);
                    double r = a.Radius * (tr?.Scale ?? 1.0);
                    // fork 角度为弧度 → 转度（参考实现入参为度）
                    EmitArcAsPolyline(sb, cx, cy, r, a.StartAngle * 180.0 / Math.PI, a.EndAngle * 180.0 / Math.PI, col, StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Circle c:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (c.Center.X, c.Center.Y) : tr.Apply(c.Center.X, c.Center.Y);
                    double r = c.Radius * (tr?.Scale ?? 1.0);
                    EmitCircle(sb, cx, cy, r, col, StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Ellipse el:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (el.Center.X, el.Center.Y) : tr.Apply(el.Center.X, el.Center.Y);
                    // fork MajorAxis=主轴全长(2a)、MinorAxis=短轴全长(2b)；EmitEllipse 的 rx/ry 是半轴
                    // → 各除以 2（此前误用全长当半轴，椭圆画成 2 倍大小，与 AutoCAD 不一致）
                    double rx = el.MajorAxis / 2.0 * (tr?.Scale ?? 1.0);
                    double ry = el.MinorAxis / 2.0 * (tr?.Scale ?? 1.0);
                    double rot = el.Rotation * 180.0 / Math.PI + (tr?.Rotation ?? 0.0); // fork 弧度 → 度
                    EmitEllipse(sb, cx, cy, rx, ry, rot, col, StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Point pt:
                {
                    var (cx, cy) = tr == null ? (pt.Location.X, pt.Location.Y) : tr.Apply(pt.Location.X, pt.Location.Y);
                    // 点标记颜色随实体/图层（与 CAD 一致）；大小与线宽保持一致（直径 = 3× 当前线宽）
                    EmitPoint(sb, cx, cy, ColorOf(EntityAciIndex(ent), layerAci, byBlockAci), StrokeWidth(ent, scaleMul));
                }
                break;

            case TextEntity t:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    // SHX 样式（txt.shx/simpex.shx+Hztxt.shx 等）：用真实 SHX 笔迹路径渲染（与 AutoCAD 一致的字宽/字形）。
                    // 非 SHX 样式或字体缺失 → 回退原系统字体（SimSun）路径，行为逐字节不变。
                    var shx = TryBuildShxTextWorld(t, tr, scaleMul);
                    if (shx != null)
                    {
                        var d = new StringBuilder();
                        for (int k = 0; k < shx.Value.Type.Count; k++)
                        {
                            var p = shx.Value.Pts[k];
                            switch (shx.Value.Type[k])
                            {
                                case 0: d.Append("M").Append(F(Sx(p[0]))).Append(',').Append(F(Sy(p[1]))); break;
                                case 1: d.Append("L").Append(F(Sx(p[0]))).Append(',').Append(F(Sy(p[1]))); break;
                                case 2: d.Append("Q").Append(F(Sx(p[0]))).Append(',').Append(F(Sy(p[1]))).Append(' ').Append(F(Sx(p[2]))).Append(',').Append(F(Sy(p[3]))); break;
                                case 3: d.Append("C").Append(F(Sx(p[0]))).Append(',').Append(F(Sy(p[1]))).Append(' ').Append(F(Sx(p[2]))).Append(',').Append(F(Sy(p[3]))).Append(' ').Append(F(Sx(p[4]))).Append(',').Append(F(Sy(p[5]))); break;
                            }
                        }
                        sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{col}\" stroke-width=\"{F(shx.Value.PenWidth)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>");
                        break;
                    }
                    // TTF 样式：cap-height 字号映射（AutoCAD 把 H 当 cap height，非 em，见 TtfCapScale）
                    var (bx, by, brot, bscale) = TextBaseline(t, tr);
                    double fs = t.Height * scaleMul * bscale * TtfCapScale(ResolveFontFamily(GetStyleFont(t.Style)));
                    string cssFont = SvgFontFamily(ResolveFontFamily(GetStyleFont(t.Style)));
                    EmitText(sb, bx, by, t.Value ?? "", Math.Max(fs, 0.5), col, -brot, cssFont, t.WidthFactor);
                }
                break;

            case MText mt:
                // 多行文字：解析格式码（\P 换行/\H 分段字号/\f 字体）+ 参考列宽换行 + 附着点定位
                EmitMTextSvg(sb, mt, tr, scaleMul, layerAci, byBlockAci, GetStyleFont(mt.Style));
                break;

            case Solid so:
                EmitQuadFill(sb, SolidQuadPts(so.FirstCorner, so.SecondCorner, so.ThirdCorner, so.FourthCorner, tr), ColorOf(EntityAciIndex(ent), layerAci, byBlockAci));
                break;

            case Face3D fd:
                EmitQuadFill(sb, QuadPts(fd.FirstCorner, fd.SecondCorner, fd.ThirdCorner, fd.FourthCorner, tr), ColorOf(EntityAciIndex(ent), layerAci, byBlockAci));
                break;

            case Hatch ht:
                // HATCH 填充（实心=实体色/真色；450=1 渐变=SVG linear/radialGradient；图案 T4.5）
                EmitHatchSvg(sb, ht, tr, scaleMul, layerAci, byBlockAci);
                break;

            case Spline sp:
                {
                    // SPLINE（控制点+节矢 → de Boor NURBS 采样；仅拟合点 → 拟合折线）
                    string col = ColorOf(EntityAciIndex(sp), layerAci, byBlockAci);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, StrokeWidth(sp, scaleMul))) break;
                    EmitSplineSvg(sb, sp, tr, scaleMul, col, dash);
                }
                break;

            case XLine xl:
                {
                    // 无限线：裁剪到画布矩形后渲染（与 AutoCAD 视口显示一致）
                    var (px, py) = tr == null ? (xl.FirstPoint.X, xl.FirstPoint.Y) : tr.Apply(xl.FirstPoint.X, xl.FirstPoint.Y);
                    var (dx, dy) = TransformDirection(xl.Direction.X, xl.Direction.Y, tr);
                    if (ClipInfiniteLineToCanvas(px, py, dx, dy, true, out var x1, out var y1, out var x2, out var y2))
                        EmitLine(sb, x1, y1, x2, y2, ColorOf(EntityAciIndex(ent), layerAci, byBlockAci), StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Ray ray:
                {
                    // 射线：仅起点之后，裁剪到画布矩形后渲染
                    var (px, py) = tr == null ? (ray.StartPoint.X, ray.StartPoint.Y) : tr.Apply(ray.StartPoint.X, ray.StartPoint.Y);
                    var (dx, dy) = TransformDirection(ray.Direction.X, ray.Direction.Y, tr);
                    if (ClipInfiniteLineToCanvas(px, py, dx, dy, false, out var x1, out var y1, out var x2, out var y2))
                        EmitLine(sb, x1, y1, x2, y2, ColorOf(EntityAciIndex(ent), layerAci, byBlockAci), StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case MLine ml:
                {
                    // 多线：按 DXF 41 参数化提取各子线的实际画线段（含画/断模式与开口长短），
                    // 逐段描边细线。MLINE 正确语义=一组平行细线（AutoCAD 2D 渲染永不填充）。
                    // 旧实现只用 p[0](offset) 画整条子线，忽略 p[2..] 画/断区间 → 缺失开口/长短齿。
                    double w = StrokeWidth(ent, scaleMul);
                    foreach (var el in MLineDrawnElements(ml, tr))
                    {
                        string col = ColorOf(el.Aci, layerAci, byBlockAci);
                        foreach (var s in el.Segs)
                        {
                            var pts = new List<(double X, double Y)> { (s.X1, s.Y1), (s.X2, s.Y2) };
                            EmitPolyline(sb, pts, col, w, false, dash);
                        }
                    }
                }
                break;

            case Leader ld:
                {
                    // 引线：折线/平滑曲线 + 尖端箭头（AutoCAD 默认实心/开放箭头，此处用开放 V 形）
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    // DXF 组码71：缺省=有箭头，0=无箭头；XData 341 指向空块（_None）= 显式无箭头
                    var (arrowBr, asz, noArrow) = LeaderArrowResolve(ld, _doc, Math.Max(w * 6.0, 4.0 * (tr?.Scale ?? 1.0)));
                    bool arrow = ld.ArrowHeadEnabled && !noArrow;
                    // 世界坐标显示路径：直线引线含钩线合成（角点 + 水平着陆段，端点按标注控制延长，详见 LeaderDisplayPath）；
                    // 样条引线返回 null（下方 LeaderSplineFlatten 处理，末段切向强制水平着陆）。
                    var worldPath = LeaderDisplayPath(ld, _doc, asz);
                    var pts = new List<(double X, double Y)>();
                    if (worldPath != null)
                    {
                        foreach (var p in worldPath) pts.Add(tr == null ? p : tr.Apply(p.X, p.Y));
                    }
                    else if (ld.Vertices != null)
                    {
                        foreach (var v in ld.Vertices)
                        {
                            var p = tr == null ? (v.X, v.Y) : tr.Apply(v.X, v.Y);
                            pts.Add(p);
                        }
                    }
                    if (pts.Count >= 2)
                    {
                        var trA = arrow ? LeaderArrowTransform(pts, asz) : null;
                        // 箭头几何（块 strokes/fills 或默认实心三角）先算，得到底边距离 trim，再截引线起点：
                        // 引线自末端侧进入箭头，线应停于箭头底边（LeaderTrimDistance），箭头叠在其上——
                        // 空心箭头不再有线穿入内部、块箭头尖端不再有线伸入块内（AutoCAD 行为）。
                        // 刻线 _ArchTick/_Oblique 的斜线过尖端 → trim≈0（不截断，线接斜线中点=尖端），
                        // 避免旧"截到块底边"丢尖端→底边一段横线的陷阱。
                        List<List<(double X, double Y)>> aStrokes = null, aFills = null;
                        List<(double X, double Y)> aTri = null;
                        double trim = 0;
                        if (arrow && trA != null)
                        {
                            if (arrowBr != null && !IsClosedFilledArrow(arrowBr))
                            {
                                aStrokes = new List<List<(double X, double Y)>>();
                                aFills = new List<List<(double X, double Y)>>();
                                LeaderArrowGeom(arrowBr, trA, aStrokes, aFills);
                                trim = LeaderTrimDistance(pts, aStrokes, aFills);
                            }
                            else
                            {
                                var tip = trA.Apply(0, 0);
                                var c1 = trA.Apply(-1.0, 0.16666667);
                                var c2 = trA.Apply(-1.0, -0.16666667);
                                aTri = new List<(double X, double Y)> { tip, c1, c2 };
                                trim = LeaderTrimDistance(pts, null, new List<List<(double X, double Y)>> { aTri });
                            }
                        }
                        // 截断距离夹到首段长度内，防止越过第一个顶点（箭头应落在首段内）。
                        double firstSeg = Math.Sqrt(
                            (pts[1].X - pts[0].X) * (pts[1].X - pts[0].X) +
                            (pts[1].Y - pts[0].Y) * (pts[1].Y - pts[0].Y));
                        if (trim > firstSeg) trim = firstSeg;
                        // 样条引线（组码72=1，PathType=Spline）：路径为穿过全部顶点的平滑曲线（LeaderSplineFlatten，末段强制水平着陆，
                        // 与 GT 一致）；直线引线用 LeaderDisplayPath 合成的显示路径（含钩线/水平着陆段）。
                        var lp = (ld.PathType == LeaderPathType.Spline)
                            ? LeaderSplineFlatten(pts, 24)
                            : pts;
                        EmitPolyline(sb, TrimPolylineStart(lp, trim), col, w, false, dash);
                        // 箭头（无箭头 71=0 / XData 空块时不画）：
                        //  · 块箭头（非 _Closed）→ 按块几何忠实渲染：空心轮廓描边 / 常宽闭合填充（_ClosedBlank 空心、_Dot 实心圆点等）。
                        //  · _Closed 块或无块（系统默认闭合箭头）→ 单个实心三角，按解析尺寸 asz（trA 已缩放）。
                        //    尺寸取样式 ArrowSize(DIMASZ, 组码41)。
                        if (arrow && trA != null)
                        {
                            if (aStrokes != null)
                            {
                                foreach (var s in aStrokes) EmitPolyline(sb, s, col, w, false, dash);
                                foreach (var f in aFills) EmitQuadFill(sb, f, col);
                            }
                            else if (aTri != null)
                            {
                                EmitQuadFill(sb, aTri, col);
                            }
                        }
                    }
                }
                break;

            case Dimension dim:
                {
                    // 标注：递归渲染 dim.Block 内实体（尺寸线/箭头/文字/弧/Insert 均为世界坐标）。
                    // 跳过 Defpoints 图层的参考点（AutoCAD 不显示）。
                    if (dim.Block != null && dim.Block.Entities.Count > 0)
                    {
                        short dimAci = EffectiveAci(ent, byBlockAci);
                        short dimLayerAci = 7;
                        try { dimLayerAci = LayerAciIndex(dim.Layer); } catch { }
                        foreach (var be in BlockEntitiesInFileOrder(dim.Block))
                        {
                            // Defpoints 图层的 Point（定义点/锚点）不渲染
                            if (be is Point)
                            {
                                try { if (be.Layer != null && be.Layer.Name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)) continue; } catch { }
                            }
                            EmitEntitySvg(sb, be, null, 1.0, dimAci, null, null, ltMul, dimLayerAci);
                        }
                    }
                    else
                        RecordSkip(ent.GetType().Name);
                }
                break;

            case Shape shp:
                {
                    // SHX 形状实体（SHAPE）：用 shape 字体库的真实字形笔迹渲染（AutoCAD shape 变换）。
                    string shpCol = ColorOf(EntityAciIndex(shp), layerAci, byBlockAci);
                    string shpPath = BuildShapePath(shp, tr, Sx, Sy, pdf: false);
                    if (shpPath.Length > 0)
                        sb.AppendLine($"  <path d=\"{shpPath}\" fill=\"none\" stroke=\"{shpCol}\" stroke-width=\"{F(StrokeWidth(shp, scaleMul))}\" stroke-linejoin=\"round\"/>");
                }
                break;

            default:
                RecordSkip(ent.GetType().Name);
                break;
        }
    }

    // SVG 光栅图片（RasterImage）：外部图片以 data URI 内联为 <image>，用仿射矩阵把图像单位矩形
    // 映射到 CAD 四边形（a0=左下 InsertPoint，a10=右下，a01=左上）。SVG 无原生解码，直接内联原文件字节；
    // 浏览器原生渲染 JPEG/PNG。文件缺失或无法识别时降级为矩形边框 + 对角线。
    private void EmitRasterImageSvg(StringBuilder sb, RasterImage ri, Transform2D tr)
    {
        var ip = ri.InsertPoint;
        double W = ri.Size.X, H = ri.Size.Y;
        var u = ri.UVector; var v = ri.VVector;
        (double X, double Y) P(double x, double y) => tr == null ? (x, y) : tr.Apply(x, y);
        var a0 = P(ip.X, ip.Y);
        var a10 = P(ip.X + u.X * W, ip.Y + u.Y * W);
        var a01 = P(ip.X + v.X * H, ip.Y + v.Y * H);
        double a0x = Sx(a0.X), a0y = Sy(a0.Y);
        double a10x = Sx(a10.X), a10y = Sy(a10.Y);
        double a01x = Sx(a01.X), a01y = Sy(a01.Y);
        string path = ResolveAssetPath(ri.Definition?.FileName);
        if (string.IsNullOrEmpty(path))
        {
            SvgImageRectFallback(sb, a0x, a0y, a10x, a10y, a01x, a01y, "#888888");
            RecordSkip("IMAGE(文件缺失)");
            _log("⚠️  IMAGE 图片文件缺失，降级为边框: " + (ri.Definition?.FileName ?? "?"));
            return;
        }
        byte[] bytes = File.ReadAllBytes(path);
        string mime = "image/png";
        if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) mime = "image/jpeg";
        else if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50) mime = "image/png";
        int iw = 0, ih = 0;
        var jpg = RasterHelpers.ProbeJpeg(bytes);
        if (jpg != null) { iw = jpg.Width; ih = jpg.Height; }
        else { try { var d = RasterHelpers.DecodePngToRgb(bytes); iw = d.width; ih = d.height; } catch { } }
        if (iw <= 0 || ih <= 0)
        {
            SvgImageRectFallback(sb, a0x, a0y, a10x, a10y, a01x, a01y, "#888888");
            RecordSkip("IMAGE(无法读取)");
            _log("⚠️  IMAGE 图片无法识别，降级为边框: " + path);
            return;
        }
        string b64 = Convert.ToBase64String(bytes);
        // 矩阵：SVG <image> 局部 (0,0)=图像顶行左 → a01(左上)；(IW,0)=顶行右 → a11(右上)；(0,IH)=底行左 → a0(左下)
        // SVG 局部 v 向下(row 增加向下)，故图像底行(底左)应映射到 a0，垂直向量取 (a0-a01)（注意符号，与 PDF y 向上相反）
        double m = (a10x - a0x) / iw;
        double bb = (a10y - a0y) / iw;
        double c = (a0x - a01x) / ih;
        double dd = (a0y - a01y) / ih;
        double e = a01x, f = a01y;
        sb.Append("  <image x=\"0\" y=\"0\" width=\"").Append(iw).Append("\" height=\"").Append(ih)
          .Append("\" preserveAspectRatio=\"none\" transform=\"matrix(")
          .Append(F(m)).Append(' ').Append(F(bb)).Append(' ').Append(F(c)).Append(' ')
          .Append(F(dd)).Append(' ').Append(F(e)).Append(' ').Append(F(f))
          .Append(")\" href=\"data:").Append(mime).Append(";base64,").Append(b64).Append("\"/>\n");
    }

    // SVG OLE2 嵌入对象（Ole2Frame，组码310 内嵌 OLE CFB 容器）：
    // 提取内嵌 BMP（OLE 图片对象的 DIB 预览）→ 解码 RGBA → 离屏透明画布编码 PNG → data URI 内联 <image>，
    // 按 UL/LR 两角矩形仿射贴图（矩阵与 EmitRasterImageSvg 同式）。非图片类 OLE/提取/解码失败 → 边框 + 对角线降级。
    private void EmitOle2FrameSvg(StringBuilder sb, Ole2Frame ole, Transform2D tr)
    {
        double xL = Math.Min(ole.UpperLeftCorner.X, ole.LowerRightCorner.X);
        double xR = Math.Max(ole.UpperLeftCorner.X, ole.LowerRightCorner.X);
        double yB = Math.Min(ole.UpperLeftCorner.Y, ole.LowerRightCorner.Y);
        double yT = Math.Max(ole.UpperLeftCorner.Y, ole.LowerRightCorner.Y);
        (double X, double Y) P(double x, double y) => tr == null ? (x, y) : tr.Apply(x, y);
        var a0 = P(xL, yB);   // 左下
        var a10 = P(xR, yB);  // 右下
        var a01 = P(xL, yT);  // 左上
        double a0x = Sx(a0.X), a0y = Sy(a0.Y);
        double a10x = Sx(a10.X), a10y = Sy(a10.Y);
        double a01x = Sx(a01.X), a01y = Sy(a01.Y);
        if (!Ole2Frame.TryExtractBmp(ole.BinaryData, out byte[] bmpBytes))
        {
            SvgImageRectFallback(sb, a0x, a0y, a10x, a10y, a01x, a01y, "#888888");
            RecordSkip("OLE2FRAME(无内嵌BMP)");
            _log("⚠️  OLE2FRAME 未提取到内嵌 BMP（类型=" + ole.OleObjectType + "），降级为边框");
            return;
        }
        int iw = 0, ih = 0; byte[] pix = null;
        try { var d = RasterHelpers.DecodeBmpToRgba(bmpBytes); iw = d.width; ih = d.height; pix = d.rgba; }
        catch (Exception ex) { _log("⚠️  OLE2FRAME 内嵌 BMP 解码失败: " + ex.Message); }
        if (iw <= 0 || ih <= 0 || pix == null)
        {
            SvgImageRectFallback(sb, a0x, a0y, a10x, a10y, a01x, a01y, "#888888");
            RecordSkip("OLE2FRAME(BMP解码失败)");
            return;
        }
        // 24/32 位 DIB 无透明通道，浏览器不支持 BMP data URI → 离屏透明画布 blit 后编码 PNG 内联
        var rc = new RasterCanvas(iw, ih);
        rc.Clear(0, 0, 0, 0);
        rc.BlitRgba(pix, iw, ih, 0, 0);
        byte[] png = rc.ToPngBytes();
        if (png == null)
        {
            SvgImageRectFallback(sb, a0x, a0y, a10x, a10y, a01x, a01y, "#888888");
            RecordSkip("OLE2FRAME(PNG编码失败)");
            return;
        }
        string b64 = Convert.ToBase64String(png);
        double m = (a10x - a0x) / iw, bb = (a10y - a0y) / iw;
        double c = (a0x - a01x) / ih, dd = (a0y - a01y) / ih;
        double e = a01x, f = a01y;
        sb.Append("  <image x=\"0\" y=\"0\" width=\"").Append(iw).Append("\" height=\"").Append(ih)
          .Append("\" preserveAspectRatio=\"none\" transform=\"matrix(")
          .Append(F(m)).Append(' ').Append(F(bb)).Append(' ').Append(F(c)).Append(' ')
          .Append(F(dd)).Append(' ').Append(F(e)).Append(' ').Append(F(f))
          .Append(")\" href=\"data:image/png;base64,").Append(b64).Append("\"/>\n");
    }

    // SVG 图片降级：用四边形边框 + 对角线表示图片位置/尺寸
    private void SvgImageRectFallback(StringBuilder sb, double a0x, double a0y, double a10x, double a10y, double a01x, double a01y, string col)
    {
        double a11x = a10x + a01x - a0x, a11y = a10y + a01y - a0y;
        sb.Append("  <polygon points=\"")
          .Append(F(a0x)).Append(',').Append(F(a0y)).Append(' ')
          .Append(F(a10x)).Append(',').Append(F(a10y)).Append(' ')
          .Append(F(a11x)).Append(',').Append(F(a11y)).Append(' ')
          .Append(F(a01x)).Append(',').Append(F(a01y))
          .Append("\" fill=\"none\" stroke=\"").Append(col).Append("\" stroke-width=\"0.5\"/>\n");
    }

    // 构建 SHX Shape 实体的 path d 串（世界坐标 → fx/fy 映射）。AutoCAD shape 变换：
    //   局部点(字体单位) → 缩放(Size，X 额外×RelativeXScale) → 斜切(Oblique: x+=y·tanθ)
    //   → 旋转(Rotation) → 平移(InsertionPoint)；块内 shape 再整体过块变换 tr。
    // 插入点 = 字形原点(0,0)（与复杂线型 shape 的"包围盒中心对齐"不同）。
    // fx/fy：SVG 用 Sx/Sy（Y 翻转）；PDF 用 Sx/Py（不翻转）。pdf 参数切换 path 语法。
    // 字形/字体不可用回退"点"（插入点处短实线），返回空串表示无可绘制。
    private string BuildShapePath(Shape shp, Transform2D tr,
                                  Func<double, double> fx, Func<double, double> fy, bool pdf)
    {
        var d = new StringBuilder();
        GlyphRec? rec = null;
        var style = shp.ShapeStyle;
        if (style != null && !string.IsNullOrEmpty(style.Filename))
        {
            var font = GetShapeFont(style.Filename);
            if (font != null)
                rec = GetGlyph(font, style.Filename, shp.ShapeIndex);
        }
        // 插入点（世界）= 块变换作用于原始插入点
        double ipx = shp.InsertionPoint.X, ipy = shp.InsertionPoint.Y;
        if (tr != null) { var a = tr.Apply(ipx, ipy); ipx = a.Item1; ipy = a.Item2; }
        double size = shp.Size > 1e-9 ? shp.Size : 1.0;
        double relX = shp.RelativeXScale > 1e-9 ? shp.RelativeXScale : 1.0;
        double shear = Math.Tan(shp.ObliqueAngle);
        double cr = Math.Cos(shp.Rotation), sr = Math.Sin(shp.Rotation);
        // 字形点(字体单位) → 输出坐标：局部变换 + 插入点 + 块变换
        void Xf(double gx, double gy, out double mx, out double my)
        {
            double sx = gx * relX * size + gy * size * shear; // X 缩放 + 斜切
            double sy = gy * size;                            // Y 缩放
            double lx = sx * cr - sy * sr;                    // 旋转
            double ly = sx * sr + sy * cr;
            double wx = ipx + lx, wy = ipy + ly;
            if (tr != null) { var t = tr.Apply(wx, wy); wx = t.Item1; wy = t.Item2; }
            mx = fx(wx); my = fy(wy);
        }
        if (rec == null)
        {
            // 回退"点"：插入点处短实线（尺寸随 size，沿世界 X）
            double dd = 0.2 * size;
            Xf(-dd, 0, out double ax, out double ay);
            Xf(dd, 0, out double bx, out double by);
            if (pdf) d.Append(F(ax)).Append(' ').Append(F(ay)).Append(" m ").Append(F(bx)).Append(' ').Append(F(by)).Append(" l ");
            else d.Append("M").Append(F(ax)).Append(',').Append(F(ay)).Append("L").Append(F(bx)).Append(',').Append(F(by));
            return d.ToString();
        }
        double curX = 0, curY = 0; bool haveCur = false;
        for (int k = 0; k < rec.Type.Count; k++)
        {
            var p = rec.Pts[k];
            switch (rec.Type[k])
            {
                case 0:
                {
                    Xf(p[0], p[1], out double mxx, out double myy);
                    if (pdf) d.Append(F(mxx)).Append(' ').Append(F(myy)).Append(" m ");
                    else d.Append("M").Append(F(mxx)).Append(',').Append(F(myy));
                    curX = mxx; curY = myy; haveCur = true;
                    break;
                }
                case 1:
                {
                    Xf(p[0], p[1], out double mxx, out double myy);
                    if (pdf) d.Append(F(mxx)).Append(' ').Append(F(myy)).Append(" l ");
                    else d.Append("L").Append(F(mxx)).Append(',').Append(F(myy));
                    curX = mxx; curY = myy; haveCur = true;
                    break;
                }
                case 2:
                {
                    Xf(p[0], p[1], out double qx, out double qy);
                    Xf(p[2], p[3], out double ex, out double ey);
                    if (pdf)
                    {
                        double p0x = haveCur ? curX : qx, p0y = haveCur ? curY : qy;
                        double c1x = p0x + 2.0 / 3.0 * (qx - p0x), c1y = p0y + 2.0 / 3.0 * (qy - p0y);
                        double c2x = ex + 2.0 / 3.0 * (qx - ex), c2y = ey + 2.0 / 3.0 * (qy - ey);
                        d.Append(F(c1x)).Append(' ').Append(F(c1y)).Append(' ').Append(F(c2x)).Append(' ')
                         .Append(F(c2y)).Append(' ').Append(F(ex)).Append(' ').Append(F(ey)).Append(" c ");
                    }
                    else d.Append("Q").Append(F(qx)).Append(',').Append(F(qy)).Append(' ').Append(F(ex)).Append(',').Append(F(ey));
                    curX = ex; curY = ey; haveCur = true;
                    break;
                }
                case 3:
                {
                    Xf(p[0], p[1], out double c1x, out double c1y);
                    Xf(p[2], p[3], out double c2x, out double c2y);
                    Xf(p[4], p[5], out double ex, out double ey);
                    if (pdf) d.Append(F(c1x)).Append(' ').Append(F(c1y)).Append(' ').Append(F(c2x)).Append(' ')
                        .Append(F(c2y)).Append(' ').Append(F(ex)).Append(' ').Append(F(ey)).Append(" c ");
                    else d.Append("C").Append(F(c1x)).Append(',').Append(F(c1y)).Append(' ')
                        .Append(F(c2x)).Append(',').Append(F(c2y)).Append(' ').Append(F(ex)).Append(',').Append(F(ey));
                    curX = ex; curY = ey; haveCur = true;
                    break;
                }
            }
        }
        return d.ToString();
    }

    private void EmitInsertSvg(StringBuilder sb, Insert ins, Transform2D parent, short byBlockAci, double[] byBlockDash, LtpPattern byBlockPat, double ltMul)
    {
        if (ins?.Block == null) return;
        // 环防护（双保险）：同一块在祖先链上重复出现（INSERT 自引用/循环嵌套）→ 截断该层。
        // 正常嵌套块随展开入栈、返回出栈，不受影响。
        if (!_insertAncestors.Add(ins.Block))
        {
            _log($"⚠️  块引用环，截断展开：{ins.Block.Name}");
            return;
        }
        try
        {
            EmitInsertSvgCore(sb, ins, parent, byBlockAci, byBlockDash, byBlockPat, ltMul);
        }
        finally
        {
            _insertAncestors.Remove(ins.Block);
        }
    }

    private void EmitInsertSvgCore(StringBuilder sb, Insert ins, Transform2D parent, short byBlockAci, double[] byBlockDash, LtpPattern byBlockPat, double ltMul)
    {
        var tr = Transform2D.Compose(ins, parent);
        // 块内容的"随块"颜色/线型 = 该 INSERT 的有效值（独立 > 图层；嵌套 ByBlock 继承上层）
        short insAci = EffectiveAci(ins, byBlockAci);
        double[] insDash = ResolveDash(ins, byBlockDash, ltMul);
        LtpPattern insPat = ResolvePattern(ins, byBlockPat, ltMul);
        // 块内"随图层"图元 = 该 INSERT 所在图层色（CAD 语义：块内 ByLayer 随插入点块图层）
        short insLayerAci = 7;
        try { insLayerAci = LayerAciIndex(ins.Layer); } catch { }
        // 块内实体的线型比例额外乘块插入比例（CAD 语义）
        double childLtMul = ltMul * BlockScaleOf(ins);
        // 块内实体。ATTDEF（属性定义/模板，默认值）不在此画——它只是属性模板；
        // 实际显示用 INSERT 跟随的 ATTRIB（ins.Attributes，本次插入的真实属性值，如 "17"）。
        // 否则块定义里的默认值（如 "1"）会被当成文字画出来，覆盖真实属性值。
        bool hasAttribs = ins.Attributes != null && ins.Attributes.Count > 0;
        // [PATCH] 按规范化文件序遍历（FileOrderEntities 优先），消除 HashSet 哈希序的跨进程漂移
        foreach (var e in BlockEntitiesInFileOrder(ins.Block))
        {
            if (hasAttribs && e is AttributeDefinition) continue; // 有实际属性值 → 跳过模板默认值
            EmitEntitySvg(sb, e, tr, tr.Scale, insAci, insDash, insPat, childLtMul, insLayerAci);
        }
        // 属性实际值（ATTRIB）：DXF 中其 InsertPoint（组10）与 Height（组40）在同一坐标系——
        // 顶层插入=世界坐标（AutoCAD 存档时已把块定义 ATDEF 的位置/字高预乘插入比例），嵌套=父块局部坐标。
        // 故位置只受父层累积变换（parent），字高也按该同一变换缩放（parent.Scale），不再乘本 INSERT 的 tr：
        // tr 含本块插入比例，会把已含插入比例的字高再放大一次（双重缩放）。
        // 治 sample.dwg gc200（scale=10）属性 "3.94"：字高 20 曾被双重缩放开成 200；
        // GT AutoCAD 暗色截图实测按 20 渲染（"3.94" 字形 ≈15px @0.8px/u ≈ 18.75≈20 数据单位）。
        if (hasAttribs)
            foreach (var att in ins.Attributes)
                EmitEntitySvg(sb, att, parent, parent?.Scale ?? 1.0, insAci, insDash, insPat, childLtMul, insLayerAci);
    }

    // 按 TEXT 对齐方式（DXF 72/73 组码）计算基线起点、旋转与缩放。
    // fork：HorizontalAlignment(72: Left/Center/Right/Aligned/Middle/Fit) + VerticalAlignment(73: Baseline/Bottom/Middle/Top)。
    // R17：完整对齐方式，语义以 AutoCAD 2021 DXF 官方规范（帮助 "TEXT (DXF)" 组码表）为准：
    //   ① 72=0 且 73=0（基线/左对正）：组 11 无意义，基线起点 = 组 10（InsertPoint）。
    //   ② Aligned(72=3)/Fit(72=5)：基线沿 10→11 方向（附加旋转 = 方向角）；Fit 额外按 |11−10|/W 缩放字高。
    //   ③ 其余全部组合（72∈{1,2,4} 或 73∈{1,2,3}）：锚点 = 组 11（AlignmentPoint），
    //      基线起点 = 锚点 + R(rot)·(dx,dy)：
    //        dx：72 Left=0 / Center=−W/2 / Middle=−W/2 / Right=−W；
    //        dy：73 Baseline=Bottom=0 / Middle=−H/2 / Top=−H（文本框模型 [基线, 基线+H]）；
    //        y 锚点：R20——73=Baseline 时取组 10y（用户 ZWCAD 2021 实测：基线过设计者真实基线，
    //        组 11 仅水平锚）；73∈{1,2,3} 取组 11y（组 10 被忽略）。
    //        W = 应用文字样式后的字符串长度 = 估算宽度 × 宽度因子(41)（CJK 全角 1.0em × WF 与 SimSun 实际字宽一致）。
    // R16 的锚点规则（72=Left 一律取 10）对 72=0/73≠0 违规：图框数值单元格（如"武汉钢铁有限公司"72=0/73=2，
    // 11=10+(0,H/2)）被渲染在 10−H/2 处，整体偏低 H/2；且未乘宽度因子 41，72=4 居中标签水平漂移 (W_est−W_actual)/2。
    // widthOverride：PNG 光栅端传入"真实字体度量宽度（数据单位，含宽度因子）"，
    // 修正估算宽度与真实字宽（CJK 实测 1.167em vs 估算 1.0em）不一致导致的居中/右对正漂移。
    // null（SVG/PDF 端）= 沿用 EstimateTextWidth 估算（行为逐字节不变）。
    private (double X, double Y, double Rot, double Scale) TextBaseline(TextEntity t, Transform2D tr, double? widthOverride = null)
    {
        double H = t.Height;
        double W = widthOverride ?? (EstimateTextWidth(t.Value ?? "", H) * Math.Max(t.WidthFactor, 1e-9));
        double rot = t.Rotation * 180.0 / Math.PI; // fork 弧度 → 度
        XYZ origin;
        double extraRot = 0.0, fitScale = 1.0;
        bool baselineLeft = t.HorizontalAlignment == TextHorizontalAlignment.Left
                            && t.VerticalAlignment == TextVerticalAlignmentType.Baseline;
        if (baselineLeft)
        {
            origin = t.InsertPoint; // ① 基线起点 = 组 10
        }
        else if (t.HorizontalAlignment == TextHorizontalAlignment.Aligned
                 || t.HorizontalAlignment == TextHorizontalAlignment.Fit)
        {
            // ② 基线沿 10→11
            origin = t.InsertPoint;
            double ddx = t.AlignmentPoint.X - t.InsertPoint.X;
            double ddy = t.AlignmentPoint.Y - t.InsertPoint.Y;
            if (ddx * ddx + ddy * ddy > 1e-18)
            {
                extraRot = Math.Atan2(ddy, ddx) * 180.0 / Math.PI;
                if (t.HorizontalAlignment == TextHorizontalAlignment.Fit && W > 1e-9)
                    fitScale = Math.Sqrt(ddx * ddx + ddy * ddy) / W;
            }
        }
        else
        {
            // ③ 锚点 = 组 11 + 72/73 偏移（局部偏移旋转到世界系）
            double dx = t.HorizontalAlignment switch
            {
                TextHorizontalAlignment.Center => -W / 2,
                TextHorizontalAlignment.Middle => -W / 2,
                TextHorizontalAlignment.Right => -W,
                _ => 0
            };
            // R22（cap-height 约定，GT 精确验证 2026-10-19）：AutoCAD 把 H 当 cap height，
            // 垂直对齐以【cap 盒】（高 H：基线→基线+H）为中心定位，而非 em 盒。
            //   Middle: baseline=anchorY−H/2。GT 英文 arial H=5(73=Middle) 实测 baseline=11y−2.5=15.70，
            //           cap 顶=20.70、descender 底=14.23 全部像素级命中。旧 −vcf·H（em 盒中心）偏低 0.14H，已废。
            //   Top: cap 顶对齐锚点（−H，与 cap 盒一致，保持）。Baseline/Bottom: 基线对齐锚点（0）。
            // 注：仅影响 TTF 路径（SHX 走 TryBuildShxTextWorld，MText 走 EmitMText*，均不经过此处）。
            double dy = t.VerticalAlignment switch
            {
                TextVerticalAlignmentType.Middle => -H / 2,
                TextVerticalAlignmentType.Top => -H,
                _ => 0 // Baseline/Bottom（保持旧行为，避免影响其他文字）
            };
            // R20（用户 ZWCAD 2021 实测，r20_base_cmp.json）：73=0（Baseline）时基线 y 过组 10
            // （设计者真实基线，如"测绘制图单位"10y=3.4805=实测 3.484），组 11 只用于水平锚（72）；
            // 73≠0 取组 11y（垂直偏移量见上方 R21 em-box 中心修正）。
            double anchorY = t.VerticalAlignment == TextVerticalAlignmentType.Baseline
                ? t.InsertPoint.Y
                : t.AlignmentPoint.Y;
            double r = rot * Math.PI / 180.0;
            double ca = Math.Cos(r), sa = Math.Sin(r);
            origin = new XYZ(
                t.AlignmentPoint.X + dx * ca - dy * sa,
                anchorY + dx * sa + dy * ca,
                t.AlignmentPoint.Z);
        }
        double totalRot = rot + extraRot;
        if (tr != null)
        {
            var applied = tr.Apply(origin.X, origin.Y);
            return (applied.Item1, applied.Item2, totalRot + (tr?.Rotation ?? 0.0), fitScale);
        }
        return (origin.X, origin.Y, totalRot, fitScale);
    }

    // 估算文本宽度（数据单位）：CJK 全角≈1.0×字高，半角≈0.55×字高，再乘宽度因子。
    private static double EstimateTextWidth(string s, double H)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        double w = 0;
        foreach (char ch in s)
        {
            bool cjk = IsCjkChar(ch);
            w += cjk ? 1.0 : 0.55;
        }
        return w * H;
    }

    // ===== 文字样式 → 字体族解析 =====
    // 已知 SHX 字体名（无 .shx 扩展名写法）：走 SHX 笔迹路径；其余无扩展名一律按 TTF 族解析。
    // 注意：**不能**把 arial/gothic 等拉丁名塞进 SHX 集合——arial.ttf 是系统 TTF，误判成 SHX →
    // 回退 SimSun（拉丁字高仅 ~0.56em，Arial ~0.72em）→ 视觉偏小 ~20% 且换行变少（历史 #1 根因）。
    private static readonly System.Collections.Generic.HashSet<string> _knownShxNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "txt", "simplex", "hztxt", "romans", "aaa", "cass", "fs", "khz",
            "rd", "rs", "intecad", "ltypeshp", "bigfont"
        };

    // 字体族名规范化（统一大小写/别名 → 渲染端 FindTtf 可解析的族名）。SHX 名返回 null。
    private static string CanonicalTtfFamily(string fam)
    {
        switch (fam.ToLowerInvariant())
        {
            case "arial": return "Arial";
            case "helvetica": return "Helvetica";
            case "times new roman": case "times": return "Times New Roman";
            case "courier new": case "courier": return "Courier New";
            case "calibri": return "Calibri";
            case "simhei": case "黑体": return "SimHei";
            case "simsun": case "宋体": return "SimSun";
            case "dengxian": case "等线": return "DengXian";
            case "kaiti": case "楷体": case "楷体_gb2312": return "KaiTi";
            case "fangsong": case "仿宋": case "仿宋_gb2312": return "FangSong";
            case "simli": case "隶书": return "SimLi";
            case "microsoft yahei": case "微软雅黑": return "Microsoft YaHei";
            default:
                if (fam.Length == 0) return fam;
                return char.ToUpperInvariant(fam[0]) + fam.Substring(1); // arial→Arial
        }
    }

    private static string ResolveFontFamily(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName)) return null;
        string f = fontName.Trim();
        string fam = f;
        string ext = "";
        int dot = f.LastIndexOf('.');
        if (dot > 0) { fam = f.Substring(0, dot); ext = f.Substring(dot + 1).ToLowerInvariant(); }

        // 显式 SHX 扩展名 → SHX（由调用方走 SHX 笔迹 / 兜底）
        if (ext == "shx") return null;
        // 显式 TTF/TTC 扩展名 → 一律按 TTF 处理（含 arial.ttf —— 旧版误映射 SimSun 的根因）
        if (ext == "ttf" || ext == "ttc") return CanonicalTtfFamily(fam);

        string lower = fam.ToLowerInvariant();
        // 仅**无扩展名**的已知 SHX 字体名才视为 SHX
        if (_knownShxNames.Contains(lower)) return null;
        // 方正字体（FZ*）：含 DengXian/等线 → DengXian；其余 → 默认
        if (lower.StartsWith("fz"))
            return (lower.Contains("dengxian") || lower.Contains("等线")) ? "DengXian" : null;
        return CanonicalTtfFamily(fam);
    }

    // 标准族名 → CSS font-family 列表（SVG）。null/默认 → 宋体。
    // 拉丁字体给出跨平台回退链（Windows: Arial/Helvetica；Linux: Liberation Sans/DejaVu Sans），
    // 末尾保留 CJK 回退，保证同一 run 混排中文时仍可显示。
    private static string SvgFontFamily(string family)
    {
        if (string.IsNullOrEmpty(family)) return "SimSun, 宋体, serif";
        switch (family)
        {
            case "Arial": return "Arial, Helvetica, Liberation Sans, DejaVu Sans, SimSun, 宋体, sans-serif";
            case "Helvetica": return "Helvetica, Arial, Liberation Sans, DejaVu Sans, SimSun, 宋体, sans-serif";
            case "Times New Roman": return "Times New Roman, Times, Liberation Serif, DejaVu Serif, SimSun, 宋体, serif";
            case "Courier New": return "Courier New, Courier, Liberation Mono, DejaVu Sans Mono, SimSun, 宋体, monospace";
            case "SimHei": return "SimHei, 黑体, sans-serif";
            case "SimSun": return "SimSun, 宋体, serif";
            case "DengXian": return "DengXian, 等线, sans-serif";
            case "KaiTi": return "KaiTi, 楷体, serif";
            case "FangSong": return "FangSong, 仿宋, serif";
            case "SimLi": return "SimLi, 隶书, serif";
            case "Microsoft YaHei": return "Microsoft YaHei, 微软雅黑, sans-serif";
            default: return family + ", SimSun, 宋体, serif";
        }
    }

    // 实体样式 → 原始拉丁字体名（fork TextStyle：Filename(3)）
    private static string GetStyleFont(TextStyle style)
    {
        if (style == null) return null;
        try
        {
            string fam = style.Filename;
            if (!string.IsNullOrWhiteSpace(fam)) return fam;
            string ff = style.BigFontFilename;
            if (!string.IsNullOrWhiteSpace(ff)) return ff;
        }
        catch { }
        return null;
    }

    // 实体样式 → 大字型（CJK）字体名（fork TextStyle：BigFontFilename(4)）；无则 null。
    // MTEXT 基础样式 simplex.shx 通常不含 CJK；若样式带 bigfont（如 HZTXT=simplex+Hztxt）则用于中文 run。
    private static string GetStyleBigFont(TextStyle style)
    {
        if (style == null) return null;
        try { string ff = style.BigFontFilename; if (!string.IsNullOrWhiteSpace(ff)) return ff; } catch { }
        return null;
    }

    // R21：字体 em-box 中心占字高 H 的比例（TTF 用真实度量，SHX/未知回退 0.5）。
    // V=Middle 垂直居中需把基线放在 11y − centerFrac·H，使 em-box 中心落在锚点。
    // 实测 SimSun/SimHei/DengXian 等：ascent≈0.86、descent≈0.14（DengXian 0.78/0.22）→ centerFrac≈0.36/0.39，
    // 而非旧 H-box 模型假设的 0.5 → 修正 V=Middle 文本偏低 0.14H 的问题。
    // family 为 ResolveFontFamily 返回的 CSS 族列表（如 "SimHei, 黑体, sans-serif"），取首个可解析族测度量。
    private static readonly Dictionary<string, double> _emCenterCache = new(StringComparer.OrdinalIgnoreCase);
    private static double GetEmBoxCenterFraction(string family)
    {
        if (string.IsNullOrEmpty(family)) return 0.5; // SHX（family=null）→ 0.5，保持 SHX 路径原行为
        string first = family.Split(',')[0].Trim();
        if (first.Length == 0) return 0.5;
        if (_emCenterCache.TryGetValue(first, out double cached)) return cached;
        double frac = 0.5;
        try
        {
            // 纯托管（零外部依赖）：族名→TTF/TTC 文件→TrueTypeFont 度量。
            string? path = PdfFontManager.FindTtf(first);
            if (!string.IsNullOrEmpty(path))
            {
                var tf = TrueTypeFont.LoadFromFile(path!);
                if (tf != null)
                {
                    // em-box = [−descent, ascent]（descent 取绝对值，兼容不同字体存储符号约定）
                    double top = Math.Abs(tf.Ascent);
                    double bottom = Math.Abs(tf.Descent);
                    double em = top + bottom;
                    if (em > 1e-6)
                    {
                        // em-box 中心相对基线 = (top − bottom)/2 → 占 em 比例
                        frac = (top - bottom) / (2.0 * em);
                        if (frac < 0) frac = 0;
                        if (frac > 1) frac = 1;
                    }
                }
            }
        }
        catch { frac = 0.5; } // 度量失败 → 回退 0.5（不劣化）
        _emCenterCache[first] = frac;
        return frac;
    }

    // ===== TTF cap-height 字号映射（与 SHX cap-height 约定对齐，2026-10-19）=====
    // 根因：AutoCAD 把文字高度 H 解释为【大写字母高度 cap height】，而非整 em 盒。
    //   实测 arial：capHeight/upem = 1467/2048 = 0.7163。旧 TTF 路径 fs=H（把 H 当 em）
    //   → cap 只渲染 0.716H → 英文单行文字偏短 ~28%（GT 71.995 vs 渲染 51.491）。
    //   正确：fs = H * (upem/capHeight) = H/0.7163 = 1.396H → cap=H → 宽 72.16（+0.23% 命中 GT）。
    // 与已确立的 SHX 约定一致（SHX: s=H/above，above=cap 区）。本 helper 返回字号放大因子
    //   factor = upem/capHeight；capHeight 未知（字体缺失/OS/2<2/CFF 轮廓）→ 返回 1.0（保持旧行为，不劣化）。
    // 调用方：fs = H * scaleMul * bscale * TtfCapScale(family)。
    //
    // TTF cap-height 字号映射（与 SHX cap-height 约定对齐，2026-10-19 修订）。
    // AutoCAD 把文字高度 H 解释为【大写字母高度 cap height】，而非整 em 盒——该约定对【全部 TTF】
    // （含 CJK）统一成立：CJK 字体（SimSun 等）的 OS/2 sCapHeight 同样定义（SimSun=175, upem=256 →
    // capScale≈1.463），其汉字字形 advance=1.0em，故宽=1.0em×fs=1.463H/字、高按 cap 映射到 H，与 AutoCAD 一致。
    // capHeight 缺失（字体缺失/OS/2<2/CFF 轮廓）→ 以 em(upem) 为基准即不放大。
    private static readonly Dictionary<string, double> _ttfCapScaleCache = new(StringComparer.OrdinalIgnoreCase);
    private static double TtfCapScale(string family)
    {
        if (string.IsNullOrEmpty(family)) return 1.0; // SHX（family=null）→ 不缩放
        string first = family.Split(',')[0].Trim();
        if (first.Length == 0) return 1.0;
        first = ResolveFontFamily(first) ?? first; // 归一化（simsun/宋体→SimSun）保 CJK 判定与缓存稳定
        if (_ttfCapScaleCache.TryGetValue(first, out double cached)) return cached;
        double factor = 1.0;
        try
        {
            string? path = PdfFontManager.FindTtf(first);
            if (!string.IsNullOrEmpty(path))
            {
                var tf = TrueTypeFont.LoadFromFile(path!);
                if (tf != null && tf.UnitsPerEm > 0)
                {
                    // 字体定义的 cap height（sCapHeight）；缺失则以 em 为基准（不放大）。
                    double denom = tf.CapHeight > 0 ? tf.CapHeight : tf.UnitsPerEm;
                    // AutoCAD 对 CJK 全宽字体的等效 cap ≈ sCapHeight×(181/175)（+3.5%，实测校准）：
                    // ① 每汉字 advance=H×upem/cap：sCapHeight=175 → 10.97，比 AutoCAD 实测 10.62
                    //    宽 3.3% → 每行少放一字（我们 6 字 vs AutoCAD 7 字，refWidth=74.688 恰放 7）；
                    //    实测依据：sample-mtext.dxf 首框，像素每字中心距 17.917px × (126px/74.688
                    //    世界) = 10.62。
                    // ② SimSun 汉字墨迹 yMax=205（远超 sCapHeight=175）→ 205/175×H=8.79 高出
                    //    cap 线 17%（"字偏高"根因）；cap→181: adv=10.61/字（7 字墨迹 73.85<
                    //    74.688 恰放、8 字 84.8 不放）、墨迹顶 8.48。
                    // 拉丁字体（arial cap=1467，GT 实测 72.16 vs 71.995 已精确校准）不适用、不修正。
                    if (PdfFontManager.IsCjkFont(first)) denom *= 181.0 / 175.0;
                    factor = tf.UnitsPerEm / denom;
                }
            }
        }
        catch { factor = 1.0; } // 度量失败 → 回退 1.0（不劣化）
        if (factor < 0.5) factor = 1.0;   // 异常值保护（capHeight 异常大）
        if (factor > 2.5) factor = 2.5;   // 异常值保护（capHeight 异常小）
        _ttfCapScaleCache[first] = factor;
        return factor;
    }

    // 字体定义宽度占位（advance，来自 hmtx）：返回字符相对 em 的步进宽度（em 占比）。
    // 替代 EstimateTextWidth 的 0.55/1.0 经验常量——布局读取字体真实 advance，与渲染笔迹一致。
    private static readonly Dictionary<string, TrueTypeFont> _ttfAdvCache = new(StringComparer.OrdinalIgnoreCase);
    private static TrueTypeFont GetTtfForWidth(string family)
    {
        if (string.IsNullOrEmpty(family)) return null;
        string first = family.Split(',')[0].Trim();
        if (first.Length == 0) return null;
        if (_ttfAdvCache.TryGetValue(first, out var c)) return c;
        TrueTypeFont tf = null;
        try { string p = PdfFontManager.FindTtf(first); if (!string.IsNullOrEmpty(p)) tf = TrueTypeFont.LoadFromFile(p!); }
        catch { tf = null; }
        _ttfAdvCache[first] = tf;
        return tf;
    }
    private static double TtfCharAdvanceEm(string family, char ch)
    {
        var tf = GetTtfForWidth(family);
        if (tf != null && tf.UnitsPerEm > 0)
        {
            float adv = tf.GetAdvanceWidth((int)ch);
            if (adv > 0) return adv / tf.UnitsPerEm;
            return IsCjkChar(ch) ? 1.0 : 0.55; // 缺字形回退
        }
        return IsCjkChar(ch) ? 1.0 : 0.55;
    }

    #endregion

    #region ===== SHX 真实字形文字（与 AutoCAD 一致：unifont 拉丁 + bigfont CJK 笔迹路径）=====

    // 根因：主文字样式为 SHX 字体（Standard=txt.shx+Hztxt.shx、HZTXT=simplex.shx+Hztxt.shx 等），
    // 旧 ResolveFontFamily 把所有 .shx 映射 null→SimSun（比例字体），而 txt.shx 是等宽（数字 0.75em）、
    // SimSun 数字仅 0.5em → 拉丁/数字标签比 AutoCAD 短 25~45%，居中/右对齐文字锚点漂移。
    // 修法：SHX 样式改用真实 SHX 笔迹路径（ShxFont 字形 + advance）渲染，与 AutoCAD 完全一致。
    // unifont（txt.shx）按字符码取字形；bigfont（Hztxt.shx）按 GBK 双字节取字形；em=UnitsPerEm。
    // 字高 H 对应【大写字母高度 cap height = above 区】→ 缩放 s=H/above（见 ShxHeightScale，非 H/em），
    // 基线 y=0 与数据系同向。TTF 样式（.ttf）仍走原系统字体路径。

    // GBK 编码（bigfont CJK → 双字节码）；CodePages 包不可用时返回 null（调用方回退 TTF 路径，不劣化）。
    private static System.Text.Encoding _shxGbk;
    private static bool _shxGbkTried;
    private static System.Text.Encoding ShxGbkEncoding()
    {
        if (_shxGbkTried) return _shxGbk;
        _shxGbkTried = true;
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            _shxGbk = System.Text.Encoding.GetEncoding(936); // GBK
        }
        catch { _shxGbk = null; }
        return _shxGbk;
    }

    // 解析文字样式的 SHX 文本面：latin=unifont/bigfont 拉丁面（按 Filename），cjk=bigfont CJK 面（按 BigFontFilename）。
    //
    // 【字体降级 2026-10-27 用户需求】渲染图片/PDF/SVG 时样式引用的 SHX 字体找不到 → 默认降级使用项目部署的
    // Support\fonts\Hztxt.shx（经 FileProvider 解析；未部署则返回 null → 行为与改动前完全一致）。
    // 注意：Hztxt.shx 为纯 CJK BigFont——实测无任何 ASCII 字形（0x20–0x7E 全缺）——只能作 CJK 面；
    // 或「文字含 CJK 且主字体缺失」时兼作 latin 面（此时拉丁/符号缺字形仅留 advance 间隙）。
    // 纯拉丁/符号文字主字体缺失时保持原 TTF 路径（降级 Hztxt 会让拉丁字消失，属劣化）。
    // 返回 false = 该文字不走 SHX 路径（调用方回退 TTF）：非 SHX 样式（.ttf/.ttc）、
    // 主字体缺失且文字不含 CJK（或 Hztxt.shx 也未部署）等。
    private bool TryGetShxTextFaces(TextEntity t, out ShxFont latin, out ShxFont cjk)
    {
        latin = null; cjk = null;
        string fname = null, bfname = null;
        try { fname = t.Style?.Filename; bfname = t.Style?.BigFontFilename; } catch { }
        if (string.IsNullOrWhiteSpace(fname)) return false;
        string low = fname.Trim().ToLowerInvariant();
        // TTF/TTC 明确走 TTF 路径（不探 SHX）。
        if (low.EndsWith(".ttf", StringComparison.Ordinal) || low.EndsWith(".ttc", StringComparison.Ordinal)) return false;
        bool hasCjk = false;
        foreach (var ch in t.Value ?? "") if (ch >= 0x2E80) { hasCjk = true; break; }
        latin = TryLoadShxTextFace(fname);
        if (latin == null)
        {
            // 主字体未找到：文字含 CJK → latin 面降级 Hztxt.shx（CJK 可渲染；拉丁/符号缺字形仅留间隙）；
            // 纯拉丁/符号 → false（原 TTF 路径；Hztxt 无 ASCII 字形，降级会劣化）。
            if (!hasCjk) return false;
            latin = FallbackShxBigFace();
            if (latin == null) return false;
            LogShxFallback(fname, "主字体");
        }
        ResolveShxCjkFace(bfname, latin, out cjk);
        return true;
    }

    // ---- SHX 字体降级辅助（降级默认字体 = Support\fonts\Hztxt.shx，2026-10-27 用户需求）----

    // 降级默认字体名（经 FileProvider 解析到 Support\fonts\ 部署目录）。
    private const string ShxTextFallbackFont = "Hztxt.shx";

    // 降级日志去重（static：同进程多次渲染同一字体名只打印一次，避免控制台刷屏）。
    private static readonly System.Collections.Generic.HashSet<string> _shxFallbackLogged =
        new(StringComparer.OrdinalIgnoreCase);

    // 字体降级日志（按 字体名+角色 去重，仅首次打印）。
    private void LogShxFallback(string fontName, string role)
    {
        string key = string.IsNullOrEmpty(fontName) ? "<未指定>" : fontName.Trim();
        lock (_shxFallbackLogged)
        {
            if (!_shxFallbackLogged.Add(key + "\u0000" + role)) return;
        }
        _log($"   [字体降级] SHX {role} \"{key}\" 未找到（缺失或格式不符）→ 降级默认字体 {ShxTextFallbackFont}（纯 CJK BigFont，无 ASCII 字形）");
    }

    // 把 fontName 解析为可用 SHX 文字面（unifont/bigfont/文字型 shape）；
    // .ttf/.ttc、解析不到（文件缺失）、加载失败、非文字面 → null。
    private ShxFont? TryLoadShxTextFace(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName)) return null;
        string low = fontName.Trim().ToLowerInvariant();
        if (low.EndsWith(".ttf", StringComparison.Ordinal) || low.EndsWith(".ttc", StringComparison.Ordinal)) return null;
        string probe = "";
        try { probe = _opt.FileProvider?.Invoke(fontName) ?? ""; } catch { }
        if (string.IsNullOrEmpty(probe) || !File.Exists(probe)) return null; // 无对应 .shx 文件
        var f = GetShapeFont(fontName); // 已确认能解析到 .shx，直接加载（按文件名缓存）
        if (f == null) return null;
        // Unifont/BigFont 是常规文字面；Shapes 容器若带 shape-0 字体定义（"形状外衣的文字面"，如 rs.shx）也接受
        // （IsTextFont 由 LoadShapes 判定），其字形按字符码=形状号直接查表，advance/缩放路径与 unifont 一致。
        return f.Format == ShxFormat.Unifont || f.Format == ShxFormat.BigFont
            || (f.Format == ShxFormat.Shapes && f.IsTextFont) ? f : null;
    }

    // 把 fontName 解析为 BigFont（CJK 面）；失败（含"能解析但非 BigFont 格式"）→ null。
    private ShxFont? TryLoadShxBigFace(string fontName)
    {
        var f = TryLoadShxTextFace(fontName);
        return f != null && f.Format == ShxFormat.BigFont ? f : null;
    }

    // 降级 CJK 面：Support\fonts\Hztxt.shx（FileProvider 解析；未部署 → null）。
    private ShxFont? FallbackShxBigFace() => TryLoadShxBigFace(ShxTextFallbackFont);

    // CJK 面解析：优先声明的 bigfont；缺失/非 BigFont → 字体降级：
    //   latin 面本身即 BigFont（如样式直接引用 hztxt.shx）→ 复用 latin 面
    //   （与 AutoCAD 一致：主字体为 BigFont 且未指定大字体时，CJK 用主字体）；
    //   否则 → 降级 Hztxt.shx（未部署 → cjk 仍 null，调用方按原逻辑回退 TTF）。
    private void ResolveShxCjkFace(string bigFontName, ShxFont latin, out ShxFont cjk)
    {
        cjk = TryLoadShxBigFace(bigFontName);
        if (cjk == null)
        {
            cjk = latin.Format == ShxFormat.BigFont ? latin : FallbackShxBigFace();
            if (cjk != null && cjk != latin)
                LogShxFallback(string.IsNullOrEmpty(bigFontName) ? null : bigFontName, "大字体");
        }
    }

    // SHX 文字 → 世界坐标（数据单位，y 向上）笔路径。返回 null = 该文字不适合 SHX 路径
    // （非 SHX 样式/字体缺失/CJK 无法定位字形），调用方回退 TTF 路径。
    //   out Type  : 逐指令类型（0=Move 1=Line 2=Quad 3=Cubic），与 GlyphRec.Type 同序
    //   out Pts   : 与 Type 一一对应的控制点数组（[x,y] / [cx,cy,ex,ey] / [c1x,c1y,c2x,c2y,ex,ey]），世界坐标（数据单位）
    //   out PenWidth : 笔画宽度（数据单位，三端描边用）
    // 算法：① 逐字符取 unifont/bigront 字形（局部：基线 y=0、+x 右，字体单位）→ 数据单位局部点（缩放 s=H/above、宽因子 wf 只乘一次）；
    //       ② 局部总宽 W 作 widthOverride 传给 TextBaseline 得基线左端 (bx,by) 与总旋转 brot（含块变换）；
    //       ③ 局部点绕 (bx,by) 旋转 brot 得世界点。
    private (List<int> Type, List<double[]> Pts, double PenWidth)? TryBuildShxTextWorld(TextEntity t, Transform2D tr, double scaleMul)
    {
        string text = t.Value ?? "";
        if (text.Length == 0) return null;
        if (!TryGetShxTextFaces(t, out ShxFont latin, out ShxFont cjk)) return null;
        // 触发 GBK 的判据须与下方逐字循环的 else 分支一致（ch>=0x80），而非仅 ch>=0x2E80：
        // 否则含 °(U+00B0)/±/′/″ 等非 ASCII 符号但无真 CJK 的 SHX 文字（分幅图常见角度/坐标标注）
        // 会在 else 分支解引用未初始化的 gbk 抛 NullReferenceException（2026-10-20 分幅图合图1008 崩溃根因）。
        System.Text.Encoding gbk = null; bool needBig = false, needCjk = false;
        foreach (var ch in text) { if (ch >= 0x80) needBig = true; if (ch >= 0x2E80) needCjk = true; }
        if (needCjk && cjk == null) return null;   // 含真 CJK 但无 bigfont → 回退 TTF（避免缺字/错字）
        if (needBig)
        {
            gbk = ShxGbkEncoding();                 // 可能为 null（CodePages 不可用）
            if (needCjk && gbk == null) return null; // 含真 CJK 且 GBK 不可用 → 回退 TTF
        }

        double H = t.Height * scaleMul;                 // 字高（数据单位，em 盒高）
        if (!(H > 1e-9)) return null;
        double wf = Math.Max(t.WidthFactor, 1e-9);      // 宽因子（只作用水平方向）

        // ① 局部路径（基线左端为原点，+x 右、+y 上，数据单位）+ 总宽 W。
        // 各面 em 不同（txt.shx=8 / Hztxt.shx=11），advance 与字形点统一换算到数据单位（s=H/em_该面）。
        var ltype = new List<int>(text.Length * 4);
        var lpts = new List<double[]>(text.Length * 4);
        double penX = 0.0; // 累计 advance（数据单位，未乘 wf）
        foreach (var ch in text)
        {
            ShxFont face; int code;
            if (ch < 0x80) { face = latin; code = ch; }
            else if (ch >= 0x2E80)
            {
                // 真 CJK：GBK 双字节 → bigfont。此处 gbk 必非 null（needCjk 且 gbk==null 已在上方 return）。
                byte[] gb = gbk.GetBytes(new char[] { ch });
                if (gb.Length != 2) continue;
                face = cjk; code = (gb[0] << 8) | gb[1];
            }
            else
            {
                // 非 CJK 符号（0x80<=ch<0x2E80，如 °/±/′/″）：unifont(latin) 面低 8 位 best-effort，
                // 不走 GBK/bigfont（GBK 双字节序列对非 CJK 无意义）；缺字形时 TryGetGlyph 失败仅留 advance 间隙。
                face = latin; code = ch & 0xFF;
            }
            int em = face.UnitsPerEm > 0 ? face.UnitsPerEm : 1;
            double s = ShxHeightScale(face, H);         // 数据单位 / 该面字体单位（cap height 映射 H，见 ShxHeightScale）
            bool hasAdv = face.TryGetAdvance(code, out float advF);
            double advU = ShxGlyphAdvance(face, hasAdv ? advF : 0f); // CJK BigFont 方格字宽（严格对齐包盒，见注释）
            if (advU <= 0) advU = 0.7 * em;                             // 缺字形仅留 0.7em 间隙
            double advData = advU * s; // 该字符 advance（数据单位）
            var g = new GlyphRec();
            bool ok = face.TryGetGlyph(code, g, ShxTextOrientation.Horizontal);
            if (ok && !g.Empty)
                for (int k = 0; k < g.Type.Count; k++)
                {
                    var p = g.Pts[k];
                    switch (g.Type[k])
                    {
                        case 0:
                        case 1:
                            ltype.Add(g.Type[k]);
                            lpts.Add(new double[] { (penX + p[0] * s) * wf, p[1] * s });
                            break;
                        case 2:
                            ltype.Add(2);
                            lpts.Add(new double[] { (penX + p[0] * s) * wf, p[1] * s, (penX + p[2] * s) * wf, p[3] * s });
                            break;
                        case 3:
                            ltype.Add(3);
                            lpts.Add(new double[] { (penX + p[0] * s) * wf, p[1] * s, (penX + p[2] * s) * wf, p[3] * s, (penX + p[4] * s) * wf, p[5] * s });
                            break;
                    }
                }
            penX += advData;
        }
        if (ltype.Count == 0) return null;              // 无任何字形（全缺）→ 回退 TTF
        double W = penX * wf;                           // 局部总宽（数据单位）

        // ② 基线左端 (bx,by) 与总旋转 brot、Fit 缩放 bscale（含块变换）；W 作 widthOverride 修正居中/右对齐锚点
        var (bx, by, brot, bscale) = TextBaseline(t, tr, W);
        double br = brot * Math.PI / 180.0;
        double ca = Math.Cos(br), sa = Math.Sin(br);
        if (!(bscale > 0)) bscale = 1.0;

        // ③ 局部 → 世界（绕基线左端缩放 bscale[Fit] 再旋转 brot）
        var wtype = ltype;
        var wpts = new List<double[]>(lpts.Count);
        foreach (var lp in lpts)
        {
            int n = lp.Length;
            var wp = new double[n];
            for (int i = 0; i < n; i += 2)
            {
                double lx = lp[i] * bscale, ly = lp[i + 1] * bscale;
                wp[i] = bx + lx * ca - ly * sa;
                wp[i + 1] = by + lx * sa + ly * ca;
            }
            wpts.Add(wp);
        }
        // 笔画宽度 = 文字实体线宽（CAD 语义：SHX 文字按实体 370/ByLayer 线宽描边，与其余图元及
        // 复杂线型 SHX 字形一致），Fit 时随 bscale 等比缩放。线宽缺失/为 0 时 StrokeWidth 兜底最小 0.1。
        double penW = StrokeWidth(t, scaleMul) * bscale;
        return (wtype, wpts, penW);
    }

    // SHX 字高缩放（三引擎共用）：AutoCAD 约定「文字高度 H = 大写字母高度 cap height = above 区」，
    // 故 s = H/above（**非** H/(above+below)）。这使 SHX 同高度比 TTF 略大（cap 映射到 H，而非整个 em 盒），
    // 与实测一致：simplex H=7.5 时「This is a sample」须换行（AutoCAD 6 行）→ 需 s∈(0.295,0.385]，
    // H/above=0.357 命中、H/em=0.268 偏低（→4 行）。
    // **CJK BigFont 修正**：hztxt/hz 等 CJK 大字的实际字形 Y 超出声明 Above（hztxt Above=9 但字形到 10.58），
    // 若仍用 H/Above 会偏大 ~17%。AutoCAD 对 CJK 的约定是 H = 完整 CJK 字格高度（≈实际字形最大 Y），
    // 故对 BigFont 格式改用 denom = max(Above, 实测字形最大 Y)，使 CJK 字格高度映射到 H（GT 框高 = H 精确验证）。
    // Unifont 拉丁（simplex/txt/romans）不受影响——其字形最大 Y ≈ Above（cap 约定正确）。
    private double ShxHeightScale(ShxFont face, double H)
    {
        int above = face.Above;
        // CJK BigFont：实测字形可能超出 Above（设计缺陷），取 max 修正。
        if (face.Format == ShxFormat.BigFont && above > 0)
        {
            double measAscent = MeasuredShxAscent(face);
            if (measAscent > above)
                return H / measAscent; // 用实测最大 Y 替代声明 Above
        }
        int denom = above > 0 ? above : (face.UnitsPerEm > 0 ? face.UnitsPerEm : 1);
        return H / denom;
    }

    // 一次性测量 BigFont 中所有字形的最大 Y（字体单位），缓存结果。
    // 用于修正 CJK 大字实际高度超出声明 Above 的缩放偏差（hztxt Above=9 但字形到 10.58）。
    private double MeasuredShxAscent(ShxFont face)
    {
        string key = face.Name; // 字体定义记录里的字样名（唯一标识）
        if (_shxAscentCache.TryGetValue(key, out double cached)) return cached;
        double maxAscent = 0;
        try
        {
            foreach (int code in face.Codes)
            {
                var sink = new GlyphRec();
                if (face.TryGetGlyph(code, sink, ShxTextOrientation.Horizontal) && !sink.Empty)
                {
                    if (sink.MaxY > maxAscent) maxAscent = sink.MaxY; // GlyphRec 已逐点维护 MaxY
                }
            }
        }
        catch { }
        _shxAscentCache[key] = maxAscent;
        return maxAscent;
    }

    // CJK BigFont 字宽（字体单位）：用户定稿——单行 CJK 文字渲染宽度须严格对齐 DXF 中绘制的文字包盒宽度。
    // 依据：hztxt 全角汉字 pen-end advance=10.0，但实测字形高（最大 Y）=10.75；CAD 全角字格为方格
    //（字格宽=字格高），advance 取 max(penEnd, 实测最大字形高) 后，"测试中文显示"(H=15.58) 的
    // ink 宽 84.2→90.1，命中包盒 90.173（+0.04%）。拉丁 Unifont 不受影响（返回 penEnd）。
    private float ShxGlyphAdvance(ShxFont face, float penEnd)
    {
        if (face.Format == ShxFormat.BigFont)
        {
            double asc = MeasuredShxAscent(face);
            if (asc > penEnd) return (float)asc;
        }
        return penEnd;
    }

    // ---- MTEXT 逐 run SHX 笔迹（基础样式 simplex.shx / \Fsimplex 等）----
    // 解析给定字体名 → SHX 面（latin），并可选 bigfont（cjk）。非 SHX（.ttf/.ttc）→ false。
    // 【字体降级 2026-10-27】与 TryGetShxTextFaces 同规则：主字体缺失且文字含 CJK → latin 面降级 Hztxt.shx；
    // 纯拉丁/符号文字主字体缺失 → false（原 TTF 路径）；bigfont 缺失 → CJK 面降级 Hztxt.shx。
    // text = run 文本（用于降级时判断是否含 CJK；必须与实际渲染文本一致，保证估宽/换行与渲染一致）。
    private bool TryResolveShxFaces(string fontName, string bigFontName, string text, out ShxFont latin, out ShxFont cjk)
    {
        latin = null; cjk = null;
        if (string.IsNullOrWhiteSpace(fontName)) return false;
        string low = fontName.Trim().ToLowerInvariant();
        if (low.EndsWith(".ttf", StringComparison.Ordinal) || low.EndsWith(".ttc", StringComparison.Ordinal)) return false;
        bool hasCjk = false;
        if (text != null) foreach (var ch in text) if (ch >= 0x2E80) { hasCjk = true; break; }
        latin = TryLoadShxTextFace(fontName);
        if (latin == null)
        {
            // 主字体未找到：含 CJK → 降级 Hztxt.shx；纯拉丁/符号 → false（Hztxt 无 ASCII 字形，降级会劣化）。
            if (!hasCjk) return false;
            latin = FallbackShxBigFace();
            if (latin == null) return false;
            LogShxFallback(fontName, "主字体");
        }
        ResolveShxCjkFace(bigFontName, latin, out cjk);
        return true;
    }

    // 为一段 MTEXT run 文本构建局部 SHX 笔迹路径（原点=基线左端、+x 右 +y 上，数据单位）。
    //   width = 实际 advance 总宽（数据单位，已乘 wf），供调用方精确定位后续 run / 换行。
    // 返回 false = 不适配 SHX（非 SHX 字体 / 含 CJK 但无可用 bigfont（声明与降级均缺失）/ 全缺字形）→ 调用方回退 TTF。
    private bool TryBuildMTextShxLocal(string fontName, string bigFontName, string text, double H, double wf,
        out List<int> types, out List<double[]> pts, out double width)
    {
        types = null; pts = null; width = 0;
        if (string.IsNullOrEmpty(text)) return false;
        if (!TryResolveShxFaces(fontName, bigFontName, text, out ShxFont latin, out ShxFont cjk)) return false;
        bool hasCjk = false;
        foreach (var ch in text) if (ch >= 0x2E80) { hasCjk = true; break; }
        System.Text.Encoding gbk = null;
        if (hasCjk)
        {
            if (cjk == null) return false;   // 含 CJK 但无 bigfont → 回退 TTF（避免缺字）
            gbk = ShxGbkEncoding();
            if (gbk == null) return false;
        }
        if (!(H > 1e-9)) return false;
        if (!(wf > 1e-9)) wf = 1.0;

        var lt = new List<int>(text.Length * 4);
        var lp = new List<double[]>(text.Length * 4);
        double penX = 0.0;
        foreach (var ch in text)
        {
            ShxFont face; int code;
            if (ch >= 0x2E80)   // CJK → bigfont（hasCjk 已保证 gbk 非 null）
            {
                byte[] gb = gbk.GetBytes(new char[] { ch });
                if (gb.Length != 2) continue;
                face = cjk; code = (gb[0] << 8) | gb[1];
            }
            else { face = latin; code = ch; } // 非 CJK 一律走 latin SHX 原始码位（含 0xB0°/0x2205∅ 等 CAD 符号；缺字形仅留 0.7em 间隙，不崩）
            int em = face.UnitsPerEm > 0 ? face.UnitsPerEm : 1;
            double s = ShxHeightScale(face, H);         // cap height 映射 H（见 ShxHeightScale），非 H/em
            bool hasAdv = face.TryGetAdvance(code, out float advF);
            double advU = ShxGlyphAdvance(face, hasAdv ? advF : 0f); // CJK BigFont 方格字宽（严格对齐包盒）
            if (advU <= 0) advU = 0.7 * em;
            double advData = advU * s;
            var g = new GlyphRec();
            if (face.TryGetGlyph(code, g, ShxTextOrientation.Horizontal) && !g.Empty)
                for (int k = 0; k < g.Type.Count; k++)
                {
                    var p = g.Pts[k];
                    switch (g.Type[k])
                    {
                        case 0: case 1:
                            lt.Add(g.Type[k]);
                            lp.Add(new double[] { penX + p[0] * s, p[1] * s });
                            break;
                        case 2:
                            lt.Add(2);
                            lp.Add(new double[] { penX + p[0] * s, p[1] * s, penX + p[2] * s, p[3] * s });
                            break;
                        case 3:
                            lt.Add(3);
                            lp.Add(new double[] { penX + p[0] * s, p[1] * s, penX + p[2] * s, p[3] * s, penX + p[4] * s, p[5] * s });
                            break;
                    }
                }
            penX += advData;
        }
        if (lt.Count == 0) return false;
        types = lt; pts = lp; width = penX * wf; // advance 仍乘 wf（调用方据此定位/换行）
        return true;
    }

    // 仅求 SHX run 的 advance 总宽（数据单位，已乘 wf），不构建点集（供 MTextRunWidth / WrapMText 换行）。
    // 返回 false = 不适配 SHX（非 SHX 字体 / 含 CJK 但无 bigfont）→ 调用方回退 TTF 估宽。
    private bool TryShxRunAdvance(string fontName, string bigFontName, string text, double H, double wf, out double width)
    {
        width = 0;
        if (string.IsNullOrEmpty(text)) return false;
        if (!TryResolveShxFaces(fontName, bigFontName, text, out ShxFont latin, out ShxFont cjk)) return false;
        bool hasCjk = false;
        foreach (var ch in text) if (ch >= 0x2E80) { hasCjk = true; break; }
        if (hasCjk)
        {
            if (cjk == null) return false;
            if (ShxGbkEncoding() == null) return false;
        }
        if (!(H > 1e-9)) return false;
        if (!(wf > 1e-9)) wf = 1.0;
        System.Text.Encoding gbk = hasCjk ? ShxGbkEncoding() : null;
        double penX = 0.0;
        foreach (var ch in text)
        {
            ShxFont face; int code;
            if (ch >= 0x2E80)   // CJK → bigfont（hasCjk 已保证 gbk 非 null）
            {
                byte[] gb = gbk.GetBytes(new char[] { ch });
                if (gb.Length != 2) continue;
                face = cjk; code = (gb[0] << 8) | gb[1];
            }
            else { face = latin; code = ch; } // 非 CJK 一律走 latin SHX 原始码位（含 0xB0°/0x2205∅ 等 CAD 符号；缺字形仅留 0.7em 间隙，不崩）
            int em = face.UnitsPerEm > 0 ? face.UnitsPerEm : 1;
            double s = ShxHeightScale(face, H);         // cap height 映射 H（见 ShxHeightScale），非 H/em
            bool hasAdv = face.TryGetAdvance(code, out float advF);
            double advU = ShxGlyphAdvance(face, hasAdv ? advF : 0f); // CJK BigFont 方格字宽（严格对齐包盒）
            if (advU <= 0) advU = 0.7 * em;
            penX += advU * s;
        }
        width = penX * wf;
        return true;
    }

    // 末字尾部留白（世界单位）：SHX latin 字形 advance − 墨迹右缘（字体单位）经 cap-height 缩放映射世界。
    // 仅处理 latin（非 CJK）；bigfont CJK 走方格 advance、墨迹通常填满方格，暂不计尾留白（保守）。
    private double ShxTrailingGapWorld(string text, double H, double wf, string fontName, string bigFontName)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int last = text.Length - 1;
        while (last >= 0 && char.IsWhiteSpace(text[last])) last--;
        if (last < 0) return 0;
        char ch = text[last];
        if (ch >= 0x2E80) return 0; // CJK bigfont → 不计
        if (!(H > 1e-9)) return 0;
        if (!(wf > 1e-9)) wf = 1.0;
        if (!TryResolveShxFaces(fontName, bigFontName, text, out ShxFont latin, out _)) return 0;
        if (latin == null) return 0;
        if (!latin.TryGetAdvance(ch, out float advF)) return 0;
        if (!latin.TryGetInkRight(ch, out float inkR)) return 0;
        double gapFont = advF - inkR;
        if (gapFont <= 0) return 0;
        double s = ShxHeightScale(latin, H); // cap height 映射 H（同 TryShxRunAdvance）
        return gapFont * s * wf;
    }

    // 【首字左侧留白】SHX：字形墨迹左缘到 pen 起点的空位（世界单位）。
    // AutoCAD 实测：每行以【墨迹左缘】对齐左边界（插入点），而非字形的 advance 占位盒左界 →
    // 排版须让出该留白，否则整行右偏、尾部墨迹越界提前换行。
    private double ShxLeadingGapWorld(string text, double H, double wf, string fontName, string bigFontName)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int first = 0;
        while (first < text.Length && char.IsWhiteSpace(text[first])) first++;
        if (first >= text.Length) return 0;
        char ch = text[first];
        if (ch >= 0x2E80) return 0; // CJK bigfont → 不计
        if (!(H > 1e-9)) return 0;
        if (!(wf > 1e-9)) wf = 1.0;
        if (!TryResolveShxFaces(fontName, bigFontName, text, out ShxFont latin, out _)) return 0;
        if (latin == null) return 0;
        if (!latin.TryGetInkLeft(ch, out float inkL)) return 0;
        if (inkL <= 0) return 0; // 墨迹左伸/贴边 → 无留白可让
        double s = ShxHeightScale(latin, H);
        return inkL * s * wf;
    }

    #endregion

    #region ===== MTEXT 多行文字 =====

    // MTEXT 段（字号/宽比/颜色相同的连续文本）
    private struct MTextRun
    {
        public string Text;
        public double Height;
        public double WidthFactor;
        public int? Color;      // \C 颜色（ACI）；null=随实体
        public bool Underline;  // 下划线（\U）
        public bool Overline;   // 上划线（\O/\o）
        public bool Bold;       // 加粗（\f 规格 |b1|）
        public bool Italic;     // 斜体（\f 规格 |i1|）
        public double Oblique;  // 倾斜角（\Q<deg>;，度，0=无）
        public string Font;     // 字体族名（\f 内联）；null=随实体样式
        public double BaselineShift; // \S 堆叠基线偏移（em，+=上标/分子，-=下标/分母；0=正常）
        public bool IsFraction; // \S<num>/<den> 分数堆叠：单 run 存 "num/den"，渲染时上下分层+分数线
        public double Tracking;   // \T<n>; 字距因子（1=正常，<1 更紧，>1 更宽；独立于 \W 宽度因子）
    }

    // MTEXT 一行
    private class MTextLine
    {
        public List<MTextRun> Runs = new();
        public double MaxHeight;
        public double Width;
        public double FirstIndent; // \pi 段落首行缩进（数据单位；仅段落首行生效，续行为 0）
        public double? ParaSpacing; // \pxsm<N>; 段落行距倍数（扩展字典缓存/内联 \px）；null=用实体组码 44
        public double InkLeft;      // 首字左侧留白（世界单位）：墨迹左缘到 pen 起点的空位，见 Eng.MTextLeadingGap
        public void Recalc(ImageEngine eng, string styleFont, string styleBig)
        {
            MaxHeight = 0; Width = 0;
            foreach (var r in Runs)
            {
                MaxHeight = Math.Max(MaxHeight, r.Height);
                Width += eng.MTextRunWidth(r, styleFont, styleBig);
            }
            InkLeft = eng.MTextLeadingGap(this, styleFont, styleBig);
        }
        // 行内【有墨迹】run 的最大字高：空白 run（任意字号的空格）AutoCAD 不渲染、不参与行距
        // （参考图实测：行首/行尾大字号空格均不改变基线距）。
        public double InkMaxHeight()
        {
            double mh = 0;
            foreach (var r in Runs)
                if (!string.IsNullOrWhiteSpace(r.Text)) mh = Math.Max(mh, r.Height);
            return mh;
        }
    }

    // \T 字距：每字符附加间距（数据单位）= (因子-1)×0.1em。1=正常(0)，<1 收窄，>1 加宽；独立于 \W（宽度因子）。
    // 校准自参考 PDF：\T1→Tc=0、\T0.75→Tc≈-25(1/1000em)，即每字 (n-1)×0.1em。
    private static double MTextTrackingAdd(in MTextRun r, int nChars)
    {
        double t = r.Tracking > 1e-9 ? r.Tracking : 1.0;
        return nChars * (t - 1.0) * 0.1 * r.Height;
    }

    // MText TTF run 宽度（数据单位，已乘 wf 与 cap-scale）：基线宽×wf×cap + \T 字距×cap。
    // 与渲染 fs=H·cap 一致——Phase 2 修复：MText TTF 路径此前未套 cap-scale，比单行/AutoCAD 窄 ~28%，
    // 致本应换行的行不换 → 整块偏矮。SHX 路径不走此函数（advance 已是真实宽）。
    private static double TtfMTextWidth(string text, double H, double wf, string font, in MTextRun r)
    {
        string fam = ResolveFontFamily(font);
        double cap = TtfCapScale(fam);
        // 逐字读取字体定义 advance（em 占比）× cap 得相对 H 的步进，累加后 ×H×wf 即世界宽（与渲染 fs=H·cap 一致）。
        double wPerH = 0;
        foreach (char ch in text) wPerH += TtfCharAdvanceEm(fam, ch) * cap;
        double w = wPerH * H * wf;
        w += MTextTrackingAdd(r, text.Length) * cap;
        return w;
    }

    // 末字尾部留白（世界单位）：advance − 墨迹右缘。换行判定须剔除（AutoCAD 以末字墨迹越界为准）。
    // text 取去尾随空白后的串，取末尾非空白字符度量。TTF：字体单位 (adv−xMax) 经 cap·H·wf/upem 映射到世界。
    private static double TtfTrailingGapWorld(string text, double H, double wf, string font)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int last = text.Length - 1;
        while (last >= 0 && char.IsWhiteSpace(text[last])) last--;
        if (last < 0) return 0;
        var tf = GetTtfForWidth(font);
        if (tf == null || tf.UnitsPerEm <= 0) return 0;
        double cap = TtfCapScale(font);
        double scale = (cap * H * wf) / tf.UnitsPerEm; // 世界单位/字体单位（与 TtfMTextWidth 一致）
        return tf.GetGlyphTrailingGap(text[last]) * scale;
    }

    // 【首字左侧留白】TTF：glyf xMin（世界单位）。语义同 ShxLeadingGapWorld：
    // 行以墨迹左缘对齐边界，该留白需让出（见此处）。
    private static double TtfLeadingGapWorld(string text, double H, double wf, string font)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int first = 0;
        while (first < text.Length && char.IsWhiteSpace(text[first])) first++;
        if (first >= text.Length) return 0;
        var tf = GetTtfForWidth(font);
        if (tf == null || tf.UnitsPerEm <= 0) return 0;
        double cap = TtfCapScale(font);
        double scale = (cap * H * wf) / tf.UnitsPerEm;
        return tf.GetGlyphLeadingGap(text[first]) * scale;
    }

    // 行级首字左侧留白（世界单位）：该行第一个【有墨迹】字符的左侧留白。
    // 用于 WrapMText 的可用宽度（+lead）与各引擎行起点（−lead，使墨迹左缘落在参考边界）。
    private double MTextLeadingGap(MTextLine ln, string styleFont, string styleBig)
    {
        foreach (var r in ln.Runs)
        {
            if (string.IsNullOrEmpty(r.Text)) continue;
            if (string.IsNullOrWhiteSpace(r.Text)) continue; // 纯空白不产生墨迹
            string eFont = !string.IsNullOrEmpty(r.Font) ? r.Font : styleFont;
            string eBig = string.IsNullOrEmpty(r.Font) ? styleBig : null;
            double wf = r.WidthFactor > 0 ? r.WidthFactor : 1.0;
            double lead = TryShxRunAdvance(eFont, eBig, r.Text, r.Height, wf, out _)
                ? ShxLeadingGapWorld(r.Text, r.Height, wf, eFont, eBig)
                : TtfLeadingGapWorld(r.Text, r.Height, wf, eFont);
            return lead > 0 ? lead : 0;
        }
        return 0;
    }

    private double MTextRunWidth(MTextRun r, string styleFont, string styleBig)
    {
        double wf = r.WidthFactor > 0 ? r.WidthFactor : 1.0;
        // 有效字体：内联 \f 优先；否则随实体样式（含其大字型）
        string eFont = !string.IsNullOrEmpty(r.Font) ? r.Font : styleFont;
        string eBig = string.IsNullOrEmpty(r.Font) ? styleBig : null;
        if (r.IsFraction)
        {
            // 分数宽 = max(分子, 分母)（分数线与较宽者同宽），不是 "num/den" 整体宽
            int sl = r.Text.IndexOf('/');
            string num = sl >= 0 ? r.Text.Substring(0, sl) : r.Text;
            string den = sl + 1 < r.Text.Length ? r.Text.Substring(sl + 1) : "";
            double numW = TryShxRunAdvance(eFont, eBig, num, r.Height, wf, out double wn) ? wn : EstimateTextWidth(num, r.Height) * wf;
            double denW = TryShxRunAdvance(eFont, eBig, den, r.Height, wf, out double wd) ? wd : EstimateTextWidth(den, r.Height) * wf;
            return Math.Max(numW, denW);
        }
        // SHX 笔迹：advance 已含字形真实宽（不含 \T 字距——simplex.shx 等技术字体极少配 \T，且保持既有渲染不回退）
        if (TryShxRunAdvance(eFont, eBig, r.Text, r.Height, wf, out double w)) return w;
        // TTF 回退：基线宽×宽度因子×cap-scale + \T 字距×cap（与渲染 fs=H·cap 一致，见 TtfMTextWidth）。
        return TtfMTextWidth(r.Text, r.Height, wf, eFont, r);
    }

    // 合成斜体倾斜角（度）：AutoCAD 对无真斜体（尤其 CJK）字体按固定倾斜角合成斜体。
    // \f...|i1| 只置 Italic 标志，此处统一折算成斜切角供三引擎（SVG skewX / PNG tanA / PDF Tm 剪切）使用。
    private const double SynthItalicOblique = 15.0;
    // 段内有效倾斜角：显式 \Q 优先；否则斜体按 SynthItalicOblique 合成；都没有 = 0
    private static double EffOblique(in MTextRun r)
        => Math.Abs(r.Oblique) > 1e-6 ? r.Oblique : (r.Italic ? SynthItalicOblique : 0.0);

    private static bool IsCjkChar(char ch)
        => (ch >= 0x2E80 && ch <= 0x9FFF) || (ch >= 0xFF00 && ch <= 0xFFEF)
           || (ch >= 0x3000 && ch <= 0x303F) || (ch >= 0x20000 && ch <= 0x2FA1F);

    private static bool IsHexChar(char ch)
        => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');

    // 解析 MTEXT 原始文本（含格式码）为行/段。styleFont/styleBig 用于 SHX 字宽（换行/定位，与 CAD 一致）。
    // AutoCAD 把 MTEXT 段落属性（\px...;，如 \pxsm1.5; 行距倍数）存于实体扩展字典的 XRecord 内容缓存串，
    // 而实体组码 44 常保持 1.0 → 仅读 44 会比 AutoCAD 渲染窄。从缓存串提取每段的 \px 属性：
    // 只保留 \P（分段）与 \px...;（段属性），其余字符丢弃 → 按 \P 切分即得逐段属性表（与 ParseMText 段号对齐）。
    private static List<string> MTextCacheParaProps(MText mt)
    {
        var props = new List<string>();
        try
        {
            string cache = null;
            var dict = mt.XDictionary;
            if (dict != null)
            {
                foreach (var obj in dict)
                {
                    if (obj is ACadSharp.Objects.XRecord xr)
                        foreach (var e in xr.Entries)
                            if (e.Value is string s && s.Contains("\\px"))
                            {
                                cache = s; break;
                            }
                    if (cache != null) break;
                }
            }
            if (cache == null) return null;
            var cur = new System.Text.StringBuilder();
            int i = 0;
            while (i < cache.Length)
            {
                if (cache[i] == '\\' && i + 1 < cache.Length)
                {
                    char c = cache[i + 1];
                    if (c == 'P' || c == 'p' && i + 2 < cache.Length && cache[i + 2] == 'x')
                    {
                        if (c == 'P') { props.Add(cur.ToString()); cur.Clear(); i += 2; continue; }
                        int end = cache.IndexOf(';', i + 2);
                        if (end < 0) end = cache.Length - 1;
                        cur.Append(cache, i, end - i + 1);
                        i = end + 1;
                        continue;
                    }
                    i += 2; // 跳过其它转义
                    continue;
                }
                i++; // 丢弃普通文本
            }
            props.Add(cur.ToString());
            return props;
        }
        catch { return null; }
    }

    private MTextLayout ParseMText(string raw, double baseHeight, string styleFont, string styleBig, List<string> paraProps = null)
    {
        var layout = new MTextLayout();
        MTextLine line = new MTextLine();
        layout.Lines.Add(line);
        var stack = new Stack<(double H, double WF, int? C, bool U, bool OV, bool B, bool I, double OB, string F, double T)>();
        double curH = baseHeight > 0 ? baseHeight : 1.0;
        double curWF = 1.0;
        int? curC = null;
        bool curU = false;
        bool curOV = false;
        bool curB = false;
        bool curI = false;
        double curOB = 0.0;
        string curFont = null;
        double curT = 1.0; // \T 字距因子（1=正常）
        double? curParaSpacing = null; // \pxsm<N>; 段落行距倍数（持续到下个 \px）
        int paraIdx = 0;               // 当前段号（\P 递增）
        var curText = new StringBuilder();

        // 段落起始：应用扩展字典缓存的 \px 属性（组码 44 常保持 1.0，AutoCAD 实际行距在缓存里）
        void ApplyParaProps()
        {
            if (paraProps != null && paraIdx < paraProps.Count)
            {
                string spec = paraProps[paraIdx];
                int smp = spec.LastIndexOf("sm", StringComparison.Ordinal);
                if (smp >= 0)
                {
                    int st = smp + 2, j = st;
                    while (j < spec.Length && (char.IsDigit(spec[j]) || spec[j] == '-' || spec[j] == '+' || spec[j] == '.')) j++;
                    if (j > st && double.TryParse(spec.Substring(st, j - st), NumberStyles.Float, Ci, out double smv) && smv > 0)
                        curParaSpacing = smv;
                }
            }
            line.ParaSpacing = curParaSpacing;
        }
        ApplyParaProps();

        void Flush()
        {
            if (curText.Length > 0)
                line.Runs.Add(new MTextRun
                {
                    Text = curText.ToString(), Height = curH, WidthFactor = curWF, Color = curC,
                    Underline = curU, Overline = curOV, Bold = curB, Italic = curI, Oblique = curOB, Font = curFont, Tracking = curT
                });
            curText.Length = 0;
        }
        void NewLine()
        {
            Flush();
            line = new MTextLine();
            paraIdx++;
            ApplyParaProps();
            layout.Lines.Add(line);
        }
        int SkipCode(int i)
        {
            int end = raw.IndexOf(';', i + 2);
            return end < 0 ? raw.Length - 1 : end;
        }

        if (!string.IsNullOrEmpty(raw))
        {
            for (int i = 0; i < raw.Length; i++)
            {
                char ch = raw[i];
                if (ch == '{')
                {
                    Flush();
                    stack.Push((curH, curWF, curC, curU, curOV, curB, curI, curOB, curFont, curT));
                }
                else if (ch == '}')
                {
                    Flush();
                    if (stack.Count > 0) (curH, curWF, curC, curU, curOV, curB, curI, curOB, curFont, curT) = stack.Pop();
                }
                else if (ch == '~' || ch == '\t')
                {
                    NewLine();
                }
                else if (ch == '\\')
                {
                    if (i + 1 >= raw.Length) break;
                    char code = raw[i + 1];
                    switch (code)
                    {
                        case 'P': NewLine(); i++; break;
                        case '\\': curText.Append('\\'); i++; break;
                        case '{': curText.Append('{'); i++; break;
                        case '}': curText.Append('}'); i++; break;
                        case 'H':
                        case 'h':
                            {
                                int end = SkipCode(i);
                                string v = raw.Substring(i + 2, end - i - 2).Trim();
                                bool rel = v.EndsWith("x") || v.EndsWith("X");
                                if (rel) v = v.Substring(0, v.Length - 1).Trim();
                                if (double.TryParse(v, NumberStyles.Float, Ci, out double h) && h > 0)
                                {
                                    // \H 只影响其后的文本：先以旧高度落段，再改高度
                                    // 相对高度（x 后缀）= 改码前的“当前高度”× 倍数（AutoCAD 语义，不是基准高度）
                                    double oldH = curH;
                                    Flush();
                                    curH = rel ? (oldH > 0 ? oldH * h : h) : h;
                                }
                                i = end;
                            }
                            break;
                        case 'W':
                        case 'w':
                            {
                                int end = SkipCode(i);
                                string v = raw.Substring(i + 2, end - i - 2).Trim();
                                if (double.TryParse(v, NumberStyles.Float, Ci, out double wf) && wf > 0)
                                {
                                    Flush(); // \W 只影响其后的文本
                                    curWF = wf;
                                }
                                i = end;
                            }
                            break;
                        case 'C':
                        case 'c':
                            {
                                int end = SkipCode(i);
                                string v = raw.Substring(i + 2, end - i - 2).Trim();
                                if (int.TryParse(v, out int c) && c >= 1 && c <= 255)
                                {
                                    Flush(); // \C 只影响其后的文本
                                    curC = c;
                                }
                                i = end;
                            }
                            break;
                        case 'L': Flush(); curU = true; i++; break;
                        case 'l': Flush(); curU = false; i++; break;
                        case 'f':
                        case 'F':
                            {
                                // \f<fontname>|b<bold>|i<italic>|c<charset>|p<pitch>;
                                // 字体名 = 第一个 '|' 之前；|b1|/|i1| 分别表示加粗/斜体
                                int end = SkipCode(i);
                                string arg = raw.Substring(i + 2, end - i - 2);
                                int pipe = arg.IndexOf('|');
                                string fontName = (pipe >= 0 ? arg.Substring(0, pipe) : arg).Trim();
                                bool specB = false, specI = false;
                                if (pipe >= 0)
                                {
                                    foreach (var part in arg.Substring(pipe + 1).Split('|'))
                                    {
                                        if (part.Length < 2) continue;
                                        char key = char.ToLowerInvariant(part[0]);
                                        string val = part.Substring(1).Trim();
                                        if (key == 'b') specB = (val == "1");
                                        else if (key == 'i') specI = (val == "1");
                                    }
                                }
                                // 兼容“字体名直接带 Bold/Italic”的写法
                                if (!specB && fontName.EndsWith("Bold", StringComparison.OrdinalIgnoreCase)) specB = true;
                                if (!specI && fontName.EndsWith("Italic", StringComparison.OrdinalIgnoreCase)) specI = true;
                                Flush(); // \f 只影响其后的文本
                                curFont = fontName.Length > 0 ? fontName : null;
                                curB = specB;
                                curI = specI;
                                i = end;
                            }
                            break;
                        case 'O': Flush(); curOV = true; i++; break;   // 上划线开
                        case 'o': Flush(); curOV = false; i++; break;  // 上划线关
                        case 'p':
                            {
                                // \p<段落格式>;  AutoCAD 段落属性码：\pi<值>; = 首行缩进（数据单位），
                                // \pxsm<倍数>; = 段落行距倍数（覆盖组码 44，持续到下个 \px）。
                                // 旧版把 \p 当未知码整段丢弃 → “\pi45.9;测试剧中”等段落全部顶到左边距（对齐不一致）。
                                int end = SkipCode(i);
                                string spec = raw.Substring(i + 2, Math.Max(0, end - i - 2));
                                int ip = spec.IndexOf('i');
                                if (ip >= 0)
                                {
                                    int st = ip + 1, j = st;
                                    while (j < spec.Length && (char.IsDigit(spec[j]) || spec[j] == '-' || spec[j] == '+' || spec[j] == '.')) j++;
                                    if (j > st && double.TryParse(spec.Substring(st, j - st), NumberStyles.Float, Ci, out double ind))
                                    {
                                        // 仅段落首行（此处紧跟 \P 后的全新行）继承缩进
                                        if (line.Runs.Count == 0) line.FirstIndent = ind;
                                    }
                                }
                                int smp = spec.IndexOf("sm", StringComparison.Ordinal);
                                if (smp >= 0)
                                {
                                    int st = smp + 2, j = st;
                                    while (j < spec.Length && (char.IsDigit(spec[j]) || spec[j] == '-' || spec[j] == '+' || spec[j] == '.')) j++;
                                    if (j > st && double.TryParse(spec.Substring(st, j - st), NumberStyles.Float, Ci, out double smv) && smv > 0)
                                    {
                                        curParaSpacing = smv;
                                        line.ParaSpacing = smv;
                                    }
                                }
                                i = end;
                            }
                            break;
                        case 'Q':
                            {
                                // \Q<倾斜角>; 倾斜角（度），影响其后的文本
                                int end = SkipCode(i);
                                string v = raw.Substring(i + 2, end - i - 2).Trim();
                                if (double.TryParse(v, NumberStyles.Float, Ci, out double ob))
                                {
                                    Flush();
                                    curOB = ob;
                                }
                                i = end;
                            }
                            break;
                        case 'T':
                            {
                                // \T<n>; 字距（tracking）因子：1=正常，<1 更紧（字符间距收窄），>1 更宽。
                                // 独立于 \W（宽度因子，改变字形宽/形状）。AutoCAD 实测：\T1→PDF Tc=0，\T0.75→Tc≈-25(1/1000em)。
                                int end = SkipCode(i);
                                string v = raw.Substring(i + 2, end - i - 2).Trim();
                                if (double.TryParse(v, NumberStyles.Float, Ci, out double tk) && tk > 0)
                                {
                                    Flush(); // \T 只影响其后的文本
                                    curT = tk;
                                }
                                i = end;
                            }
                            break;
                        case 'R':
                        case 'A':
                        case 'B':
                        case 'E':
                        case 'I':
                        case 'K':
                        case 'X':
                        case 'Y':
                            i = SkipCode(i); // 对齐/其他——消费，不渲染
                            break;
                        case 'k':
                            i++; // 删除线关（无参数，暂不渲染）
                            break;
                        case 'U':
                            if (i + 2 < raw.Length && raw[i + 2] == '+')
                            {
                                int end = i + 3;
                                while (end < raw.Length && end < i + 7 && IsHexChar(raw[end])) end++;
                                if (end > i + 3 && int.TryParse(raw.Substring(i + 3, end - i - 3), NumberStyles.HexNumber, Ci, out int cp))
                                    curText.Append((char)cp);
                                i = end - 1; // end=第一个非hex字符，i++ 后恰指向它（不能 i=end，否则 for 的 i++ 吞掉一个字符）
                            }
                            else i = SkipCode(i); // \U<on|off>; 下划线
                            break;
                        case 'S':
                            {
                                // \S<上>^<下>（超/下标，两 run 基线偏移）或 \S<分子>/<分母>（分数堆叠，单 run 上下分层+分数线）
                                // 旧版把 "^"/"/" 原样当正文 → 出现 "2^" "^3" 乱码字符；现按分隔符拆分。
                                // 分数用单 run（IsFraction，Text="num/den"）：渲染时分子/分母上下分层 + 中间画横线，
                                // 宽度取 max(分子,分母)。上下标用两 run（BaselineShift ±em，字号已由前置 \H 控制）。
                                int end = SkipCode(i);
                                string sc = raw.Substring(i + 2, end - i - 2).Replace("^B", "");
                                int caret = sc.IndexOf('^');
                                int slash = caret < 0 ? sc.IndexOf('/') : -1;
                                if (slash >= 0)
                                {
                                    string num = sc.Substring(0, slash);
                                    string den = slash + 1 < sc.Length ? sc.Substring(slash + 1) : "";
                                    Flush(); // \S 只影响其后自身
                                    line.Runs.Add(new MTextRun
                                    {
                                        Text = num + "/" + den, Height = curH, WidthFactor = curWF, Color = curC,
                                        Underline = curU, Overline = curOV, Bold = curB, Italic = curI,
                                        Oblique = curOB, Font = curFont, BaselineShift = 0, IsFraction = true, Tracking = curT
                                    });
                                }
                                else if (caret >= 0)
                                {
                                    string up = sc.Substring(0, caret);
                                    string lo = caret + 1 < sc.Length ? sc.Substring(caret + 1) : "";
                                    Flush(); // \S 只影响其后自身
                                    void AddStackRun(string txt, double shift)
                                    {
                                        if (string.IsNullOrEmpty(txt)) return;
                                        line.Runs.Add(new MTextRun
                                        {
                                            Text = txt, Height = curH, WidthFactor = curWF, Color = curC,
                                            Underline = curU, Overline = curOV, Bold = curB, Italic = curI,
                                            Oblique = curOB, Font = curFont, BaselineShift = shift, Tracking = curT
                                        });
                                    }
                                    AddStackRun(up, 0.35);   // 上标：基线上移 0.35em
                                    AddStackRun(lo, -0.30);  // 下标：基线下移 0.30em
                                }
                                else curText.Append(sc);
                                i = end;
                            }
                            break;
                        default:
                            if (char.IsLetter(code)) i = SkipCode(i); // 未知码（如 \pt7;）——消费
                            else curText.Append(ch);
                            break;
                    }
                }
                else
                {
                    curText.Append(ch);
                }
            }
        }
        Flush();

        foreach (var ln in layout.Lines) ln.Recalc(this, styleFont, styleBig);
        FinishMTextLayout(layout);
        return layout;
    }

    // MTEXT 排版结果
    private class MTextLayout
    {
        public List<MTextLine> Lines = new();
        public double MaxLineWidth;
    }

    private static void FinishMTextLayout(MTextLayout layout)
    {
        // 末尾纯空白行（如内容以大字号空格结尾）AutoCAD 不成行（无墨迹不渲染）→ 移除，避免框高虚增
        while (layout.Lines.Count > 1)
        {
            var last = layout.Lines[layout.Lines.Count - 1];
            bool allWs = true;
            foreach (var r in last.Runs)
                if (!string.IsNullOrWhiteSpace(r.Text)) { allWs = false; break; }
            if (!allWs) break;
            layout.Lines.RemoveAt(layout.Lines.Count - 1);
        }
        layout.MaxLineWidth = 0;
        foreach (var ln in layout.Lines)
            layout.MaxLineWidth = Math.Max(layout.MaxLineWidth, ln.Width);
    }

    // 行距（基线到基线）：AutoCAD 默认 1×（单倍）行距 = 字体定义的占位行高。
    //   用户公式：行距 = 字体占位高 × 字体大小(字高) × 行距倍数 + 固定间隙；实测 1.5×/2.5× 基线距均 = 倍数×单倍
    //   （比值精确）→ 倍数整体缩放，固定间隙已含在占位高内（SHX=Below、TTF=LineGap），加项为 0。
    //   factor（组码 44 / \pxsm）是【倍数】：Exact=倍数×占位×基准字高 H（不随行内高字符增长）；
    //   AtLeast=max(倍数×占位×H, 占位×行内墨迹最大字高)；默认=占位×行内墨迹最大字高。
    //   行内最大字高只数有墨迹的 run（AutoCAD 实测：行首/行尾大字号空格不改变基线距）。
    private double MTextLineGap(MTextLine prev, MTextLine cur, LineSpacingStyleType style, double factor, double lineRatio, double baseH)
    {
        double maxH = Math.Max(Math.Max(prev.InkMaxHeight(), cur.InkMaxHeight()), 0.1);
        double f = factor > 0 ? factor : 1.0;
        switch (style)
        {
            case LineSpacingStyleType.Exact:
                return f * lineRatio * baseH;
            case LineSpacingStyleType.AtLeast:
                return Math.Max(f * lineRatio * baseH, lineRatio * maxH);
            default:
                // None(0)：DXF 组码 73 缺省（ACadSharp 读不到即 None）——AutoCAD/DXF 惯例
                // 缺省按 AtLeast 语义，\pxsm 倍数必须生效。否则 SimSun \pxsm2.5 基线距退化为
                // 1×（9.94 世界 vs AutoCAD 实测 35.32，行距明显偏挤）。不含 maxH 下限
                // （None=高字符不覆盖 → 行距即倍数×占位×基准H）。
                return f * lineRatio * baseH;
        }
    }

    // 字体“自然行高占位”比值（世界单位 gap ÷ 字高 H），即 AutoCAD 默认 1×（单倍）行距所依据的“字体定义占位高度”。
    // 用户公式：行距 = 字体占位高 × 字体大小(字高) × 行距倍数 + 固定间隙。
    //   TTF 占位高 = (Ascent-Descent+2×LineGap)/CapHeight（cap=0 时退化为 /UnitsPerEm）
    //     —— SimSun: (220+36+72)/175 = 328/175 = 1.8743。AutoCAD 参考图实测（sample-mtext.bmp，
    //     SimSun 块 \pxsm2.5，基线距 35.32 世界 ÷ 2.5 ÷ 7.5 = 1.884，误差 0.5%）；LineGap 计两次
    //     （行盒一次 + 行间固定占位一次）。✗ 旧 (Asc-Desc+LineGap)/Cap=1.6686 比 AutoCAD 窄 11%。
    //   SHX 占位高 = (Above+2×Below)/Above = (em 盒 Above+Below + 一个下降部 Below 作行间间隙)/Above；
    //     simplex.shx: (21+14)/21 = 35/21 = 1.6667 —— 实测（simplex 块 \pxsm1.5，基线距 18.77 ÷ 1.5 ÷ 7.5
    //     = 1.668）误差 0.1%；txt/romans 同为 above:below=3:1 度量 → 1.6667。
    //   ✗ 旧公式 (Above+Below)/Above = 28/21 = 1.333 → 比 AutoCAD 窄 ~24%（图#3“间距不一致”根因）。
    //   ✗ SHX LF(shape#10) 下移量（simplex 裸移笔 36、净落 34；21+14=35 才合实测）仅用于 DTEXT 重复换行，非 MTEXT。
    //   固定间隙项：英文块 1.5×/中文块 2.5× 基线距均 = 倍数×单倍（比值精确）→ 倍数整体缩放、无不缩放加项；
    //     间隙已含于占位高（Below/LineGap 分量），故加项为 0。
    //   非 Unifont（bigfont/shapes 无有效 above/below 度量）回落文档统一常数 1.66。
    private const double MTextShxSingleSpacing = 1.66;
    private double MTextFontLineRatio(string font, string styleBig)
    {
        if (string.IsNullOrEmpty(font)) return MTextShxSingleSpacing; // 无字体（纯形状）→ 文档统一默认
        // TTF（含 CJK）：族名→ttf；占位 = (Asc-Desc+2×LineGap)/Cap（SimSun: 328/175 = 1.8743，AutoCAD 实测校准）
        var tf = GetTtfForWidth(font);
        if (tf != null && tf.UnitsPerEm > 0)
        {
            double denom = tf.CapHeight > 0 ? tf.CapHeight : tf.UnitsPerEm;
            double nat = (tf.Ascent - tf.Descent + 2 * tf.LineGap) / denom;
            if (nat > 0.5 && nat < 4.0) return nat;
        }
        // SHX：占位高由字体度量推导 = (Above + 2×Below)/Above
        //   —— em 盒(Above+Below) + 一个下降部(Below)作行间固定间隙；simplex=(21+14)/21=1.6667。
        //   bigfont/shapes 无有效 above/below 文本度量 → 回落文档统一常数 1.66。
        var shx = GetShapeFont(font);
        if (shx != null && shx.Above > 0
            && (shx.Format == ShxFormat.Unifont || (shx.Format == ShxFormat.Shapes && shx.IsTextFont)))
        {
            double nat = (double)(shx.Above + 2 * shx.Below) / shx.Above;
            if (nat > 0.5 && nat < 4.0) return nat;
        }
        return MTextShxSingleSpacing;
    }

    // 按参考列宽（组码41）换行：CJK 逐字可断，拉丁按词（含尾随空格）断
    private MTextLayout WrapMText(MTextLayout layout, double refWidth, string styleFont, string styleBig)
    {
        if (refWidth <= 0) return layout;
        // 换行判定须含首行缩进 \pi：行宽 = 缩进 + 文本宽（AutoCAD 右对正/居中段落同理，缩进挤占可用宽）。
        // 旧版仅比 line.Width →「测试右对齐」缩进 80.41 + 文本 54.86 = 135.27 > 133.12 本应把「齐」换到次行，
        // 却因 54.86 < 133.12 被误判整行放下 → 行数偏少、整块偏矮（与 AutoCAD 截图不符）。
        bool needWrap = false;
        foreach (var line in layout.Lines)
            if (line.FirstIndent + line.Width > refWidth && line.Runs.Count > 0) { needWrap = true; break; }
        if (!needWrap) return layout;

        var result = new MTextLayout();
        foreach (var line in layout.Lines)
        {
            if (line.FirstIndent + line.Width <= refWidth || line.Runs.Count == 0)
            {
                result.Lines.Add(line);
                continue;
            }
            // 分词：CJK 单字 / 连续空白 / 拉丁词+尾随空白
            var frags = new List<(string Text, MTextRun Run)>();
            foreach (var r in line.Runs)
            {
                int i = 0;
                while (i < r.Text.Length)
                {
                    char ch = r.Text[i];
                    int j = i + 1;
                    if (IsCjkChar(ch))
                    {
                        frags.Add((r.Text.Substring(i, 1), r));
                    }
                    else if (char.IsWhiteSpace(ch))
                    {
                        while (j < r.Text.Length && char.IsWhiteSpace(r.Text[j])) j++;
                        frags.Add((r.Text.Substring(i, j - i), r));
                    }
                    else
                    {
                        while (j < r.Text.Length && !IsCjkChar(r.Text[j]) && !char.IsWhiteSpace(r.Text[j])) j++;
                        int k = j;
                        while (k < r.Text.Length && char.IsWhiteSpace(r.Text[k])) k++;
                        frags.Add((r.Text.Substring(i, k - i), r));
                        j = k;
                    }
                    i = j;
                }
            }
            // 贪心填充
            var cur = new List<(string Text, MTextRun Run)>();
            double curW = line.FirstIndent; // 首行可用宽 = refWidth − 首行缩进
            double curLead = 0;             // 当前行首字左侧留白（换行时可让出的宽度）
            bool hasText = false;
            bool firstSub = true;           // 拆行后仅第一条子行继承段落首行缩进
            void FlushCur()
            {
                if (cur.Count > 0)
                {
                    var ln = new MTextLine();
                    ln.FirstIndent = firstSub ? line.FirstIndent : 0.0;
                    ln.ParaSpacing = line.ParaSpacing; // 换行产生的子行继承段落行距
                    firstSub = false;
                    foreach (var (t, run) in cur)
                    {
                        if (ln.Runs.Count > 0)
                        {
                            var last = ln.Runs[ln.Runs.Count - 1];
                            if (last.Height == run.Height && last.WidthFactor == run.WidthFactor
                                && last.Color == run.Color && last.Underline == run.Underline
                                && last.Overline == run.Overline && last.Bold == run.Bold
                                && last.Italic == run.Italic && last.Oblique == run.Oblique
                                && last.BaselineShift == run.BaselineShift
                                && last.IsFraction == run.IsFraction
                                && last.Tracking == run.Tracking
                                && (last.Font ?? "") == (run.Font ?? ""))
                            {
                                last.Text += t;
                                ln.Runs[ln.Runs.Count - 1] = last;
                                continue;
                            }
                        }
                        ln.Runs.Add(new MTextRun { Text = t, Height = run.Height, WidthFactor = run.WidthFactor, Color = run.Color, Underline = run.Underline, Overline = run.Overline, Bold = run.Bold, Italic = run.Italic, Oblique = run.Oblique, Font = run.Font, BaselineShift = run.BaselineShift, IsFraction = run.IsFraction, Tracking = run.Tracking });
                    }
                    // 行尾空白裁剪（仅对含墨迹的行）：行尾空格（含换行时从上一行掉落的长空格串）
                    // AutoCAD 不渲染、不计入行宽（否则合并单元格居中时行宽被空格串虚增 →
                    // 居中/右对齐偏移错误）。末 run 尾随空白 TrimEnd；末 run 纯空白则移除后
                    // 继续检查新末 run。纯空白行（无墨迹）不裁剪——保留原行为（FinishMTextLayout
                    // 移除末尾纯空白行），避免影响段落行距。
                    bool hasInk = false;
                    foreach (var r in ln.Runs) if (!string.IsNullOrWhiteSpace(r.Text)) { hasInk = true; break; }
                    if (hasInk)
                        while (ln.Runs.Count > 0)
                        {
                            var tail = ln.Runs[ln.Runs.Count - 1];
                            string tailTrim = tail.Text.TrimEnd();
                            if (tailTrim.Length == tail.Text.Length) break; // 无尾随空白
                            if (tailTrim.Length == 0) { ln.Runs.RemoveAt(ln.Runs.Count - 1); continue; }
                            tail.Text = tailTrim;
                            ln.Runs[ln.Runs.Count - 1] = tail;
                            break;
                        }
                    ln.Recalc(this, styleFont, styleBig);
                    result.Lines.Add(ln);
                }
                cur.Clear(); curW = 0; hasText = false; curLead = 0;
            }
            foreach (var (t, run) in frags)
            {
                string eFont = !string.IsNullOrEmpty(run.Font) ? run.Font : styleFont;
                string eBig = string.IsNullOrEmpty(run.Font) ? styleBig : null;
                double wf = run.WidthFactor > 0 ? run.WidthFactor : 1.0;
                bool shxOK = TryShxRunAdvance(eFont, eBig, t, run.Height, wf, out double wFull);
                double fw = shxOK ? wFull
                    : TtfMTextWidth(t, run.Height, wf, eFont, run);
                // 换行判定用【去掉尾随空白】的宽：行尾空白 AutoCAD 不渲染、不计入列宽
                // （否则「multiline text.」恰好 80 单位时会因尾随空格被误判越界而提前断行）。
                // curW 仍累加整段宽 fw（含尾随空白）作为下一词的分隔。
                double fwText = fw;
                string tTrim = t.TrimEnd();
                if (tTrim.Length != t.Length)
                {
                    if (tTrim.Length == 0) fwText = 0;
                    else fwText = shxOK ? (TryShxRunAdvance(eFont, eBig, tTrim, run.Height, wf, out double wT) ? wT : fw)
                        : TtfMTextWidth(tTrim, run.Height, wf, eFont, run);
                }
                // 换行判定须用【末字墨迹右缘】而非 advance 占位：advance 含末字右侧留白（CJK 约 6% em），
                // 导致比 AutoCAD 早换行（导出图多换行、AutoCAD 不换行）。墨迹右缘 = advance − 尾留白。
                // 定位 pen 仍按 advance 累加（保证字间距/字母定位正确），仅换行测试剔除尾留白。
                string inkSrc = tTrim.Length == 0 ? t : tTrim;
                double trailing = shxOK
                    ? ShxTrailingGapWorld(inkSrc, run.Height, wf, eFont, eBig)
                    : TtfTrailingGapWorld(inkSrc, run.Height, wf, eFont);
                double inkExtent = fwText - trailing;
                if (inkExtent < 0) inkExtent = 0;
                // 行首留白：该 frag 若为当前行首个有墨迹片段，其首字左侧留白不计入占位
                // （AutoCAD 以墨迹左缘对齐左边界 → 左侧留白可让给右侧，见 ShxLeadingGapWorld 注释）。
                if (cur.Count == 0 && !hasText)
                    curLead = shxOK
                        ? ShxLeadingGapWorld(t, run.Height, wf, eFont, eBig)
                        : TtfLeadingGapWorld(t, run.Height, wf, eFont);
                if (hasText && curW + inkExtent - curLead > refWidth) FlushCur();
                // 行首空白丢弃（AutoCAD 行首空白不渲染、不占宽）：上一行换行时，尾随大字号
                // 空格（{\H1.83x; } 等）掉到下一行行首——本 frag 处理中才触发 FlushCur，
                // 故 skip 必须在 flush 之后判（在循环开头判会因上一行仍在 cur 中而漏过）。
                // 旧版留在行首 → 该行文字被推后一个空格宽（实测 12.36 世界）、Width/居中/
                // 右对正基准被带偏；AutoCAD 参考图中该行紧贴左缘。
                if (cur.Count == 0 && !hasText && string.IsNullOrWhiteSpace(t)) continue;
                if (!string.IsNullOrWhiteSpace(t)) hasText = true;
                cur.Add((t, run));
                curW += fw;
            }
            FlushCur();
        }
        FinishMTextLayout(result);
        return result;
    }

    // MTEXT 局部排版：各行基线（第0行=0，y 向上）+ 附着点锚（局部坐标，数据单位）
    private (double[] Baselines, double AnchorX, double AnchorY) MTextLocalLayout(MTextLayout layout, MText mt, string styleFont, string styleBig)
    {
        int n = layout.Lines.Count;
        var baselines = new double[n];
        // 局部坐标系 y 向上（与 CAD 一致）：第 0 行基线在 0，其后各行向下（基线 y 递减），
        // 行距 = 基线到基线距离 = 字体定义自然行高（1× 行距）。旧版误用“+”使基线递增 → 多行上下颠倒、相互重叠。
        for (int i = 1; i < n; i++)
        {
            // 取当前行字体（内联 \f 优先，否则随实体样式）的自然行高比值
            string lineFont = styleFont;
            foreach (var r in layout.Lines[i].Runs)
                if (!string.IsNullOrEmpty(r.Font)) { lineFont = r.Font; break; }
            double ratio = MTextFontLineRatio(lineFont, styleBig);
            // 行距倍数：段落 \pxsm（扩展字典缓存/内联）优先，否则实体组码 44
            double fac = layout.Lines[i].ParaSpacing ?? mt.LineSpacing;
            baselines[i] = baselines[i - 1] - MTextLineGap(layout.Lines[i - 1], layout.Lines[i], mt.LineSpacingStyle, fac, ratio, mt.Height);
        }
        // 文本框：顶=首行基线 + cap height（字高 H 即 cap：H→大写顶部，见 ShxHeightScale/TtfCapScale）。
        //   H 对应 cap height（非 em），故首行 cap 顶 = baseline + MaxHeight。旧值 0.8×H 使 cap 高出
        //   插入点 0.2×H（图#2“顶部高出插入点”根因）；改为 MaxHeight 使首行 cap 顶恰在插入点（图#1 实测，不超出）。
        //   底=末行基线 − 0.2×行高（下降部，容纳 g/p/y 等下伸部）。
        double top = layout.Lines[0].MaxHeight;
        double bottom = baselines[n - 1] - 0.2 * layout.Lines[n - 1].MaxHeight;
        double boxH = top - bottom;
        double anchorX = 0, anchorY = top;
        switch (mt.AttachmentPoint)
        {
            case AttachmentPointType.TopCenter:
            case AttachmentPointType.MiddleCenter:
            case AttachmentPointType.BottomCenter: anchorX = layout.MaxLineWidth / 2; break;
            case AttachmentPointType.TopRight:
            case AttachmentPointType.MiddleRight:
            case AttachmentPointType.BottomRight: anchorX = layout.MaxLineWidth; break;
        }
        switch (mt.AttachmentPoint)
        {
            case AttachmentPointType.MiddleLeft:
            case AttachmentPointType.MiddleCenter:
            case AttachmentPointType.MiddleRight: anchorY = top - boxH / 2; break;
            case AttachmentPointType.BottomLeft:
            case AttachmentPointType.BottomCenter:
            case AttachmentPointType.BottomRight: anchorY = bottom; break;
        }
        return (baselines, anchorX, anchorY);
    }

    // MTEXT → SVG：每行每段独立 <text>（各自字号/颜色），基线按附着点+旋转定位
    private void EmitMTextSvg(StringBuilder sb, MText mt, Transform2D tr, double scaleMul, short layerAci, short byBlockAci, string styleFont)
    {
        string baseCol = ColorOf(EntityAciIndex(mt), layerAci, byBlockAci);
        string styleBig = GetStyleBigFont(mt.Style);
        var layout = WrapMText(ParseMText(mt.Value ?? "", mt.Height, styleFont, styleBig, MTextCacheParaProps(mt)), mt.RectangleWidth, styleFont, styleBig);
        if (layout.Lines.Count == 0) return;
        var (baselines, anchorX, anchorY) = MTextLocalLayout(layout, mt, styleFont, styleBig);

        double rot = mt.Rotation * 180.0 / Math.PI + (tr?.Rotation ?? 0.0); // fork 弧度 → 度（MText.Rotation 由 AlignmentPoint 推导）
        double rad = rot * Math.PI / 180.0;
        double ca = Math.Cos(rad), sa = Math.Sin(rad);
        double px = mt.InsertPoint.X, py = mt.InsertPoint.Y;
        if (tr != null) { var ap = tr.Apply(px, py); px = ap.Item1; py = ap.Item2; }

        // 局部点→世界：P + R·(local − anchor)（局部偏移乘 scaleMul）
        void ToWorld(double lx, double ly, out double wx, out double wy)
        {
            double dx = (lx - anchorX) * scaleMul, dy = (ly - anchorY) * scaleMul;
            wx = px + dx * ca - dy * sa;
            wy = py + dx * sa + dy * ca;
        }

        for (int li = 0; li < layout.Lines.Count; li++)
        {
            // 行起点 = 段落首行缩进 − 首字左侧留白：AutoCAD 以【墨迹左缘】对齐左边界（插入点），
            // 而非字形 advance 占位盒左界（参考图实测：每行墨迹左缘距插入点≈0，我们旧版右偏 0.44 单位）。
            // 左对齐：墨迹左缘对齐文本框左缘；居中/右对齐：每行在文本框内按附件点水平定位
            // （多行 MTEXT 的短行必须各自居中/右对齐，而非都贴在文本框左缘——合并单元格多行居中修复）。
            bool leftAligned = layout.Lines[li].InkLeft > 0 &&
                (mt.AttachmentPoint == AttachmentPointType.TopLeft
                 || mt.AttachmentPoint == AttachmentPointType.MiddleLeft
                 || mt.AttachmentPoint == AttachmentPointType.BottomLeft);
            bool centerAligned = mt.AttachmentPoint == AttachmentPointType.TopCenter
                || mt.AttachmentPoint == AttachmentPointType.MiddleCenter
                || mt.AttachmentPoint == AttachmentPointType.BottomCenter;
            bool rightAligned = mt.AttachmentPoint == AttachmentPointType.TopRight
                || mt.AttachmentPoint == AttachmentPointType.MiddleRight
                || mt.AttachmentPoint == AttachmentPointType.BottomRight;
            double lineX = layout.Lines[li].FirstIndent;
            if (centerAligned) lineX += (layout.MaxLineWidth - layout.Lines[li].Width) / 2;
            else if (rightAligned) lineX += layout.MaxLineWidth - layout.Lines[li].Width;
            double x = (lineX - (leftAligned ? layout.Lines[li].InkLeft : 0.0)) * scaleMul;
            foreach (var run in layout.Lines[li].Runs)
            {
                double rw = MTextRunWidth(run, styleFont, styleBig) * scaleMul;
                if (run.IsFraction)
                {
                    // \S<num>/<den> 分数：分子/分母上下分层 + 中间分数线（横线宽=分数宽）
                    string col = run.Color.HasValue ? AciColor(run.Color.Value) : baseCol;
                    string fracFont = !string.IsNullOrEmpty(run.Font) ? run.Font : styleFont;
                    double fracCap = TtfCapScale(ResolveFontFamily(fracFont));
                    double fs = Math.Max(run.Height * scaleMul * fracCap, 0.2);
                    string cssFont = SvgFontFamily(ResolveFontFamily(fracFont));
                    int sl = run.Text.IndexOf('/');
                    string num = run.Text.Substring(0, sl);
                    string den = sl + 1 < run.Text.Length ? run.Text.Substring(sl + 1) : "";
                    double wf = run.WidthFactor > 0 ? run.WidthFactor : 1.0;
                    double numW = EstimateTextWidth(num, run.Height) * wf * scaleMul * fracCap;
                    double denW = EstimateTextWidth(den, run.Height) * wf * scaleMul * fracCap;
                    double fracW = Math.Max(numW, denW); // = rw
                    // 分数堆叠几何（CAD y 向上，相对主基线；AutoCAD 实测：分子体全在分数线上方、
                    // 分母体全在线下方，线本身在主基线上方约 0.58×字高。旧版线画在主基线上 →
                    // 分母上缘越过分数线，视觉“横线偏下/穿过 2”。）
                    double H = run.Height;
                    double numOff = 0.72 * H;   // 分子基线：主基线上方
                    double denOff = 0.30 * H;   // 分母基线：主基线下方
                    double barOff = 0.58 * H;   // 分数线：主基线上方
                    double ob = EffOblique(run); // 斜体合成倾斜角
                    double numX = x + (fracW - numW) / 2.0; // 分子水平居中
                    double denX = x + (fracW - denW) / 2.0; // 分母水平居中
                    void Emtxt(double lx, double ly, string s)
                    {
                        ToWorld(lx, ly, out double wx, out double wy);
                        double sx = Sx(wx), sy = Sy(wy);
                        var tf = new List<string> { $"translate({F(sx)}, {F(sy)})" };
                        if (Math.Abs(rot) > 1e-6) tf.Add($"rotate({F(-rot)})");
                        if (Math.Abs(ob) > 1e-6) tf.Add($"skewX({F(-ob)})");
                        if (Math.Abs(run.WidthFactor - 1.0) > 1e-6) tf.Add($"scale({F(run.WidthFactor)}, 1)");
                        sb.AppendLine($"  <text x=\"0\" y=\"0\" font-family=\"{cssFont}\" font-size=\"{F(fs)}\" fill=\"{col}\" transform=\"{string.Join(" ", tf)}\">{EscapeXml(s)}</text>");
                    }
                    if (!string.IsNullOrEmpty(num)) Emtxt(numX, baselines[li] + numOff, num);
                    if (!string.IsNullOrEmpty(den)) Emtxt(denX, baselines[li] - denOff, den);
                    // 分数线：主基线上方 barOff，宽=分数宽
                    ToWorld(x, baselines[li] + barOff, out double f1x, out double f1y);
                    ToWorld(x + fracW, baselines[li] + barOff, out double f2x, out double f2y);
                    sb.AppendLine($"  <line x1=\"{F(Sx(f1x))}\" y1=\"{F(Sy(f1y))}\" x2=\"{F(Sx(f2x))}\" y2=\"{F(Sy(f2y))}\" stroke=\"{col}\" stroke-width=\"{F(Math.Max(fs * 0.04, 0.1))}\"/>");
                }
                else if (!string.IsNullOrEmpty(run.Text))
                {
                    string col = run.Color.HasValue ? AciColor(run.Color.Value) : baseCol;
                    double fs = Math.Max(run.Height * scaleMul, 0.2);
                    double baseLineY = baselines[li] + run.BaselineShift * run.Height;
                    string effFont = !string.IsNullOrEmpty(run.Font) ? run.Font : styleFont;
                    string effBig = string.IsNullOrEmpty(run.Font) ? styleBig : null;
                    double ob = EffOblique(run);                 // 斜体合成倾斜角（度，正=前倾）
                    double obRad = ob * Math.PI / 180.0;          // 局部 y 向上坐标系：x' += tan(ob)·y（与 <text> skewX(-ob) 经 Y 翻转等价）
                    double wf = run.WidthFactor > 0 ? run.WidthFactor : 1.0;

                    // 局部笔画点（基线原点 / 数据单位 / y 向上）→ wf 压缩 + 斜切 → 平移到 run 原点 → ToWorld(旋转+缩放) → Sx/Sy(翻转)
                    void LocalToPixel(double lx, double ly, out double px, out double py)
                    {
                        double ax = lx * wf + Math.Tan(obRad) * ly;
                        double wxLocal = x + ax;
                        double wyLocal = baseLineY + ly;
                        ToWorld(wxLocal, wyLocal, out double wx, out double wy);
                        px = Sx(wx); py = Sy(wy);
                    }

                    // SHX 笔迹优先（simplex.shx / \Fsimplex 等）：真实 SHX 字形，字高随 em 自然，不再经 SimSun 压缩→偏小
                    if (TryBuildMTextShxLocal(effFont, effBig, run.Text, run.Height, wf, out List<int> st, out List<double[]> sp, out _))
                    {
                        var sd = new StringBuilder();
                        for (int k = 0; k < st.Count; k++)
                        {
                            var p = sp[k];
                            switch (st[k])
                            {
                                case 0:
                                    LocalToPixel(p[0], p[1], out double m0x, out double m0y);
                                    sd.Append("M").Append(F(m0x)).Append(',').Append(F(m0y));
                                    break;
                                case 1:
                                    LocalToPixel(p[0], p[1], out double l1x, out double l1y);
                                    sd.Append("L").Append(F(l1x)).Append(',').Append(F(l1y));
                                    break;
                                case 2:
                                    LocalToPixel(p[0], p[1], out double q0x, out double q0y);
                                    LocalToPixel(p[2], p[3], out double q1x, out double q1y);
                                    sd.Append("Q").Append(F(q0x)).Append(',').Append(F(q0y)).Append(' ').Append(F(q1x)).Append(',').Append(F(q1y));
                                    break;
                                case 3:
                                    LocalToPixel(p[0], p[1], out double c0x, out double c0y);
                                    LocalToPixel(p[2], p[3], out double c1x, out double c1y);
                                    LocalToPixel(p[4], p[5], out double c2x, out double c2y);
                                    sd.Append("C").Append(F(c0x)).Append(',').Append(F(c0y)).Append(' ').Append(F(c1x)).Append(',').Append(F(c1y)).Append(' ').Append(F(c2x)).Append(',').Append(F(c2y));
                                    break;
                            }
                        }
                        double penW = Math.Max(fs * 0.05, 0.2); // SHX 笔画线宽（像素）：约 5% 字高，保底 0.2
                        sb.AppendLine($"  <path d=\"{sd}\" fill=\"none\" stroke=\"{col}\" stroke-width=\"{F(penW)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>");
                    }
                    else
                    {
                        // 回退 TTF（含 CJK 无 bigfont / 内联 TTF 字体 / 字形缺失）→ 原 <text> 路径
                        string cssFont = SvgFontFamily(ResolveFontFamily(effFont));
                        // TTF cap-height 字号映射（Phase 2）：AutoCAD 把 H 当 cap height，非 em。
                        // 与单行 TTF（line 2165）及宽度估算 TtfMTextWidth 一致——此前 MText TTF 未套
                        // cap-scale，字形比单行/AutoCAD 小 ~28%（"多行富文本字体不一致"）。SHX 分支不受影响。
                        double ttfFs = Math.Max(run.Height * scaleMul * TtfCapScale(ResolveFontFamily(effFont)), 0.2);
                        ToWorld(x, baseLineY, out double wx, out double wy);
                        double sx = Sx(wx), sy = Sy(wy);
                        // 富文本属性：字体族/字号/颜色/加粗/下划线+上划线
                        var attr = new List<string>
                        {
                            $"x=\"0\" y=\"0\"",
                            $"font-family=\"{cssFont}\"",
                            $"font-size=\"{F(ttfFs)}\"",
                            $"fill=\"{col}\""
                        };
                        if (run.Bold) attr.Add($"font-weight=\"bold\"");
                        // \T 字距：letter-spacing（文字局部坐标单位；会被后续 scale(wf) 缩放，除以 wf 使最终像素字距独立于 \W，与 MTextRunWidth/PDF Tc 一致）
                        double trackLocal = (run.Tracking - 1.0) * 0.1 * ttfFs / (wf > 1e-9 ? wf : 1.0);
                        if (Math.Abs(trackLocal) > 1e-6) attr.Add($"letter-spacing=\"{F(trackLocal)}\"");
                        var deco = new List<string>();
                        if (run.Underline) deco.Add("underline");
                        if (run.Overline) deco.Add("overline");
                        if (deco.Count > 0) attr.Add($"text-decoration=\"{string.Join(" ", deco)}\"");

                        // transform：平移到基线左端点，再绕该点 rotate(旋转) / skewX(倾斜) / scale(宽度压缩)
                        // 斜体统一用 skewX 合成（替代 font-style:italic —— CJK/无真斜体字体无效果）
                        var tf = new List<string> { $"translate({F(sx)}, {F(sy)})" };
                        if (Math.Abs(rot) > 1e-6) tf.Add($"rotate({F(-rot)})");
                        if (Math.Abs(ob) > 1e-6) tf.Add($"skewX({F(-ob)})");
                        if (Math.Abs(run.WidthFactor - 1.0) > 1e-6) tf.Add($"scale({F(run.WidthFactor)}, 1)");
                        attr.Add($"transform=\"{string.Join(" ", tf)}\"");

                        sb.AppendLine($"  <text {string.Join(" ", attr)}>{EscapeXml(run.Text)}</text>");
                    }
                }
                x += rw;
            }
        }
    }

    #endregion

    #region ===== 基础图元发射（内部统一经 Sx/Sy 翻转坐标）=====
    // dash 为虚线模式（数据单位）；null/空 = 实线。
    private void EmitLine(StringBuilder sb, double x1, double y1, double x2, double y2, string stroke, double w, double[] dash)
    {
        string da = DashAttr(dash);
        sb.AppendLine($"  <line x1=\"{F(Sx(x1))}\" y1=\"{F(Sy(y1))}\" x2=\"{F(Sx(x2))}\" y2=\"{F(Sy(y2))}\" stroke=\"{stroke}\" stroke-width=\"{F(w)}\"{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
    }

    private void EmitPolyline(StringBuilder sb, List<(double X, double Y)> pts, string stroke, double w, bool closed, double[] dash)
    {
        if (pts.Count < 2) return;
        var d = new StringBuilder();
        d.Append("M");
        for (int i = 0; i < pts.Count; i++)
            d.Append($"{(i == 0 ? "" : "L")}{F(Sx(pts[i].X))},{F(Sy(pts[i].Y))}");
        if (closed) d.Append("Z");
        string da = DashAttr(dash);
        sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{F(w)}\" stroke-linejoin=\"round\"{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
    }

    // 变宽折线（组码43 起宽>0）按 CAD 语义渲染为封闭填充多边形
    private void EmitTaperedPolyline(StringBuilder sb, LwPolyline pl, Transform2D tr, string stroke, double scaleMul, double[] dash)
    {
        var verts = pl.Vertices;
        int n = verts.Count;
        if (n < 2) return;
        // 逐顶点有效宽度（数据单位）：无 bulge 弦偏移路径用（顶点 i 宽 = 起宽40，为0时继承入段终宽41）。
        double[] w = TaperedVertexWidths(pl);
        // 逐段起/终点宽（含 bulge 弧偏移路径用）：段 i 从 40_i 渐变到 41_i，段间顶点收尖。
        var (sw, ew) = TaperedSegmentWidths(pl);

        List<(double X, double Y)> left, right;
        var taper = TryBuildTaperOffset(pl, sw, ew, tr, _opt.TaperOffsetBuilder);
        if (taper.Ok)
        {
            // 含 bulge（弧段）：弧采样偏移轮廓（GC200 圆盘修复，T5.4）
            left = taper.Left; right = taper.Right;
        }
        else
        {
            // 无 bulge：原逐顶点弦偏移路径（输出与旧实现逐字节一致）
            left = new List<(double X, double Y)>(n);
            right = new List<(double X, double Y)>(n);
            for (int i = 0; i < n; i++)
            {
                double px = verts[i].Location.X, py = verts[i].Location.Y;
                int ni = pl.IsClosed ? (i + 1) % n : (i + 1 < n ? i + 1 : i);
                int pi = pl.IsClosed ? (i - 1 + n) % n : (i - 1 >= 0 ? i - 1 : i);
                double fx = verts[ni].Location.X - px, fy = verts[ni].Location.Y - py;
                double bx = px - verts[pi].Location.X, by = py - verts[pi].Location.Y;
                double fl = Math.Sqrt(fx * fx + fy * fy); if (fl < 1e-9) { fx = 1; fy = 0; } else { fx /= fl; fy /= fl; }
                double bl = Math.Sqrt(bx * bx + by * by); if (bl < 1e-9) { bx = 1; by = 0; } else { bx /= bl; by /= bl; }
                double tx = fx + bx, ty = fy + by;
                double tl = Math.Sqrt(tx * tx + ty * ty); if (tl < 1e-9) { tx = fx; ty = fy; } else { tx /= tl; ty /= tl; }
                double nx = -ty, ny = tx;
                double hw = w[i] / 2.0;
                double lx = px + nx * hw, ly = py + ny * hw;
                double rx = px - nx * hw, ry = py - ny * hw;
                if (tr != null) { var a = tr.Apply(lx, ly); left.Add((a.Item1, a.Item2)); var b = tr.Apply(rx, ry); right.Add((b.Item1, b.Item2)); }
                else { left.Add((lx, ly)); right.Add((rx, ry)); }
            }
        }

        string da = DashAttr(dash);
        var d = new StringBuilder();
        int m = left.Count;
        if (pl.IsClosed)
        {
            // 闭合折线：left/right 各是一条封闭偏移轮廓（内/外环），两个子路径 + evenodd 填充得到"环"
            // （与 OFF 路由 DxfImageExporter.EmitTaperedPolyline 逐字节一致：属性序/0.1/round）
            d.Append("M");
            for (int i = 0; i < m; i++) d.Append($"{(i == 0 ? "" : "L")}{F(Sx(left[i].X))},{F(Sy(left[i].Y))}");
            d.Append("Z M");
            for (int i = 0; i < m; i++) d.Append($"{(i == 0 ? "" : "L")}{F(Sx(right[i].X))},{F(Sy(right[i].Y))}");
            d.Append("Z");
            sb.AppendLine($"  <path d=\"{d}\" fill=\"{stroke}\" fill-rule=\"evenodd\" stroke=\"{stroke}\" stroke-width=\"0.1\" stroke-linejoin=\"round\"{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
        }
        else
        {
            // 开放折线：left 正向 + right 反向，两端闭合填充（带/渐变楔形）
            d.Append("M");
            for (int i = 0; i < m; i++) d.Append($"{(i == 0 ? "" : "L")}{F(Sx(left[i].X))},{F(Sy(left[i].Y))}");
            for (int i = m - 1; i >= 0; i--) d.Append($"L{F(Sx(right[i].X))},{F(Sy(right[i].Y))}");
            d.Append("Z");
            sb.AppendLine($"  <path d=\"{d}\" fill=\"{stroke}\" stroke=\"{stroke}\" stroke-width=\"0.1\" stroke-linejoin=\"round\"{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
        }
    }

    // [T5.4 GC200 修复] 含 bulge（弧段）的变宽折线偏移轮廓：
    // 弧段按每 2° 采样（与 EmitArcAsPolyline 一致），沿真实弧法向（径向）偏移——
    // 典型收益：GC200 点块（2 顶点 bulge=1、常数宽 0.2、闭合，r=hw=0.1）从 0.2×0.2 弦方块
    // 变为外半径 r+hw 的正确填充圆盘（内环 r−hw≤0 时退化为圆心点，evenodd 仍填实）。
    // 角顶点取入/出边切向的平分线法向（直线边=弦向，弧边=端点切向），宽度沿弧线性插值。
    // 无 bulge 时返回 false → 调用方走原逐顶点弦路径（输出保持不变，像素 parity 不受影响）。
    // 几何核心经 ITaperOffsetBuilder 注入（R13 架构拆分：算法在宿主程序 GDB2CAD.Core.TaperOffset，
    // 本包只含渲染契约）；builder 为 null 时回退弦路径（R10 前行为）。本方法只负责实体类型
    // 适配 + 变换应用（逐点顺序与旧实现一致 → 输出字节不变）。
    private static (bool Ok, List<(double X, double Y)> Left, List<(double X, double Y)> Right) TryBuildTaperOffset(LwPolyline pl, double[] sw, double[] ew, Transform2D tr, ITaperOffsetBuilder builder)
    {
        if (builder == null) return (false, null, null);
        var verts = pl.Vertices;
        int n = verts.Count;
        double[] xs = new double[n];
        double[] ys = new double[n];
        double[] bs = new double[n];
        for (int i = 0; i < n; i++) { xs[i] = verts[i].Location.X; ys[i] = verts[i].Location.Y; bs[i] = verts[i].Bulge; }
        var core = builder.TryBuild(n, pl.IsClosed, xs, ys, bs, sw, ew);
        if (core == null) return (false, null, null);
        var left = new List<(double X, double Y)>(core.Value.Left.Count);
        var right = new List<(double X, double Y)>(core.Value.Right.Count);
        for (int i = 0; i < core.Value.Left.Count; i++)
        {
            double lx = core.Value.Left[i].X, ly = core.Value.Left[i].Y;
            double rx = core.Value.Right[i].X, ry = core.Value.Right[i].Y;
            if (tr != null)
            {
                var a = tr.Apply(lx, ly); left.Add((a.Item1, a.Item2));
                var b = tr.Apply(rx, ry); right.Add((b.Item1, b.Item2));
            }
            else { left.Add((lx, ly)); right.Add((rx, ry)); }
        }
        return (true, left, right);
    }

    private void EmitCircle(StringBuilder sb, double cx, double cy, double r, string stroke, double w, double[] dash)
    {
        string da = DashAttr(dash);
        sb.AppendLine($"  <circle cx=\"{F(Sx(cx))}\" cy=\"{F(Sy(cy))}\" r=\"{F(r)}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{F(w)}\"{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
    }

    private void EmitEllipse(StringBuilder sb, double cx, double cy, double rx, double ry, double rotDeg, string stroke, double w, double[] dash)
    {
        double scx = Sx(cx), scy = Sy(cy);
        // Y 翻转使椭圆主轴角度取负
        string trs = (Math.Abs(rotDeg) > 1e-6) ? $" transform=\"rotate({F(-rotDeg)} {F(scx)} {F(scy)})\"" : "";
        string da = DashAttr(dash);
        sb.AppendLine($"  <ellipse cx=\"{F(scx)}\" cy=\"{F(scy)}\" rx=\"{F(rx)}\" ry=\"{F(ry)}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{F(w)}\"{trs}{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
    }

    // CAD 弧恒为从起始角到终止角逆时针（end<start 时跨过 360°）
    private void EmitArcAsPolyline(StringBuilder sb, double cx, double cy, double r, double startDeg, double endDeg, string stroke, double w, double[] dash)
    {
        if (r <= 0) return;
        double s = startDeg * Math.PI / 180.0;
        double e = endDeg * Math.PI / 180.0;
        double span = e - s;
        while (span < 0) span += 2 * Math.PI;
        while (span >= 2 * Math.PI) span -= 2 * Math.PI;
        if (span < 1e-6) span = 2 * Math.PI; // start==end 视为整圆
        int seg = Math.Max(8, (int)Math.Ceiling(span / (Math.PI / 90.0))); // 每 2° 一段
        var d = new StringBuilder();
        for (int i = 0; i <= seg; i++)
        {
            double ang = s + span * i / seg;
            double x = cx + r * Math.Cos(ang);
            double y = cy + r * Math.Sin(ang);
            d.Append($"{(i == 0 ? "M" : "L")}{F(Sx(x))},{F(Sy(y))}");
        }
        string da = DashAttr(dash);
        sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{F(w)}\" stroke-linejoin=\"round\"{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
    }

    private void EmitPoint(StringBuilder sb, double cx, double cy, string color, double lw)
    {
        // 2026-10-26 用户裁定：$PDSIZE=0 不再抑制点——
        //   ① 中心点恒按基础 1 像素绘制（0.5 输出单位半径，不随 PDSIZE/缩放变化）；
        //   ② 配置了外框/臂（+/×/竖线/圆框/方框）时，按设置比例（PointGlyphRadius）绘制边显示；
        //   ③ PDMODE=1 且无外框 → 完全无显示。
        // 点样式（$PDMODE/$PDSIZE）——2026-10-16 用户定稿标准 + 2026-10-17 整体缩小一半：
        //   ① 笔画与直线一致（lw，1 像素不加粗，废除随字形缩放的加粗公式）
        //   ② 半径 = 点大小值 × 0.5（数据单位）：PDSIZE=0 → 视高 5%；>0 → 绝对值；<0 → 视高百分比
        //      （“不管设置是 5% 还是 5 单位，1×5×10=50 像素”——折算为数据单位半径，
        //        经渲染变换映射为像素：size(数据单位) × 每单位像素数）
        //   ③ 方框半宽 = 半径，圆框半径 = 半径（同一半径）
        //   ④ 加号半伸出（沿轴）= 2r；X 臂长（沿 45° 对角）= 2r（尖坐标偏移 r√2，伸出圆外恰一 r）；竖线总长 = r（第 5 列）；点为小实心圆
        //   2026-10-17 用户“绘制的有点大了，缩小一半”→ PointGlyphRadius 整体 ×0.5（三引擎统一）；
        //   2026-10-26 用户“点存在边框渲染时感觉有点大了，边框按缩小一半渲染”→ 整体再减半 ×0.25
        //   PDMODE 位0-2：0=点 1=不显示 2=加号 3=X 4=竖线；位5(32)=外加圆框；位6(64)=外加方框
        int mode = _pdMode;
        int shape = mode & 7;
        bool hasFrame = (mode & 96) != 0;
        if (shape == 1 && !hasFrame) return;       // PDMODE=1：无中心图形也无外框 → 完全无显示
                                                      // 位0=1 但带外框（33/65/97）= 只画外框、无中心图形
        double r = PointGlyphRadius();                // 框半径（数据单位）= 点大小值
        if (!double.IsFinite(r) || r <= 0) return;
        double w = lw;                                // 笔画与直线一致，不加粗
        double arm = r * 2.0;                         // 加号半伸出（沿轴）= 2r（超出框一 r）
        double xarm = r * Math.Sqrt(2.0);             // X 臂尖坐标偏移 = 2r/√2：沿 45° 对角臂长恰为 2r，
                                                     // 伸出圆（半径 r）外恰一 r（2026-10-26 用户修正：
                                                     // 原尖在 (±2r,±2r)，沿臂实际 2√2r≈2.83r，伸出 1.83r 太长）
        double scx = Sx(cx), scy = Sy(cy);
        double fx0 = Sx(cx - r), fx1 = Sx(cx + r), fy0 = Sy(cy - r), fy1 = Sy(cy + r);
        double ax0 = Sx(cx - arm), ax1 = Sx(cx + arm), ay0 = Sy(cy - arm), ay1 = Sy(cy + arm);
        double xax0 = Sx(cx - xarm), xax1 = Sx(cx + xarm), xay0 = Sy(cy - xarm), xay1 = Sy(cy + xarm);
        // 外加圆框（PDMODE 位5=32；对话框第 2 行）：半径 = 点大小值
        if ((mode & 32) != 0)
            sb.AppendLine($"  <circle cx=\"{F(scx)}\" cy=\"{F(scy)}\" r=\"{F(Math.Abs(fx1 - scx))}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{F(w)}\"/>");
        // 外加方框（PDMODE 位6=64；对话框第 3 行）：半宽 = 点大小值
        if ((mode & 64) != 0)
        {
            var d = new StringBuilder();
            d.Append("M").Append(F(fx0)).Append(',').Append(F(fy0))
             .Append("H").Append(F(fx1)).Append("V").Append(F(fy1))
             .Append("H").Append(F(fx0)).Append("Z");
            sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{F(w)}\" stroke-linejoin=\"miter\"/>");
        }
        // 中心图形
        switch (shape)
        {
            case 0: // 点：基础 1 像素小实心圆（2026-10-26 用户裁定：中心点恒 1px 屏幕直径 = 0.5 输出单位半径，
            // 不随 PDSIZE/缩放变化，纯填充无背景光晕；仅外框/臂随 PointGlyphRadius 比例缩放）
            {
                sb.AppendLine($"  <circle cx=\"{F(scx)}\" cy=\"{F(scy)}\" r=\"0.5\" fill=\"{color}\"/>");
                break;
            }
            case 2: // 加号（±2r，伸出框外一 r）
            {
                var d = new StringBuilder();
                d.Append("M").Append(F(ax0)).Append(',').Append(F(scy)).Append("H").Append(F(ax1))
                 .Append("M").Append(F(scx)).Append(',').Append(F(ay0)).Append("V").Append(F(ay1));
                sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{F(w)}\" stroke-linecap=\"butt\"/>");
                break;
            }
            case 3: // X（臂长沿 45° 对角 = 2r，尖在 ±r√2，伸出圆外一 r）
            {
                var d = new StringBuilder();
                d.Append("M").Append(F(xax0)).Append(',').Append(F(xay0)).Append("L").Append(F(xax1)).Append(',').Append(F(xay1))
                 .Append("M").Append(F(xax0)).Append(',').Append(F(xay1)).Append("L").Append(F(xax1)).Append(',').Append(F(xay0));
                sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{F(w)}\" stroke-linecap=\"butt\"/>");
                break;
            }
            case 4: // 竖线（总长 = r；2026-10-16 用户定稿：第 5 列竖线长度为 r（点大小值）；10-17 缩小一半后 = 点大小值/2）
            {
                double vh = r * 0.5;
                double vy0 = Sy(cy - vh), vy1 = Sy(cy + vh);
                var d = new StringBuilder();
                d.Append("M").Append(F(scx)).Append(',').Append(F(vy0)).Append("V").Append(F(vy1));
                sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{F(w)}\" stroke-linecap=\"butt\"/>");
                break;
            }
        }
    }

    // 点样式半径（数据单位）= 点大小值 × PointGlyphScale：
    // PDSIZE=0 → 视高 5%；>0 → 绝对单位；<0 → |PDSIZE|% 视高。
    // 用户定稿：“不管设置是 5% 还是 5 单位”——一律折算为数据单位半径（方框半宽/圆框半径），
    // 渲染时经变换映射为像素（如 5 单位 × 每单位 10 像素 = 50 像素半径）。
    // 2026-10-17 用户反馈“绘制的有点大了，缩小一半”→ 整体乘以 0.5；
    // 2026-10-26 用户反馈“点存在边框渲染时感觉有点大了，边框大小按缩小一半渲染”→
    // 点按直径算法的比例缩放整体再减半（PointGlyphScale=0.25）：
    // 方框半宽/圆框半径 = 点大小值/4；加号半伸出 = 点大小值/2；X 臂长（沿对角）= 点大小值/2；竖线总长 = 点大小值/4；
    // 中心点恒 1 像素（0.5 输出单位半径，不随比例缩放，2026-10-26 裁定不变）。
    // 依赖 _h（画布高度），须在 WriteSvg/WritePdf/WritePng 定稿 _h 之后调用。
    private const double PointGlyphScale = 0.25;  // 2026-10-17 减半 0.5；2026-10-26 用户：边框再减半 → 0.25
    // 2026-10-26 用户裁定：中心点为固定 1 像素（0.5 输出单位半径），不随点大小/缩放变化（原 CenterDotRadius=1.5 数据单位已删除）；
    // 仅周围方框/圆框/+/X/竖线随点渲染比例（PointGlyphRadius）缩放。
    private double PointGlyphRadius()
    {
        double viewH = _h;
        if (!double.IsFinite(viewH) || viewH <= 0) viewH = 1.0;
        double r;
        if (Math.Abs(_pdSize) < 1e-12) r = viewH * 0.05;
        else if (_pdSize > 0) r = _pdSize;
        else r = viewH * Math.Abs(_pdSize) / 100.0;
        if (!double.IsFinite(r) || r <= 0) r = viewH * 0.05;
        return r * PointGlyphScale;
    }

    // 点样式字形会超出点实体位置范围：PDSIZE=0 时 r=5% 视高，加号/X 更伸到 2r，
    // 边缘点的框/标记会被画布裁掉（曾致总览图首格圆形只剩半弧）。
    // 这里把视界四边各向外扩 r，保证字形完整可见。
    // PDSIZE=0 时 r 依赖 _h（循环依赖），迭代 3 次收敛（公比 5%）。
    private void ExpandViewForPointGlyphs()
    {
        if (!HasVisiblePointGlyph()) return;
        for (int i = 0; i < 3; i++)
        {
            double r = PointGlyphRadius();
            _minX -= r; _maxX += r; _minY -= r; _maxY += r;
            _w = (_maxX - _minX) + 2 * _margin;
            _h = (_maxY - _minY) + 2 * _margin;
        }
    }

    private bool HasVisiblePointGlyph()
    {
        // 2026-10-26 用户裁定：中心点恒为固定 1 像素（0.5 输出单位半径），不会超出画布被裁 → 纯点无需扩界；
        // 仅当点样式含外框（位 32/64）或臂（+/×/竖线，shape 2/3/4）时字形会超出点位置，才需扩界保证完整可见。
        int mode = _pdMode;
        int shape = mode & 7;
        bool hasFrame = (mode & 96) != 0;
        if (shape == 0) return false;                   // 纯点：固定 1px，不扩界
        if (shape == 1 && !hasFrame) return false;      // PDMODE=1 无框：无可见字形（与 EmitPoint 一致）
        foreach (var ent in AllEntities())
            if (ent is Point) return true;
        return false;
    }

    // R18：普通 TEXT 宽度因子（DXF 41 / WidthFactor）——水平缩放锚定基线起点 (sx,sy)：
    // translate(sx,sy) scale(WF,1) translate(-sx,-sy) 先按文字局部系压缩字宽，再整体旋转（与 MText SVG 的 scale(WF,1) 同语义）。
    // WF=1 时不输出任何 scale（保持既有输出逐字节不变）。
    private void EmitText(StringBuilder sb, double x, double y, string content, double fs, string fill, double rotDeg, string fontFamily, double widthFactor = 1.0)
    {
        if (string.IsNullOrEmpty(content)) return;
        double sx = Sx(x), sy = Sy(y);
        var tf = new List<string>();
        if (Math.Abs(rotDeg) > 1e-6) tf.Add($"rotate({F(rotDeg)} {F(sx)} {F(sy)})");
        if (Math.Abs(widthFactor - 1.0) > 1e-6)
            tf.Add($"translate({F(sx)}, {F(sy)}) scale({F(widthFactor)}, 1) translate({F(-sx)}, {F(-sy)})");
        string trs = tf.Count > 0 ? $" transform=\"{string.Join(" ", tf)}\"" : "";
        string esc = EscapeXml(content);
        sb.AppendLine($"  <text x=\"{F(sx)}\" y=\"{F(sy)}\" font-family=\"{fontFamily}\" font-size=\"{F(fs)}\" fill=\"{fill}\"{trs}>{esc}</text>");
    }

    // Face3D(3DFACE) 四角（DXF 组码 10/11/12/13 = 角1..4）按**周长顺序**存储：角1-2、2-3、3-4、4-1 是四条边，
    // 直接 1→2→3→4 连线即正确边界（不可对角跳连，否则三维面交叉）。
    private static List<(double X, double Y)> QuadPts(XYZ a, XYZ b, XYZ c, XYZ d, Transform2D tr)
        => new[] { a, b, c, d }.Select(v => tr == null ? (v.X, v.Y) : tr.Apply(v.X, v.Y)).ToList();

    // Solid(TRACE) 四角：不同导出器写入约定不一——有的按**顺序四边形**（边 1-2、2-3、3-4、4-1），
    // 有的按**对角配对**（角1-2 与角3-4 为对边）。两种约定下「角1-2 恒为一条边」，故候选绕序仅两种：
    //   周长序 1→2→3→4 与 对角序 1→2→4→3，二者**恰有一个**是不自交的简单多边形。
    // 自交「蝴蝶结」的有向面积相互抵消(趋近 0)，简单多边形面积=真实面积，故取 |有向面积| 较大者即自动选对。
    // 三角形退化(角4=角1，如 sample-hatch GC526 倒三角)：对角序面积=0、周长序=真实三角面积，自动选周长序。
    // （历史：硬编码 1→2→4→3 修过"表格蓝底行交叉"，却把顺序四边形的三角 SOLID 画成零面积，故改为自动判别。）
    private static List<(double X, double Y)> SolidQuadPts(XYZ a, XYZ b, XYZ c, XYZ d, Transform2D tr)
    {
        (double X, double Y) T(XYZ v) => tr == null ? (v.X, v.Y) : tr.Apply(v.X, v.Y);
        var pa = T(a);
        var pb = T(b);
        var pc = T(c);
        var pd = T(d);
        var perimeter = new[] { pa, pb, pc, pd };
        var diagonal = new[] { pa, pb, pd, pc };
        var pick = Math.Abs(PolySignedArea(diagonal)) > Math.Abs(PolySignedArea(perimeter)) ? diagonal : perimeter;
        return pick.ToList();
    }

    // 多边形有向面积（Shoelace）；自交蝴蝶结因方向相反的叶瓣相消而趋近 0。
    private static double PolySignedArea((double X, double Y)[] p)
    {
        double s = 0;
        int n = p.Length;
        for (int i = 0; i < n; i++)
        {
            var q = p[(i + 1) % n];
            s += p[i].X * q.Y - q.X * p[i].Y;
        }
        return s / 2.0;
    }

    private void EmitQuadFill(StringBuilder sb, List<(double X, double Y)> pts, string color)
    {
        if (pts.Count < 3) return;
        var d = new StringBuilder();
        d.Append("M");
        for (int i = 0; i < pts.Count; i++)
            d.Append($"{(i == 0 ? "" : "L")}{F(Sx(pts[i].X))},{F(Sy(pts[i].Y))}");
        d.Append("Z");
        sb.AppendLine($"  <path d=\"{d}\" fill=\"{color}\" stroke=\"{color}\" stroke-width=\"0.15\"/>");
    }

    // 等轴测 3D 网格渲染（Bug4 DOME）：把 (x,y,z) 投影到等轴测 2D（世界坐标，y 向上），
    // 背面剔除 + 法线光照灰度着色 + 画家算法深度排序；邻接≥1可见面的边=实线，其余=虚线。
    // 返回 (面多边形[远→近], 每面灰度, 可见边, 隐藏边)；所有坐标为等轴测世界坐标（y 向上）。
    private static (List<List<(double X,double Y)>> Faces, List<byte> Grays,
                     List<(double X1,double Y1,double X2,double Y2)> Vis,
                     List<(double X1,double Y1,double X2,double Y2)> Hid)
        MeshIsoRender(Mesh mesh)
    {
        var eFaces = new List<List<(double X,double Y)>>();
        var eGrays = new List<byte>();
        var eVis = new List<(double X1,double Y1,double X2,double Y2)>();
        var eHid = new List<(double X1,double Y1,double X2,double Y2)>();
        if (mesh == null) return (eFaces, eGrays, eVis, eHid);
        var vs = mesh.Vertices;
        var fs = mesh.Faces;
        if (vs == null || vs.Count < 3) return (eFaces, eGrays, eVis, eHid);

        // 1) (x,y) 质心，作为等轴测投影锚点（保持 DOME 位于其顶视中心）
        double cx = 0, cy = 0;
        foreach (var v in vs) { cx += v.X; cy += v.Y; }
        cx /= vs.Count; cy /= vs.Count;

        // 2) 等轴测投影：iso_x = cx + 0.866*(dx-dy);  iso_y = cy + 0.5*(dx+dy) + dz
        const double C30 = 0.8660254037844386;
        var iso = new (double X, double Y)[vs.Count];
        for (int i = 0; i < vs.Count; i++)
        {
            double dx = vs[i].X - cx, dy = vs[i].Y - cy, dz = vs[i].Z;
            iso[i] = (cx + C30 * (dx - dy), cy + 0.5 * (dx + dy) + dz);
        }

        // 光照方向（顶偏左）：使 顶面最亮、左面中等、右面最暗
        double Lx = -0.75, Ly = -0.35, Lz = 0.95;
        double Ln = Math.Sqrt(Lx * Lx + Ly * Ly + Lz * Lz); Lx /= Ln; Ly /= Ln; Lz /= Ln;
        const double Ambient = 0.35, Diffuse = 0.62;

        // 视图方向（进入屏幕）= (1,1,-1)
        var faces = new List<(List<(double X,double Y)> Pts, byte Gray, double Depth)>();
        var faceVis = fs != null ? new bool[fs.Count] : new bool[0];

        if (fs != null)
        {
            for (int fi = 0; fi < fs.Count; fi++)
            {
                var f = fs[fi];
                if (f == null || f.Length < 3) continue;
                var a = vs[f[0]];
                var b = vs[f[1]];
                var c = vs[f[2]];
                double e1x = b.X - a.X, e1y = b.Y - a.Y, e1z = b.Z - a.Z;
                double e2x = c.X - a.X, e2y = c.Y - a.Y, e2z = c.Z - a.Z;
                double nx = e1y * e2z - e1z * e2y;
                double ny = e1z * e2x - e1x * e2z;
                double nz = e1x * e2y - e1y * e2x;
                double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (nl < 1e-9) continue;
                nx /= nl; ny /= nl; nz /= nl;
                // 朝相机（-视图方向）为可见：facing = nz - nx - ny
                bool vis = (nz - nx - ny) > 1e-6;
                faceVis[fi] = vis;
                if (!vis) continue;
                int n = f.Length;
                double px = 0, py = 0, pz = 0;
                for (int i = 0; i < n; i++) { var vv = vs[f[i]]; px += vv.X; py += vv.Y; pz += vv.Z; }
                px /= n; py /= n; pz /= n;
                double depth = (px + py - pz) / Math.Sqrt(3.0); // 进入屏幕深度（越大越远）
                double lam = nx * Lx + ny * Ly + nz * Lz;
                if (lam < 0) lam = 0;
                int gray = (int)Math.Round((Ambient + Diffuse * lam) * 255);
                if (gray < 0) gray = 0; if (gray > 255) gray = 255;
                var pts = new List<(double X,double Y)>(n);
                for (int i = 0; i < n; i++) pts.Add(iso[f[i]]);
                faces.Add((pts, (byte)gray, depth));
            }
        }

        // 3) 画家算法：远（depth 大）先画，近后画
        faces.Sort((p, q) => q.Depth.CompareTo(p.Depth));

        // 4) 边：邻接≥1 可见面 → 实线；否则虚线
        var edgeFaces = new Dictionary<long, List<int>>();
        if (fs != null)
        {
            for (int fi = 0; fi < fs.Count; fi++)
            {
                var f = fs[fi];
                if (f == null || f.Length < 3) continue;
                for (int i = 0; i < f.Length; i++)
                {
                    int u = f[i], w = f[(i + 1) % f.Length];
                    long key = ((long)Math.Min(u, w) << 32) | (uint)Math.Max(u, w);
                    if (!edgeFaces.TryGetValue(key, out var lst)) { lst = new List<int>(); edgeFaces[key] = lst; }
                    lst.Add(fi);
                }
            }
        }
        var visEdges = new List<(double X1,double Y1,double X2,double Y2)>();
        var hidEdges = new List<(double X1,double Y1,double X2,double Y2)>();
        var es = mesh.Edges;
        if (es != null)
        {
            foreach (var e in es)
            {
                int u = e.Start, w = e.End;
                if (u < 0 || w < 0 || u >= vs.Count || w >= vs.Count) continue;
                long key = ((long)Math.Min(u, w) << 32) | (uint)Math.Max(u, w);
                bool solid = false;
                if (edgeFaces.TryGetValue(key, out var fl))
                    for (int k = 0; k < fl.Count; k++)
                        if (fl[k] < faceVis.Length && faceVis[fl[k]]) { solid = true; break; }
                var pa = iso[u];
                var pb = iso[w];
                (solid ? visEdges : hidEdges).Add((pa.X, pa.Y, pb.X, pb.Y));
            }
        }

        var facePolys = new List<List<(double X,double Y)>>(faces.Count);
        var grays = new List<byte>(faces.Count);
        foreach (var fc in faces) { facePolys.Add(fc.Pts); grays.Add(fc.Gray); }
        return (facePolys, grays, visEdges, hidEdges);
    }
    #endregion

    #region ===== HATCH / SPLINE 渲染核心 =====

    // 记录一个被跳过的实体（类型名或细分原因），导出结束统一告警
    private void RecordSkip(string key)
    {
        _skippedCounts.TryGetValue(key, out int c);
        _skippedCounts[key] = c + 1;
    }

    private void LogSkipped(string kind)
    {
        if (_skippedCounts.Count == 0) return;
        var parts = _skippedCounts
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key + "×" + kv.Value);
        _log($"⚠️  {kind} 未渲染实体（跳过）：{string.Join("  ", parts)}");
    }

    private static void AddPoint(List<XY> pts, double x, double y, Transform2D tr)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;
        if (tr == null) pts.Add(new XY(x, y));
        else { var (ax, ay) = tr.Apply(x, y); pts.Add(new XY(ax, ay)); }
    }

    // 弧采样（角度为度）
    private static void AppendArcPoints(List<XY> pts, XY center, double r, double startDeg, double endDeg, bool ccw, Transform2D tr)
    {
        if (r <= 0 || !double.IsFinite(r)) return;
        double s = startDeg * Math.PI / 180.0;
        double e = endDeg * Math.PI / 180.0;
        double span = e - s;
        while (span < 0) span += 2 * Math.PI;
        while (span >= 2 * Math.PI) span -= 2 * Math.PI;
        if (span < 1e-6) span = 2 * Math.PI; // start==end 视为整圆
        double dir = ccw ? 1.0 : -1.0;
        int seg = Math.Max(8, (int)Math.Ceiling(span / (Math.PI / 90.0))); // 每 2° 一段
        for (int i = 0; i <= seg; i++)
        {
            double ang = s + dir * span * i / seg;
            AddPoint(pts, center.X + r * Math.Cos(ang), center.Y + r * Math.Sin(ang), tr);
        }
    }

    // 椭圆弧边采样：EndMajorAxis 为主轴向量（长=主轴半径 A），参数角 φ 从主轴起算
    private static void AppendEllipseEdge(List<XY> pts, Hatch.BoundaryPath.Ellipse el, Transform2D tr)
    {
        double ax = el.MajorAxisEndPoint.X, ay = el.MajorAxisEndPoint.Y;
        double A = Math.Sqrt(ax * ax + ay * ay);
        if (A <= 0 || !double.IsFinite(A)) return;
        double ratio = el.RadiusRatio;
        if (!double.IsFinite(ratio) || ratio <= 0) ratio = 1.0;
        if (ratio > 1.0) ratio = 1.0 / ratio; // AutoCAD 恒 ≤1；>1 视为轴互换，取倒数保持 A 为主轴
        double B = A * ratio;
        double ux = ax / A, uy = ay / A;   // 主轴单位向量
        double vx = -uy, vy = ux;          // 次轴单位向量（主轴旋转 90°）
        double s = el.StartAngle;          // fork 弧度（参考实现此处为度→弧度）
        double e = el.EndAngle;
        double span = e - s;
        while (span < 0) span += 2 * Math.PI;
        while (span >= 2 * Math.PI) span -= 2 * Math.PI;
        if (span < 1e-6) span = 2 * Math.PI;
        double dir = el.CounterClockWise ? 1.0 : -1.0;
        int seg = Math.Max(12, (int)Math.Ceiling(span / (Math.PI / 45.0))); // 每 4° 一段
        for (int i = 0; i <= seg; i++)
        {
            double phi = s + dir * span * i / seg;
            AddPoint(pts, el.Center.X + ux * A * Math.Cos(phi) + vx * B * Math.Sin(phi),
                              el.Center.Y + uy * A * Math.Cos(phi) + vy * B * Math.Sin(phi), tr);
        }
    }

    // 多线段边：顶点 Z=bulge（≠0 时该顶点到下一顶点为弧）
    private static void AppendPolylineEdge(List<XY> pts, Hatch.BoundaryPath.Polyline poly, Transform2D tr)
    {
        if (poly.Vertices == null || poly.Vertices.Count == 0) return;
        int nv = poly.Vertices.Count;
        for (int i = 0; i < nv; i++)
        {
            var v = poly.Vertices[i];
            AddPoint(pts, v.X, v.Y, tr);
            var v2 = poly.Vertices[i == nv - 1 && poly.IsClosed ? 0 : i + 1];
            double dx = v2.X - v.X, dy = v2.Y - v.Y;
            if (dx * dx + dy * dy <= 1e-18) continue;      // 零长段
            if (Math.Abs(v.Z) < 1e-12) continue;          // 直线段
            var arc = ArcFromBulge(new XY(v.X, v.Y), new XY(v2.X, v2.Y), v.Z);
            if (Math.Abs(arc.Radius) < 1e-12) continue;    // 退化弧按直线处理
            AppendArcPoints(pts, arc.Center, arc.Radius, arc.StartDeg, arc.EndDeg, true, tr);
        }
    }

    // bulge 分解：p1→p2 弦 + bulge=tan(θ/4) → 弧（带符号 sweep θ=4*atan(bulge)）。
    // 圆心计算与 ACadSharp.Arc.GetCenter **逐字一致**（权威参考，PolylineExtensions.GetPoints 同源）：
    // 从 p1 起、沿 p1→p2 方向角偏转 sign(bulge)*gamma 定位圆心。
    // 旧"弦中垂线"版本把圆心取到弦的**错误一侧**（sgn*h 符号反了），导致采样时
    // a1+θ 的落点偏离分段第二点 ≈ 弦长（实测云形段偏离 18.68），弧线终点对不上 V2，
    // 留下缺口被折线补成直线弦 = 用户所见"凸度弧度多渲染了线段 / 云形出现直线尖刺"。
    // 修复后 a1+θ ≡ a2 精确命中 V2，相邻弧段首尾相接、无直线弦。
    private static (XY Center, double Radius, double StartDeg, double EndDeg) ArcFromBulge(XY p1, XY p2, double bulge)
    {
        double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
        double chord = Math.Sqrt(dx * dx + dy * dy);
        if (chord < 1e-12) return (p1, 0, 0, 0);
        double theta = 4.0 * Math.Atan(Math.Abs(bulge));            // 正角（绝对值）
        double r = chord / (2.0 * Math.Sin(theta / 2.0));
        if (!double.IsFinite(r) || r <= 0) return (p1, 0, 0, 0);
        double gamma = (Math.PI - theta) / 2.0;
        double phi = Math.Atan2(dy, dx) + Math.Sign(bulge) * gamma; // p1→p2 方向 + 符号偏转
        double cx = p1.X + r * Math.Cos(phi);
        double cy = p1.Y + r * Math.Sin(phi);
        double a1 = Math.Atan2(p1.Y - cy, p1.X - cx) * 180.0 / Math.PI;
        double a2 = Math.Atan2(p2.Y - cy, p2.X - cx) * 180.0 / Math.PI;
        return (new XY(cx, cy), r, a1, a2);
    }

    // 变宽折线逐顶点有效宽度（三引擎共享）：AutoCAD 语义——顶点 i 宽度 = 本顶点起宽(40)；
    // 起宽为 0 时继承入段（顶点 i-1→i）终宽(41)。
    //  · 连续折线（41[i-1]==40[i]，如纺锤形 40=[0,5,0,5]）：两式等价，w=[0,5,0,5]（零回归）。
    //  · 不连续折线（如云形全 40=0/41=0.85）：取入段终宽 → 每顶点 0.85，渲染为**均匀带**
    //    （AutoCAD 实测 4-6px 均匀，非 0→W 楔形锯齿）——旧"物化 max(40,41)"捷径会把真楔形
    //    （40=0/41=5）也错画成均匀 5，本模型对真楔形正确渐变。
    //  · 开放折线首顶点无入段 → 用其自身起宽(40)。
    private static double[] TaperedVertexWidths(LwPolyline pl)
    {
        var verts = pl.Vertices;
        int n = verts.Count;
        var w = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (verts[i].StartWidth > 0) { w[i] = verts[i].StartWidth; continue; }
            int pi = pl.IsClosed ? (i - 1 + n) % n : (i - 1);
            w[i] = (pi >= 0) ? verts[pi].EndWidth : verts[i].StartWidth;
        }
        return w;
    }

    // 逐段起/终点全宽（三引擎共享，喂给 ITaperOffsetBuilder 的含 bulge 变宽路径）：
    // 段 i（顶点 i→i+1）起点宽 = 组码 40_i(StartWidth[i])、终点宽 = 组码 41_i(EndWidth[i])，
    // 共 ec = closed ? n : n−1 段。段 i 内从 sw[i] 渐变到 ew[i]；段间在共享顶点处
    // 入段终宽 ew[i−1] 与出段起宽 sw[i] 可不连续 → 偏移轮廓在该顶点收尖（云形 scallop 凹谷，
    // 用户反馈"凸度每小段两端点宽不一致"）。连续折线（ew[i]==sw[i+1]）退化为逐顶点等宽（零回归）。
    private static (double[] Sw, double[] Ew) TaperedSegmentWidths(LwPolyline pl)
    {
        var verts = pl.Vertices;
        int n = verts.Count;
        int ec = pl.IsClosed ? n : n - 1;
        var sw = new double[ec];
        var ew = new double[ec];
        for (int i = 0; i < ec; i++)
        {
            sw[i] = verts[i].StartWidth;
            ew[i] = verts[i].EndWidth;
        }
        return (sw, ew);
    }

    // LwPolyline 采样为世界系折线点（三引擎共享，含 bulge 弧段）：
    // 逐顶点处理，bulge≠0 的段用 ArcFromBulge+AppendArcPoints 采样为弧（每 2° 一段），否则直线弦。
    // tr 非空时所有点世界化。此前普通路径只取 v.Location.X/Y 丢 Bulge → 带凸度的圆环/弧段
    // （如 verts=35 全 bulge 的圆、verts=27 带 26 个 bulge 的弧）被画成顶点折线 = "控制点连线"。
    // 变宽（ConstantWidth/StartWidth>0）路径不经过本方法（走 EmitTaperedPolyline 自有 bulge 偏移逻辑）。
    private static List<(double X, double Y)> LwPolylineSamplePoints(LwPolyline pl, Transform2D tr)
    {
        var res = new List<(double X, double Y)>();
        var verts = pl?.Vertices;
        if (verts == null) return res;
        int n = verts.Count;
        if (n < 2) return res;
        var xy = new List<XY>(n * 2);
        void Push(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) return;
            if (tr == null) xy.Add(new XY(x, y));
            else { var a = tr.Apply(x, y); xy.Add(new XY(a.Item1, a.Item2)); }
        }
        Push(verts[0].Location.X, verts[0].Location.Y);
        int last = pl.IsClosed ? n : n - 1;
        for (int i = 0; i < last; i++)
        {
            int ni = (i + 1) % n;
            var v1 = verts[i].Location;
            var v2 = verts[ni].Location;
            double bulge = verts[i].Bulge;
            if (Math.Abs(bulge) > 1e-9)
            {
                var (c, r, sdeg, edeg) = ArcFromBulge(new XY(v1.X, v1.Y), new XY(v2.X, v2.Y), bulge);
                if (r > 0 && xy.Count > 0)
                {
                    // 用**带符号 sweep 角** theta=4·atan(bulge) 从起始角 a1 直接采样到 a2：
                    // AppendArcPoints 会把 span 无条件规整到 [0,2π]（假设逆时针），对 bulge<0 的
                    // 顺时针小弧（如 -90°）误画成 270° 补大弧 → 全 bulge 圆环渲染成波浪形。
                    // a1+theta ≡ a2（mod 2π），故从 a1 沿符号方向走 theta 正好 p1→p2。
                    xy.RemoveAt(xy.Count - 1); // 去掉 v1（弧从 v1 重新采样）
                    double theta = 4.0 * Math.Atan(bulge); // 带符号 sweep（弧度）
                    double a1 = sdeg * Math.PI / 180.0;
                    int seg = Math.Max(8, (int)Math.Ceiling(Math.Abs(theta) / (Math.PI / 90.0))); // 每 2°
                    for (int k = 0; k <= seg; k++)
                    {
                        double ang = a1 + theta * k / seg;
                        Push(c.X + r * Math.Cos(ang), c.Y + r * Math.Sin(ang));
                    }
                    continue;
                }
            }
            Push(v2.X, v2.Y);
        }
        foreach (var p in xy) res.Add((p.X, p.Y));
        return res;
    }

    // hatch 边界样条边：控制点 Z=权重（DXF 规定），NURBS 采样
    private static void AppendSplineEdge(List<XY> pts, Hatch.BoundaryPath.Spline sp, Transform2D tr)
    {
        if (sp.ControlPoints == null || sp.ControlPoints.Count < 2 || sp.Knots == null) return;
        int n = sp.ControlPoints.Count;
        int p = sp.Degree; if (p < 1) p = 1;
        if (sp.Knots.Count != n + p + 1)
        {
            for (int i = 0; i < n; i++) AddPoint(pts, sp.ControlPoints[i].X, sp.ControlPoints[i].Y, tr); // 回退折线
            return;
        }
        var hx = new double[n]; var hy = new double[n]; var hw = new double[n];
        for (int i = 0; i < n; i++)
        {
            double w = sp.ControlPoints[i].Z;
            if (!double.IsFinite(w) || Math.Abs(w) < 1e-9) w = 1.0;
            hx[i] = sp.ControlPoints[i].X * w; hy[i] = sp.ControlPoints[i].Y * w; hw[i] = w;
        }
        pts.AddRange(SampleNurbs(sp.Knots.ToArray(), hx, hy, hw, p, 8, tr));
    }

    // 把 hatch 边界分解为折线子路径集合（tr 变换后的模型坐标），每子路径闭环。
    private static List<List<XY>> BuildHatchLoops(Hatch hatch, Transform2D tr)
    {
        var result = new List<List<XY>>();
        if (hatch == null) return result;
        foreach (var path in hatch.Paths)
        {
            var pts = new List<XY>();
            foreach (var edge in path.Edges)
            {
                switch (edge)
                {
                    case Hatch.BoundaryPath.Line ln:
                        AddPoint(pts, ln.Start.X, ln.Start.Y, tr);
                        AddPoint(pts, ln.End.X, ln.End.Y, tr);
                        break;
                    case Hatch.BoundaryPath.Arc arc:
                        AppendArcPoints(pts, new XY(arc.Center.X, arc.Center.Y), arc.Radius,
                            arc.StartAngle * 180.0 / Math.PI, arc.EndAngle * 180.0 / Math.PI, arc.CounterClockWise, tr);
                        break;
                    case Hatch.BoundaryPath.Ellipse el:
                        AppendEllipseEdge(pts, el, tr);
                        break;
                    case Hatch.BoundaryPath.Spline sp:
                        AppendSplineEdge(pts, sp, tr);
                        break;
                    case Hatch.BoundaryPath.Polyline poly:
                        AppendPolylineEdge(pts, poly, tr);
                        break;
                }
            }
            if (pts.Count >= 2)
            {
                double dx = pts[pts.Count - 1].X - pts[0].X, dy = pts[pts.Count - 1].Y - pts[0].Y;
                if (dx * dx + dy * dy > 1e-12) pts.Add(pts[0]); // 显式闭合（hatch 区域必须封闭）
            }
            if (pts.Count >= 3) result.Add(pts);
        }
        return result;
    }

    // 顶层 Spline 实体采样：控制点+节矢 → de Boor NURBS（支持有理权重）；
    // 仅拟合点 → 过拟合点折线；节矢缺失 → 控制点折线回退。
    private static List<XY> SplineSample(Spline sp, Transform2D tr, int samplesPerSpan)
    {
        var pts = new List<XY>();
        if (sp == null) return pts;
        var cp = sp.ControlPoints;
        if (cp != null && cp.Count >= 2)
        {
            int p = sp.Degree; if (p < 1) p = 1;
            int n = cp.Count;
            if (sp.Knots != null && sp.Knots.Count == n + p + 1)
            {
                var hx = new double[n]; var hy = new double[n]; var hw = new double[n];
                var w = sp.Weights;
                for (int i = 0; i < n; i++)
                {
                    double wi = (w != null && i < w.Count && Math.Abs(w[i]) >= 1e-9) ? w[i] : 1.0;
                    hx[i] = cp[i].X * wi; hy[i] = cp[i].Y * wi; hw[i] = wi;
                }
                pts.AddRange(SampleNurbs(sp.Knots.ToArray(), hx, hy, hw, p, samplesPerSpan, tr));
            }
            else
                for (int i = 0; i < n; i++) AddPoint(pts, cp[i].X, cp[i].Y, tr); // 回退：控制点折线
        }
        else if (sp.FitPoints != null)
            foreach (var fp in sp.FitPoints) AddPoint(pts, fp.X, fp.Y, tr);

        // 闭合样条：SampleNurbs 用 span 中点采样（j+0.5/ns），首尾采样点不精确重合，
        // 三引擎描边又都按开放路径发射（SVG 无 Z / PNG closed=false / PDF 无闭合）→ 闭合样条
        // 首尾留缺口（#52）。这里补回首采样点使首尾重合，描边自然闭合（三引擎共用本方法）。
        if (sp.IsClosed && pts.Count >= 2) pts.Add(pts[0]);
        return pts;
    }

    // NURBS 逐节 span 中点采样（de Boor 算法；hx/hy/hw 为齐次坐标 x·w/y·w/w）
    private static List<XY> SampleNurbs(double[] knots, double[] hx, double[] hy, double[] hw, int p, int samplesPerSpan, Transform2D tr)
    {
        var pts = new List<XY>();
        int n = hx.Length;
        if (p < 1 || p >= n) return pts;
        for (int s = p; s <= n - 1; s++)
        {
            double k0 = knots[s], k1 = knots[s + 1];
            if (!(k1 > k0)) continue; // 跳过零长 span
            int ns = Math.Max(2, samplesPerSpan);
            for (int j = 0; j < ns; j++)
            {
                double t = k0 + (k1 - k0) * (j + 0.5) / ns;
                DeBoor(hx, hy, hw, knots, p, s, t, out double x, out double y);
                AddPoint(pts, x, y, tr);
            }
        }
        return pts;
    }

    // de Boor 算法求 (齐次) NURBS 在节 span s、参数 t 处的点
    private static void DeBoor(double[] hx, double[] hy, double[] hw, double[] knots, int p, int s, double t, out double x, out double y)
    {
        int n = hx.Length;
        var dx = new double[p + 1];
        var dy = new double[p + 1];
        var dw = new double[p + 1];
        for (int j = 0; j <= p; j++)
        {
            int idx = s - p + j;
            if (idx < 0) idx = 0; if (idx >= n) idx = n - 1;
            dx[j] = hx[idx]; dy[j] = hy[idx]; dw[j] = hw[idx];
        }
        for (int k = 1; k <= p; k++)
        {
            for (int j = s - p + k; j <= s; j++)
            {
                double n1 = knots[j], n2 = knots[j + p - k + 1];
                double denom = n2 - n1;
                double a = denom == 0 ? 0.0 : (t - n1) / denom;
                int jj = j - (s - p + k);
                // 标准 de Boor 递推：d_j^(k) = (1-α)·d_j^(k-1) + α·d_{j+1}^(k-1)，α=(t-N_j)/(N_{j+p-k+1}-N_j)
                // 注意 α 乘的是上标更高的 d_{j+1}，(1-α) 乘 d_j —— 系数不可颠倒，否则曲线错乱交叉。
                dx[jj] = (1.0 - a) * dx[jj] + a * dx[jj + 1];
                dy[jj] = (1.0 - a) * dy[jj] + a * dy[jj + 1];
                dw[jj] = (1.0 - a) * dw[jj] + a * dw[jj + 1];
            }
        }
        double w = Math.Abs(dw[0]) < 1e-12 ? 1.0 : dw[0];
        x = dx[0] / w; y = dy[0] / w;
    }

    // 判断 hatch 图案是否为"实心填充"（SOLID）。
    private static bool IsSolidPattern(Hatch ht)
    {
        if (ht == null) return true;
        var gp = ht.GradientColor;
        if (gp != null && gp.Enabled) return false; // 渐变（450=1）
        if (ht.IsSolid) return true;
        var p = ht.Pattern;
        // fork HatchPattern 无 Fill 字段：按原始名 SOLID 判断（与 netDxf Name==SOLID 分支一致）
        return p == null || string.Equals(p.Name, "SOLID", StringComparison.OrdinalIgnoreCase);
    }

    // hatch 实心填充色：优先实体真色（420），否则按 CAD ACI 语义（独立 > 随块 > 随图层）
    private string HatchSolidFillHex(Hatch ht, short layerAci, short byBlockAci)
    {
        try
        {
            var c = ht.Color;
            if (c.IsTrueColor)
                return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }
        catch { }
        short aci = 7;
        try { aci = ht.Color.Index; } catch { }
        return ColorOf(aci, layerAci, byBlockAci);
    }

    // ============================================================================
    // 渐变类型共享模型（AutoCAD GFNAME 9 种）—— 三引擎（PNG 逐像素 / SVG 渐变定义 /
    // PDF shading）共享此分类、剖面函数与几何（轴向端点 / 径向焦点）。
    //   GFNAME: 1=LINEAR 2=CYLINDER 3=INVCYLINDER 4=SPHERICAL 5=HEMISPHERICAL
    //           6=CURVED 7=INVSPHERICAL 8=INVHEMISPHERICAL 9=INVCURVED
    // 关键属性（DXF 组码）：470=名称(类型) 460=角度(弧度) 461=Shift(居中 0/1)
    //   452=单色/双色 453/463/421=色标(位置+颜色)。
    // 剖面 profile(u)：u∈[0,1]，0=C1 端，1=C2 端。
    //   LINEAR 恒等；CYLINDER 轴向 S 形 0.5(1-cosπu)；CURVED 抛物 u²；
    //   SPHERICAL 径向 1-cos(πr/2)；HEMISPHERICAL 径向 √(2r-r²)。Inverted=1-profile。
    // 居中/Shift：径向类非居中→焦点移向角(沿-角度方向)、居中→焦点在中心；
    //   轴向非线性类非居中→高光偏移向角、居中→对称居中。LINEAR 不受 Shift 影响。
    // ============================================================================
    private enum HatchGradKind { Linear, Cylinder, Spherical, Hemispherical, Curved }

    // 渐变类型解析结果（非 readonly struct：字段供对象初始化器赋值）
    private struct HatchGradSpec
    {
        public HatchGradKind Kind;
        public bool Inverted;      // INVCYLINDER/INVSPHERICAL/INVHEMISPHERICAL/INVCURVED
        public bool Radial;        // 径向（SPHERICAL/INVSPHERICAL/HEMISPHERICAL/INVHEMISPHERICAL）
        public double Shift;       // 461（0=非居中, 1=居中）
        public bool Single;        // 452 单色渐变
        public ACadSharp.Color C1, C2; // 双端颜色（463=0 端 / 463=1 端）
    }

    private static HatchGradSpec MakeHatchGradSpec(HatchGradientPattern gp)
    {
        string n = (gp?.Name ?? "").Trim().ToUpperInvariant();
        bool inverted = n.StartsWith("INV", StringComparison.Ordinal);
        string baseName = inverted ? n.Substring(3) : n;
        HatchGradKind kind; bool radial;
        switch (baseName)
        {
            case "CYLINDER": kind = HatchGradKind.Cylinder; radial = false; break;
            case "HEMISPHERICAL": kind = HatchGradKind.Hemispherical; radial = true; break;
            case "CURVED": kind = HatchGradKind.Curved; radial = false; break;
            case "SPHERICAL": kind = HatchGradKind.Spherical; radial = true; break;
            default: kind = HatchGradKind.Linear; radial = false; break; // LINEAR / 空名 / 默认
        }
        var cols = gp?.Colors;
        var c1 = cols != null && cols.Count > 0 ? cols[0].Color : ACadSharp.Color.Default;
        var c2 = cols != null && cols.Count > 1 ? cols[cols.Count - 1].Color : ACadSharp.Color.Default;
        return new HatchGradSpec
        {
            Kind = kind, Inverted = inverted, Radial = radial,
            Shift = Math.Clamp(gp?.Shift ?? 0.0, 0.0, 1.0),
            Single = gp?.IsSingleColorGradient ?? false,
            C1 = c1, C2 = c2,
        };
    }

    // ============================================================================
    // 渐变剖面 LUT（数据驱动 ground-truth）——从 AutoCAD 渲染逐像素提取，33 点。
    //   f∈[0,1]：0=C1(红)端，1=C2(蓝)端。轴向=沿梯度轴归一化位置 t；径向=焦点归一化距离 r。
    //   9 类型×{shift0,shift1}；INV 类型 = 1 − base（渲染时取反，已验证均差<0.08）。
    //   关键：轴向类是"钟形"非单调（CYLINDER≈sin(πt)、CURVED 极缓钟形、LINEAR 线性）；
    //         径向类=峰值/谷值曲线（SPHERICAL 峰 1.0、HEMISPHERICAL 峰 0.70）。
    //   shift 直接映射 LUT 索引（s0 对称/s1 偏斜），偏斜已编码进 LUT，几何不再偏移轴。
    //   角度由几何旋转处理，LUT 只依赖 t/r（按 0° 提取）。
    // ============================================================================
    private static readonly double[] LUT_LINEAR_s0 =
        { 0.059, 0.073, 0.099, 0.128, 0.154, 0.183, 0.212, 0.242, 0.272, 0.299, 0.328, 0.357, 0.386, 0.414, 0.441, 0.469, 0.498,
          0.527, 0.556, 0.582, 0.612, 0.642, 0.672, 0.701, 0.727, 0.756, 0.784, 0.813, 0.842, 0.869, 0.899, 0.929, 0.945 };
    private static readonly double[] LUT_CYLINDER_s0 =
        { 0.176, 0.219, 0.297, 0.382, 0.462, 0.537, 0.603, 0.669, 0.733, 0.797, 0.852, 0.899, 0.934, 0.959, 0.978, 0.987, 0.988,
          0.982, 0.969, 0.946, 0.919, 0.876, 0.823, 0.761, 0.697, 0.633, 0.571, 0.501, 0.422, 0.335, 0.250, 0.165, 0.120 };
    private static readonly double[] LUT_CYLINDER_s1 =
        { 0.312, 0.380, 0.505, 0.640, 0.742, 0.833, 0.894, 0.944, 0.971, 0.985, 0.986, 0.981, 0.964, 0.950, 0.933, 0.916, 0.891,
          0.859, 0.824, 0.786, 0.752, 0.713, 0.674, 0.624, 0.571, 0.517, 0.466, 0.418, 0.361, 0.305, 0.244, 0.182, 0.149 };
    private static readonly double[] LUT_CURVED_s0 =
        { 0.608, 0.614, 0.625, 0.637, 0.646, 0.653, 0.658, 0.663, 0.669, 0.673, 0.678, 0.683, 0.688, 0.692, 0.697, 0.703, 0.705,
          0.704, 0.700, 0.695, 0.690, 0.684, 0.680, 0.676, 0.673, 0.669, 0.665, 0.659, 0.652, 0.642, 0.632, 0.622, 0.616 };
    private static readonly double[] LUT_CURVED_s1 =
        { 0.678, 0.682, 0.688, 0.696, 0.701, 0.703, 0.700, 0.696, 0.692, 0.688, 0.684, 0.680, 0.676, 0.674, 0.671, 0.667, 0.658,
          0.645, 0.629, 0.614, 0.598, 0.582, 0.567, 0.551, 0.534, 0.518, 0.501, 0.486, 0.465, 0.444, 0.419, 0.395, 0.382 };
    private static readonly double[] LUT_SPHERICAL_s0 =
        { 0.998, 0.994, 0.986, 0.976, 0.963, 0.950, 0.929, 0.906, 0.877, 0.841, 0.807, 0.765, 0.724, 0.669, 0.612, 0.558, 0.513,
          0.469, 0.418, 0.352, 0.283, 0.225, 0.170, 0.114, 0.061, 0.023, 0.008, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000 };
    private static readonly double[] LUT_SPHERICAL_s1 =
        { 0.992, 0.986, 0.972, 0.946, 0.914, 0.871, 0.816, 0.748, 0.673, 0.605, 0.530, 0.450, 0.363, 0.278, 0.190, 0.105, 0.040,
          0.008, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000 };
    private static readonly double[] LUT_HEMISPHERICAL_s0 =
        { 0.700, 0.690, 0.667, 0.642, 0.622, 0.600, 0.579, 0.554, 0.530, 0.509, 0.488, 0.468, 0.443, 0.422, 0.401, 0.379, 0.354,
          0.327, 0.301, 0.276, 0.250, 0.222, 0.195, 0.169, 0.141, 0.110, 0.081, 0.059, 0.040, 0.023, 0.010, 0.005, 0.005 };
    private static readonly double[] LUT_HEMISPHERICAL_s1 =
        { 0.322, 0.327, 0.339, 0.351, 0.361, 0.372, 0.382, 0.394, 0.405, 0.414, 0.424, 0.429, 0.428, 0.420, 0.410, 0.398, 0.388,
          0.378, 0.368, 0.358, 0.348, 0.337, 0.325, 0.306, 0.284, 0.257, 0.229, 0.198, 0.170, 0.142, 0.114, 0.082, 0.067 };

    // 按 (类型, shift) 取对应 LUT（INV 由调用方对返回值取反）
    private static double[] GradLut(HatchGradKind kind, int shift)
    {
        switch (kind)
        {
            case HatchGradKind.Linear: return LUT_LINEAR_s0;
            case HatchGradKind.Cylinder: return shift >= 1 ? LUT_CYLINDER_s1 : LUT_CYLINDER_s0;
            case HatchGradKind.Curved: return shift >= 1 ? LUT_CURVED_s1 : LUT_CURVED_s0;
            case HatchGradKind.Spherical: return shift >= 1 ? LUT_SPHERICAL_s1 : LUT_SPHERICAL_s0;
            case HatchGradKind.Hemispherical: return shift >= 1 ? LUT_HEMISPHERICAL_s1 : LUT_HEMISPHERICAL_s0;
            default: return LUT_LINEAR_s0;
        }
    }

    // 剖面函数 profile(u)：u∈[0,1]（0=C1 端，1=C2 端），返回插值系数 f。
    // LINEAR=恒等 t（线性渐变本质，已验证 MAE<2）；其余按 (类型,shift) 查 LUT 线性插值。
    // Inverted=1−f（INV 类型）。shift 决定 LUT 索引（s0 对称/s1 偏斜）。
    private static double HatchGradProfileLut(HatchGradSpec spec, double u)
    {
        if (u < 0) u = 0; else if (u > 1) u = 1;
        double f;
        if (spec.Kind == HatchGradKind.Linear)
        {
            f = u;
        }
        else
        {
            int shift = spec.Shift >= 0.5 ? 1 : 0;
            var lut = GradLut(spec.Kind, shift);
            double x = u * (lut.Length - 1);
            int i0 = (int)Math.Floor(x);
            int i1 = Math.Min(lut.Length - 1, i0 + 1);
            double fr = x - i0;
            f = lut[i0] * (1.0 - fr) + lut[i1] * fr;
        }
        if (spec.Inverted) f = 1.0 - f;
        if (f < 0) f = 0; else if (f > 1) f = 1;
        return f;
    }

    // 渐变几何（画布系）：轴向→端点(Px0,Py0)-(Px1,Py1)；径向→焦点(Fx,Fy)+半径R。
    // bbox 为画布坐标；ux,uy=画布系单位方向向量（Y 已按各引擎翻转/朝向）。
    private readonly struct HatchGradGeom
    {
        public readonly bool Radial;
        public readonly double Px0, Py0, Px1, Py1;
        public readonly double Fx, Fy, R;
        public HatchGradGeom(bool radial, double px0, double py0, double px1, double py1, double fx, double fy, double r)
        { Radial = radial; Px0 = px0; Py0 = py0; Px1 = px1; Py1 = py1; Fx = fx; Fy = fy; R = r; }
    }

    // 径向 shift=1 焦点（归一化图像坐标，(0,0)=左上）——从 CAD ground-truth 提取（SPHERICAL 可靠峰值）。
    private static readonly double RadialFocusS1X = 0.23, RadialFocusS1Y = 0.23;

    private static HatchGradGeom ComputeHatchGradGeom(HatchGradSpec spec, double minX, double minY, double maxX, double maxY, double ux, double uy, bool yDown)
    {
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        double W = maxX - minX, H = maxY - minY;
        double diag = Math.Sqrt(W * W + H * H);
        if (diag <= 1e-9) diag = 1;
        // 归一化画布系方向（PNG 的 x/y 缩放不同可能传入非单位向量；L 公式与投影均假设单位向量）
        double umag = Math.Sqrt(ux * ux + uy * uy);
        if (umag < 1e-12) { ux = 1; uy = 0; } else { ux /= umag; uy /= umag; }
        // 沿方向 u 的投影半长（轴向轴长一半）
        double L = 0.5 * (W * Math.Abs(ux) + H * Math.Abs(uy));
        if (L <= 1e-9) L = 0.25 * diag;
        int shift = spec.Shift >= 0.5 ? 1 : 0;
        if (spec.Radial)
        {
            // 径向焦点（图像归一化 (0,0)=左上）：s0 中心 / s1 左上。按 yDown 转画布坐标。
            double nfx = shift == 1 ? RadialFocusS1X : 0.5;
            double nfy = shift == 1 ? RadialFocusS1Y : 0.5;
            double fx = minX + nfx * W;
            double fy = yDown ? (minY + nfy * H) : (maxY - nfy * H); // PDF y 向上：图像顶=画布 maxY
            // R = 焦点到包围盒最远角点距离（远角处 t≈1，LUT 尾部到 0）
            double R = 0;
            foreach (double ax in new[] { minX, maxX })
                foreach (double ay in new[] { minY, maxY })
                {
                    double dd = Math.Sqrt((ax - fx) * (ax - fx) + (ay - fy) * (ay - fy));
                    if (dd > R) R = dd;
                }
            if (R <= 1e-9) R = L;
            return new HatchGradGeom(true, 0, 0, 0, 0, fx, fy, R);
        }
        else
        {
            // 轴向：轴恒为 bbox 沿 u 的全跨度（居中于 bbox 中心）。偏斜由 LUT 编码（s1 峰值偏左），
            // 几何不再偏移轴（旧实现的"非居中偏移 L"是错的，会与 LUT 偏斜叠加）。
            return new HatchGradGeom(false, cx - L * ux, cy - L * uy, cx + L * ux, cy + L * uy, 0, 0, 0);
        }
    }

    // 像素 (px,py) → 渐变参数 t∈[0,1]（0=C1 端，1=C2 端）。轴向=沿轴投影；径向=到焦点距离/半径。
    private static double HatchGradParam(HatchGradGeom g, double px, double py)
    {
        double t;
        if (g.Radial)
        {
            double dx = px - g.Fx, dy = py - g.Fy;
            t = g.R > 1e-9 ? Math.Sqrt(dx * dx + dy * dy) / g.R : 0;
        }
        else
        {
            double dx = g.Px1 - g.Px0, dy = g.Py1 - g.Py0;
            double len2 = dx * dx + dy * dy;
            t = len2 > 1e-9 ? ((px - g.Px0) * dx + (py - g.Py0) * dy) / len2 : 0;
        }
        if (t < 0) t = 0; else if (t > 1) t = 1;
        return t;
    }

    // 渐变色标 hex：真色直取，否则走 ACI 官方表（含 7 号白/黑随底色反色）
    private string StopHex(ACadSharp.Color c)
    {
        if (c.IsTrueColor)
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        return AciColor((int)c.Index);
    }

    // 子路径集合 → SVG path d（M/L/Z）
    private static string BuildPathDSvg(List<List<XY>> loops, Func<double, double> fx, Func<double, double> fy)
    {
        var d = new StringBuilder();
        foreach (var loop in loops)
        {
            if (loop.Count == 0) continue;
            for (int i = 0; i < loop.Count; i++)
                d.Append($"{(i == 0 ? "M" : "L")}{F(fx(loop[i].X))},{F(fy(loop[i].Y))}");
            d.Append("Z");
        }
        return d.ToString();
    }

    // SVG HATCH：实心=实体色/真色；渐变=linear/radialGradient；图案 hatch T4.5 实现
    private void EmitHatchSvg(StringBuilder sb, Hatch ht, Transform2D tr, double scaleMul, short layerAci, short byBlockAci)
    {
        var loops = BuildHatchLoops(ht, tr);
        if (loops.Count == 0) { RecordSkip("HATCH(空)"); return; }
        var gp = ht.GradientColor;
        bool gradient = gp != null && gp.Enabled;
        if (!gradient && !IsSolidPattern(ht))
        {
            EmitHatchPatternSvg(sb, ht, loops, tr, scaleMul, layerAci, byBlockAci);
            return;
        }
        string rule = loops.Count > 1 ? "evenodd" : "nonzero";
        string d = BuildPathDSvg(loops, x => Sx(x), y => Sy(y));
        if (gradient)
        {
            string id = "hg" + (_hatchGradSeq++);
            EmitHatchGradientDef(id, loops, gp);
            sb.AppendLine($"  <path d=\"{d}\" fill=\"url(#{id})\" fill-rule=\"{rule}\"/>");
        }
        else
        {
            sb.AppendLine($"  <path d=\"{d}\" fill=\"{HatchSolidFillHex(ht, layerAci, byBlockAci)}\" fill-rule=\"{rule}\"/>");
        }
    }

    // SVG 渐变定义（写入 _svgDefs；userSpaceOnUse 坐标 = 画布像素坐标）。
    // 接入共享 9 类型模型：轴向 linearGradient / 径向 radialGradient（焦点由居中决定）；
    // 非线性剖面（圆柱/球/抛物）用多 stop 采样逼近（u=0..1 步进 0.05）。
    private void EmitHatchGradientDef(string id, List<List<XY>> loops, HatchGradientPattern gp)
    {
        var spec = MakeHatchGradSpec(gp);
        // 画布系 bbox（SVG Y 翻转：世界 minY→画布大 Y）
        double cMinX = double.MaxValue, cMaxX = double.MinValue, cMinY = double.MaxValue, cMaxY = double.MinValue;
        foreach (var loop in loops)
            foreach (var p in loop)
            {
                double px = Sx(p.X), py = Sy(p.Y);
                if (px < cMinX) cMinX = px; if (px > cMaxX) cMaxX = px;
                if (py < cMinY) cMinY = py; if (py > cMaxY) cMaxY = py;
            }
        // 画布系单位方向（SVG Y 翻转：uy 取反，yDown=true）
        double ang = gp.Angle;
        double ux = Math.Cos(ang), uy = -Math.Sin(ang);
        var geom = ComputeHatchGradGeom(spec, cMinX, cMinY, cMaxX, cMaxY, ux, uy, true);

        string c1 = StopHex(spec.C1), c2 = StopHex(spec.C2);
        if (geom.Radial)
        {
            _svgDefs.AppendLine($"    <radialGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" cx=\"{F(geom.Fx)}\" cy=\"{F(geom.Fy)}\" r=\"{F(geom.R)}\" fx=\"{F(geom.Fx)}\" fy=\"{F(geom.Fy)}\">");
            AppendGradientProfileStops(spec, c1, c2);
            _svgDefs.AppendLine("    </radialGradient>");
        }
        else
        {
            _svgDefs.AppendLine($"    <linearGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" x1=\"{F(geom.Px0)}\" y1=\"{F(geom.Py0)}\" x2=\"{F(geom.Px1)}\" y2=\"{F(geom.Py1)}\">");
            AppendGradientProfileStops(spec, c1, c2);
            _svgDefs.AppendLine("    </linearGradient>");
        }
    }

    // 按 LUT 剖面生成 SVG <stop> 序列：u=0..1 步进 0.03（34 点，≥LUT 33 点保精度），
    // 颜色 = lerp(c1,c2,LutProfile(u))。LINEAR 恒等；非线性（圆柱/球/抛物钟形）由采样逼近。
    // 注意：**不要强制端点 i==0→c1 / i==N→c2**——径向类的 LUT 首/尾并不等于 C1/C2
    //   （如 SPHERICAL_s0 首值 0.998≈C2、尾值 0.000=C1），强制端点会在中心/边缘造成颜色突变的"点/环"
    //   （曾致 SVG 径向渐变中心出现深色小点聚集）。统一按 LUT 计算，与 PNG/PDF 逐像素结果一致。
    private void AppendGradientProfileStops(HatchGradSpec spec, string c1, string c2)
    {
        const int N = 33; // 与 LUT 点数一致
        for (int i = 0; i <= N; i++)
        {
            double u = i / (double)N;
            double f = HatchGradProfileLut(spec, u);
            string col = LerpHex(c1, c2, f);
            _svgDefs.AppendLine($"      <stop offset=\"{u.ToString("0.###", Ci)}\" stop-color=\"{col}\"/>");
        }
    }

    // hex 颜色按 t∈[0,1] 线性插值（RGB 分量）
    private static string LerpHex(string h1, string h2, double t)
    {
        if (t < 0) t = 0; else if (t > 1) t = 1;
        byte r1 = Convert.ToByte(h1.Substring(1, 2), 16), g1 = Convert.ToByte(h1.Substring(3, 2), 16), b1 = Convert.ToByte(h1.Substring(5, 2), 16);
        byte r2 = Convert.ToByte(h2.Substring(1, 2), 16), g2 = Convert.ToByte(h2.Substring(3, 2), 16), b2 = Convert.ToByte(h2.Substring(5, 2), 16);
        byte R = (byte)(r1 + (r2 - r1) * t);
        byte G = (byte)(g1 + (g2 - g1) * t);
        byte B = (byte)(b1 + (b2 - b1) * t);
        return "#" + R.ToString("X2") + G.ToString("X2") + B.ToString("X2");
    }

    // ===== 图案 HATCH（ANSI31/411B 等带显式线定义者）=====
    // 图案线在 hatch 局部系生成（线定义 → 块变换 tr），与边界（evenodd）求交裁剪后按描边输出；
    // 每 hatch 线数封顶（密度上限），防止 scale=0.0001 之类病态数据生成上万条线。
    // ⚠️ fork 差异：加载器已把图案旋转(52)/缩放(41)烘进 Pattern.Lines（Angle/BasePoint/Offset/DashLengths 为最终值），
    // 故此处不再额外乘图案旋转/缩放（netDxf 版为原始线定义+显式旋转/缩放，结果等价）。
    private const int MaxPatternLinesPerHatch = 2000;
    private static readonly Transform2D IdentityTr = new Transform2D { Ca = 1, Sa = 0, Sx = 1, Sy = 1, Tx = 0, Ty = 0 };

    private void EmitHatchPatternSvg(StringBuilder sb, Hatch ht, List<List<XY>> loops, Transform2D tr, double scaleMul, short layerAci, short byBlockAci)
    {
        var segs = BuildPatternSegments(loops, ht, tr, MaxPatternLinesPerHatch);
        if (segs.Count == 0) { RecordSkip("HATCH(图案)"); return; }
        string col = HatchSolidFillHex(ht, layerAci, byBlockAci);
        // 图案线细；线宽为屏幕空间常量，不乘块缩放 scaleMul（间距随 tr 缩放，2026-10-29 用户裁定）
        double w = 0.1;
        var d = new StringBuilder();
        foreach (var s in segs)
            d.Append("M").Append(F(Sx(s.X1))).Append(',').Append(F(Sy(s.Y1)))
             .Append("L").Append(F(Sx(s.X2))).Append(',').Append(F(Sy(s.Y2))).Append(' ');
        sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{col}\" stroke-width=\"{F(w)}\"/>");
    }

    // 生成图案线线段（世界坐标）。loops=边界闭合折线（世界系），ht=hatch，tr=块变换。
    // fork 版：Lines 已含图案旋转/缩放（烘焙）→ patAngle=0/patScale=1；
    // 每条图案线（无限长）与边界求交，按 evenodd 保留内部区间，dash 段按虚线拆分。
    private List<(double X1, double Y1, double X2, double Y2)> BuildPatternSegments(
        List<List<XY>> loops, Hatch ht, Transform2D tr, int maxLines)
    {
        var segs = new List<(double X1, double Y1, double X2, double Y2)>();
        var pat = ht?.Pattern;
        if (pat == null || pat.Lines == null || pat.Lines.Count == 0) return segs;
        if (loops == null || loops.Count == 0) return segs;
        var t = tr ?? IdentityTr; // 模型空间 hatch 无块变换（恒等）

        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
        foreach (var loop in loops)
            foreach (var p in loop)
            {
                if (p.X < minx) minx = p.X; if (p.Y < miny) miny = p.Y;
                if (p.X > maxx) maxx = p.X; if (p.Y > maxy) maxy = p.Y;
            }
        if (!(maxx > minx && maxy > miny)) return segs;

        var edges = new List<(double ax, double ay, double bx, double by)>();
        foreach (var loop in loops)
            for (int i = 0; i + 1 < loop.Count; i++)
                edges.Add((loop[i].X, loop[i].Y, loop[i + 1].X, loop[i + 1].Y));
        if (edges.Count == 0) return segs;

        // fork：图案旋转/缩放已烘焙进 Lines → 恒等（netDxf 版此处用 pat.Angle/pat.Scale）
        double patAngle = 0.0;
        double ca = Math.Cos(patAngle), sa = Math.Sin(patAngle);
        double patScale = 1.0;

        // 图案基点（DXF 10/20）：fork 存于 SeedPoints 首项
        double ox = 0, oy = 0;
        if (ht.SeedPoints != null && ht.SeedPoints.Count > 0) { ox = ht.SeedPoints[0].X; oy = ht.SeedPoints[0].Y; }

        int total = 0;
        foreach (var ld in pat.Lines)
        {
            if (ld == null) continue;
            double th = ld.Angle; // fork 弧度（已含图案旋转）
            double ux = Math.Cos(th), uy = Math.Sin(th); // 局部线方向（单位）
            // 局部 → 世界方向：先图案旋转、图案缩放，再块变换的线性部分
            double rux = ca * ux - sa * uy, ruy = sa * ux + ca * uy;
            var (dirX, dirY) = LinearTr(t, rux * patScale, ruy * patScale);
            double dirLen = Math.Sqrt(dirX * dirX + dirY * dirY);
            if (dirLen < 1e-12) continue;
            // 逐线偏移（delta）→ 世界
            double rdx = ca * ld.Offset.X - sa * ld.Offset.Y, rdy = sa * ld.Offset.X + ca * ld.Offset.Y;
            var (offX, offY) = LinearTr(t, rdx * patScale, rdy * patScale);
            double offLen = Math.Sqrt(offX * offX + offY * offY);
            if (offLen < 1e-12) continue;
            // 世界锚点（k=0）：hatch 基点 + 图案旋转缩放后的线原点
            double lox = ca * ld.BasePoint.X - sa * ld.BasePoint.Y, loy = sa * ld.BasePoint.X + ca * ld.BasePoint.Y;
            var (anchorX, anchorY) = t.Apply(ox + lox * patScale, oy + loy * patScale);

            // 世界线法向 & 垂直间距
            double nX = -dirY / dirLen, nY = dirX / dirLen;
            double perp = offX * nX + offY * nY;
            if (Math.Abs(perp) < 1e-12) continue; // 退化（delta 平行于线）

            // bbox 在法向的投影范围 → k 范围
            double nc1 = minx * nX + miny * nY, nc2 = maxx * nX + miny * nY;
            double nc3 = minx * nX + maxy * nY, nc4 = maxx * nX + maxy * nY;
            double nMin = Math.Min(Math.Min(nc1, nc2), Math.Min(nc3, nc4));
            double nMax = Math.Max(Math.Max(nc1, nc2), Math.Max(nc3, nc4));
            double baseN = anchorX * nX + anchorY * nY;
            int kMin = (int)Math.Floor((nMin - baseN) / perp) - 1;
            int kMax = (int)Math.Ceiling((nMax - baseN) / perp) + 1;
            if (kMax < kMin) { int tmp = kMin; kMin = kMax; kMax = tmp; }
            int count = kMax - kMin + 1;
            if (count <= 0) continue;
            // 密度上限：超限时按步长抽稀
            int stride = 1;
            if (total + count > maxLines)
            {
                int remain = Math.Max(1, maxLines - total);
                stride = (int)Math.Ceiling(count / (double)remain);
                if (stride < 1) stride = 1;
            }
            for (int k = kMin; k <= kMax; k += stride)
            {
                double ax = anchorX + k * offX, ay = anchorY + k * offY;
                ClipLineToPoly(ax, ay, dirX, dirY, edges, out var ts);
                for (int i = 0; i + 1 < ts.Count; i++)
                {
                    double tm = (ts[i] + ts[i + 1]) * 0.5;
                    if (InsideEvenOdd(ax + tm * dirX, ay + tm * dirY, edges))
                    {
                        double x1 = ax + ts[i] * dirX, y1 = ay + ts[i] * dirY;
                        double x2 = ax + ts[i + 1] * dirX, y2 = ay + ts[i + 1] * dirY;
                        AppendPatternSegs(segs, x1, y1, x2, y2, ld.DashLengths, dirLen);
                    }
                }
                total++;
                if (total >= maxLines) break;
            }
            if (total >= maxLines) break;
        }
        return segs;
    }

    // tr 的线性部分作用于向量（不含平移）
    private static (double X, double Y) LinearTr(Transform2D tr, double vx, double vy)
    {
        double rx = vx * tr.Sx, ry = vy * tr.Sy;
        return (rx * tr.Ca - ry * tr.Sa, rx * tr.Sa + ry * tr.Ca);
    }

    // 直线 A+t·dir 与折线各边的交点参数 t（世界系），排序去重
    private static void ClipLineToPoly(double ax, double ay, double dx, double dy,
        List<(double ax, double ay, double bx, double by)> edges, out List<double> ts)
    {
        ts = new List<double>();
        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            double ex = e.bx - e.ax, ey = e.by - e.ay;
            double den = dx * ey - dy * ex; // cross(dir, e)
            if (Math.Abs(den) < 1e-15) continue; // 平行
            double px = e.ax - ax, py = e.ay - ay;
            double t = (px * ey - py * ex) / den; // cross(P1-A, e)
            double s = (px * dy - py * dx) / den; // cross(P1-A, dir)
            if (s >= 0 && s <= 1) ts.Add(t);
        }
        ts.Sort();
        double eps = 1e-9 * Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
        var dedup = new List<double>(ts.Count);
        foreach (var t in ts)
            if (dedup.Count == 0 || Math.Abs(t - dedup[dedup.Count - 1]) > eps)
                dedup.Add(t);
        ts = dedup;
    }

    // evenodd（射线法）内部判定：水平射线与所有边交点数为奇 → 内部
    private static bool InsideEvenOdd(double x, double y, List<(double ax, double ay, double bx, double by)> edges)
    {
        bool inside = false;
        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            if ((e.ay > y) != (e.by > y))
            {
                double xint = e.ax + (y - e.ay) * (e.bx - e.ax) / (e.by - e.ay);
                if (x > xint) inside = !inside;
            }
        }
        return inside;
    }

    // 追加图案线段；有 dash 则按虚线拆分（dash 局部单位，dirLen=局部→世界比例）
    private static void AppendPatternSegs(List<(double X1, double Y1, double X2, double Y2)> segs,
        double x1, double y1, double x2, double y2, List<double> dash, double dirLen)
    {
        if (dash == null || dash.Count == 0 || dirLen < 1e-12)
        {
            segs.Add((x1, y1, x2, y2));
            return;
        }
        double dx = x2 - x1, dy = y2 - y1;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return;
        double ux = dx / len, uy = dy / len;
        double period = 0;
        for (int i = 0; i < dash.Count; i++) period += Math.Abs(dash[i]);
        if (period < 1e-12) { segs.Add((x1, y1, x2, y2)); return; }
        double s = 0.0; int di = 0;
        while (s < len - 1e-12)
        {
            int idx = di % dash.Count;
            double dl = Math.Abs(dash[idx]) * dirLen;
            double end = Math.Min(s + dl, len);
            if (dash[idx] > 0) // 正=落笔
                segs.Add((x1 + s * ux, y1 + s * uy, x1 + end * ux, y1 + end * uy));
            s = end; di++;
        }
    }

    // SVG 样条（描边折线逼近，宽度/颜色/虚线随实体）
    private void EmitSplineSvg(StringBuilder sb, Spline sp, Transform2D tr, double scaleMul, string col, double[] dash)
    {
        var pts = SplineSample(sp, tr, 16);
        if (pts.Count < 2) { RecordSkip("SPLINE(空)"); return; }
        var d = new StringBuilder();
        for (int i = 0; i < pts.Count; i++)
            d.Append($"{(i == 0 ? "M" : "L")}{F(Sx(pts[i].X))},{F(Sy(pts[i].Y))}");
        string da = DashAttr(dash);
        sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{col}\" stroke-width=\"{F(StrokeWidth(sp, scaleMul))}\" stroke-linejoin=\"round\"{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
    }

    #endregion

    #region ===== 线宽 / 工具 =====

    private static int LineweightValue(LineWeightType lw)
    {
        int v = (int)lw;
        return v; // fork：Default=-3 / ByBlock=-2 / ByLayer=-1 / 0..200
    }

    // 读取实体的显式线宽（毫米）
    private static double LwMm(Entity e)
    {
        try
        {
            int v = LineweightValue(e.LineWeight);
            if (v >= 0) return v / 100.0;
            if (v == (int)LineWeightType.ByLayer) // ByLayer → 取所在图层线宽
            {
                int lv = LineweightValue(e.Layer?.LineWeight ?? LineWeightType.Default);
                if (lv >= 0) return lv / 100.0;
            }
        }
        catch { }
        return 0;
    }

    // 线宽 → 描边宽度（数据单位，1:1 实际尺寸）
    private const double BaseStroke = 0.1;
    private const double DefaultLwMm = 0.13;
    private double StrokeWidth(Entity e, double scaleMul)
    {
        double lw = LwMm(e);
        // 全局线宽缩放：仅作用于"显示线宽"(组码370)的描边路径。
        // $LWDISPLAY 开→乘 $LWEIGHTSCALE(全局缩放)；关→按实际(未缩放)线宽渲染。
        // 多段线几何宽(全局宽度/40/43)走 EmitTaperedPolyline 填充多边形、不经过本方法，故不受全局缩放。
        if (lw > 0 && _lwDisplay && _lwScale > 0)
            lw *= _lwScale;
        double w = lw > 0 ? BaseStroke * lw / DefaultLwMm : BaseStroke;
        // 显示线宽为屏幕空间常量：【不】乘块插入缩放 scaleMul。
        // 2026-10-29 用户裁定：渲染时只缩放渲染尺寸（几何），不缩放绘制线条的像素——
        // 块缩放 27× 且内线宽为 0 时，旧逻辑把 1px 兜底线宽放大 27× 成 27px，符号糊成一团。
        // AutoCAD 语义：显示线宽（组码 370 及其 0 兜底）是屏幕属性，不受缩放级别/块插入比例影响；
        // 点实体中心点同样恒 1px（见 EmitPointPng 注释）。几何（字高、折线几何宽度 40/43、
        // 箭头、图案间距等）仍随块缩放——那些走各自的 tr/scaleMul 路径，不经过此处。
        return Math.Max(0.1, w);
    }

    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static string F(double v) => (double.IsFinite(v) ? v : 0).ToString("0.######", Ci);

    private static string EscapeXml(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");
    #endregion
}

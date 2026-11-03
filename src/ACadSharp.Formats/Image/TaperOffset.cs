using System;
using System.Collections.Generic;

namespace ACadSharp.Formats.Image;

/// <summary>
/// 变宽折线（逐顶点宽度 + bulge 弧段）偏移轮廓的预处理几何核心（本包默认实现）。
/// 算法自 GDB2CAD.Core.TaperOffset 逐字节移植（2026-11-03），收敛到本包单一实现：
/// <see cref="ImageConfiguration.TaperOffsetBuilder"/> 默认即 <c>new TaperOffset()</c>，
/// 宿主无需注入即可获得含 bulge 的锥形/云形折线真实弧偏移渲染；
/// 显式赋 <c>null</c> 可回退逐顶点弦偏移（R10 前行为）。
/// 原 R13「算法归宿主、本包只留渲染契约」的拆分让各宿主各自维护算法副本
/// （cadconvert 曾另行移植一份），2026-11-03 起统一到本包。
/// 核心只接收裸数组（与具体实体类型无关）、输出未变换坐标；坐标变换（块插入 tr）
/// 由调用方在结果上逐点应用。
/// </summary>
public sealed class TaperOffset : ACadSharp.Formats.Image.ITaperOffsetBuilder
{
    /// <summary>
    /// 含 bulge（|bulge|&gt;1e-9 的边）时构建弧采样偏移轮廓；无 bulge 返回 null
    /// → 调用方走原逐顶点弦偏移路径（输出保持不变，像素 parity 不受影响）。
    /// <para>弧段按每 2° 采样，沿真实弧法向（径向）偏移；典型收益：GC200 点块
    /// （2 顶点 bulge=1、常数宽 0.2、闭合，r=hw=0.1）从 0.2×0.2 弦方块变为外半径
    /// r+hw 的正确填充圆盘（内环 r−hw≤0 时退化为圆心点）。</para>
    /// <para>角顶点取入/出边切向的平分线法向（直线边=弦向，弧边=端点切向），
    /// 段 i 内宽度从起点宽 sw[i](组码40_i) 线性插值到终点宽 ew[i](组码41_i)；
    /// 段间在共享顶点宽度不连续（ew[i-1]≠sw[i]）时偏移轮廓收尖（云形 scallop 凹谷）。
    /// 弧分解约定与 ArcFromBulge 完全一致
    /// （θ=4·atan(b)，圆心在弦中垂线 h=r·|cos(θ/2)| 处、侧由 bulge 符号定）。</para>
    /// </summary>
    /// <param name="n">顶点数（≥2）。</param>
    /// <param name="closed">是否闭合（闭合时边数 = n，开放 = n−1）。</param>
    /// <param name="xs">n 个顶点 X（实体局部坐标）。</param>
    /// <param name="ys">n 个顶点 Y。</param>
    /// <param name="bulges">顶点 i 处出边 bulge（n 个；开放折线末顶点忽略）。</param>
    /// <param name="sw">段 i（顶点 i→i+1）起点全宽（数据单位，CAD 组码 40_i；ec 个）。</param>
    /// <param name="ew">段 i 终点全宽（CAD 组码 41_i；ec 个）。</param>
    /// <returns>(左偏移环, 右偏移环)——行进方向左侧/右侧，局部坐标、未变换；无 bulge 时 null。</returns>
    public static (List<(double X, double Y)> Left, List<(double X, double Y)> Right)? TryBuild(
        int n, bool closed, double[] xs, double[] ys, double[] bulges, double[] sw, double[] ew)
    {
        int ec = closed ? n : n - 1;

        bool anyBulge = false;
        for (int i = 0; i < ec; i++)
            if (Math.Abs(bulges[i]) > 1e-9) { anyBulge = true; break; }
        if (!anyBulge) return null;

        // 逐边分解：直线 = 单位弦向；弧 = (圆心, 半径, 起点角, 带符号扫角)
        var isArc = new bool[ec];
        var cX = new double[ec]; var cY = new double[ec]; var rad = new double[ec];
        var a1 = new double[ec]; var sweep = new double[ec];
        var dX = new double[ec]; var dY = new double[ec];
        for (int i = 0; i < ec; i++)
        {
            int j = closed ? (i + 1) % n : i + 1;
            double x1 = xs[i], y1 = ys[i];
            double x2 = xs[j], y2 = ys[j];
            double dx = x2 - x1, dy = y2 - y1;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            double b = bulges[i];
            if (chord < 1e-12 || Math.Abs(b) < 1e-9)
            {
                double L = chord > 1e-12 ? chord : 1.0;
                dX[i] = dx / L; dY[i] = dy / L;
                continue;
            }
            double theta = 4.0 * Math.Atan(b);                       // 带符号 sweep
            double thetaAbs = 4.0 * Math.Atan(Math.Abs(b));
            double r = chord / (2.0 * Math.Sin(thetaAbs / 2.0));
            if (!double.IsFinite(r) || r <= 1e-12)
            {
                dX[i] = dx / (chord > 1e-12 ? chord : 1.0); dY[i] = dy / (chord > 1e-12 ? chord : 1.0);
                continue;
            }
            // 圆心与 FullImageEngine.ArcFromBulge / ACadSharp.Arc.GetCenter **一致**：从 p1 起
            // 沿 p1→p2 方向偏转 sign(b)·gamma 定位。旧"弦中垂线"版本取错弦侧 → 弧采样端点
            // 偏离分段第二点、偏移轮廓弧段间出现直线弦（用户反馈"凸度弧度多渲染了线段"在变宽
            // 粗带上的表现）。修复后 a1+sweep 精确命中顶点，偏移环沿真实弧走向、无直线弦。
            double gamma = (Math.PI - thetaAbs) / 2.0;
            double phi = Math.Atan2(dy, dx) + Math.Sign(b) * gamma;
            cX[i] = x1 + r * Math.Cos(phi);
            cY[i] = y1 + r * Math.Sin(phi);
            isArc[i] = true;
            rad[i] = r;
            a1[i] = Math.Atan2(y1 - cY[i], x1 - cX[i]);
            sweep[i] = theta;
        }

        // 各边起/终点单位切向（弧端点取弧切向，直线取弦向）——角顶点平分线法向用
        var sTx = new double[ec]; var sTy = new double[ec];
        var eTx = new double[ec]; var eTy = new double[ec];
        for (int i = 0; i < ec; i++)
        {
            if (!isArc[i]) { sTx[i] = dX[i]; sTy[i] = dY[i]; eTx[i] = dX[i]; eTy[i] = dY[i]; continue; }
            double sgn = sweep[i] >= 0 ? 1.0 : -1.0;
            double a2 = a1[i] + sweep[i];
            sTx[i] = -Math.Sin(a1[i]) * sgn; sTy[i] = Math.Cos(a1[i]) * sgn;
            eTx[i] = -Math.Sin(a2) * sgn; eTy[i] = Math.Cos(a2) * sgn;
        }

        var left = new List<(double X, double Y)>(n * 4);
        var right = new List<(double X, double Y)>(n * 4);

        void AddSample(double px, double py, double tx, double ty, double hw)
        {
            double nx = -ty, ny = tx; // 行进方向左侧法向（与旧实现约定一致）
            left.Add((px + nx * hw, py + ny * hw));
            right.Add((px - nx * hw, py - ny * hw));
        }

        for (int i = 0; i < ec; i++)
        {
            // 角顶点 i：入/出边切向的平分线法向（开放折线首顶点无入边 → 取出边切向）
            int pi = i == 0 ? (closed ? ec - 1 : -1) : i - 1;
            double tix, tiy;
            if (pi >= 0) { tix = eTx[pi]; tiy = eTy[pi]; }
            else { tix = sTx[i]; tiy = sTy[i]; }
            double btx = tix + sTx[i], bty = tiy + sTy[i];
            double bl = Math.Sqrt(btx * btx + bty * bty);
            if (bl < 1e-9) { btx = sTx[i]; bty = sTy[i]; }
            else { btx /= bl; bty /= bl; }
            // 角顶点 i 宽 = 本顶点出段起点宽（组码 40_i）。段间在共享顶点宽度不连续时
            // （入段终宽 ew[i-1] ≠ 出段起宽 sw[i]），偏移轮廓在此处收尖——云形 scallop 凹谷。
            AddSample(xs[i], ys[i], btx, bty, sw[i] / 2.0);

            // 弧段采样（不含端点：起点=当前角顶点、终点=下一角顶点，由角顶点样本覆盖）
            if (isArc[i])
            {
                double sgn = sweep[i] >= 0 ? 1.0 : -1.0;
                int seg = Math.Max(8, (int)Math.Ceiling(Math.Abs(sweep[i]) / (Math.PI / 90.0))); // 每 2° 一段
                for (int k = 1; k < seg; k++)
                {
                    double t = (double)k / seg;
                    double a = a1[i] + sweep[i] * t;
                    double px = cX[i] + rad[i] * Math.Cos(a);
                    double py = cY[i] + rad[i] * Math.Sin(a);
                    // 段 i 内从起点宽 sw[i] 线性渐变到终点宽 ew[i]（组码 40_i→41_i）
                    AddSample(px, py, -Math.Sin(a) * sgn, Math.Cos(a) * sgn, (sw[i] + (ew[i] - sw[i]) * t) / 2.0);
                }
            }
        }

        // 开放折线补末顶点（无出边 → 法向取末边切向；宽度取末段终点宽 41_{n-2}）
        if (!closed)
            AddSample(xs[n - 1], ys[n - 1], eTx[ec - 1], eTy[ec - 1], ew[ec - 1] / 2.0);

        return (left, right);
    }

    /// <summary>
    /// 两偏移环之一退化为单点（宽度 ≥ 弧半径，如 GC200 点块：hw=r 内环=圆心）→
    /// 返回实心圆盘 (圆心, 半径)（取非退化环的质心与最大距离）；否则 null。
    /// PDF 圆盘特例据此发「三次贝塞尔整圆 + 非零填充」——evenodd + 退化子路径组合
    /// 在部分合规查看器中整条被忽略（圆盘不显示，R11 用户反馈根因）。
    /// </summary>
    public static (double Cx, double Cy, double R)? TryCircleDisc(List<(double X, double Y)> a, List<(double X, double Y)> b)
    {
        double? sa = RingSpread(a, out double ax, out double ay);
        double? sb = RingSpread(b, out double bx, out double by);
        const double Deg = 1e-5; // 画布单位（=世界单位×1），远小于任何可见尺寸
        if (sa != null && sb != null && sa < Deg && sb > Deg)
            return (bx, by, sb.Value);
        if (sa != null && sb != null && sb < Deg && sa > Deg)
            return (ax, ay, sa.Value);
        return null;
    }

    // 环质心 (cx,cy) 与质心最大距离（≈半径）；空环 → null。
    private static double? RingSpread(List<(double X, double Y)> pts, out double cx, out double cy)
    {
        cx = 0; cy = 0;
        if (pts == null || pts.Count == 0) return null;
        double sx = 0, sy = 0;
        for (int i = 0; i < pts.Count; i++) { sx += pts[i].X; sy += pts[i].Y; }
        cx = sx / pts.Count; cy = sy / pts.Count;
        double maxd = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            double dx = pts[i].X - cx, dy = pts[i].Y - cy;
            double v = dx * dx + dy * dy;
            if (v > maxd) maxd = v;
        }
        return Math.Sqrt(maxd);
    }

    // ===== ON 路由渲染器注入（ACadSharp.Formats.Image.ITaperOffsetBuilder 实现） =====

    (List<(double X, double Y)> Left, List<(double X, double Y)> Right)?
        ACadSharp.Formats.Image.ITaperOffsetBuilder.TryBuild(int n, bool closed, double[] xs, double[] ys, double[] bulges, double[] sw, double[] ew)
            => TaperOffset.TryBuild(n, closed, xs, ys, bulges, sw, ew);

    (double Cx, double Cy, double R)? ACadSharp.Formats.Image.ITaperOffsetBuilder.TryCircleDisc(List<(double X, double Y)> a, List<(double X, double Y)> b)
        => TaperOffset.TryCircleDisc(a, b);
}

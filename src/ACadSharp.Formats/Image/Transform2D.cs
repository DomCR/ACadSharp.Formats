using System;
using ACadSharp.Entities;

namespace ACadSharp.Formats.Image;

/// <summary>2D 仿射变换（旋转+缩放+平移），与 DxfImageExporter.Transform2D 一致。</summary>
internal class Transform2D
{
    public double Ca, Sa;     // 旋转
    public double Sx, Sy;     // 缩放
    public double Tx, Ty;     // 平移
    public double Scale => Math.Abs(Sx);
    public double Rotation => Math.Atan2(Sa, Ca) * 180.0 / Math.PI;

    public (double X, double Y) Apply(double x, double y)
    {
        double rx = x * Sx, ry = y * Sy;
        return (Tx + rx * Ca - ry * Sa, Ty + rx * Sa + ry * Ca);
    }

    public static Transform2D Compose(Insert ins, Transform2D parent)
    {
        double rot = ins.Rotation; // fork 弧度（参考实现此处度→弧度）
        double ca = Math.Cos(rot), sa = Math.Sin(rot);
        double sx = ins.XScale, sy = ins.YScale;
        double px = ins.InsertPoint.X, py = ins.InsertPoint.Y;
        // 局部变换：先旋转+缩放，再平移
        var local = new Transform2D { Ca = ca, Sa = sa, Sx = sx, Sy = sy, Tx = px, Ty = py };
        if (parent == null) return local;
        // 组合：world = parent(Apply(local(x)))
        return new Transform2D
        {
            Ca = parent.Ca * local.Ca - parent.Sa * local.Sa,
            Sa = parent.Sa * local.Ca + parent.Ca * local.Sa,
            Sx = parent.Sx * local.Sx,
            Sy = parent.Sy * local.Sy,
            Tx = parent.Tx + (local.Tx * parent.Sx) * parent.Ca - (local.Ty * parent.Sy) * parent.Sa,
            Ty = parent.Ty + (local.Tx * parent.Sx) * parent.Sa + (local.Ty * parent.Sy) * parent.Ca,
        };
    }
}

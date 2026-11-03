using System;
using ACadSharp;
using ACadSharp.Formats.Image;

namespace ACadSharp.Formats.Svg;

/// <summary>
/// GDB2CAD 专属 SVG 导出器（T4.2-T4.6）：
/// 1:1 内容画布 + 2% 边距 + OutputZoom 放大（默认 10）+ 黑/白背景 + ACI 官方色表
/// （ACI 7 黑底显白/白底显黑）+ $LTSCALE×实体48×嵌套块比例 的虚线/复杂线型（含 SHX 真实字形，
/// 经 SharpAstro.Fonts ShxFont 解析）+ NURBS 样条 de Boor 采样 + MText 格式码解析/换行 +
/// 渐变 HATCH（&lt;linearGradient&gt;/&lt;radialGradient&gt;）+ ByBlock/ByLayer 继承 + 嵌套块展开。
/// 渲染语义与 GDB2CAD.Core.DxfImageExporter 一致（像素级对齐目标，见 T4.8）。
/// R15：自 Gdb\ 目录迁入 Svg 格式目录并更名（避开上游 Svg.SvgExporter）；渲染引擎在 Image\FullImageEngine.Svg.cs（partial ImageEngine，命名空间 ACadSharp.Formats.Image）。
/// </summary>
public static class FullSvgExporter
{
    /// <summary>导出 ACadSharp 文档的模型空间为 SVG。</summary>
    /// <returns>成功=含画布像素与数据包围盒（供生成坐标 JSON）；失败=Success=false。</returns>
    public static ImageExportResult Export(CadDocument doc, string outPath, ImageConfiguration options = null, string sourceFilePath = null)
    {
        if (doc == null || string.IsNullOrEmpty(outPath))
            return new ImageExportResult { Success = false };
        options ??= new ImageConfiguration();
        try
        {
            var engine = new ImageEngine(doc, options);
            engine.SourceFilePath = sourceFilePath;
            return engine.WriteSvg(outPath);
        }
        catch (Exception ex)
        {
            options.Notify("❌  SVG 导出异常：" + ex.Message);
            return new ImageExportResult { Success = false };
        }
    }
}

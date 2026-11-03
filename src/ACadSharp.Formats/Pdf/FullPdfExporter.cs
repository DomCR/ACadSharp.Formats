using System;
using ACadSharp;
using ACadSharp.Formats.Image;

namespace ACadSharp.Formats.Pdf;

/// <summary>
/// GDB2CAD 专属 PDF 导出器（T4.4）：
/// 与 FullSvgExporter 相同的画布/颜色/线型/渐变语义，PDF 原生 y 向上坐标系（不翻转），
/// 真实 xref 偏移表 + startxref；中文文字内嵌 CJK TrueType（Type0 + Identity-H + ToUnicode，
/// 2 字节 GID，字体度量修补）；弧线三次贝塞尔逼近；d（虚线）状态持久且实线显式复位。
/// 渲染语义与 GDB2CAD.Core.DxfImageExporter 一致（像素级对齐目标，见 T4.8）。
/// R15：自 Gdb\ 目录迁入 Pdf 格式目录并更名（避开上游 Pdf.PdfExporter）；渲染引擎在 Image\FullImageEngine.Pdf.cs（partial ImageEngine，命名空间 ACadSharp.Formats.Image）。
/// </summary>
public static class FullPdfExporter
{
    /// <summary>导出 ACadSharp 文档的模型空间为 PDF。</summary>
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
            return engine.WritePdf(outPath);
        }
        catch (Exception ex)
        {
            options.Notify("❌  PDF 导出异常：" + ex.Message);
            return new ImageExportResult { Success = false };
        }
    }
}

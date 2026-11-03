using System;
using ACadSharp;
using ACadSharp.Formats.Image;

namespace ACadSharp.Formats.Image;

/// <summary>
/// GDB2CAD 专属 PNG 导出器（T4.9）：模型空间 → 纯托管 RasterCanvas 位图 → PNG 文件。
/// 渲染语义与 FullSvgExporter/FullPdfExporter 共用同一 ImageEngine（画布/颜色/线型/SHX 字形/
/// HATCH/文字/块展开一致）；位图特有像素上限见 ImageEngine.WritePng。
/// </summary>
public static class FullPngExporter
{
    /// <summary>导出 ACadSharp 文档的模型空间为 PNG。</summary>
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
            return engine.WritePng(outPath);
        }
        catch (Exception ex)
        {
            // ex.ToString() 含完整堆栈（ex.Message 仅首行，无法定位 NRE 抛出点）。
            options.Notify("❌  PNG 导出异常：" + ex.Message);
            options.Notify("    堆栈：" + ex.ToString());
            return new ImageExportResult { Success = false };
        }
    }
}

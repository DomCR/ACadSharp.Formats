namespace ACadSharp.Formats.Image;

/// <summary>
/// PNG 分块（瓦片）描述：整幅 1:1×OutputZoom 画布超出位图像素上限且未开启合并时，
/// 按网格切分为若干瓦片文件，每个瓦片对应一张 PNG。
/// 坐标系：像素、原点左上、+x 向右 +y 向下（与位图一致）。
/// 瓦片是整幅画布的一个矩形子区域（px0,py0 为子区域左上角在整幅画布中的像素偏移，
/// tilePxW/tilePxH 为子区域像素尺寸），渲染时把整幅场景画进该子区域画布（越界像素自动裁剪）。
/// 故每张瓦片的「0 点」（子区域左上角）与「对角点」（子区域右下角）对应的真实数据坐标可由
/// 整幅画布包围盒（ImageExportResult.DataX0/X1/Y0/Y1）线性反算。
///
/// OGC WMTS 命名：瓦片按标准 WMTS 斜切图（slippy/quadkey 风格）三层地址 {z}/{row}/{col}.png 存放，
/// z=缩放级别（= 渲染放大倍数 OutputZoom 四舍五入，2026-10-21 用户：原固定 0 无信息量，改为实际缩放级别）、
/// row=自顶向下的行号、col=自左向右的列号。
/// 文件路径 = {输出目录}/tiles/{z}/{row}/{col}.png（见 <see cref="RelPath"/>）。
/// </summary>
public sealed class ImageTile
{
    /// <summary>瓦片 PNG 文件名（不含目录，如 "0_0.png"）。</summary>
    public string FileName { get; init; } = "";

    /// <summary>瓦片 PNG 绝对路径。</summary>
    public string FilePath { get; init; } = "";

    /// <summary>瓦片像素宽。</summary>
    public int TilePxW { get; init; }

    /// <summary>瓦片像素高。</summary>
    public int TilePxH { get; init; }

    /// <summary>瓦片左上角在整幅画布中的像素 X 偏移。</summary>
    public int Px0 { get; init; }

    /// <summary>瓦片左上角在整幅画布中的像素 Y 偏移。</summary>
    public int Py0 { get; init; }

    /// <summary>OGC WMTS 缩放级别 z（= 渲染放大倍数 OutputZoom 四舍五入）。</summary>
    public int Z { get; init; }

    /// <summary>OGC WMTS 列号 col（自左向右，0 起）。</summary>
    public int X { get; init; }

    /// <summary>OGC WMTS 行号 row（自顶向下，0 起）。</summary>
    public int Y { get; init; }

    /// <summary>
    /// OGC WMTS 标准相对路径（正斜杠）：tiles/{z}/{row}/{col}.png。
    /// 供坐标 JSON 与下游按 WMTS 协议取瓦片使用。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string RelPath =>
        "tiles/" + Z + "/" + Y + "/" + X + ".png";
}

namespace ACadSharp.Formats.Pdf.Extensions;

internal static class ColorExtensions
{
	public static string ToPdfString(this Color color)
	{
		return $"{color.R / 255d} {color.G / 255d} {color.B / 255d} RG";
	}
}
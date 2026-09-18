using ACadSharp.Formats.Pdf.Core;
using ACadSharp.Objects;

namespace ACadSharp.Formats.Pdf.Extensions;

internal static class DoubleExtensions
{
	public static double ToPdfUnit(this double value, PlotPaperUnits unit)
	{
		switch (unit)
		{
			case PlotPaperUnits.Inches:
				return PdfUnitType.Inch.Transform(value);
			case PlotPaperUnits.Millimeters:
				return PdfUnitType.Millimeter.Transform(value);
			case PlotPaperUnits.Pixels:
			default:
				return PdfUnitType.Point.Transform(value);
		}
	}

	public static double ToPdfUnit(this double value, PdfUnitType unit)
	{
		return unit.Transform(value);
	}
}

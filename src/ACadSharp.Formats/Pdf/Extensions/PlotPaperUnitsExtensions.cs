using ACadSharp.Formats.Pdf.Core;
using ACadSharp.Objects;
using System.ComponentModel;

namespace ACadSharp.Formats.Pdf.Extensions;

internal static class PlotPaperUnitsExtensions
{
	public static PdfUnitType ToPdfUnit(this PlotPaperUnits unit)
	{
		switch (unit)
		{
			case PlotPaperUnits.Inches:
				return PdfUnitType.Inch;
			case PlotPaperUnits.Millimeters:
				return PdfUnitType.Millimeter;
			case PlotPaperUnits.Pixels:
				return PdfUnitType.Point;
			default:
				throw new InvalidEnumArgumentException(nameof(unit), (int)unit, typeof(PdfUnitType));
		}
	}
}

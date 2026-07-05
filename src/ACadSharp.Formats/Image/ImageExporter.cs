using ACadSharp.Objects;
using ACadSharp.Tables;
using System;
using System.IO;

namespace ACadSharp.Formats.Image;

public class ImageExporter : ExporterBase
{
	public ImageExporter(Stream stream) : base(stream)
	{
	}

	public override void Export(Layout layout)
	{
		throw new NotImplementedException();
	}

	public override void Export(BlockRecord record)
	{
		throw new NotImplementedException();
	}

	public override void Export(CadDocument document)
	{
		throw new NotImplementedException();
	}
}
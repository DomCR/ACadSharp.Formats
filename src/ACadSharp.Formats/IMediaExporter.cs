using ACadSharp.Objects;

namespace ACadSharp.Formats;

public interface IMediaExporter : IExporter
{
	public void Export(Layout layout);
}
#if NET
using ACadSharp.Entities;
using ACadSharp.Formats.Json;
using ACadSharp.Formats.Json.Converters;
using CSMath;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ACadSharp.Formats.Tests.Json;

public class JsonExporterTests
{
	[Fact]
	public void EntityToJsonTest()
	{
		Line line = new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0));

		string json = JsonExporter.Serialize(line, new JsonSerializerOptions
		{
			IgnoreReadOnlyProperties = true,
			IgnoreReadOnlyFields = true,
		});

		JsonObject obj = JsonNode.Parse(json).AsObject();

		this.assertCadObjectJson(obj);

		Assert.True(obj.ContainsKey(nameof(Line.StartPoint)));
		Assert.True(obj.ContainsKey(nameof(Line.EndPoint)));
	}

	private void assertCadObjectJson(JsonObject obj)
	{
		Assert.True(obj.ContainsKey(nameof(CadObject.Document)));
		Assert.True(obj.ContainsKey(nameof(CadObject.ExtendedData)));
		Assert.True(obj.ContainsKey(nameof(CadObject.Handle)));
		Assert.True(obj.ContainsKey(nameof(CadObject.Owner)));
		Assert.True(obj.ContainsKey(nameof(CadObject.XDictionary)));
	}
}
#endif
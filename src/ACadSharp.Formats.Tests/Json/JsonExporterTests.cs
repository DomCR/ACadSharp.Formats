using ACadSharp.Entities;
using ACadSharp.Entities.AecEntities;
using ACadSharp.Formats.Json;
using ACadSharp.Formats.Json.Converters;
using ACadSharp.Formats.Tests.Common;
using ACadSharp.Tables;
using CSMath;
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace ACadSharp.Formats.Tests.Json;

public class CadJsonSerializerTests
{
	public static readonly TheoryData<Type> InvalidEntityTypes = new TheoryData<Type>();

	public static readonly TheoryData<Type> ValidEntityTypes = new TheoryData<Type>();

	private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
	};

	private readonly ITestOutputHelper _output;

	static CadJsonSerializerTests()
	{
		var d = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.ManifestModule.Name == "ACadSharp.dll");
		foreach (var item in d.GetTypes().Where(i => !i.IsAbstract && i.IsPublic))
		{
			if (item.IsSubclassOf(typeof(Entity)) && !item.IsSubclassOf(typeof(AecEntity))
				&& item.GetConstructor(Array.Empty<Type>()) != null)
			{
				ValidEntityTypes.Add(item);
			}
			else if (item.IsSubclassOf(typeof(AecEntity)))
			{
				InvalidEntityTypes.Add(item);
			}
		}
	}

	public CadJsonSerializerTests(ITestOutputHelper output)
	{
		this._output = output;
	}

	[Fact]
	public void BlockRecordToJsonTest()
	{
		BlockRecord blk = new BlockRecord("my_block");
		blk.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0)));

		string json = CadJsonSerializer.Serialize(blk, this._jsonOptions);

		JsonObject obj = JsonNode.Parse(json).AsObject();

		this._output.WriteLine(json);
		this.assertCadObjectJson(obj);
	}

	[Fact]
	public void CadDocumentToJsonTest()
	{
		CadDocument doc = new();
		doc.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0)));

		string json = CadJsonSerializer.Serialize(doc, this._jsonOptions);

		JsonObject obj = JsonNode.Parse(json).AsObject();

		this._output.WriteLine(json);
	}

	[Theory]
	[MemberData(nameof(ValidEntityTypes))]
	public void EntityToJsonTest(Type type)
	{
		Entity entity = (Entity)Factory.CreateObject(type);

		string json = CadJsonSerializer.Serialize(entity, this._jsonOptions);

		JsonObject obj = JsonNode.Parse(json).AsObject();

		this._output.WriteLine(json);
		this.assertCadObjectJson(obj);
		this.assertEntityJson(obj);
	}

	[Theory]
	[MemberData(nameof(InvalidEntityTypes))]
	public void InvalidTypesTest(Type type)
	{
		CadConverterFactory factory = new CadConverterFactory();
		Assert.False(factory.CanConvert(type));
	}

	[Fact(Skip = "Not implemented yet")]
	public void JsonToCadDocumentTest()
	{
		CadDocument doc = new();
		doc.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0)));

		string json = CadJsonSerializer.Serialize(doc, this._jsonOptions);

		JsonObject obj = JsonNode.Parse(json).AsObject();

		this._output.WriteLine(json);

		var result = Formats.Json.CadJsonSerializer.DeserializeDocument(json);
	}

	[Theory]
	[MemberData(nameof(ValidEntityTypes))]
	public void JsonToEntityTest(Type type)
	{
		Entity entity = (Entity)Factory.CreateObject(type);

		string json = Formats.Json.CadJsonSerializer.Serialize(entity, this._jsonOptions);

		JsonObject obj = JsonNode.Parse(json).AsObject();

		this._output.WriteLine(json);

		var cadobj = Formats.Json.CadJsonSerializer.Deserialize(json, type);

		Assert.Equal(type, cadobj.GetType());
		//EntityComparator.Equals(entity, (Entity)cadobj);
	}

	private void assertCadObjectJson(JsonObject obj)
	{
		Assert.True(obj.ContainsKey(nameof(CadObject.ObjectName)));
		Assert.True(obj.ContainsKey(nameof(CadObject.SubclassMarker)));
		Assert.True(obj.ContainsKey(nameof(CadObject.Handle)));
		Assert.True(obj.ContainsKey(nameof(CadObject.Owner)));
		Assert.True(obj.ContainsKey(nameof(CadObject.XDictionary)));
	}

	private void assertEntityJson(JsonObject obj)
	{
		Assert.True(obj.ContainsKey(nameof(Entity.Layer)));
		Assert.True(obj.ContainsKey(nameof(Entity.Color)));
		Assert.True(obj.ContainsKey(nameof(Entity.IsInvisible)));
		Assert.True(obj.ContainsKey(nameof(Entity.LineType)));
		Assert.True(obj.ContainsKey(nameof(Entity.LineTypeScale)));
		Assert.True(obj.ContainsKey(nameof(Entity.LineWeight)));
		Assert.True(obj.ContainsKey(nameof(Entity.Material)));
		Assert.True(obj.ContainsKey(nameof(Entity.Transparency)));
	}
}
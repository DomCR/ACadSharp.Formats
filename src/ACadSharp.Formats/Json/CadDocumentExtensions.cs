#if NET
using ACadSharp;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACadSharp.Formats.Json;

public static class CadDocumentExtensions
{
	public static string ToJson(this CadDocument document)
	{
		string json = JsonSerializer.Serialize(document, document.GetType(),
			options: new JsonSerializerOptions
			{
				WriteIndented = true,
				//ReferenceHandler = ReferenceHandler.IgnoreCycles,
				ReferenceHandler = new CadDocumentReferenceHandler(),
			});

		//var node = JsonSerializer.SerializeToNode(document,
		//	options: new JsonSerializerOptions
		//	{
		//		WriteIndented = true,
		//		ReferenceHandler = new CadDocumentReferenceHandler(),
		//	});

		return json;
	}
}

public class CadDocumentReferenceHandler : ReferenceHandler
{
	private ReferenceResolver _rootedResolver;

	public CadDocumentReferenceHandler()
	{
		this._rootedResolver = new CadDocumentResolver();
	}

	public override ReferenceResolver CreateResolver()
	{
		return _rootedResolver;
	}
}

public class CadDocumentResolver : ReferenceResolver
{
	private readonly Dictionary<object, string> _objectToReferenceIdMap = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);

	private readonly Dictionary<string, object> _referenceIdToObjectMap = new Dictionary<string, object>();

	private uint _referenceCount;
	public override void AddReference(string referenceId, object value)
	{
		if (!_referenceIdToObjectMap.TryAdd(referenceId, value))
		{
			throw new JsonException();
		}
	}

	public override string GetReference(object value, out bool alreadyExists)
	{
		string referenceId = string.Empty;

		if (_referenceCount == 0)
		{
			alreadyExists = false;
			_referenceCount++;

			if (value is IHandledCadObject id)
			{
				referenceId = id.Handle.ToString();
			}

			return referenceId;
		}

		if (value is INamedCadObject named)
		{
			referenceId = named.Name;
		}
		else if (value is IHandledCadObject handled)
		{
			referenceId = handled.Handle.ToString();
		}
		else
		{
			alreadyExists = false;
			return string.Empty;
		}

		alreadyExists = this._referenceIdToObjectMap.ContainsKey(referenceId);
		if (!alreadyExists)
		{
			this._referenceIdToObjectMap.Add(referenceId, value);
		}

		return referenceId;
	}

	public override object ResolveReference(string referenceId)
	{
		if (!_referenceIdToObjectMap.TryGetValue(referenceId, out object value))
		{
			throw new JsonException();
		}

		return value;
	}
}
#endif
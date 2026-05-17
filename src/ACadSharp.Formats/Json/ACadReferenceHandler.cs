#if NET
using ACadSharp;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACadSharp.Formats.Json;

public class ACadReferenceHandler : ReferenceHandler
{
	private ReferenceResolver _rootedResolver;

	public ACadReferenceHandler()
	{
		this._rootedResolver = new ACadResolver();
	}

	public override ReferenceResolver CreateResolver()
	{
		return _rootedResolver;
	}
}
#endif
# ACadSharp.Formats
![Build&Test](https://github.com/DomCr/ACadSharp.Formats/actions/workflows/csharp.yml/badge.svg) ![License](https://img.shields.io/github/license/DomCr/ACadSharp.Formats) ![nuget](https://img.shields.io/nuget/v/Acadsharp.Formats) ![downloads](https://img.shields.io/nuget/dt/ACadSharp.Formats) [![Coverage Status](https://coveralls.io/repos/github/DomCR/ACadSharp.Formats/badge.svg?branch=master)](https://coveralls.io/github/DomCR/ACadSharp.Formats?branch=master) 
---

**ACadSharp.Formats** is a .NET companion library for [ACadSharp](https://github.com/DomCR/ACadSharp) that exports CAD documents (DWG/DXF) into other common file formats.
It builds on top of the ACadSharp object model, so any `CadDocument` you read or create can be converted through a simple, shared exporter API.

### Features

Exporters currently under development:

- **SVG** – vector export of model/paper space entities, preserving layers, colors and line weights.
- **PDF** – page-based vector export driven by the document layouts and plot settings.
- **Image** – raster output (PNG/JPEG and similar) rendered from the CAD drawing.
- **JSON** – serialization of the full CAD document structure or individual components for inspection, diffing or interop with non-.NET tools.

> Note: all exporters are work in progress and their public APIs may change between releases.

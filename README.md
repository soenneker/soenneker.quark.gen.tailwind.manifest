[![](https://img.shields.io/nuget/v/soenneker.quark.gen.tailwind.manifest.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.quark.gen.tailwind.manifest/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.quark.gen.tailwind.manifest/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.quark.gen.tailwind.manifest/actions/workflows/publish-package.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.quark.gen.tailwind.manifest/build-and-test.yml?label=Build&style=for-the-badge)](https://github.com/soenneker/soenneker.quark.gen.tailwind.manifest/actions/workflows/build-and-test.yml)
[![](https://img.shields.io/nuget/dt/soenneker.quark.gen.tailwind.manifest.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.quark.gen.tailwind.manifest/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.quark.gen.tailwind.manifest/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.quark.gen.tailwind.manifest/actions/workflows/codeql.yml)

# Soenneker.Quark.Gen.Tailwind.Manifest

Generates a Tailwind source manifest for class names composed through Quark’s fluent style builders.

## Install

```bash
dotnet add package Soenneker.Quark.Gen.Tailwind.Manifest
```

## Usage

Install the package in the project containing Quark component and builder usage, then build normally:

```bash
dotnet build
```

The build writes `tailwind/quark-tailwind-manifest.txt`. `Soenneker.Quark.Gen.Tailwind` consumes that file so Tailwind retains classes that are assembled by fluent expressions and therefore are not visible as ordinary class literals.

For example, a Quark builder expression such as:

```csharp
Padding.OnX.Is4
```

contributes `px-4` even though that complete class name is not written as a literal in application source. Read chains left to right: a modifier configures the next concrete value. `Padding.OnX.Is4.OnY.Is2` describes `px-4 py-2`; a trailing `OnX` does not modify an earlier value.

This package discovers classes; it does not compile CSS or register runtime services. Pair it with `Soenneker.Quark.Gen.Tailwind`, enable `TailwindGeneratorBuildEnabled` in the application, and load the generated stylesheet. See the [complete Quark setup](https://quark.soenneker.com/installation) and [generator guide](https://quark.soenneker.com/generators).

The generated file is replaced on subsequent builds and should not be edited manually. Runtime-generated tokens that never appear in source cannot be inferred; add those classes explicitly to the Tailwind input or another source manifest.

## Configuration

Generation is enabled by default. It can be disabled or redirected in the project file:

```xml
<PropertyGroup>
  <QuarkTailwindManifestGeneratorBuildEnabled>false</QuarkTailwindManifestGeneratorBuildEnabled>
  <QuarkTailwindManifestOutput>$(IntermediateOutputPath)quark-tailwind-manifest.txt</QuarkTailwindManifestOutput>
</PropertyGroup>
```

Set only the property you need; disabling generation means the Tailwind build will not receive newly composed classes from this project.

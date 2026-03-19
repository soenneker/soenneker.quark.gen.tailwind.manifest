using Microsoft.CodeAnalysis;

namespace Soenneker.Quark.Gen.Tailwind.Manifest;

[Generator]
public sealed class QuarkTailwindManifestGeneratorGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Generator runs only on build; BuildTasks write the Tailwind class manifest.
    }
}

using Microsoft.CodeAnalysis;

namespace Soenneker.Quark.Gen.Tailwind.Manifest;

/// <summary>
/// Provides the analyzer entry point for the Quark Tailwind manifest build package.
/// </summary>
[Generator]
public sealed class QuarkTailwindManifestGeneratorGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Initializes the analyzer entry point. Manifest generation is performed by the package's MSBuild task.
    /// </summary>
    /// <param name="context">The incremental generator initialization context.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Generator runs only on build; BuildTasks write the Tailwind class manifest.
    }
}

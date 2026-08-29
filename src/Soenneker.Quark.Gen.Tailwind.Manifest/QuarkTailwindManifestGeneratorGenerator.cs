using Microsoft.CodeAnalysis;

namespace Soenneker.Quark.Gen.Tailwind.Manifest;

/// <summary>
/// Represents the quark tailwind manifest generator generator.
/// </summary>
[Generator]
public sealed class QuarkTailwindManifestGeneratorGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Initializes the Quark Tailwind Manifest Generator Generator so it is ready for use.
    /// </summary>
    /// <param name="context">HTTP context containing the Authorization header.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Generator runs only on build; BuildTasks write the Tailwind class manifest.
    }
}

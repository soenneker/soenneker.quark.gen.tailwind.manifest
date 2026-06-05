using Microsoft.CodeAnalysis;

namespace Soenneker.Quark.Gen.Tailwind.Manifest;

/// <summary>
/// Represents the quark tailwind manifest generator generator.
/// </summary>
[Generator]
public sealed class QuarkTailwindManifestGeneratorGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Executes the initialize operation.
    /// </summary>
    /// <param name="context">The context.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Generator runs only on build; BuildTasks write the Tailwind class manifest.
    }
}

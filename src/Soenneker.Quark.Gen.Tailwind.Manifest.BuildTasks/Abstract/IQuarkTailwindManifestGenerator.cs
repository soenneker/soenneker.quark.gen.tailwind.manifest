using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks.Abstract;

/// <summary>
/// Defines the quark tailwind manifest generator contract.
/// </summary>
public interface IQuarkTailwindManifestGenerator
{
    /// <summary>
    /// Runs quark Tailwind Manifest Generator for the Quark Tailwind Manifest Generator.
    /// </summary>
    /// <param name="args">Command-line arguments passed to the application.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task whose result is the requested value.</returns>
    ValueTask<int> Run(string[] args, CancellationToken cancellationToken);
}

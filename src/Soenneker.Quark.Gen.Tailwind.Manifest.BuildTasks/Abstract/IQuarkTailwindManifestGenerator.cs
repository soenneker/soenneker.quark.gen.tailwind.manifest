using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks.Abstract;

/// <summary>
/// Generates a Tailwind source manifest from Quark builder usage in a project.
/// </summary>
public interface IQuarkTailwindManifestGenerator
{
    /// <summary>
    /// Generates the manifest using the supplied build-task arguments.
    /// </summary>
    /// <param name="args">Command-line arguments passed to the application.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The process exit code: zero on success; otherwise nonzero.</returns>
    ValueTask<int> Run(string[] args, CancellationToken cancellationToken);
}

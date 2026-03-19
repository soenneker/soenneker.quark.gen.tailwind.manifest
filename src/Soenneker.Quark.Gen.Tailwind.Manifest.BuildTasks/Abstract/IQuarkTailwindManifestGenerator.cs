using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks.Abstract;

public interface IQuarkTailwindManifestGenerator
{
    ValueTask<int> Run(CancellationToken cancellationToken);
}
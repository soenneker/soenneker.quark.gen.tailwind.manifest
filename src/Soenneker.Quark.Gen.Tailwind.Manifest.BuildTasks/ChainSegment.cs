using System.Collections.Generic;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks;

internal readonly record struct ChainSegment(string Name, List<string> Args);

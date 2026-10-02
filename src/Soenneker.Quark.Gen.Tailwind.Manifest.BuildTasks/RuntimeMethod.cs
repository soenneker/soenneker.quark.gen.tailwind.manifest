using System.Reflection;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks;

internal readonly record struct RuntimeMethod(MethodInfo Method, ParameterInfo[] Parameters);

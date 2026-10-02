using System;
using System.Collections.Generic;
using System.Reflection;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks;

internal sealed class RuntimeMethodCache
{
    private readonly Dictionary<(string Name, int Count, bool IsStatic), List<RuntimeMethod>> _methods = new();

    public RuntimeMethodCache(Type type)
    {
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
        {
            if (method.IsSpecialName)
                continue;

            ParameterInfo[] parameters = method.GetParameters();
            var key = (method.Name, parameters.Length, method.IsStatic);
            if (!_methods.TryGetValue(key, out List<RuntimeMethod>? methods))
                _methods.Add(key, methods = []);
            methods.Add(new RuntimeMethod(method, parameters));
        }
    }

    public bool TryGetMethods(string name, int count, bool isStatic, out List<RuntimeMethod>? methods) =>
        _methods.TryGetValue((name, count, isStatic), out methods);
}

using AwesomeAssertions;
using Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks;
using Soenneker.Tests.Unit;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.Tests;

public sealed class QuarkTailwindManifestGeneratorTests : UnitTest
{
    [Test]
    public void AddGeneralClassStrings_keeps_arbitrary_variant_tokens_with_parentheses_from_concatenated_literals()
    {
        MethodInfo method = typeof(QuarkTailwindManifestGenerator).GetMethod("AddGeneralClassStrings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = new HashSet<string>(StringComparer.Ordinal);

        const string source = """
                              builder.AppendClass(ref sb, "[&>*:not(:first-child)]:rounded-l-none " +
                                  "[&>*:not(:first-child)]:border-l-0 " +
                                  "[&>*:not(:last-child)]:rounded-r-none");
                              """;

        method.Invoke(null, [result, source]);

        result.Should().Contain("[&>*:not(:first-child)]:rounded-l-none");
        result.Should().Contain("[&>*:not(:first-child)]:border-l-0");
        result.Should().Contain("[&>*:not(:last-child)]:rounded-r-none");
    }

    [Test]
    public void AddGeneralClassStrings_keeps_decimal_tailwind_utilities()
    {
        MethodInfo method = typeof(QuarkTailwindManifestGenerator).GetMethod("AddGeneralClassStrings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = new HashSet<string>(StringComparer.Ordinal);

        const string source = """
                              Padding ??= "pt-0 pb-2.5";
                              Margin ??= "my-1.5";
                              """;

        method.Invoke(null, [result, source]);

        result.Should().Contain("pt-0");
        result.Should().Contain("pb-2.5");
        result.Should().Contain("my-1.5");
    }

    [Test]
    public void TryEvaluateRuntimeChain_handles_quark_qualified_fluent_roots()
    {
        List<string> classes = EvaluateClasses(("TextSize", []), ("Sm", []));

        classes.Should().Contain("text-sm");
    }

    [Test]
    public void TryEvaluateRuntimeChain_handles_quark_qualified_nested_fluent_roots()
    {
        List<string> classes = EvaluateClasses(("Rounded", []), ("Top", []), ("Xl", []));

        classes.Should().Contain("rounded-t-xl");
    }

    [Test]
    public void TryEvaluateRuntimeChain_handles_namespace_qualified_fluent_roots()
    {
        List<string> classes = EvaluateClasses("Soenneker", ("Quark", []), ("TextSize", []), ("Sm", []));

        classes.Should().Contain("text-sm");
    }

    [Test]
    public void TryEvaluateRuntimeChain_handles_arbitrary_token_values_with_parentheses()
    {
        List<string> classes = EvaluateClasses(("Top", []), ("Token", ["\"[calc(var(--header-height)+1rem)]\""]));

        classes.Should().Contain("top-[calc(var(--header-height)+1rem)]");
    }

    private static List<string> EvaluateClasses(params (string Name, string[] Args)[] segments)
    {
        return EvaluateClasses("Quark", segments);
    }

    private static List<string> EvaluateClasses(string root, params (string Name, string[] Args)[] segments)
    {
        Type generatorType = typeof(QuarkTailwindManifestGenerator);
        MethodInfo rootsMethod = generatorType.GetMethod("CollectRuntimeFluentRoots", BindingFlags.NonPublic | BindingFlags.Static)!;
        object roots = rootsMethod.Invoke(null, [])!;

        object segmentList = BuildSegmentList(generatorType, segments);
        MethodInfo evaluateMethod = generatorType.GetMethod("TryEvaluateRuntimeChain", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] args = [roots, root, segmentList, null, null];

        bool result = (bool) evaluateMethod.Invoke(null, args)!;

        result.Should().BeTrue();
        return (List<string>) args[3]!;
    }

    private static object BuildSegmentList(Type generatorType, params (string Name, string[] Args)[] segments)
    {
        Type segmentType = generatorType.GetNestedType("ChainSegment", BindingFlags.NonPublic)!;
        Type listType = typeof(List<>).MakeGenericType(segmentType);
        var list = (IList) Activator.CreateInstance(listType)!;

        foreach ((string name, string[] args) in segments)
        {
            object segment = Activator.CreateInstance(segmentType, name, new List<string>(args))!;
            list.Add(segment);
        }

        return list;
    }
}

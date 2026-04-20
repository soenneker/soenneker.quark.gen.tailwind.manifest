using AwesomeAssertions;
using Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks;
using Soenneker.Tests.Unit;
using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.Tests;

public sealed class QuarkTailwindManifestGeneratorTests : UnitTest
{
    [Fact]
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
}

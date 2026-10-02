using System;
using System.Collections.Generic;
using System.Reflection;
using Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.Tests;

public sealed class ManifestTokenTests
{
    [Test]
    public void RepeatedTokensPreserveSetComparerAndSupportOtherSets()
    {
        var add = typeof(QuarkTailwindManifestGenerator).GetMethod("AddCandidateClassString", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Action<ISet<string>, string>>();
        foreach (ISet<string> target in new ISet<string>[] { new HashSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase) })
        {
            add(target, "text-sm p-2 text-sm");
            add(target, "text-sm p-2");
            if (!target.SetEquals(["text-sm", "p-2"]))
                throw new InvalidOperationException("Token deduplication changed.");
        }
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TEXT-SM" };
        add(insensitive, "text-sm");
        if (insensitive.Count != 1)
            throw new InvalidOperationException("The set comparer was ignored.");
    }
}

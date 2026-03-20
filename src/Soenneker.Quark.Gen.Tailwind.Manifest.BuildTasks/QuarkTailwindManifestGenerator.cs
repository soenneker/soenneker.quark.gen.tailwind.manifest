using Microsoft.Extensions.Logging;
using Soenneker.Extensions.String;
using Soenneker.Extensions.Task;
using Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.PooledStringBuilders;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.ValueTask;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks;

/// <inheritdoc cref="IQuarkTailwindManifestGenerator"/>
public sealed partial class QuarkTailwindManifestGenerator : IQuarkTailwindManifestGenerator
{
    private const string _inlineGeneratedTxtFileName = "tw-inline.generated.txt";

    private static readonly string[] _responsivePrefixes = ["", "sm:", "md:", "lg:", "xl:", "2xl:"];

    [GeneratedRegex(
        @"\[(?<attr>[^\]]*TailwindPrefix[^\]]*)\]\s*(?:(?:public|internal|private|protected)\s+)?(?:sealed\s+)?class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b(?<after>[^{]*)\{",
        RegexOptions.Singleline)]
    private static partial Regex ClassWithAttrRegex();

    [GeneratedRegex(@"TailwindPrefix\s*\(\s*""(?<prefix>[^""]+)""(?:\s*,\s*Responsive\s*=\s*(?<resp>true|false))?\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TailwindPrefixArgsRegex();

    [GeneratedRegex(@"public\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<prop>[A-Za-z_][A-Za-z0-9_]*)\s*=>\s*(?<method>Chain[A-Za-z]*)\s*\(\s*(?<args>[^;]*)\)\s*;",
        RegexOptions.Singleline)]
    private static partial Regex ChainPropRegex();

    [GeneratedRegex(@"\b(?:class|Class)\s*=\s*""(?<classes>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RazorClassAttributeRegex();

    [GeneratedRegex(@"AppendClass\s*\(\s*ref\s+[A-Za-z_][A-Za-z0-9_]*\s*,\s*""(?<classes>(?:[^""\\]|\\.)*)""\s*\)", RegexOptions.Singleline)]
    private static partial Regex AppendClassLiteralRegex();

    [GeneratedRegex(@"(?<!@)""(?<value>(?:[^""\\]|\\.)*)""", RegexOptions.Singleline)]
    private static partial Regex RegularStringLiteralRegex();

    [GeneratedRegex("@\"(?<value>(?:[^\"]|\"\")*)\"", RegexOptions.Singleline)]
    private static partial Regex VerbatimStringLiteralRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CStyleCommentRegex();

    [GeneratedRegex(@"//.*?$", RegexOptions.Multiline)]
    private static partial Regex CppStyleCommentRegex();

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorCommentRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlCommentRegex();

    private static readonly HashSet<string> StandaloneClassTokens = new(StringComparer.Ordinal)
    {
        "absolute", "block", "container", "contents", "disabled", "dropend", "fixed", "flex", "grid", "group", "hidden", "inline",
        "italic", "outline", "peer", "preview", "relative", "ring", "shadow", "show", "sr-only", "static", "sticky", "truncate",
        "underline", "visible", "invisible"
    };

    private readonly ILogger<QuarkTailwindManifestGenerator> _logger;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;

    public QuarkTailwindManifestGenerator(ILogger<QuarkTailwindManifestGenerator> logger, IFileUtil fileUtil, IDirectoryUtil directoryUtil)
    {
        _logger = logger;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
    }

    public async ValueTask<int> Run(CancellationToken cancellationToken)
    {
        string[] args = Environment.GetCommandLineArgs();
        Dictionary<string, string> map = ParseArgs(args);

        if (!map.TryGetValue("--projectDir", out string? projectDir) || projectDir.IsNullOrWhiteSpace())
            return Fail("Missing required --projectDir");

        projectDir = NormalizeFullPath(projectDir);

        var sourceRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            projectDir
        };

        if (map.TryGetValue("--sourceDirs", out string? sourceDirs) && sourceDirs.HasContent())
        {
            foreach (string dir in sourceDirs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (dir.Length == 0)
                    continue;

                sourceRoots.Add(NormalizeFullPath(dir));
            }
        }

        string? projectParent = Path.GetDirectoryName(projectDir);

        if (projectParent.HasContent() && await _directoryUtil.Exists(projectParent, cancellationToken)
                                                              .NoSync())
            sourceRoots.Add(projectParent);

        string outputPath = map.TryGetValue("--manifestOutput", out string? manifestOutput) && manifestOutput.HasContent()
            ? NormalizeFullPath(manifestOutput)
            : Path.Combine(projectDir, "tailwind", _inlineGeneratedTxtFileName);

        string? outputDir = Path.GetDirectoryName(outputPath);

        if (outputDir.HasContent())
            await _directoryUtil.Create(outputDir, log: false, cancellationToken)
                                .NoSync();

        _logger.LogInformation("Starting Tailwind manifest generation for project {ProjectDir}.", projectDir);
        _logger.LogInformation("Generating inline Tailwind manifest from {SourceRootCount} source roots to {OutputPath}.", sourceRoots.Count, outputPath);

        await GenerateInlineManifest(sourceRoots, outputPath, cancellationToken)
            .NoSync();

        _logger.LogInformation("Completed Tailwind manifest generation for project {ProjectDir}.", projectDir);
        return 0;
    }

    private async Task GenerateInlineManifest(HashSet<string> sourceRoots, string outputPath, CancellationToken cancellationToken)
    {
        var uniqueLines = new HashSet<string>(StringComparer.Ordinal);
        var totalFilesScanned = 0;
        var tailwindPrefixClasses = 0;
        var componentCodeClasses = 0;
        var razorClasses = 0;

        foreach (string sourceRoot in sourceRoots)
        {
            if (!await _directoryUtil.Exists(sourceRoot, cancellationToken)
                                     .NoSync())
            {
                _logger.LogWarning("Skipping missing source root {SourceRoot}.", sourceRoot);
                continue;
            }

            _logger.LogInformation("Scanning source root {SourceRoot} for Tailwind classes.", sourceRoot);

            List<string> csFiles = await _directoryUtil.GetFilesByExtension(sourceRoot, ".cs", recursive: true, cancellationToken)
                                                       .NoSync();

            foreach (string file in csFiles)
            {
                if (IsExcluded(file))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                totalFilesScanned++;

                string? text = await TryReadFile(file, isRazor: false, cancellationToken)
                    .NoSync();

                if (text is null)
                    continue;

                ProcessCsFile(file, text, uniqueLines, ref tailwindPrefixClasses, ref componentCodeClasses);
            }

            List<string> razorFiles = await _directoryUtil.GetFilesByExtension(sourceRoot, ".razor", recursive: true, cancellationToken)
                                                          .NoSync();

            foreach (string file in razorFiles)
            {
                if (IsExcluded(file))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                totalFilesScanned++;

                string? text = await TryReadFile(file, isRazor: true, cancellationToken)
                    .NoSync();

                if (text is null)
                    continue;

                ProcessRazorFile(file, text, uniqueLines, ref razorClasses);
            }
        }

        var final = new List<string>(uniqueLines);
        final.Sort(StringComparer.Ordinal);

        _logger.LogInformation(
            "Tailwind manifest scan complete: {FileCount} files scanned, {ClassCount} class names (TailwindPrefix={TailwindPrefixCount}, ComponentCode={ComponentCodeCount}, Razor={RazorCount}), output {OutputPath}.",
            totalFilesScanned, final.Count, tailwindPrefixClasses, componentCodeClasses, razorClasses, outputPath);

        if (final.Count > 0)
        {
            int sampleCount = Math.Min(15, final.Count);
            _logger.LogInformation("Sample class names: {SampleClasses}", string.Join(", ", final.GetRange(0, sampleCount)));
        }

        var sb = new PooledStringBuilder(4096);
        sb.AppendLine("# Auto-generated by Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks");
        sb.AppendLine("# Do not edit manually. Class names for Tailwind @source to scan.");

        foreach (string line in final)
            sb.AppendLine(line);

        _logger.LogInformation("Writing Tailwind inline manifest to {OutputPath}.", outputPath);
        await _fileUtil.Write(outputPath, sb.ToStringAndDispose(), cancellationToken: cancellationToken)
                       .NoSync();
    }

    private async ValueTask<string?> TryReadFile(string file, bool isRazor, CancellationToken cancellationToken)
    {
        try
        {
            string text = await _fileUtil.Read(file, log: false, cancellationToken)
                                         .NoSync();
            return isRazor ? StripRazorComments(text) : StripComments(text);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to read {Kind} file {File}", isRazor ? "razor" : "source", file);
            return null;
        }
    }

    private void ProcessCsFile(string file, string text, HashSet<string> uniqueLines, ref int tailwindPrefixClasses, ref int componentCodeClasses)
    {
        foreach (Match match in ClassWithAttrRegex()
                     .Matches(text))
        {
            string attrBlob = match.Groups["attr"].Value;
            Match attrMatch = TailwindPrefixArgsRegex()
                .Match(attrBlob);

            if (!attrMatch.Success)
                continue;

            string prefix = attrMatch.Groups["prefix"].Value;
            bool responsive = !attrMatch.Groups["resp"].Success || !bool.TryParse(attrMatch.Groups["resp"].Value, out bool parsedResponsive) ||
                              parsedResponsive;

            string className = match.Groups["name"].Value;
            int braceIdx = match.Index + match.Length - 1;
            string? body = TryGetClassBody(text, braceIdx);

            if (body is null)
                continue;

            var classNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match propMatch in ChainPropRegex()
                         .Matches(body))
            {
                string typeName = propMatch.Groups["type"].Value;

                if (!string.Equals(typeName, className, StringComparison.Ordinal))
                    continue;

                string prop = propMatch.Groups["prop"].Value;
                string method = propMatch.Groups["method"].Value;
                string argsText = propMatch.Groups["args"].Value;

                List<string> argsList = SplitArguments(argsText);
                string? classValue = ResolveClassName(prefix, method, argsList, prop);

                if (classValue.HasContent())
                    classNames.Add(classValue);
            }

            if (classNames.Count == 0)
                continue;

            int added = AddManifestClasses(uniqueLines, classNames, responsive);
            tailwindPrefixClasses += added;

            LogClasses("[TailwindPrefix]", file, classNames, added, prefix, responsive, className);
        }

        // Do not scan all string literals in .cs files — they are mostly non-class (messages, keys, etc.).
        // TailwindPrefix extraction above is the only source of classes from C#.
    }

    private void ProcessRazorFile(string file, string text, HashSet<string> uniqueLines, ref int razorClasses)
    {
        var classNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in RazorClassAttributeRegex()
                     .Matches(text))
        {
            AddClassTokens(classNames, match.Groups["classes"].Value);
        }

        foreach (Match match in AppendClassLiteralRegex()
                     .Matches(text))
        {
            AddClassTokens(classNames, match.Groups["classes"].Value);
        }

        AddGeneralClassStrings(classNames, text);

        if (classNames.Count == 0)
            return;

        int added = AddManifestClasses(uniqueLines, classNames, responsive: false);
        razorClasses += added;

        LogClasses("[Razor]", file, classNames, added);
    }

    private void LogClasses(string tag, string file, HashSet<string> classNames, int added, string? prefix = null, bool? responsive = null,
        string? className = null)
    {
        var classList = new List<string>(classNames);
        classList.Sort(StringComparer.Ordinal);

        if (prefix is not null && responsive is not null && className is not null)
        {
            _logger.LogInformation("{Tag} {File} -> class {ClassName}, prefix={Prefix}, responsive={Responsive}, classes=[{Classes}], lines added={Added}", tag,
                file, className, prefix, responsive.Value, string.Join(", ", classList), added);
            return;
        }

        _logger.LogInformation("{Tag} {File} -> classes=[{Classes}], lines added={Added}", tag, file, string.Join(", ", classList), added);
    }

    private static int AddManifestClasses(HashSet<string> uniqueLines, HashSet<string> classes, bool responsive)
    {
        var added = 0;

        if (responsive)
        {
            foreach (string breakpoint in _responsivePrefixes)
            {
                foreach (string classValue in classes)
                {
                    if (uniqueLines.Add(breakpoint + classValue))
                        added++;
                }
            }

            return added;
        }

        foreach (string classValue in classes)
        {
            if (uniqueLines.Add(classValue))
                added++;
        }

        return added;
    }

    private static bool IsExcluded(string fullPath)
    {
        return ContainsPathSegment(fullPath, "/bin/") || ContainsPathSegment(fullPath, "/obj/") || ContainsPathSegment(fullPath, "/node_modules/") ||
               ContainsPathSegment(fullPath, "/.git/") || ContainsPathSegment(fullPath, "/tailwind/");
    }

    private static bool ShouldScanGeneralClassStrings(string file)
    {
        return ContainsPathSegment(file, "/Components/");
    }

    private static bool ContainsPathSegment(string path, string normalizedSegment)
    {
        return path.Contains(normalizedSegment, StringComparison.OrdinalIgnoreCase) ||
               path.Contains(normalizedSegment.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFullPath(string path)
    {
        return Path.GetFullPath(path.Trim()
                                    .Trim('"'));
    }

    private static string StripComments(string value)
    {
        value = CStyleCommentRegex()
            .Replace(value, string.Empty);
        value = CppStyleCommentRegex()
            .Replace(value, string.Empty);
        return value;
    }

    private static string StripRazorComments(string value)
    {
        value = RazorCommentRegex()
            .Replace(value, string.Empty);
        value = HtmlCommentRegex()
            .Replace(value, string.Empty);
        return value;
    }

    private static string? TryGetClassBody(string text, int openBraceIndex)
    {
        var depth = 0;

        for (int i = openBraceIndex; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;

                if (depth == 0)
                    return text.Substring(openBraceIndex + 1, i - openBraceIndex - 1);
            }
        }

        return null;
    }

    private static string? ParseToken(string arg, string propName)
    {
        arg = arg.Trim();

        if (arg.Length >= 2 && arg[0] == '"' && arg[^1] == '"')
            return arg.Substring(1, arg.Length - 2);

        if (arg.Length >= 3 && arg[0] == '@' && arg[1] == '"' && arg[^1] == '"')
            return arg.Substring(2, arg.Length - 3)
                      .Replace("\"\"", "\"", StringComparison.Ordinal);

        if (arg.Contains('.', StringComparison.Ordinal))
            return propName.ToLowerInvariantFast();

        return propName.ToLowerInvariantFast();
    }

    private static List<string> SplitArguments(string args)
    {
        var results = new List<string>(4);

        if (args.IsNullOrWhiteSpace())
            return results;

        var start = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inString = false;

        for (var i = 0; i < args.Length; i++)
        {
            char c = args[i];

            if (c == '"' && (i == 0 || args[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            switch (c)
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    braceDepth--;
                    break;
                case ',' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                {
                    if (i > start)
                        results.Add(args.Substring(start, i - start)
                                        .Trim());

                    start = i + 1;
                    break;
                }
            }
        }

        if (start < args.Length)
        {
            string last = args.Substring(start)
                              .Trim();

            if (last.Length > 0)
                results.Add(last);
        }

        return results;
    }

    private static string BuildPrefixedClass(string prefix, string token)
    {
        if (token.IsNullOrWhiteSpace())
            return token;

        if (prefix.IsNullOrWhiteSpace())
            return token;

        if (token.StartsWith(prefix + "-", StringComparison.Ordinal))
            return token;

        if (prefix.EndsWith("-", StringComparison.Ordinal) || prefix.EndsWith(":", StringComparison.Ordinal))
            return prefix + token;

        if (string.Equals(prefix, token, StringComparison.Ordinal))
            return prefix;

        return $"{prefix}-{token}";
    }

    private static string? ResolveClassName(string prefix, string methodName, List<string> args, string propName)
    {
        if (args.Count == 0)
            return null;

        if (string.Equals(methodName, "ChainValue", StringComparison.Ordinal))
        {
            string? token = ParseToken(args[0], propName);
            return token.HasContent() ? BuildPrefixedClass(prefix, token) : null;
        }

        if (!string.Equals(methodName, "Chain", StringComparison.Ordinal))
            return null;

        if (args.Count == 1)
        {
            string? token = ParseToken(args[0], propName);
            return token.HasContent() ? BuildPrefixedClass(prefix, token) : null;
        }

        string? utility = ParseToken(args[0], propName);

        if (utility.IsNullOrWhiteSpace())
            return null;

        string? value = ParseToken(args[1], propName);

        if (value.IsNullOrWhiteSpace())
            return BuildPrefixedClass(prefix, utility);

        if (string.Equals(utility, "display", StringComparison.Ordinal))
            return value;

        return BuildPrefixedClass(prefix, $"{utility}-{value}");
    }

    private static void AddClassTokens(ISet<string> target, string classList)
    {
        if (classList.IsNullOrWhiteSpace())
            return;

        ReadOnlySpan<char> span = classList.AsSpan()
                                           .Trim();

        if (span.Length == 0 || span[0] == '@')
            return;

        var i = 0;

        while (i < span.Length)
        {
            while (i < span.Length && char.IsWhiteSpace(span[i]))
                i++;

            int start = i;

            while (i < span.Length && !char.IsWhiteSpace(span[i]))
                i++;

            if (i > start)
                target.Add(span[start..i]
                    .ToString());
        }
    }

    private static bool LooksLikeStandaloneClassToken(string token)
    {
        if (token.IsNullOrWhiteSpace())
            return false;

        if (token.StartsWith("@", StringComparison.Ordinal) && !token.StartsWith("@container", StringComparison.Ordinal))
            return false;

        if (token.AsSpan()
                 .IndexOfAny(_invalidTokenChars) >= 0)
            return false;

        if (token.StartsWith("q-", StringComparison.Ordinal) || token.StartsWith("@container", StringComparison.Ordinal))
            return true;

        if (StandaloneClassTokens.Contains(token))
            return true;

        return token.AsSpan()
                    .IndexOfAny(_standaloneTokenSpecialChars) >= 0;
    }

    /// <summary>
    /// Returns true only for tokens that look like real Tailwind class names (used when scanning
    /// arbitrary string literals to avoid adding code fragments, paths, numbers, etc.).
    /// </summary>
    private static bool IsValidTailwindClassToken(ReadOnlySpan<char> token)
    {
        if (token.Length < 2)
            return false;

        if (token.IndexOfAny(_disallowedInTailwindClass) >= 0)
            return false;

        if (token.Slice(0, 1)
                 .IndexOfAny(_validTailwindFirstChar) < 0)
            return false;

        if (token[0] == '@' && !token.StartsWith("@container", StringComparison.Ordinal))
            return false;

        bool hasLetter = false;
        for (int i = 0; i < token.Length; i++)
        {
            if (char.IsLetter(token[i]))
            {
                hasLetter = true;
                break;
            }
        }

        return hasLetter;
    }

    private static void AddCandidateClassString(ISet<string> target, string value)
    {
        if (value.IsNullOrWhiteSpace())
            return;

        ReadOnlySpan<char> span = value.AsSpan()
                                       .Trim();

        if (span.Length == 0 || span[0] == '@')
            return;

        var hasStrongToken = false;
        var tokenCount = 0;
        var i = 0;

        while (i < span.Length)
        {
            while (i < span.Length && char.IsWhiteSpace(span[i]))
                i++;

            int start = i;

            while (i < span.Length && !char.IsWhiteSpace(span[i]))
                i++;

            if (i <= start)
                continue;

            tokenCount++;

            ReadOnlySpan<char> tok = span[start..i];
            if (IsValidTailwindClassToken(tok))
                hasStrongToken = true;
        }

        if (tokenCount == 0)
            return;

        if (tokenCount == 1)
        {
            ReadOnlySpan<char> single = span.Trim();
            if (IsValidTailwindClassToken(single))
                target.Add(single.ToString());
            return;
        }

        if (!hasStrongToken)
            return;

        i = 0;

        while (i < span.Length)
        {
            while (i < span.Length && char.IsWhiteSpace(span[i]))
                i++;

            int start = i;

            while (i < span.Length && !char.IsWhiteSpace(span[i]))
                i++;

            if (i <= start)
                continue;

            ReadOnlySpan<char> token = span[start..i];
            if (!IsValidTailwindClassToken(token))
                continue;

            target.Add(token.ToString());
        }
    }

    private static void AddGeneralClassStrings(ISet<string> target, string text)
    {
        foreach (Match match in VerbatimStringLiteralRegex()
                     .Matches(text))
        {
            string value = match.Groups["value"]
                                .Value.Replace("\"\"", "\"", StringComparison.Ordinal);
            AddCandidateClassString(target, value);
        }

        foreach (Match match in RegularStringLiteralRegex()
                     .Matches(text))
        {
            string value = match.Groups["value"]
                                .Value.Replace("\\\"", "\"", StringComparison.Ordinal);
            AddCandidateClassString(target, value);
        }
    }

    private static readonly SearchValues<char> _invalidTokenChars = SearchValues.Create("{};,");
    private static readonly SearchValues<char> _standaloneTokenSpecialChars = SearchValues.Create("-:/[]()!._");

    /// <summary>
    /// Characters that must not appear in a Tailwind class token (avoids C#/code fragments).
    /// </summary>
    private static readonly SearchValues<char> _disallowedInTailwindClass = SearchValues.Create("()'$=;,\" \t");

    /// <summary>
    /// Valid first character for a Tailwind class token (letter, important, arbitrary, variant, etc.).
    /// </summary>
    private static readonly SearchValues<char> _validTailwindFirstChar = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ![-:@");

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(Math.Max(4, args.Length / 2), StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length - 1; i++)
        {
            string arg = args[i];

            if (arg.Length > 2 && arg[0] == '-' && arg[1] == '-')
            {
                map[arg] = args[i + 1];
                i++;
            }
        }

        return map;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
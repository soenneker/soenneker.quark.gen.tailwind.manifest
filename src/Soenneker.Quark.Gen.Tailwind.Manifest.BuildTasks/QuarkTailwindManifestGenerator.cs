using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Extensions.String;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.PooledStringBuilders;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks;

///<inheritdoc cref="IQuarkTailwindManifestGenerator"/>
public sealed class QuarkTailwindManifestGenerator : IQuarkTailwindManifestGenerator
{
    private const string _inlineGeneratedTxtFileName = "tw-inline.generated.txt";

    private static readonly Regex ClassWithAttrRegex = new(
        @"\[(?<attr>[^\]]*TailwindPrefix[^\]]*)\]\s*" +
        @"(?:(?:public|internal|private|protected)\s+)?(?:sealed\s+)?class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b(?<after>[^{]*)\{",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex TailwindPrefixArgsRegex = new(
        @"TailwindPrefix\s*\(\s*""(?<prefix>[^""]+)""(?:\s*,\s*Responsive\s*=\s*(?<resp>true|false))?\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ChainPropRegex = new(
        @"public\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<prop>[A-Za-z_][A-Za-z0-9_]*)\s*=>\s*Chain\s*\(\s*(?<arg>[^)]+)\)\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline);

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

        projectDir = Path.GetFullPath(projectDir.Trim()
                                                .Trim('"'));

        var sourceRoots = new List<string> { projectDir };

        if (map.TryGetValue("--sourceDirs", out string? sourceDirs) && sourceDirs.HasContent())
        {
            foreach (string dir in sourceDirs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (dir.Length == 0)
                    continue;

                string full = Path.GetFullPath(dir.Trim()
                                                  .Trim('"'));

                if (!sourceRoots.Contains(full))
                    sourceRoots.Add(full);
            }
        }

        string? projectParent = Path.GetDirectoryName(projectDir);

        if (projectParent.HasContent() && await _directoryUtil.Exists(projectParent, cancellationToken)
                                                              .NoSync() && !sourceRoots.Contains(projectParent))
            sourceRoots.Add(projectParent);

        string outputPath = map.TryGetValue("--manifestOutput", out string? manifestOutput) && manifestOutput.HasContent()
            ? Path.GetFullPath(manifestOutput.Trim()
                                             .Trim('"'))
            : Path.Combine(projectDir, "tailwind", _inlineGeneratedTxtFileName);

        string? outputDir = Path.GetDirectoryName(outputPath);

        if (outputDir.HasContent())
        {
            await _directoryUtil.Create(outputDir, log: false, cancellationToken)
                                .NoSync();
        }

        Console.WriteLine($"QuarkTailwindManifestGenerator: projectDir={projectDir}, sourceRoots={sourceRoots.Count}, output={outputPath}");

        await GenerateInlineManifest(sourceRoots, outputPath, cancellationToken)
            .NoSync();

        return 0;
    }

    private static bool IsExcluded(string fullPath)
    {
        string path = fullPath.Replace('\\', '/');

        return path.Contains("/bin/", StringComparison.OrdinalIgnoreCase) || path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase) || path.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/tailwind/", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripComments(string value)
    {
        value = Regex.Replace(value, @"/\*.*?\*/", "", RegexOptions.Singleline);
        value = Regex.Replace(value, @"//.*?$", "", RegexOptions.Multiline);
        return value;
    }

    private static string? TryGetClassBody(string text, int openBraceIndex)
    {
        var depth = 0;

        for (int i = openBraceIndex; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '{')
                depth++;
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
        int commaIndex = arg.IndexOf(',');

        if (commaIndex >= 0)
            arg = arg.Substring(0, commaIndex);

        arg = arg.Trim();

        if (arg.Length >= 2 && arg[0] == '"' && arg[^1] == '"')
            return arg.Substring(1, arg.Length - 2);

        if (arg.Contains('.', StringComparison.Ordinal))
            return propName.ToLowerInvariantFast();

        return propName.ToLowerInvariantFast();
    }

    private async Task GenerateInlineManifest(List<string> sourceRoots, string outputPath, CancellationToken cancellationToken)
    {
        var uniqueLines = new HashSet<string>(StringComparer.Ordinal);
        var totalFilesScanned = 0;
        var tailwindPrefixClasses = 0;

        foreach (string sourceRoot in sourceRoots)
        {
            if (!await _directoryUtil.Exists(sourceRoot, cancellationToken)
                                     .NoSync())
            {
                Console.WriteLine($"QuarkTailwindManifestGenerator [inline]: skipping missing source root: {sourceRoot}");
                continue;
            }

            Console.WriteLine($"QuarkTailwindManifestGenerator [inline]: scanning source root: {sourceRoot}");

            List<string> csFiles = await _directoryUtil.GetFilesByExtension(sourceRoot, ".cs", recursive: true, cancellationToken)
                                                       .NoSync();

            foreach (string file in csFiles)
            {
                if (IsExcluded(file))
                    continue;

                totalFilesScanned++;
                cancellationToken.ThrowIfCancellationRequested();

                string text;

                try
                {
                    text = await _fileUtil.Read(file, log: false, cancellationToken)
                                          .NoSync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to read source file {File}", file);
                    continue;
                }

                text = StripComments(text);

                foreach (Match match in ClassWithAttrRegex.Matches(text))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string attrBlob = match.Groups["attr"].Value;
                    Match attrMatch = TailwindPrefixArgsRegex.Match(attrBlob);

                    if (!attrMatch.Success)
                        continue;

                    string prefix = attrMatch.Groups["prefix"].Value;
                    var responsive = true;

                    if (attrMatch.Groups["resp"].Success && bool.TryParse(attrMatch.Groups["resp"].Value, out bool parsedResponsive))
                        responsive = parsedResponsive;

                    string className = match.Groups["name"].Value;
                    int braceIdx = match.Index + match.Length - 1;
                    string? body = TryGetClassBody(text, braceIdx);

                    if (body is null)
                        continue;

                    var tokens = new HashSet<string>(StringComparer.Ordinal);

                    foreach (Match propMatch in ChainPropRegex.Matches(body))
                    {
                        string typeName = propMatch.Groups["type"].Value;

                        if (!string.Equals(typeName, className, StringComparison.Ordinal))
                            continue;

                        string prop = propMatch.Groups["prop"].Value;
                        string arg = propMatch.Groups["arg"].Value;
                        string? token = ParseToken(arg, prop);

                        if (token.HasContent())
                            tokens.Add(token);
                    }

                    if (tokens.Count == 0)
                        continue;

                    var tokenList = new List<string>(tokens);
                    tokenList.Sort(StringComparer.Ordinal);
                    var added = 0;

                    if (responsive)
                    {
                        foreach (string breakpoint in new[] { "", "sm:", "md:", "lg:", "xl:", "2xl:" })
                        {
                            foreach (string token in tokenList)
                            {
                                uniqueLines.Add(breakpoint + prefix + token);
                                added++;
                            }
                        }
                    }
                    else
                    {
                        foreach (string token in tokenList)
                        {
                            uniqueLines.Add(prefix + token);
                            added++;
                        }
                    }

                    tailwindPrefixClasses += added;

                    Console.WriteLine(
                        $"QuarkTailwindManifestGenerator [inline]: [TailwindPrefix] {file} -> class {className}, prefix=\"{prefix}\", responsive={responsive}, tokens=[{string.Join(", ", tokenList)}], lines added={added}");
                }
            }
        }

        var final = new List<string>(uniqueLines);
        final.Sort(StringComparer.Ordinal);

        Console.WriteLine(
            $"QuarkTailwindManifestGenerator [inline]: summary: {totalFilesScanned} .cs files scanned, {final.Count} class names (TailwindPrefix={tailwindPrefixClasses})");
        Console.WriteLine($"QuarkTailwindManifestGenerator [inline]: output -> {outputPath}");

        if (final.Count > 0)
        {
            int sampleCount = Math.Min(15, final.Count);
            List<string> sample = final.GetRange(0, sampleCount);
            Console.WriteLine($"QuarkTailwindManifestGenerator [inline]: sample classes: [{string.Join(", ", sample)}]");
        }

        var sb = new PooledStringBuilder(4096);
        sb.AppendLine("# Auto-generated by Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks");
        sb.AppendLine("# Do not edit manually. Class names for Tailwind @source to scan.");

        foreach (string line in final)
            sb.AppendLine(line);

        await _fileUtil.Write(outputPath, sb.ToStringAndDispose(), cancellationToken: cancellationToken)
                       .NoSync();
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i]
                    .StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                map[args[i]] = args[i + 1];
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
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
using System.Reflection;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Soenneker.Extensions.ValueTask;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks;

/// <inheritdoc cref="IQuarkTailwindManifestGenerator"/>
public sealed partial class QuarkTailwindManifestGenerator : IQuarkTailwindManifestGenerator
{
    private const string _tailwindDirName = "tailwind";
    private const string _projectManifestFileName = "quark-tailwind-manifest.txt";
    private const string _suiteManifestFileName = "quark-suite-tailwind-manifest.txt";
    private const string _projectMode = "project";
    private const string _suiteMode = "suite";
    private const string _suitePackageId = "soenneker.quark.suite";

    private static readonly string[] _responsivePrefixes = ["", "sm:", "md:", "lg:", "xl:", "2xl:"];

    private readonly record struct ChainSegment(string Name, List<string> Args);

    private const int _runtimeBuilderExpansionDepth = 2;

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

    private static readonly HashSet<string> IgnoredRuntimeClassTokens = new(StringComparer.Ordinal)
    {
        "xs", "sm", "md", "lg", "xl", "2xl"
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

    public async ValueTask<int> Run(string[] args, CancellationToken cancellationToken)
    {
        Dictionary<string, string> map = ParseArgs(args);

        if (!map.TryGetValue("--projectDir", out string? projectDir) || projectDir.IsNullOrWhiteSpace())
            return Fail("Missing required --projectDir");

        projectDir = NormalizeFullPath(projectDir);

        string mode = ResolveScanMode(map);
        string outputPath = map.TryGetValue("--manifestOutput", out string? manifestOutput) && manifestOutput.HasContent()
            ? NormalizeFullPath(manifestOutput)
            : Path.Combine(projectDir, "tailwind", GetDefaultOutputFileName(mode));

        string? outputDir = Path.GetDirectoryName(outputPath);

        if (outputDir.HasContent())
            await _directoryUtil.Create(outputDir, log: false, cancellationToken)
                                .NoSync();

        _logger.LogInformation("Starting Tailwind manifest generation for project {ProjectDir} in {Mode} mode.", projectDir, mode);

        await GenerateInlineManifest(projectDir, outputPath, mode, cancellationToken)
            .NoSync();

        if (!IsSuiteMode(mode))
        {
            await EnsureLocalSuiteManifest(projectDir, outputPath, cancellationToken)
                .NoSync();
        }

        _logger.LogInformation("Completed Tailwind manifest generation for project {ProjectDir} in {Mode} mode.", projectDir, mode);
        return 0;
    }

    private async Task GenerateInlineManifest(string sourceRoot, string outputPath, string mode, CancellationToken cancellationToken)
    {
        if (!await _directoryUtil.Exists(sourceRoot, cancellationToken)
                                 .NoSync())
        {
            _logger.LogWarning("Skipping missing source root {SourceRoot}.", sourceRoot);
            return;
        }

        _logger.LogInformation("Scanning source root {SourceRoot} for Tailwind classes in {Mode} mode.", sourceRoot, mode);

        var uniqueLines = new HashSet<string>(StringComparer.Ordinal);
        var totalFilesScanned = 0;
        var runtimeBuilderClasses = 0;
        var tailwindPrefixClasses = 0;
        var componentCodeClasses = 0;
        var razorClasses = 0;
        var fluentClasses = 0;
        var csSources = new List<(string File, string Text)>();
        var razorSources = new List<(string File, string Text)>();
        Dictionary<string, Type> runtimeRoots = CollectRuntimeFluentRoots();
        bool suiteMode = IsSuiteMode(mode);

        if (suiteMode)
            runtimeBuilderClasses += AddRuntimeBuilderManifestClasses(uniqueLines, runtimeRoots);

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

            csSources.Add((file, text));
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

            razorSources.Add((file, text));
        }

        foreach ((string file, string text) in csSources)
        {
            if (suiteMode)
            {
                ProcessCsFile(file, text, uniqueLines, runtimeRoots, ref tailwindPrefixClasses, ref componentCodeClasses);
                continue;
            }

            AddFluentInvocationClasses(file, text, runtimeRoots, uniqueLines, ref fluentClasses);
        }

        foreach ((string file, string text) in razorSources)
        {
            if (suiteMode)
            {
                ProcessRazorFile(file, text, uniqueLines, runtimeRoots, ref razorClasses);
                continue;
            }

            AddFluentInvocationClasses(file, text, runtimeRoots, uniqueLines, ref fluentClasses);
        }

        var final = new List<string>(uniqueLines);
        final.Sort(StringComparer.Ordinal);

        if (suiteMode)
        {
            _logger.LogInformation(
                "Tailwind manifest scan complete: {FileCount} files scanned, {ClassCount} class names (RuntimeBuilders={RuntimeBuilderCount}, TailwindPrefix={TailwindPrefixCount}, ComponentCode={ComponentCodeCount}, Razor={RazorCount}), output {OutputPath}.",
                totalFilesScanned, final.Count, runtimeBuilderClasses, tailwindPrefixClasses, componentCodeClasses, razorClasses, outputPath);
        }
        else
        {
            _logger.LogInformation(
                "Tailwind manifest scan complete: {FileCount} files scanned, {ClassCount} class names from fluent builder usage only, output {OutputPath}.",
                totalFilesScanned, final.Count, outputPath);
            _logger.LogInformation("Fluent builder-derived class entries added: {FluentClassCount}.", fluentClasses);
        }

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

        _logger.LogInformation("Writing Tailwind manifest to {OutputPath}.", outputPath);
        await _fileUtil.Write(outputPath, sb.ToStringAndDispose(), cancellationToken: cancellationToken)
                       .NoSync();
    }

    private async Task EnsureLocalSuiteManifest(string projectDir, string outputPath, CancellationToken cancellationToken)
    {
        string? outputDir = Path.GetDirectoryName(outputPath);

        if (!outputDir.HasContent())
            return;

        string localSuiteManifestPath = Path.Combine(outputDir, _suiteManifestFileName);
        string? suiteManifestPath = await ResolveSuiteManifestPath(projectDir, cancellationToken)
            .NoSync();

        if (suiteManifestPath.HasContent())
        {
            suiteManifestPath = NormalizeFullPath(suiteManifestPath);

            if (string.Equals(suiteManifestPath, NormalizeFullPath(localSuiteManifestPath), StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Local suite Tailwind manifest already available at {ManifestPath}.", localSuiteManifestPath);
                return;
            }

            if (await _fileUtil.Exists(suiteManifestPath, cancellationToken)
                              .NoSync())
            {
                string contents = await _fileUtil.Read(suiteManifestPath, log: false, cancellationToken)
                                                 .NoSync();

                await _fileUtil.Write(localSuiteManifestPath, contents, log: false, cancellationToken: cancellationToken)
                               .NoSync();

                _logger.LogInformation("Copied suite Tailwind manifest from {SourcePath} to {DestinationPath}.", suiteManifestPath,
                    localSuiteManifestPath);
                return;
            }
        }

        if (await _fileUtil.Exists(localSuiteManifestPath, cancellationToken)
                          .NoSync())
        {
            return;
        }

        await _fileUtil.Write(localSuiteManifestPath,
                              "# No upstream Soenneker.Quark.Suite Tailwind manifest was resolved for this project." + Environment.NewLine,
                              log: false, cancellationToken: cancellationToken)
                       .NoSync();

        _logger.LogInformation("Created placeholder suite Tailwind manifest at {ManifestPath}.", localSuiteManifestPath);
    }

    private async Task<string?> ResolveSuiteManifestPath(string projectDir, CancellationToken cancellationToken)
    {
        string? projectReferenceManifest = await TryResolveSuiteManifestFromProjectReferences(projectDir, cancellationToken)
            .NoSync();

        if (projectReferenceManifest.HasContent())
            return projectReferenceManifest;

        return await TryResolveSuiteManifestFromPackages(projectDir, cancellationToken)
            .NoSync();
    }

    private async Task<string?> TryResolveSuiteManifestFromProjectReferences(string projectDir, CancellationToken cancellationToken)
    {
        string? projectFilePath = GetProjectFilePath(projectDir);

        if (projectFilePath.IsNullOrWhiteSpace())
            return null;

        XDocument document;

        try
        {
            string xml = await _fileUtil.Read(projectFilePath, log: false, cancellationToken)
                                        .NoSync();
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to inspect project references for {ProjectDir}", projectDir);
            return null;
        }

        IEnumerable<string?> includes = document.Descendants()
                                                .Where(element => element.Name.LocalName == "ProjectReference")
                                                .Select(element => element.Attribute("Include")?.Value);

        foreach (string? include in includes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (include.IsNullOrWhiteSpace())
                continue;

            string referencePath = NormalizeFullPath(Path.Combine(projectDir, include));
            string? referenceDirectory = Path.GetDirectoryName(referencePath);

            if (referenceDirectory.IsNullOrWhiteSpace())
                continue;

            string manifestPath = Path.Combine(referenceDirectory, _tailwindDirName, _suiteManifestFileName);

            if (await _fileUtil.Exists(manifestPath, cancellationToken)
                              .NoSync())
            {
                return manifestPath;
            }
        }

        return null;
    }

    private async Task<string?> TryResolveSuiteManifestFromPackages(string projectDir, CancellationToken cancellationToken)
    {
        string assetsPath = Path.Combine(projectDir, "obj", "project.assets.json");

        if (!await _fileUtil.Exists(assetsPath, cancellationToken)
                          .NoSync())
        {
            return null;
        }

        try
        {
            string assetsJson = await _fileUtil.Read(assetsPath, log: false, cancellationToken)
                                               .NoSync();

            using JsonDocument document = JsonDocument.Parse(assetsJson);

            if (!document.RootElement.TryGetProperty("libraries", out JsonElement libraries) ||
                !document.RootElement.TryGetProperty("packageFolders", out JsonElement packageFolders))
            {
                return null;
            }

            string[] suiteLibraries = libraries.EnumerateObject()
                                             .Select(property => property.Name)
                                             .Where(name => name.StartsWith(_suitePackageId + "/", StringComparison.OrdinalIgnoreCase))
                                             .ToArray();

            if (suiteLibraries.Length == 0)
                return null;

            string[] folders = packageFolders.EnumerateObject()
                                             .Select(property => property.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                                             .ToArray();

            foreach (string library in suiteLibraries)
            {
                int separatorIndex = library.IndexOf('/');

                if (separatorIndex < 0 || separatorIndex == library.Length - 1)
                    continue;

                string version = library.Substring(separatorIndex + 1);

                foreach (string folder in folders)
                {
                    string packageManifestPath = Path.Combine(folder, _suitePackageId, version, _tailwindDirName, _suiteManifestFileName);

                    if (await _fileUtil.Exists(packageManifestPath, cancellationToken)
                                      .NoSync())
                    {
                        return packageManifestPath;
                    }

                    string contentFilesManifestPath = Path.Combine(folder, _suitePackageId, version, "contentFiles", "any", "any", _tailwindDirName,
                        _suiteManifestFileName);

                    if (await _fileUtil.Exists(contentFilesManifestPath, cancellationToken)
                                      .NoSync())
                    {
                        return contentFilesManifestPath;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to inspect package assets for {ProjectDir}", projectDir);
        }

        return null;
    }

    private static string? GetProjectFilePath(string projectDir)
    {
        string[] projectFiles = Directory.GetFiles(projectDir, "*.csproj", SearchOption.TopDirectoryOnly);

        if (projectFiles.Length == 0)
            return null;

        if (projectFiles.Length == 1)
            return projectFiles[0];

        string directoryName = Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return projectFiles.FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), directoryName, StringComparison.OrdinalIgnoreCase))
               ?? projectFiles[0];
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

    private void ProcessCsFile(string file, string text, HashSet<string> uniqueLines, Dictionary<string, Type> runtimeRoots,
        ref int tailwindPrefixClasses, ref int componentCodeClasses)
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

        AddFluentInvocationClasses(file, text, runtimeRoots, uniqueLines, ref componentCodeClasses);
    }

    private void ProcessRazorFile(string file, string text, HashSet<string> uniqueLines, Dictionary<string, Type> runtimeRoots,
        ref int razorClasses)
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

        if (classNames.Count > 0)
        {
            int added = AddManifestClasses(uniqueLines, classNames, responsive: false);
            razorClasses += added;

            LogClasses("[Razor]", file, classNames, added);
        }

        AddFluentInvocationClasses(file, text, runtimeRoots, uniqueLines, ref razorClasses);
    }

    private void AddFluentInvocationClasses(string file, string text, Dictionary<string, Type> runtimeRoots,
        HashSet<string> uniqueLines, ref int count)
    {
        var classNames = new HashSet<string>(StringComparer.Ordinal);

        foreach ((string root, List<ChainSegment> segments) in EnumerateFluentChains(text))
        {
            List<string>? classes = null;

            if (!TryEvaluateRuntimeChain(runtimeRoots, root, segments, out classes, out _))
                continue;

            foreach (string className in classes!)
            {
                if (className.HasContent())
                    classNames.Add(className);
            }
        }

        if (classNames.Count == 0)
            return;

        int added = AddManifestClasses(uniqueLines, classNames, responsive: false);
        count += added;
        LogClasses("[Fluent]", file, classNames, added);
    }

    private static Dictionary<string, Type> CollectRuntimeFluentRoots()
    {
        var result = new Dictionary<string, Type>(StringComparer.Ordinal);
        Assembly assembly = typeof(ICssBuilder).Assembly;

        foreach (Type type in assembly.GetExportedTypes())
        {
            if (!string.Equals(type.Namespace, "Soenneker.Quark", StringComparison.Ordinal) || type.IsNested)
                continue;

            result[type.Name] = type;
        }

        return result;
    }

    private static int AddRuntimeBuilderManifestClasses(HashSet<string> uniqueLines, Dictionary<string, Type> runtimeRoots)
    {
        var added = 0;

        foreach ((string rootName, Type rootType) in runtimeRoots)
        {
            if (!IsStaticContainerType(rootType))
                continue;

            var rootChains = new List<List<ChainSegment>>();
            CollectRuntimeRootChains(rootType, new List<ChainSegment>(), rootChains);

            foreach (List<ChainSegment> rootChain in rootChains)
            {
                added += AddRuntimeChainClasses(uniqueLines, runtimeRoots, rootName, rootChain);

                if (!TryEvaluateRuntimeChain(runtimeRoots, rootName, rootChain, out _, out Type? builderType) || builderType is null)
                    continue;

                List<ChainSegment> builderMembers = GetRuntimeBuilderSegments(builderType);
                if (builderMembers.Count == 0)
                    continue;

                ExpandRuntimeBuilderChains(uniqueLines, runtimeRoots, rootName, rootChain, builderMembers, _runtimeBuilderExpansionDepth, ref added);
            }
        }

        return added;
    }

    private static void ExpandRuntimeBuilderChains(HashSet<string> uniqueLines, Dictionary<string, Type> runtimeRoots, string rootName, List<ChainSegment> prefix,
        List<ChainSegment> builderMembers, int remainingDepth, ref int added)
    {
        if (remainingDepth <= 0)
            return;

        foreach (ChainSegment member in builderMembers)
        {
            var next = new List<ChainSegment>(prefix.Count + 1);
            next.AddRange(prefix);
            next.Add(member);

            added += AddRuntimeChainClasses(uniqueLines, runtimeRoots, rootName, next);
            ExpandRuntimeBuilderChains(uniqueLines, runtimeRoots, rootName, next, builderMembers, remainingDepth - 1, ref added);
        }
    }

    private static int AddRuntimeChainClasses(HashSet<string> uniqueLines, Dictionary<string, Type> runtimeRoots, string rootName, List<ChainSegment> segments)
    {
        if (!TryEvaluateRuntimeChain(runtimeRoots, rootName, segments, out List<string>? classes, out _))
            return 0;

        if (classes is null || classes.Count == 0)
            return 0;

        var set = new HashSet<string>(classes, StringComparer.Ordinal);
        return AddManifestClasses(uniqueLines, set, responsive: false);
    }

    private static void CollectRuntimeRootChains(Type containerType, List<ChainSegment> prefix, List<List<ChainSegment>> results)
    {
        foreach (Type nestedType in containerType.GetNestedTypes(BindingFlags.Public))
        {
            if (!IsStaticContainerType(nestedType))
                continue;

            var nestedPrefix = new List<ChainSegment>(prefix.Count + 1);
            nestedPrefix.AddRange(prefix);
            nestedPrefix.Add(new ChainSegment(nestedType.Name, new List<string>()));
            CollectRuntimeRootChains(nestedType, nestedPrefix, results);
        }

        foreach (PropertyInfo property in containerType.GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod is null || !typeof(ICssBuilder).IsAssignableFrom(property.PropertyType))
                continue;

            var chain = new List<ChainSegment>(prefix.Count + 1);
            chain.AddRange(prefix);
            chain.Add(new ChainSegment(property.Name, new List<string>()));
            results.Add(chain);
        }

        foreach (MethodInfo method in containerType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.IsSpecialName || method.GetParameters().Length != 0 || !typeof(ICssBuilder).IsAssignableFrom(method.ReturnType))
                continue;

            var chain = new List<ChainSegment>(prefix.Count + 1);
            chain.AddRange(prefix);
            chain.Add(new ChainSegment(method.Name, new List<string>()));
            results.Add(chain);
        }
    }

    private static List<ChainSegment> GetRuntimeBuilderSegments(Type builderType)
    {
        var results = new List<ChainSegment>();

        foreach (PropertyInfo property in builderType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod is null || property.PropertyType != builderType)
                continue;

            results.Add(new ChainSegment(property.Name, new List<string>()));
        }

        foreach (MethodInfo method in builderType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.IsSpecialName || method.GetParameters().Length != 0 || method.ReturnType != builderType)
                continue;

            results.Add(new ChainSegment(method.Name, new List<string>()));
        }

        results.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return results;
    }

    private static bool TryEvaluateRuntimeChain(Dictionary<string, Type> runtimeRoots, string root, List<ChainSegment> segments, out List<string>? classes,
        out Type? builderType)
    {
        classes = null;
        builderType = null;

        if (!TryEvaluateRuntimeChainObject(runtimeRoots, root, segments, out object? value) || value is not ICssBuilder builder)
            return false;

        builderType = builder.GetType();

        string classValue = builder.ToClass();
        classes = SplitClassList(classValue);
        return classes.Count > 0;
    }

    private static bool TryEvaluateRuntimeChainObject(Dictionary<string, Type> runtimeRoots, string root, List<ChainSegment> segments, out object? value)
    {
        value = null;

        if (!runtimeRoots.TryGetValue(root, out Type? rootType))
            return false;

        object current = rootType;

        foreach (ChainSegment segment in segments)
        {
            if (current is Type staticType)
            {
                if (TryResolveStaticSegment(staticType, segment, out object? nextStatic))
                {
                    current = nextStatic!;
                    continue;
                }

                return false;
            }

            Type instanceType = current.GetType();

            if (TryResolveInstanceSegment(current, instanceType, segment, out object? nextInstance))
            {
                current = nextInstance!;
                continue;
            }

            return false;
        }

        value = current;
        return true;
    }

    private static bool TryResolveStaticSegment(Type type, ChainSegment segment, out object? value)
    {
        value = null;

        if (segment.Args.Count == 0)
        {
            Type? nestedType = type.GetNestedType(segment.Name, BindingFlags.Public);

            if (nestedType is not null)
            {
                value = nestedType;
                return true;
            }

            PropertyInfo? property = type.GetProperty(segment.Name, BindingFlags.Public | BindingFlags.Static);

            if (property?.GetMethod is not null)
            {
                value = property.GetValue(null);
                return value is not null;
            }
        }

        return TryInvokeMethod(null, type, segment, BindingFlags.Public | BindingFlags.Static, out value);
    }

    private static bool TryResolveInstanceSegment(object instance, Type type, ChainSegment segment, out object? value)
    {
        value = null;

        if (segment.Args.Count == 0)
        {
            PropertyInfo? property = type.GetProperty(segment.Name, BindingFlags.Public | BindingFlags.Instance);

            if (property?.GetMethod is not null)
            {
                value = property.GetValue(instance);
                return value is not null;
            }
        }

        return TryInvokeMethod(instance, type, segment, BindingFlags.Public | BindingFlags.Instance, out value);
    }

    private static bool TryInvokeMethod(object? instance, Type type, ChainSegment segment, BindingFlags bindingFlags, out object? value)
    {
        foreach (MethodInfo method in type.GetMethods(bindingFlags))
        {
            if (method.IsSpecialName || !string.Equals(method.Name, segment.Name, StringComparison.Ordinal))
                continue;

            ParameterInfo[] parameters = method.GetParameters();

            if (parameters.Length != segment.Args.Count)
                continue;

            var args = new object?[parameters.Length];
            var supported = true;

            for (var i = 0; i < parameters.Length; i++)
            {
                if (TryConvertRuntimeArgument(segment.Args[i], parameters[i].ParameterType, out object? converted))
                {
                    args[i] = converted;
                    continue;
                }

                supported = false;
                break;
            }

            if (!supported)
                continue;

            try
            {
                value = method.Invoke(instance, args);
                return value is not null;
            }
            catch
            {
                // Ignore unsupported runtime paths and let fallback logic decide if it can handle the chain.
            }
        }

        value = null;
        return false;
    }

    private static bool TryConvertRuntimeArgument(string arg, Type parameterType, out object? value)
    {
        value = null;
        Type targetType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;

        if (targetType == typeof(string))
        {
            value = ParseQuotedTokenLiteral(arg);
            return value is not null;
        }

        if (TryResolveStaticMemberExpression(arg, targetType, out object? resolved))
        {
            value = resolved;
            return true;
        }

        return false;
    }

    private static bool TryResolveStaticMemberExpression(string expression, Type targetType, out object? value)
    {
        value = null;
        expression = expression.Trim();

        if (expression.Length == 0)
            return false;

        string[] parts = expression.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
            return false;

        Type? type = FindRuntimeType(parts[0], targetType.Assembly);

        if (type is null)
            return false;

        object? current = null;
        Type currentType = type;

        for (var i = 1; i < parts.Length; i++)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;

            PropertyInfo? property = currentType.GetProperty(parts[i], flags);

            if (property?.GetMethod is not null)
            {
                current = property.GetValue(null);

                if (current is null)
                    return false;

                currentType = current.GetType();
                continue;
            }

            FieldInfo? field = currentType.GetField(parts[i], flags);

            if (field is not null)
            {
                current = field.GetValue(null);

                if (current is null)
                    return false;

                currentType = current.GetType();
                continue;
            }

            return false;
        }

        if (current is null)
            return false;

        if (!targetType.IsAssignableFrom(current.GetType()))
            return false;

        value = current;
        return true;
    }

    private static Type? FindRuntimeType(string typeName, Assembly assembly)
    {
        foreach (Type type in assembly.GetExportedTypes())
        {
            if (string.Equals(type.Name, typeName, StringComparison.Ordinal))
                return type;
        }

        return null;
    }

    private static List<string> SplitClassList(string classList)
    {
        var result = new List<string>();

        if (classList.IsNullOrWhiteSpace())
            return result;

        ReadOnlySpan<char> span = classList.AsSpan();
        var i = 0;

        while (i < span.Length)
        {
            while (i < span.Length && char.IsWhiteSpace(span[i]))
                i++;

            int start = i;

            while (i < span.Length && !char.IsWhiteSpace(span[i]))
                i++;

            if (i > start)
            {
                string value = span[start..i].ToString();

                if (!IgnoredRuntimeClassTokens.Contains(value))
                    result.Add(value);
            }
        }

        return result;
    }

    private static bool IsStaticContainerType(Type type)
    {
        return type.IsClass && type.IsAbstract && type.IsSealed;
    }

    private static IEnumerable<(string Root, List<ChainSegment> Segments)> EnumerateFluentChains(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsIdentifierStart(text[i]))
                continue;

            int rootStart = i;
            int rootEnd = ReadIdentifier(text, i);
            int cursor = SkipWhitespace(text, rootEnd);

            if (cursor >= text.Length || text[cursor] != '.')
                continue;

            var segments = new List<ChainSegment>(4);
            int end = cursor;

            while (cursor < text.Length && text[cursor] == '.')
            {
                cursor++;
                cursor = SkipWhitespace(text, cursor);

                if (cursor >= text.Length || !IsIdentifierStart(text[cursor]))
                    break;

                int nameStart = cursor;
                int nameEnd = ReadIdentifier(text, cursor);
                string name = text.Substring(nameStart, nameEnd - nameStart);
                cursor = SkipWhitespace(text, nameEnd);
                var args = new List<string>(2);

                if (cursor < text.Length && text[cursor] == '(')
                {
                    if (!TryReadParenthesized(text, cursor, out string? argsText, out int closeIndex))
                        break;

                    args = SplitArguments(argsText!);
                    cursor = closeIndex + 1;
                }

                segments.Add(new ChainSegment(name, args));
                end = cursor;
                cursor = SkipWhitespace(text, cursor);
            }

            if (segments.Count > 0)
                yield return (text.Substring(rootStart, rootEnd - rootStart), segments);

            i = Math.Max(i, end - 1);
        }
    }

    private static bool TryReadParenthesized(string text, int openParenIndex, out string? value, out int closeIndex)
    {
        value = null;
        closeIndex = -1;

        var depth = 0;
        var inString = false;

        for (int i = openParenIndex; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '"' && (i == 0 || text[i - 1] != '\\'))
            {
                inString = !inString;
            }

            if (inString)
                continue;

            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c != ')')
                continue;

            depth--;

            if (depth != 0)
                continue;

            closeIndex = i;
            value = text.Substring(openParenIndex + 1, i - openParenIndex - 1);
            return true;
        }

        return false;
    }

    private static bool IsIdentifierStart(char c)
    {
        return char.IsLetter(c) || c == '_';
    }

    private static int ReadIdentifier(string text, int index)
    {
        int i = index;

        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            i++;

        return i;
    }

    private static int SkipWhitespace(string text, int index)
    {
        int i = index;

        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;

        return i;
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

    private static string? ParseQuotedTokenLiteral(string arg)
    {
        arg = arg.Trim();

        if (arg.Length >= 2 && arg[0] == '"' && arg[^1] == '"')
            return NormalizeLiteralToken(arg.Substring(1, arg.Length - 2)
                                            .Replace("\\\"", "\"", StringComparison.Ordinal)
                                            .Replace("\"\"", "\"", StringComparison.Ordinal));

        if (arg.Length >= 3 && arg[0] == '@' && arg[1] == '"' && arg[^1] == '"')
            return NormalizeLiteralToken(arg.Substring(2, arg.Length - 3)
                                            .Replace("\"\"", "\"", StringComparison.Ordinal)
                                            .Replace("\\\"", "\"", StringComparison.Ordinal));

        return null;
    }

    private static string NormalizeLiteralToken(string value)
    {
        value = value.Trim();

        while (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value.Substring(1, value.Length - 2)
                         .Trim();
        }

        return value;
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

        if (IsBareArbitraryValue(token))
            return false;

        if (HasUnbracketedDot(token))
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

    private static bool IsBareArbitraryValue(ReadOnlySpan<char> token)
    {
        return token.Length >= 2 && token[0] == '[' && token[^1] == ']';
    }

    private static bool HasUnbracketedDot(ReadOnlySpan<char> token)
    {
        var bracketDepth = 0;

        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];

            switch (c)
            {
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '.':
                    if (bracketDepth == 0)
                        return true;
                    break;
            }
        }

        return false;
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

    private static string ResolveScanMode(Dictionary<string, string> map)
    {
        if (map.TryGetValue("--mode", out string? value) && string.Equals(value, _suiteMode, StringComparison.OrdinalIgnoreCase))
            return _suiteMode;

        return _projectMode;
    }

    private static bool IsSuiteMode(string mode)
    {
        return string.Equals(mode, _suiteMode, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDefaultOutputFileName(string mode)
    {
        return IsSuiteMode(mode) ? _suiteManifestFileName : _projectManifestFileName;
    }

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
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

    private enum InvocationActionKind
    {
        AddBuilderTokenFromArgument,
        AddBuilderTokenLiteral,
        AddUtilityTokenFromArgument,
        AddUtilityTokenLiteral,
        AddLiteralClass,
        AddLastUtilityTokenFromArgument,
        SetPendingBreakpoint,
        MutateLastSide,
        MutateLastDirection
    }

    private readonly record struct InvocationAction(InvocationActionKind Kind, string? Value = null, string? Value2 = null);

    private readonly record struct TailwindBuilderMetadata(string Prefix, bool Responsive, Dictionary<string, InvocationAction> Members);

    private readonly record struct TailwindFacadeMetadata(TailwindBuilderMetadata? Builder, Dictionary<string, InvocationAction> Members);

    private readonly record struct ChainSegment(string Name, List<string> Args);

    private readonly record struct EmittedClass(string? Utility, string? Token, string? RawClass, string? Breakpoint)
    {
        public string ToTailwindClass()
        {
            string className = RawClass.HasContent()
                ? RawClass
                : Token.HasContent()
                    ? $"{Utility}-{Token}"
                    : Utility ?? string.Empty;

            if (!Breakpoint.HasContent())
                return className;

            return $"{Breakpoint}:{className}";
        }
    }

    [GeneratedRegex(
        @"\[(?<attr>[^\]]*TailwindPrefix[^\]]*)\]\s*(?:(?:public|internal|private|protected)\s+)?(?:sealed\s+)?class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b(?<after>[^{]*)\{",
        RegexOptions.Singleline)]
    private static partial Regex ClassWithAttrRegex();

    [GeneratedRegex(@"TailwindPrefix\s*\(\s*""(?<prefix>[^""]+)""(?:\s*,\s*Responsive\s*=\s*(?<resp>true|false))?\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TailwindPrefixArgsRegex();

    [GeneratedRegex(
        @"\s*(?:(?:public|internal|private|protected)\s+)?static\s+class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b(?<after>[^{]*)\{",
        RegexOptions.Singleline)]
    private static partial Regex StaticClassRegex();

    [GeneratedRegex(@"public\s+static\s+(?<builder>[A-Za-z_][A-Za-z0-9_]*)\s+Token\s*\(\s*string\s+token\s*\)\s*=>\s*new\s*\(\s*token\s*\)\s*;",
        RegexOptions.Singleline)]
    private static partial Regex StaticTokenMethodRegex();

    [GeneratedRegex(@"(?<root>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*Token\s*\(\s*(?<token>@?""(?:[^""\\]|\\.|"""")*"")\s*\)",
        RegexOptions.Singleline)]
    private static partial Regex StaticTokenInvocationRegex();

    [GeneratedRegex(
        @"public\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*\((?<params>[^)]*)\))?\s*=>\s*(?<method>[A-Za-z_][A-Za-z0-9_]*)\s*\(\s*(?<args>[^;]*)\)\s*;",
        RegexOptions.Singleline)]
    private static partial Regex BuilderMemberRegex();

    [GeneratedRegex(@"public\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<prop>[A-Za-z_][A-Za-z0-9_]*)\s*=>\s*(?<method>Chain[A-Za-z]*)\s*\(\s*(?<args>[^;]*)\)\s*;",
        RegexOptions.Singleline)]
    private static partial Regex ChainPropRegex();

    [GeneratedRegex(
        @"public\s+static\s+(?<builder>[A-Za-z_][A-Za-z0-9_]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*\((?<params>[^)]*)\))?\s*=>\s*new\s*\(\s*(?<args>[^;]*)\)\s*;",
        RegexOptions.Singleline)]
    private static partial Regex StaticBuilderMemberRegex();

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

        if (projectParent.HasContent() && string.Equals(Path.GetFileName(projectParent), "src", StringComparison.OrdinalIgnoreCase))
        {
            string? projectGrandparent = Path.GetDirectoryName(projectParent);

            if (projectGrandparent.HasContent() && await _directoryUtil.Exists(projectGrandparent, cancellationToken)
                                                                        .NoSync())
                sourceRoots.Add(projectGrandparent);
        }

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
        var csSources = new List<(string File, string Text)>();
        var razorSources = new List<(string File, string Text)>();

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
        }

        Dictionary<string, TailwindBuilderMetadata> builders = CollectBuilderMetadata(csSources);
        Dictionary<string, TailwindFacadeMetadata> tokenFacades = CollectTokenFacadeMetadata(csSources, builders);

        foreach ((string file, string text) in csSources)
        {
            ProcessCsFile(file, text, uniqueLines, tokenFacades, ref tailwindPrefixClasses, ref componentCodeClasses);
        }

        foreach ((string file, string text) in razorSources)
        {
            ProcessRazorFile(file, text, uniqueLines, tokenFacades, ref razorClasses);
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

    private void ProcessCsFile(string file, string text, HashSet<string> uniqueLines, Dictionary<string, TailwindFacadeMetadata> tokenFacades,
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

        AddFluentInvocationClasses(file, text, tokenFacades, uniqueLines, ref componentCodeClasses);
    }

    private void ProcessRazorFile(string file, string text, HashSet<string> uniqueLines, Dictionary<string, TailwindFacadeMetadata> tokenFacades,
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

        AddFluentInvocationClasses(file, text, tokenFacades, uniqueLines, ref razorClasses);
    }

    private static Dictionary<string, TailwindBuilderMetadata> CollectBuilderMetadata(List<(string File, string Text)> csSources)
    {
        var result = new Dictionary<string, TailwindBuilderMetadata>(StringComparer.Ordinal);

        foreach ((_, string text) in csSources)
        {
            foreach (Match match in ClassWithAttrRegex().Matches(text))
            {
                Match attrMatch = TailwindPrefixArgsRegex().Match(match.Groups["attr"].Value);

                if (!attrMatch.Success)
                    continue;

                string prefix = attrMatch.Groups["prefix"].Value;
                bool responsive = !attrMatch.Groups["resp"].Success || !bool.TryParse(attrMatch.Groups["resp"].Value, out bool parsedResponsive) ||
                                  parsedResponsive;

                int braceIdx = match.Index + match.Length - 1;
                string? body = TryGetClassBody(text, braceIdx);
                var members = body is null
                    ? new Dictionary<string, InvocationAction>(StringComparer.Ordinal)
                    : CollectBuilderMemberActions(match.Groups["name"].Value, body);

                result[match.Groups["name"].Value] = new TailwindBuilderMetadata(prefix, responsive, members);
            }
        }

        return result;
    }

    private static Dictionary<string, TailwindFacadeMetadata> CollectTokenFacadeMetadata(List<(string File, string Text)> csSources,
        Dictionary<string, TailwindBuilderMetadata> builders)
    {
        var result = new Dictionary<string, TailwindFacadeMetadata>(StringComparer.Ordinal);

        foreach ((_, string text) in csSources)
        {
            foreach (Match match in StaticClassRegex().Matches(text))
            {
                string facadeName = match.Groups["name"].Value;
                int braceIdx = match.Index + match.Length - 1;
                string? body = TryGetClassBody(text, braceIdx);

                if (body is null)
                    continue;

                var members = new Dictionary<string, InvocationAction>(StringComparer.Ordinal);
                TailwindBuilderMetadata? builderMetadata = null;

                foreach (Match memberMatch in StaticBuilderMemberRegex().Matches(body))
                {
                    string builderName = memberMatch.Groups["builder"].Value;

                    if (!builders.TryGetValue(builderName, out TailwindBuilderMetadata metadata))
                        continue;

                    if (TryCreateFacadeAction(memberMatch.Groups["params"].Value, SplitArguments(memberMatch.Groups["args"].Value), metadata, out InvocationAction action))
                    {
                        members[memberMatch.Groups["name"].Value] = action;
                        builderMetadata ??= metadata;
                    }
                }

                if (members.Count > 0)
                    result[facadeName] = new TailwindFacadeMetadata(builderMetadata, members);
            }
        }

        return result;
    }

    private void AddFluentInvocationClasses(string file, string text, Dictionary<string, TailwindFacadeMetadata> tokenFacades,
        HashSet<string> uniqueLines, ref int count)
    {
        var classNames = new HashSet<string>(StringComparer.Ordinal);

        foreach ((string root, List<ChainSegment> segments) in EnumerateFluentChains(text))
        {
            if (!tokenFacades.TryGetValue(root, out TailwindFacadeMetadata facade))
                continue;

            if (!ContainsTokenSegment(segments))
                continue;

            if (!TryEvaluateChain(facade, segments, out List<string>? classes))
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

    private static bool ContainsTokenSegment(List<ChainSegment> segments)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            if (string.Equals(segments[i].Name, "Token", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static Dictionary<string, InvocationAction> CollectBuilderMemberActions(string className, string body)
    {
        var result = new Dictionary<string, InvocationAction>(StringComparer.Ordinal);

        foreach (Match match in BuilderMemberRegex().Matches(body))
        {
            if (!string.Equals(match.Groups["type"].Value, className, StringComparison.Ordinal))
                continue;

            if (TryCreateBuilderAction(match.Groups["params"].Value, match.Groups["method"].Value, SplitArguments(match.Groups["args"].Value),
                    out InvocationAction action))
            {
                result[match.Groups["name"].Value] = action;
            }
        }

        return result;
    }

    private static bool TryCreateBuilderAction(string parameters, string methodName, List<string> args, out InvocationAction action)
    {
        action = default;
        string? parameterName = GetSingleParameterName(parameters);

        if (string.Equals(methodName, "SetPendingBreakpoint", StringComparison.Ordinal) ||
            string.Equals(methodName, "ChainBreakpoint", StringComparison.Ordinal))
        {
            string? breakpoint = args.Count > 0 ? ParseBreakpoint(args[0]) : null;

            if (breakpoint is null && args.Count > 0 && IsBaseBreakpoint(args[0]))
                breakpoint = string.Empty;

            if (breakpoint is null)
                return false;

            action = new InvocationAction(InvocationActionKind.SetPendingBreakpoint, breakpoint);
            return true;
        }

        if (string.Equals(methodName, "AddRule", StringComparison.Ordinal))
        {
            string? side = args.Count > 0 ? ParseElementSide(args[0]) : null;

            if (!side.HasContent() && !IsAllElementSide(args.Count > 0 ? args[0] : null))
                return false;

            action = new InvocationAction(InvocationActionKind.MutateLastSide, side ?? string.Empty);
            return true;
        }

        if (string.Equals(methodName, "ChainWithDirection", StringComparison.Ordinal))
        {
            string? direction = args.Count > 0 ? ParseQuotedTokenLiteral(args[0]) : null;

            if (!direction.HasContent())
                return false;

            action = new InvocationAction(InvocationActionKind.MutateLastDirection, direction);
            return true;
        }

        if (IsBuilderTokenMethod(methodName))
        {
            if (args.Count == 0)
                return false;

            if (parameterName.HasContent() && string.Equals(args[0].Trim(), parameterName, StringComparison.Ordinal))
            {
                action = new InvocationAction(InvocationActionKind.AddBuilderTokenFromArgument);
                return true;
            }

            string? literalToken = ParseTokenLiteral(args[0]);

            if (!literalToken.HasContent())
                return false;

            action = new InvocationAction(InvocationActionKind.AddBuilderTokenLiteral, literalToken);
            return true;
        }

        if (!string.Equals(methodName, "Chain", StringComparison.Ordinal))
            return false;

        if (args.Count < 2)
            return false;

        string utilityExpr = args[0].Trim();
        string valueExpr = args[1].Trim();

        if (parameterName.HasContent() && string.Equals(valueExpr, parameterName, StringComparison.Ordinal))
        {
            string? utilityLiteral = ParseQuotedTokenLiteral(utilityExpr);

            if (utilityLiteral.HasContent())
            {
                action = new InvocationAction(InvocationActionKind.AddUtilityTokenFromArgument, utilityLiteral);
                return true;
            }

            string? fallbackUtility = ParseFallbackUtilityExpression(utilityExpr);

            if (fallbackUtility.HasContent())
            {
                action = new InvocationAction(InvocationActionKind.AddLastUtilityTokenFromArgument, fallbackUtility);
                return true;
            }

            return false;
        }

        string? utility = ParseQuotedTokenLiteral(utilityExpr);
        string? value = ParseTokenLiteral(valueExpr);

        if (!utility.HasContent())
            return false;

        action = new InvocationAction(InvocationActionKind.AddUtilityTokenLiteral, utility, value ?? string.Empty);
        return true;
    }

    private static bool TryCreateFacadeAction(string parameters, List<string> args, TailwindBuilderMetadata metadata, out InvocationAction action)
    {
        action = default;
        string? parameterName = GetSingleParameterName(parameters);

        if (args.Count == 1)
        {
            string arg = args[0].Trim();

            if (parameterName.HasContent() && string.Equals(arg, parameterName, StringComparison.Ordinal))
            {
                action = new InvocationAction(InvocationActionKind.AddBuilderTokenFromArgument);
                return true;
            }

            string? literalToken = ParseTokenLiteral(arg);

            if (!literalToken.HasContent())
                return false;

            if (LooksLikeFullClassLiteral(literalToken, metadata.Prefix))
            {
                action = new InvocationAction(InvocationActionKind.AddLiteralClass, literalToken);
                return true;
            }

            action = new InvocationAction(InvocationActionKind.AddBuilderTokenLiteral, literalToken);
            return true;
        }

        if (args.Count != 2)
            return false;

        string? utility = ParseQuotedTokenLiteral(args[0]);

        if (!utility.HasContent())
            return false;

        string valueExpr = args[1].Trim();

        if (parameterName.HasContent() && string.Equals(valueExpr, parameterName, StringComparison.Ordinal))
        {
            action = new InvocationAction(InvocationActionKind.AddUtilityTokenFromArgument, utility);
            return true;
        }

        string? literalValue = ParseTokenLiteral(valueExpr);

        if (!literalValue.HasContent())
            return false;

        action = new InvocationAction(InvocationActionKind.AddUtilityTokenLiteral, utility, literalValue);
        return true;
    }

    private static bool TryEvaluateChain(TailwindFacadeMetadata facade, List<ChainSegment> segments, out List<string>? classes)
    {
        classes = null;

        if (segments.Count == 0)
            return false;

        var emitted = new List<EmittedClass>(4);
        TailwindBuilderMetadata? builder = facade.Builder;
        string? pendingBreakpoint = null;

        foreach (ChainSegment segment in segments)
        {
            InvocationAction action;

            if (facade.Members.TryGetValue(segment.Name, out InvocationAction facadeAction))
            {
                action = facadeAction;
            }
            else if (builder is TailwindBuilderMetadata builderMetadata && builderMetadata.Members.TryGetValue(segment.Name, out InvocationAction builderAction))
            {
                action = builderAction;
            }
            else
            {
                return false;
            }

            if (!TryApplyInvocationAction(action, builder, segment.Args, emitted, ref pendingBreakpoint))
                return false;
        }

        if (emitted.Count == 0)
            return false;

        classes = new List<string>(emitted.Count);

        foreach (EmittedClass item in emitted)
        {
            string value = item.ToTailwindClass();

            if (value.HasContent())
                classes.Add(value);
        }

        return classes.Count > 0;
    }

    private static bool TryApplyInvocationAction(InvocationAction action, TailwindBuilderMetadata? builder, List<string> args, List<EmittedClass> emitted,
        ref string? pendingBreakpoint)
    {
        switch (action.Kind)
        {
            case InvocationActionKind.AddBuilderTokenFromArgument:
            {
                if (builder is not TailwindBuilderMetadata builderMetadata || args.Count == 0)
                    return false;

                string? token = ParseQuotedTokenLiteral(args[0]);

                if (!token.HasContent())
                    return false;

                emitted.Add(CreateBuilderTokenClass(builderMetadata.Prefix, token, pendingBreakpoint));
                pendingBreakpoint = null;
                return true;
            }
            case InvocationActionKind.AddBuilderTokenLiteral:
            {
                if (builder is not TailwindBuilderMetadata builderMetadata || !action.Value.HasContent())
                    return false;

                emitted.Add(CreateBuilderTokenClass(builderMetadata.Prefix, action.Value, pendingBreakpoint));
                pendingBreakpoint = null;
                return true;
            }
            case InvocationActionKind.AddUtilityTokenFromArgument:
            {
                if (!action.Value.HasContent() || args.Count == 0)
                    return false;

                string? token = ParseQuotedTokenLiteral(args[0]);

                if (!token.HasContent())
                    return false;

                emitted.Add(new EmittedClass(action.Value, token, null, pendingBreakpoint));
                pendingBreakpoint = null;
                return true;
            }
            case InvocationActionKind.AddUtilityTokenLiteral:
            {
                if (!action.Value.HasContent())
                    return false;

                emitted.Add(new EmittedClass(action.Value, action.Value2, null, pendingBreakpoint));
                pendingBreakpoint = null;
                return true;
            }
            case InvocationActionKind.AddLiteralClass:
            {
                if (!action.Value.HasContent())
                    return false;

                emitted.Add(new EmittedClass(null, null, action.Value, pendingBreakpoint));
                pendingBreakpoint = null;
                return true;
            }
            case InvocationActionKind.AddLastUtilityTokenFromArgument:
            {
                if (args.Count == 0)
                    return false;

                string? token = ParseQuotedTokenLiteral(args[0]);

                if (!token.HasContent())
                    return false;

                string? utility = emitted.Count > 0 ? emitted[^1].Utility : action.Value;

                if (!utility.HasContent())
                    return false;

                emitted.Add(new EmittedClass(utility, token, null, pendingBreakpoint));
                pendingBreakpoint = null;
                return true;
            }
            case InvocationActionKind.SetPendingBreakpoint:
                pendingBreakpoint = action.Value ?? string.Empty;
                return true;
            case InvocationActionKind.MutateLastSide:
            {
                if (emitted.Count == 0)
                    return false;

                emitted[^1] = ApplySideMutation(emitted[^1], action.Value ?? string.Empty, pendingBreakpoint);
                pendingBreakpoint = null;
                return true;
            }
            case InvocationActionKind.MutateLastDirection:
            {
                if (emitted.Count == 0)
                    return false;

                emitted[^1] = ApplyDirectionMutation(emitted[^1], action.Value, pendingBreakpoint);
                pendingBreakpoint = null;
                return true;
            }
            default:
                return false;
        }
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

    private static EmittedClass CreateBuilderTokenClass(string prefix, string token, string? breakpoint)
    {
        if (prefix.IsNullOrWhiteSpace())
            return new EmittedClass(null, null, token, breakpoint);

        string utility = prefix.TrimEnd('-', ':');

        if (!utility.HasContent())
            return new EmittedClass(null, null, BuildPrefixedClass(prefix, token), breakpoint);

        return new EmittedClass(utility, token, null, breakpoint);
    }

    private static EmittedClass ApplySideMutation(EmittedClass emitted, string side, string? pendingBreakpoint)
    {
        string breakpoint = pendingBreakpoint ?? emitted.Breakpoint ?? string.Empty;

        if (!emitted.Utility.HasContent())
            return emitted with { Breakpoint = breakpoint };

        if (!side.HasContent())
            return emitted with { Breakpoint = breakpoint };

        return emitted with
        {
            Utility = ApplySideToUtility(emitted.Utility, side),
            Breakpoint = breakpoint
        };
    }

    private static EmittedClass ApplyDirectionMutation(EmittedClass emitted, string? direction, string? pendingBreakpoint)
    {
        string breakpoint = pendingBreakpoint ?? emitted.Breakpoint ?? string.Empty;

        if (!emitted.Utility.HasContent())
            return emitted with { Breakpoint = breakpoint };

        string? utility = direction switch
        {
            "column" => "gap-x",
            "row" => "gap-y",
            _ => emitted.Utility
        };

        return emitted with
        {
            Utility = utility,
            Breakpoint = breakpoint
        };
    }

    private static string ApplySideToUtility(string utility, string side)
    {
        int lastDash = utility.LastIndexOf('-');
        string suffix = lastDash >= 0 ? utility.Substring(lastDash + 1) : utility;

        if (suffix.Length == 1)
            return utility + side;

        return utility + "-" + side;
    }

    private static bool IsBuilderTokenMethod(string methodName)
    {
        return string.Equals(methodName, "ChainSize", StringComparison.Ordinal) ||
               string.Equals(methodName, "ChainWithSize", StringComparison.Ordinal) ||
               string.Equals(methodName, "ChainValue", StringComparison.Ordinal) ||
               string.Equals(methodName, "ChainWithValue", StringComparison.Ordinal);
    }

    private static string? ParseTokenLiteral(string arg)
    {
        string? token = ParseQuotedTokenLiteral(arg);

        if (token.HasContent())
            return token;

        arg = arg.Trim();

        if (!arg.StartsWith("ScaleType.Is", StringComparison.Ordinal))
            return null;

        string value = arg.Substring("ScaleType.Is".Length);

        if (value.EndsWith("Value", StringComparison.Ordinal))
            value = value.Substring(0, value.Length - "Value".Length);

        return value.ToLowerInvariant();
    }

    private static string? ParseBreakpoint(string arg)
    {
        arg = arg.Trim();

        return arg switch
        {
            "BreakpointType.Base" => string.Empty,
            "BreakpointType.Sm" => "sm",
            "BreakpointType.Md" => "md",
            "BreakpointType.Lg" => "lg",
            "BreakpointType.Xl" => "xl",
            "BreakpointType.Xxl" => "2xl",
            _ => null
        };
    }

    private static bool IsBaseBreakpoint(string? arg)
    {
        return string.Equals(arg?.Trim(), "BreakpointType.Base", StringComparison.Ordinal);
    }

    private static string? ParseElementSide(string arg)
    {
        arg = arg.Trim();

        return arg switch
        {
            "ElementSideType.Top" => "t",
            "ElementSideType.Right" => "e",
            "ElementSideType.Bottom" => "b",
            "ElementSideType.Left" => "s",
            "ElementSideType.Horizontal" => "x",
            "ElementSideType.Vertical" => "y",
            "ElementSideType.InlineStart" => "s",
            "ElementSideType.InlineEnd" => "e",
            _ => null
        };
    }

    private static bool IsAllElementSide(string? arg)
    {
        return string.Equals(arg?.Trim(), "ElementSideType.All", StringComparison.Ordinal);
    }

    private static string? ParseFallbackUtilityExpression(string expression)
    {
        int quoteStart = expression.LastIndexOf('"');

        if (quoteStart < 1)
            return null;

        int quoteEnd = expression.LastIndexOf('"', quoteStart - 1);

        if (quoteEnd < 0)
            return null;

        return expression.Substring(quoteEnd + 1, quoteStart - quoteEnd - 1);
    }

    private static string? GetSingleParameterName(string parameters)
    {
        parameters = parameters.Trim();

        if (parameters.Length == 0 || parameters.IndexOf(',') >= 0)
            return null;

        int lastSpace = parameters.LastIndexOf(' ');

        if (lastSpace < 0 || lastSpace == parameters.Length - 1)
            return null;

        return parameters.Substring(lastSpace + 1)
                         .Trim();
    }

    private static bool LooksLikeFullClassLiteral(string value, string prefix)
    {
        string trimmedPrefix = prefix.TrimEnd('-', ':');

        return trimmedPrefix.HasContent() && value.StartsWith(trimmedPrefix, StringComparison.Ordinal);
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
            return arg.Substring(1, arg.Length - 2)
                      .Replace("\\\"", "\"", StringComparison.Ordinal);

        if (arg.Length >= 3 && arg[0] == '@' && arg[1] == '"' && arg[^1] == '"')
            return arg.Substring(2, arg.Length - 3)
                      .Replace("\"\"", "\"", StringComparison.Ordinal);

        return null;
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
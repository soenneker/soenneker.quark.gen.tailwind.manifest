using Soenneker.Extensions.ValueTask;
using Soenneker.Extensions.Task;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.Tests;

public sealed class ManifestPipelineTests
{
    [Test]
    public async Task Batches_preserve_classes_and_unchanged_outputs(CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Startup.ConfigureServices(services);
        ServiceProvider provider = services.BuildServiceProvider();
        var runner = ActivatorUtilities.CreateInstance<QuarkTailwindManifestGenerator>(provider);
        string root = Path.Combine(Path.GetTempPath(), "quark-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var i = 1; i <= 17; i++)
                await File.WriteAllTextAsync(Path.Combine(root, "Input" + i + (i % 2 == 0 ? ".cs" : ".razor")),
                    "var value = Quark.Width.Token(\"[" + i + "px]\"); var other = TextSize.Sm; service.Call(Quark.TextSize.OnHover.Lg).Value;", cancellationToken: cancellationToken).NoSync();
            Directory.CreateDirectory(Path.Combine(root, "node_modules"));
            await File.WriteAllTextAsync(Path.Combine(root, "node_modules", "Excluded.cs"), "Quark.TextSize.OnHover.Lg;", cancellationToken: cancellationToken).NoSync();
            string output = Path.Combine(root, "manifest.txt");
            string[] args = ["--projectDir", root, "--manifestOutput", output];
            if (await runner.Run(args, cancellationToken).NoSync() != 0)
                throw new Exception("Generation failed.");
            string text = await File.ReadAllTextAsync(output, cancellationToken: cancellationToken).NoSync();
            for (var i = 1; i <= 17; i++)
                if (!text.Contains("w-[" + i + "px]", StringComparison.Ordinal))
                    throw new Exception("A batched source file was missed.");
            if (!text.Contains("text-sm", StringComparison.Ordinal) || text.Contains("hover:text-lg", StringComparison.Ordinal))
                throw new Exception("Fluent root filtering changed.");

            File.SetLastWriteTimeUtc(output, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            DateTime timestamp = File.GetLastWriteTimeUtc(output);
            await runner.Run(args, cancellationToken).NoSync();
            if (await File.ReadAllTextAsync(output, cancellationToken: cancellationToken).NoSync() != text || File.GetLastWriteTimeUtc(output) != timestamp)
                throw new Exception("Unchanged output was rewritten.");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await runner.Run(args, cancellation.Token).NoSync();
                throw new Exception("Cancellation was swallowed.");
            }
            catch (OperationCanceledException) { }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            finally { await provider.DisposeAsync().NoSync(); }
        }
    }
}

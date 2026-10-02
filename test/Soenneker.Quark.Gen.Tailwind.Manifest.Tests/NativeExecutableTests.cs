using Soenneker.Extensions.ValueTask;
using Soenneker.Extensions.Task;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.Tests;

public sealed class NativeExecutableTests
{
    [Test]
    public async Task Native_executable_generates_expected_output_and_preserves_unchanged_files()
    {
        // Native matrix jobs supply the published executable; ordinary managed test runs do not.
        string? executable = Environment.GetEnvironmentVariable("QUARK_NATIVE_TOOL");
        if (string.IsNullOrEmpty(executable)) { TUnit.Core.Skip.Test("Set QUARK_NATIVE_TOOL to run native integration tests."); return; }
        string directory = Path.Combine(Path.GetTempPath(), "quark native " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "Usage.cs"), "var a = Quark.TextSize.Sm; var b = Quark.Rounded.Top.Xl; var c = Quark.Top.Token(\"[calc(var(--header-height)+1rem)]\"); var d = Quark.TextSize.OnFocusVisible.Sm;").NoSync();
            string output = Path.Combine(directory, "manifest.txt");
            string[] arguments = ["--projectDir", directory, "--manifestOutput", output];
            string[] expectedValues = ["text-sm", "rounded-t-xl", "top-[calc(var(--header-height)+1rem)]", "focus-visible:text-sm"];
            async Task Run()
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false };
                foreach (string argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new Exception("Native tool could not start.");
                await process.WaitForExitAsync().NoSync();
                if (process.ExitCode != 0) throw new Exception("Native tool failed: " + process.ExitCode);
            }
            await Run().NoSync();
            string content = await File.ReadAllTextAsync(output).NoSync();
            foreach (string expected in expectedValues)
                if (!content.Contains(expected, StringComparison.Ordinal)) throw new Exception("Native output is missing " + expected);
            DateTime timestamp = File.GetLastWriteTimeUtc(output);
            await Run().NoSync();
            if (File.GetLastWriteTimeUtc(output) != timestamp) throw new Exception("Unchanged native output was rewritten.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

[![](https://img.shields.io/nuget/v/soenneker.quark.gen.tailwind.manifest.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.quark.gen.tailwind.manifest/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.quark.gen.tailwind.manifest/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.quark.gen.tailwind.manifest/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.quark.gen.tailwind.manifest.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.quark.gen.tailwind.manifest/)

# Soenneker.Quark.Gen.Tailwind.Manifest

Defines the quark tailwind manifest generator contract.

## Install

```bash
dotnet add package Soenneker.Quark.Gen.Tailwind.Manifest
```

## Quick start

```csharp
using Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks.Abstract;

IQuarkTailwindManifestGenerator quarkTailwindManifestGenerator = /* resolve from DI */;
var result = await quarkTailwindManifestGenerator.Run("value", default);
```

Runs quark Tailwind Manifest Generator for the Quark Tailwind Manifest Generator.

## What you get

- `IQuarkTailwindManifestGenerator` — Defines the quark tailwind manifest generator contract.
- `Startup` — Represents the startup.
- `BuildTasksCommandLineArgs` — Represents the build tasks command line args.
- `ConsoleHostedService` — Represents the console hosted service.
- `Program` — Represents the program.
- `QuarkTailwindManifestGeneratorGenerator` — Represents the quark tailwind manifest generator generator.

## API at a glance

| API | What it does | Result / important behavior |
| --- | --- | --- |
| `BuildTasksCommandLineArgs.Args` | Gets args. | Gets args. |
| `ConsoleHostedService.StartAsync(cancellationToken)` | Starts the Console Hosted Service and begins its background work. | A task that completes after the Console Hosted Service has started. |
| `ConsoleHostedService.StopAsync(cancellationToken)` | Stops the Console Hosted Service and waits for its background work to finish. | A task that completes after the Console Hosted Service has stopped. |
| `Program.Main(args)` | Runs the application using the supplied command-line arguments. | A task that completes when the application exits. |
| `QuarkTailwindManifestGeneratorGenerator.Initialize(context)` | Initializes the Quark Tailwind Manifest Generator Generator so it is ready for use. | Returns no value; the requested change is complete when the method returns. |

## Practical notes

- Cancellation stops pending work; it does not undo work that has already completed.

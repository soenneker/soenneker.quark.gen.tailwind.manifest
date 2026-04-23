using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks.Abstract;
using Soenneker.Utils.Directory.Registrars;
using Soenneker.Utils.File.Registrars;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks;

public static class Startup
{
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddFileUtilAsSingleton()
                .AddDirectoryUtilAsSingleton();
        services.TryAddSingleton<IQuarkTailwindManifestGenerator, QuarkTailwindManifestGenerator>();
        services.AddHostedService<ConsoleHostedService>();
    }
}
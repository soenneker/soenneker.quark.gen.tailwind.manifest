using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Soenneker.Quark.Gen.Tailwind.Manifest.Demo;
using Soenneker.Quark;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddQuarkSuiteAsScoped();

var host = builder.Build();

await host.Services.LoadQuarkResources();
await host.RunAsync();

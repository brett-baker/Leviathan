using System.IO.Abstractions;
using Leviathan.Diagnostics;
using Leviathan.Diagnostics.Crash;
using Leviathan.Ipc;
using Leviathan.ShellHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const string ProcessName = "Leviathan.ShellHost";

// Tests start a private host on their own pipe with --pipe <name>, so they never meet a real user's host.
var pipeName = args.Length >= 2 && args[0] == "--pipe" ? args[1] : PipeNaming.ShellHostPipe();

// One shell host per pipe, which is one per Windows user. A second copy exits quietly; the UI connects to the first.
using var singleInstance = new Mutex(initiallyOwned: true, $@"Local\{ProcessName}.{pipeName}", out var isFirst);
if (!isFirst)
{
    return 0;
}

var recorder = new FlightRecorder(ProcessName);
var crashStore = new CrashBundleStore(new FileSystem(), CrashBundleStore.DefaultRoot);
var crashBuilder = new CrashReportBuilder(ProcessName, CrashReportBuilder.ReadBuildInfo(typeof(ShellHostService).Assembly), recorder);
using var crashHandlers = new CrashHandlers(crashBuilder, crashStore);
crashHandlers.Install();

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddProvider(new FlightRecorderLoggerProvider(recorder));
builder.Logging.AddProvider(new JsonLinesFileLoggerProvider(JsonLinesFileLoggerProvider.DefaultFolder, ProcessName));
#if DEBUG
builder.Logging.AddConsole();
#endif

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(recorder);
builder.Services.AddSingleton(crashHandlers);

// Two STA threads are enough for menus and previews, which come one at a time from a person; more would only multiply stuck ones.
builder.Services.AddSingleton(sp => new StaPool(2, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<ShellHostService>();
builder.Services.AddSingleton(sp => new ShellPipeService(sp.GetRequiredService<ShellHostService>(), sp.GetRequiredService<ILogger<PipeServer>>(), pipeName));
builder.Services.AddHostedService(sp => sp.GetRequiredService<ShellPipeService>());
builder.Services.AddHostedService<ShellIdleShutdownService>();
builder.Services.AddHostedService<PoolHealthService>();

using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
return Environment.ExitCode;

using System.IO.Abstractions;
using Leviathan.Core.Platform;
using Leviathan.Data;
using Leviathan.Diagnostics;
using Leviathan.Diagnostics.Crash;
using Leviathan.Ipc;
using Leviathan.Media;
using Leviathan.MediaHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const string ProcessName = "Leviathan.Media";

// Tests start a private process on their own pipe and database with --pipe <name> --db <path> --thumbs <folder>, so they never meet the user's.
var pipeName = ArgumentAfter(args, "--pipe") ?? PipeNaming.MediaPipe();
var databasePath = ArgumentAfter(args, "--db") ?? LeviathanDatabase.DefaultPath;
var thumbnailFolder = ArgumentAfter(args, "--thumbs") ?? Path.Combine(DataLocation.Root(), "thumbs");

// One media process per pipe, which is one per Windows user and data folder. A second copy exits quietly; the UI connects to the first.
using var singleInstance = new Mutex(initiallyOwned: true, $@"Local\{ProcessName}.{pipeName}", out var isFirst);
if (!isFirst)
{
    return 0;
}

var recorder = new FlightRecorder(ProcessName);
var crashStore = new CrashBundleStore(new FileSystem(), CrashBundleStore.DefaultRoot);
var crashBuilder = new CrashReportBuilder(ProcessName, CrashReportBuilder.ReadBuildInfo(typeof(MediaHostService).Assembly), recorder);
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
builder.Services.AddSingleton(FfmpegLocator.Create().Locate());
builder.Services.AddSingleton<IMediaMetadataStore>(_ => new SqliteMediaMetadataStore(new LeviathanDatabase(databasePath)));
builder.Services.AddSingleton(sp => new MediaMetadataService(
    new FfprobeRunner(new SystemProcessRunner(), () => sp.GetRequiredService<FfmpegTools>()),
    sp.GetRequiredService<IMediaMetadataStore>(),
    sp.GetRequiredService<TimeProvider>()));

builder.Services.AddSingleton(sp => new ThumbnailService(new ThumbnailMaker(
    new SystemProcessRunner(),
    () => sp.GetRequiredService<FfmpegTools>(),
    new ThumbnailDiskCache(thumbnailFolder))));

// Whatever the previous process was reading when it died is the suspect: it is marked unreadable now, before any request, so one bad
// file cannot make this process crash again and again (spec 9.3, 3.4).
builder.Services.AddSingleton(sp =>
{
    var quarantined = sp.GetRequiredService<MediaMetadataService>().RecoverFromCrash();
    if (quarantined > 0)
    {
        sp.GetRequiredService<ILoggerFactory>().CreateLogger(ProcessName).LogWarning("Quarantined {Count} file(s) that were being read when the last media process stopped", quarantined);
    }

    return new MediaHostService(sp.GetRequiredService<MediaMetadataService>(), sp.GetRequiredService<FfmpegTools>(), quarantined, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ThumbnailService>());
});
builder.Services.AddSingleton(sp => new MediaPipeService(sp.GetRequiredService<MediaHostService>(), sp.GetRequiredService<ILogger<PipeServer>>(), pipeName));
builder.Services.AddHostedService(sp => sp.GetRequiredService<MediaPipeService>());
builder.Services.AddHostedService<MediaIdleShutdownService>();

using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
return Environment.ExitCode;

static string? ArgumentAfter(string[] arguments, string name)
{
    var at = Array.IndexOf(arguments, name);
    return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
}

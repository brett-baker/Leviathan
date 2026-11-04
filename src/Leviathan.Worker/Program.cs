using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Core.Providers;
using Leviathan.Data;
using Leviathan.Diagnostics;
using Leviathan.Diagnostics.Crash;
using Leviathan.Engine.Execution;
using Leviathan.Engine.FolderSizes;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Ipc;
using Leviathan.Providers.Local;
using Leviathan.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

const string ProcessName = "Leviathan.Worker";

// One worker per Windows user. A second copy exits quietly; the UI connects to the first.
using var singleInstance = new Mutex(initiallyOwned: true, $@"Local\{ProcessName}.{PipeNaming.UserHash()}", out var isFirst);
if (!isFirst)
{
    return 0;
}

var recorder = new FlightRecorder(ProcessName);
var crashStore = new CrashBundleStore(new FileSystem(), CrashBundleStore.DefaultRoot);
var crashBuilder = new CrashReportBuilder(ProcessName, CrashReportBuilder.ReadBuildInfo(typeof(WorkerService).Assembly), recorder);
using var crashHandlers = new CrashHandlers(crashBuilder, crashStore);
crashHandlers.Install();

var builder = Host.CreateApplicationBuilder(args);
var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Leviathan");

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddProvider(new FlightRecorderLoggerProvider(recorder));
builder.Logging.AddProvider(new JsonLinesFileLoggerProvider(JsonLinesFileLoggerProvider.DefaultFolder, ProcessName));
#if DEBUG
builder.Logging.AddConsole();
#endif

// Traces: exported over OTLP only when an endpoint is configured (e.g. the standalone Aspire Dashboard in development).
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(ProcessName))
        .WithTracing(tracing => tracing.AddSource("Leviathan.*").AddOtlpExporter());
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(recorder);
builder.Services.AddSingleton(crashHandlers);
builder.Services.AddSingleton<IFileSystem, FileSystem>();

builder.Services.AddSingleton(_ => new LeviathanDatabase(LeviathanDatabase.DefaultPath));
builder.Services.AddSingleton<IFolderSizeStore, SqliteFolderSizeStore>();
builder.Services.AddSingleton<ILocationProvider, LocalLocationProvider>();
builder.Services.AddSingleton(sp => new LocationProviderRegistry(sp.GetServices<ILocationProvider>()));
builder.Services.AddSingleton<FolderSizeService>();
builder.Services.AddSingleton<IJobExecutor, ComputeFolderSizeExecutor>();
builder.Services.AddSingleton<IJobExecutor>(sp => new Leviathan.Engine.Transfers.CopyMoveExecutor(sp.GetRequiredService<IFileSystem>(), planStore: new Leviathan.Engine.Transfers.FileTransferPlanStore(sp.GetRequiredService<IFileSystem>(), Path.Combine(appData, "plans")), identities: sp.GetRequiredService<Leviathan.Core.Resilience.ServerIdentityMap>(), copier: new Leviathan.Interop.WindowsFileCopier(), bandwidth: sp.GetRequiredService<Leviathan.Engine.Execution.BandwidthController>(), journal: new Leviathan.Engine.Transfers.FileTransferJournal(sp.GetRequiredService<IFileSystem>(), Path.Combine(appData, "progress"))));
builder.Services.AddSingleton<IJobExecutor>(sp => new Leviathan.Engine.Transfers.RestoreExecutor(sp.GetRequiredService<IFileSystem>()));
builder.Services.AddSingleton<Leviathan.Core.Platform.IRecycleBin, Leviathan.Interop.WindowsRecycleBin>();
builder.Services.AddSingleton<IJobExecutor>(sp => new Leviathan.Engine.Transfers.DeleteExecutor(sp.GetRequiredService<IFileSystem>(), sp.GetRequiredService<Leviathan.Core.Platform.IRecycleBin>(), sp.GetRequiredService<TimeProvider>()));

builder.Services.AddSingleton(_ => new JobScheduler(new ConcurrencyLimits()));
builder.Services.AddSingleton<IJobJournal>(sp => new FileJobJournal(sp.GetRequiredService<IFileSystem>(), Path.Combine(appData, "jobs.journal")));
builder.Services.AddSingleton<Leviathan.Engine.Execution.IJobReportStore>(sp => new Leviathan.Engine.Execution.FileJobReportStore(sp.GetRequiredService<IFileSystem>(), Path.Combine(appData, "reports")));
builder.Services.AddSingleton<JobHost>();
builder.Services.AddSingleton<Leviathan.Core.Resilience.IHostAddressResolver, Leviathan.Core.Resilience.DnsHostAddressResolver>();
builder.Services.AddSingleton<Leviathan.Core.Resilience.ServerIdentityMap>();
builder.Services.AddSingleton<Leviathan.Engine.Execution.BandwidthController>();
builder.Services.AddSingleton<WorkerHub>();
builder.Services.AddSingleton<WorkerService>();

builder.Services.AddHostedService<JobHostService>();
builder.Services.AddSingleton<PipeServerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PipeServerService>());
builder.Services.AddHostedService<IdleShutdownService>();

using var host = builder.Build();

// Build the hub eagerly so it subscribes to job events before the first client connects.
_ = host.Services.GetRequiredService<WorkerHub>();

await host.RunAsync().ConfigureAwait(false);
return 0;

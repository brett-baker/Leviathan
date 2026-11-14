using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Leviathan.Contracts;
using Leviathan.Core.Platform;
using Leviathan.Engine.Elevation;

namespace Leviathan.Interop.Tests;

/// <summary>Asking the administrator helper to do one action (spec §15.6), with a pretend helper on the other end of a real pipe.</summary>
public sealed class ElevatedHelperTests
{
    private sealed class PretendHelper(ElevatedResult? answer, LaunchResult? launch = null) : IProcessStarter
    {
        public ProcessStartInfo? Info { get; private set; }

        public Task<LaunchResult> StartAsync(ProcessStartInfo info, CancellationToken cancellationToken)
        {
            Info = info;
            if (launch is { Started: false } refused)
            {
                return Task.FromResult(refused);
            }

            var pipe = info.Arguments.Split(' ')[1];
            _ = Task.Run(
                async () =>
                {
                    await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out);
                    await client.ConnectAsync(5000, CancellationToken.None);
                    await using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                    await writer.WriteLineAsync(answer is null ? "{broken" : ElevatedWork.EncodeResult(answer));
                },
                CancellationToken.None);
            return Task.FromResult(LaunchResult.Ok);
        }
    }

    private static JobRequest Delete() => new(JobKind.Delete, [@"C:\Windows\Temp\x.txt"], null);

    [Fact]
    public async Task TheHelper_IsStartedWithTheRunAsVerb_AndItsAnswerComesBack()
    {
        var starter = new PretendHelper(new ElevatedResult(JobState.Completed, null, [new ItemOutcome(@"C:\Windows\Temp\x.txt", null, ItemStatus.Done)]));

        var result = await new ElevatedHelper(starter, @"C:\App\Leviathan.Elevated.exe").RunAsync(Delete(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("runas", starter.Info!.Verb);
        Assert.True(starter.Info.UseShellExecute);
        Assert.Equal(@"C:\App\Leviathan.Elevated.exe", starter.Info.FileName);
        Assert.Contains("--request ", starter.Info.Arguments);
    }

    [Fact]
    public async Task ADeclinedPrompt_MeansNothingRan_AndSaysSo()
    {
        var starter = new PretendHelper(null, new LaunchResult(false, null, Cancelled: true));

        var result = await new ElevatedHelper(starter, @"C:\App\Leviathan.Elevated.exe").RunAsync(Delete(), CancellationToken.None);

        Assert.False(result.Ran);
        Assert.Contains("not given", result.Summary);
    }

    [Fact]
    public async Task AnAnswerThatCannotBeRead_IsAFailure_NotACrash()
    {
        var result = await new ElevatedHelper(new PretendHelper(null), @"C:\App\Leviathan.Elevated.exe").RunAsync(Delete(), CancellationToken.None);

        Assert.False(result.Ran);
    }

    [Fact]
    public async Task ARequestThatIsNotAllowed_NeverStartsTheHelper()
    {
        var starter = new PretendHelper(null);

        var result = await new ElevatedHelper(starter, @"C:\App\Leviathan.Elevated.exe").RunAsync(new JobRequest(JobKind.Extract, [@"C:\a.zip"], @"C:\x"), CancellationToken.None);

        Assert.False(result.Ran);
        Assert.Null(starter.Info);
    }
}

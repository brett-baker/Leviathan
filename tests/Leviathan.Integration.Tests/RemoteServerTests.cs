using System.Diagnostics;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Providers.Remote;

namespace Leviathan.Integration.Tests;

/// <summary>
/// The SFTP and FTP providers against real servers in Docker containers (spec §7.1, §19): <c>atmoz/sftp</c> and <c>delfer/alpine-ftp-server</c>. Skipped, with the reason, when
/// Docker or the images are missing; set LEVIATHAN_NO_DOCKER=1 to skip them on purpose. The containers are named for the test run and always removed.
/// </summary>
public sealed class RemoteServerTests : IAsyncLifetime
{
    private const string User = "leviathan";
    private const string Password = "s3cretpass";
    private readonly List<string> _containers = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var container in _containers)
        {
            await Docker($"rm -f {container}", TimeSpan.FromSeconds(30));
        }
    }

    private sealed class MemorySecrets : IRemoteSecrets
    {
        public Dictionary<string, string?> Saved { get; } = [];

        public string? Get(RemoteEndpoint endpoint) => Saved.GetValueOrDefault(endpoint.Key);

        public void Set(RemoteEndpoint endpoint, string? secret) => Saved[endpoint.Key] = secret;
    }

    private sealed class MemoryHosts : IKnownHosts
    {
        public Dictionary<string, string> Saved { get; } = [];

        public string? Get(string endpointKey) => Saved.GetValueOrDefault(endpointKey);

        public void Set(string endpointKey, string fingerprint) => Saved[endpointKey] = fingerprint;
    }

    private static async Task<(int ExitCode, string Output)> Docker(string arguments, TimeSpan timeout)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null)
            {
                return (-1, string.Empty);
            }

            using var cts = new CancellationTokenSource(timeout);
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var error = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            return (process.ExitCode, (output + error).Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or IOException)
        {
            return (-1, ex.Message);
        }
    }

    private async Task<string> StartAsync(string image, string options, string containerArguments = "")
    {
        if (Environment.GetEnvironmentVariable("LEVIATHAN_NO_DOCKER") == "1")
        {
            Assert.Skip("LEVIATHAN_NO_DOCKER=1");
        }

        var (haveImage, _) = await Docker($"image inspect {image}", TimeSpan.FromSeconds(20));
        if (haveImage != 0)
        {
            Assert.Skip($"Docker or the image {image} is not available (docker pull {image}).");
        }

        var name = "leviathan-test-" + Guid.NewGuid().ToString("N")[..10];
        var (exit, output) = await Docker($"run -d --name {name} {options} {image} {containerArguments}", TimeSpan.FromSeconds(60));
        if (exit != 0)
        {
            Assert.Skip("The container could not be started: " + output);
        }

        _containers.Add(name);
        return name;
    }

    private async Task<int> PortOfAsync(string container, int inside)
    {
        var (_, output) = await Docker($"port {container} {inside}/tcp", TimeSpan.FromSeconds(20));
        return int.Parse(output.Split('\n')[0].Split(':')[^1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    // Waits until the server says hello ("SSH-..." or "220 ..."): a port that is open only means Docker is forwarding it.
    private static async Task WaitForPortAsync(int port, string banner)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                var buffer = new byte[64];
                using var read = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var count = await client.GetStream().ReadAsync(buffer, read.Token);
                if (count > 0 && System.Text.Encoding.ASCII.GetString(buffer, 0, count).StartsWith(banner, StringComparison.Ordinal))
                {
                    await Task.Delay(500);
                    return;
                }
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or IOException)
            {
                // Not ready yet.
            }

            await Task.Delay(400);
        }

        Assert.Fail($"The server on port {port} did not say hello in 60 s.");
    }

    private sealed record Server(RemoteLocationProvider Provider, LocationPath Root, string Container, MemorySecrets Secrets, MemoryHosts Hosts, RemoteEndpoint Endpoint);

    private async Task<Server> StartSftpAsync(string? password = Password)
    {
        var container = await StartAsync("atmoz/sftp", "-p 127.0.0.1::22", $"{User}:{Password}:::upload");
        var port = await PortOfAsync(container, 22);
        await WaitForPortAsync(port, "SSH-");
        var secrets = new MemorySecrets();
        var hosts = new MemoryHosts();
        var endpoint = new RemoteEndpoint(RemoteProtocol.Sftp, "127.0.0.1", port, User);
        secrets.Set(endpoint, password);
        var provider = new RemoteLocationProvider(new Dictionary<RemoteProtocol, IRemoteSessionFactory> { [RemoteProtocol.Sftp] = new SftpSessionFactory(secrets, hosts) });
        return new Server(provider, LocationPath.Parse($"sftp://{User}@127.0.0.1:{port}/upload"), container, secrets, hosts, endpoint);
    }

    private async Task<Server> StartFtpAsync(string? password = Password)
    {
        var container = await StartAsync("delfer/alpine-ftp-server", $"-p 127.0.0.1::21 -p 127.0.0.1:31000-31010:31000-31010 -e ADDRESS=127.0.0.1 -e MIN_PORT=31000 -e MAX_PORT=31010 -e \"USERS={User}|{Password}|/ftp/{User}\"");
        var port = await PortOfAsync(container, 21);
        await WaitForPortAsync(port, "220");
        var secrets = new MemorySecrets();
        var hosts = new MemoryHosts();
        var endpoint = new RemoteEndpoint(RemoteProtocol.Ftp, "127.0.0.1", port, User);
        secrets.Set(endpoint, password);
        var provider = new RemoteLocationProvider(new Dictionary<RemoteProtocol, IRemoteSessionFactory> { [RemoteProtocol.Ftp] = new FtpSessionFactory(secrets, hosts) });
        // This image does not confine users to a home folder, so the tests work inside the one it made for the user.
        return new Server(provider, LocationPath.Parse($"ftp://{User}@127.0.0.1:{port}/ftp/{User}"), container, secrets, hosts, endpoint);
    }

    private static async Task<List<string>> NamesAsync(RemoteLocationProvider provider, LocationPath folder)
    {
        var names = new List<string>();
        await foreach (var batch in provider.ListAsync(folder, new ListingOptions { IncludeHidden = true }, CancellationToken.None))
        {
            names.AddRange(batch.Items.Select(i => i.Name + (i.Kind == Leviathan.Core.Model.FileEntryKind.Folder ? "/" : string.Empty)));
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    // The same journey on both protocols: a file written under a temporary name and renamed, listed, read back, a folder, a long file, odd names, deletes.
    private static async Task JourneyAsync(Server server)
    {
        var provider = server.Provider;
        var root = server.Root;
        Assert.Empty(await NamesAsync(provider, root));

        var content = new byte[3 * 1024 * 1024 + 17];
        new Random(5).NextBytes(content);
        var part = root.Combine("Ünïcödé file ✓.bin.leviathan-part");
        var final = root.Combine("Ünïcödé file ✓.bin");
        await provider.WriteAsync(part, new MemoryStream(content), CancellationToken.None);
        Assert.Contains("Ünïcödé file ✓.bin.leviathan-part", await NamesAsync(provider, root));
        await provider.RenameAsync(part, final, CancellationToken.None);

        var names = await NamesAsync(provider, root);
        Assert.Equal(["Ünïcödé file ✓.bin"], names);
        var entry = await provider.StatAsync(final, CancellationToken.None);
        Assert.Equal(content.Length, entry!.Size);
        Assert.True((DateTimeOffset.UtcNow - entry.Modified).Duration() < TimeSpan.FromDays(1));

        await using (var read = await provider.OpenReadAsync(final, CancellationToken.None))
        using (var copy = new MemoryStream())
        {
            await read.CopyToAsync(copy);
            Assert.Equal(content, copy.ToArray());
        }

        var folder = root.Combine("sub folder");
        await provider.CreateFolderAsync(folder, CancellationToken.None);
        await provider.WriteAsync(folder.Combine("inner.txt"), new MemoryStream("hello"u8.ToArray()), CancellationToken.None);
        Assert.Equal(["sub folder/", "Ünïcödé file ✓.bin"], await NamesAsync(provider, root));
        Assert.Equal(["inner.txt"], await NamesAsync(provider, folder));

        await provider.DeleteAsync(folder.Combine("inner.txt"), isFolder: false, CancellationToken.None);
        await provider.DeleteAsync(folder, isFolder: true, CancellationToken.None);
        await provider.DeleteAsync(final, isFolder: false, CancellationToken.None);
        Assert.Empty(await NamesAsync(provider, root));
        Assert.Null(await provider.StatAsync(final, CancellationToken.None));
    }

    [Fact]
    public async Task Sftp_FullJourney()
    {
        var server = await StartSftpAsync();
        await JourneyAsync(server);
        Assert.Equal(1, server.Provider.OpenConnections);
        await server.Provider.DisposeAsync();
    }

    [Fact]
    public async Task Ftp_FullJourney()
    {
        var server = await StartFtpAsync();
        await JourneyAsync(server);
        await server.Provider.DisposeAsync();
    }

    private sealed class InlineUi : Leviathan.ViewModels.Folders.IUiContext
    {
        public void Post(Action action) => action();
    }

    // What a tab does: the folder view model asks the registry, which finds the remote provider, and the rows arrive in the tab's own list.
    [Fact]
    public async Task Sftp_ATabLoadsTheFolder_ThroughTheFolderViewModel()
    {
        var server = await StartSftpAsync();
        await server.Provider.WriteAsync(server.Root.Combine("hello.txt"), new MemoryStream("hello"u8.ToArray()), CancellationToken.None);
        await server.Provider.CreateFolderAsync(server.Root.Combine("docs"), CancellationToken.None);
        var folder = new Leviathan.ViewModels.Folders.FolderViewModel(new LocationProviderRegistry([server.Provider]), new InlineUi());

        await folder.LoadAsync(server.Root, CancellationToken.None);

        Assert.Equal(Leviathan.ViewModels.Folders.FolderLoadState.Loaded, folder.State);
        Assert.Equal(["docs", "hello.txt"], folder.Entries.Select(e => e.Name).Order());
        Assert.Equal(5, folder.Entries.Single(e => e.Name == "hello.txt").Size);
        await server.Provider.DisposeAsync();
    }

    // A tab on a server that is gone shows the classified error, and the next tab is not held up by it.
    [Fact]
    public async Task Ftp_ATabOnAClosedPort_ShowsTheErrorAndLeavesTheOtherTabsAlone()
    {
        var endpoint = new RemoteEndpoint(RemoteProtocol.Ftp, "127.0.0.1", 1, User);
        var secrets = new MemorySecrets();
        secrets.Set(endpoint, Password);
        var remote = new RemoteLocationProvider(new Dictionary<RemoteProtocol, IRemoteSessionFactory> { [RemoteProtocol.Ftp] = new FtpSessionFactory(secrets, new MemoryHosts()) });
        var registry = new LocationProviderRegistry([remote, new Leviathan.Providers.Local.LocalLocationProvider()]);
        var dead = new Leviathan.ViewModels.Folders.FolderViewModel(registry, new InlineUi());
        var local = new Leviathan.ViewModels.Folders.FolderViewModel(registry, new InlineUi());

        var deadLoad = dead.LoadAsync(LocationPath.Parse($"ftp://{User}@127.0.0.1:1/"), CancellationToken.None);
        await local.LoadAsync(LocationPath.Parse(Path.GetTempPath()), CancellationToken.None);
        await deadLoad;

        Assert.Equal(Leviathan.ViewModels.Folders.FolderLoadState.Loaded, local.State);
        Assert.Equal(Leviathan.ViewModels.Folders.FolderLoadState.Offline, dead.State);
        Assert.Contains("could not be reached", dead.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sftp_WrongPassword_IsAnAuthenticationError_NotAHang()
    {
        var server = await StartSftpAsync("wrong");
        var started = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<LocationException>(() => NamesAsync(server.Provider, server.Root));

        Assert.Equal(LocationError.AuthenticationRequired, error.Error);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Ftp_WrongPassword_IsAnAuthenticationError()
    {
        var server = await StartFtpAsync("wrong");

        var error = await Assert.ThrowsAsync<LocationException>(() => NamesAsync(server.Provider, server.Root));

        Assert.True(error.Error == LocationError.AuthenticationRequired, error.ToString());
    }

    [Fact]
    public async Task Sftp_FirstContactTrustsTheHostKey_AndAChangedKeyIsRefused()
    {
        var server = await StartSftpAsync();
        await NamesAsync(server.Provider, server.Root);
        var remembered = Assert.Single(server.Hosts.Saved);
        Assert.StartsWith("SHA256:", remembered.Value, StringComparison.Ordinal);
        await server.Provider.DisposeAsync();

        // The same address now presents another key (as a reinstalled server, or an attacker in between, would).
        server.Hosts.Saved[remembered.Key] = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var fresh = new RemoteLocationProvider(new Dictionary<RemoteProtocol, IRemoteSessionFactory> { [RemoteProtocol.Sftp] = new SftpSessionFactory(server.Secrets, server.Hosts) });

        var error = await Assert.ThrowsAsync<LocationException>(() => NamesAsync(fresh, server.Root));

        Assert.Equal(LocationError.AuthenticationRequired, error.Error);
        Assert.Contains("not the one trusted", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sftp_AServerThatWentAway_FailsFast_AndComesBack()
    {
        var server = await StartSftpAsync();
        await NamesAsync(server.Provider, server.Root);
        await Docker($"pause {server.Container}", TimeSpan.FromSeconds(20));
        var started = Stopwatch.StartNew();

        var error = await Assert.ThrowsAnyAsync<LocationException>(() => NamesAsync(server.Provider, server.Root));

        Assert.True(error.Error is LocationError.Timeout or LocationError.Unreachable, error.Error.ToString());
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(150), $"took {started.Elapsed}");

        await Docker($"unpause {server.Container}", TimeSpan.FromSeconds(20));
        await Task.Delay(1000);
        Assert.Empty(await NamesAsync(server.Provider, server.Root));
        await server.Provider.DisposeAsync();
    }

    [Fact]
    public async Task Ftp_NothingListening_IsUnreachable_Quickly()
    {
        var endpoint = new RemoteEndpoint(RemoteProtocol.Ftp, "127.0.0.1", 1, User);
        var secrets = new MemorySecrets();
        secrets.Set(endpoint, Password);
        var provider = new RemoteLocationProvider(new Dictionary<RemoteProtocol, IRemoteSessionFactory> { [RemoteProtocol.Ftp] = new FtpSessionFactory(secrets, new MemoryHosts()) });
        var started = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<LocationException>(() => NamesAsync(provider, LocationPath.Parse($"ftp://{User}@127.0.0.1:1/")));

        Assert.Equal(LocationError.Unreachable, error.Error);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"took {started.Elapsed}");
    }
}

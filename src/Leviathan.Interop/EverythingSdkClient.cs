using System.Runtime.InteropServices;
using Leviathan.Search;

namespace Leviathan.Interop;

/// <summary>
/// Talks to Everything (voidtools) through its SDK library (spec §13, D14). The library is not shipped with Leviathan: it is loaded from, in order,
/// <c>LEVIATHAN_EVERYTHING_DLL</c>, an <c>everything</c> folder next to the app, and Everything's own install folders. If it is not found the state is
/// <see cref="EverythingState.NotInstalled"/> and search uses the other sources. The SDK keeps one query in global state, so calls are taken one at a
/// time, off the caller's thread, with a time limit. Written to the documented SDK (Everything 1.4.1 and later); it has not yet been run against a real
/// Everything because none is installed on the development machine (docs/phase-4.md).
/// </summary>
public sealed class EverythingSdkClient : IEverythingClient, IDisposable
{
    private const uint RequestFullPath = 0x4;
    private const uint RequestSize = 0x10;
    private const uint RequestDateCreated = 0x20;
    private const uint RequestDateModified = 0x40;
    private const uint RequestAttributes = 0x100;
    private const uint ErrorIpc = 2;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<IEnumerable<string>> _candidates;
    private Api? _api;
    private bool _triedLoad;

    /// <summary>Looks for the library in the usual places.</summary>
    public EverythingSdkClient()
        : this(DefaultCandidates)
    {
    }

    /// <summary>Looks for the library at <paramref name="candidates"/>, in order (tests pass none or a missing path).</summary>
    public EverythingSdkClient(Func<IEnumerable<string>> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _candidates = candidates;
    }

    private static string LibraryName => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "EverythingARM64.dll",
        Architecture.X86 => "Everything32.dll",
        _ => "Everything64.dll",
    };

    private static IEnumerable<string> DefaultCandidates()
    {
        if (Environment.GetEnvironmentVariable("LEVIATHAN_EVERYTHING_DLL") is { Length: > 0 } fromEnvironment)
        {
            yield return fromEnvironment;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "everything", LibraryName);
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (root.Length == 0)
            {
                continue;
            }

            foreach (var folder in new[] { "Everything", "Everything 1.5a" })
            {
                yield return Path.Combine(root, folder, LibraryName);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<EverythingState> GetStateAsync(CancellationToken cancellationToken) =>
        new(Task.Run(() => StateNow(), cancellationToken));

    private unsafe EverythingState StateNow()
    {
        if (Load() is not { } api)
        {
            return EverythingState.NotInstalled;
        }

        _gate.Wait();
        try
        {
            if (api.IsDbLoaded() != 0)
            {
                return EverythingState.Ready;
            }

            return api.GetLastError() == ErrorIpc ? EverythingState.NotRunning : EverythingState.Loading;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<EverythingPage> QueryAsync(string search, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var api = Load() ?? throw new InvalidOperationException("Everything's library was not found.");
        var work = Task.Run(() => QueryNow(api, search, offset, count), CancellationToken.None);
        try
        {
            return await work.WaitAsync(QueryTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Everything did not answer in time.");
        }
    }

    private unsafe EverythingPage QueryNow(Api api, string search, int offset, int count)
    {
        _gate.Wait();
        try
        {
            fixed (char* text = search)
            {
                api.SetSearch(text);
            }

            api.SetRequestFlags(RequestFullPath | RequestSize | RequestDateCreated | RequestDateModified | RequestAttributes);
            api.SetOffset((uint)Math.Max(0, offset));
            api.SetMax((uint)Math.Max(1, count));
            if (api.Query(1) == 0)
            {
                throw new InvalidOperationException(api.GetLastError() == ErrorIpc ? "Everything is not running." : "Everything could not run the search.");
            }

            var returned = (int)api.GetNumResults();
            var hits = new List<EverythingHit>(returned);
            var buffer = new char[1024];
            for (uint i = 0; i < returned; i++)
            {
                var length = (int)api.GetResultFullPath(i, null, 0);
                if (length == 0)
                {
                    continue;
                }

                if (buffer.Length <= length)
                {
                    buffer = new char[length + 1];
                }

                string path;
                fixed (char* p = buffer)
                {
                    path = new string(p, 0, (int)api.GetResultFullPath(i, p, (uint)buffer.Length));
                }

                long size = -1;
                if (api.GetResultSize(i, &size) == 0)
                {
                    size = -1;
                }

                hits.Add(new EverythingHit(path, size, ReadTime(api.GetResultDateModified, i), ReadTime(api.GetResultDateCreated, i), (FileAttributes)api.GetResultAttributes(i), api.IsFolderResult(i) != 0));
            }

            return new EverythingPage(hits, api.GetTotResults());
        }
        finally
        {
            api.Reset();
            _gate.Release();
        }
    }

    private static unsafe DateTimeOffset ReadTime(delegate* unmanaged<uint, long*, int> read, uint index)
    {
        long fileTime = 0;
        return read(index, &fileTime) != 0 && fileTime > 0 ? DateTimeOffset.FromFileTime(fileTime) : DateTimeOffset.UnixEpoch;
    }

    private Api? Load()
    {
        if (_triedLoad)
        {
            return _api;
        }

        lock (_gate)
        {
            if (_triedLoad)
            {
                return _api;
            }

            foreach (var candidate in _candidates())
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle) && Api.TryBind(handle) is { } api)
                {
                    _api = api;
                    break;
                }
            }

            _triedLoad = true;
            return _api;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    // The SDK's exports, bound once. Everything's functions use the platform's only calling convention on x64 and ARM64.
    private sealed unsafe class Api
    {
        public required delegate* unmanaged<uint> GetLastError;
        public required delegate* unmanaged<char*, void> SetSearch;
        public required delegate* unmanaged<uint, void> SetRequestFlags;
        public required delegate* unmanaged<uint, void> SetOffset;
        public required delegate* unmanaged<uint, void> SetMax;
        public required delegate* unmanaged<int, int> Query;
        public required delegate* unmanaged<uint> GetNumResults;
        public required delegate* unmanaged<uint> GetTotResults;
        public required delegate* unmanaged<uint, char*, uint, uint> GetResultFullPath;
        public required delegate* unmanaged<uint, long*, int> GetResultSize;
        public required delegate* unmanaged<uint, long*, int> GetResultDateModified;
        public required delegate* unmanaged<uint, long*, int> GetResultDateCreated;
        public required delegate* unmanaged<uint, uint> GetResultAttributes;
        public required delegate* unmanaged<uint, int> IsFolderResult;
        public required delegate* unmanaged<int> IsDbLoaded;
        public required delegate* unmanaged<void> Reset;

        private static readonly string[] ExportNames =
        [
            "Everything_GetLastError", "Everything_SetSearchW", "Everything_SetRequestFlags", "Everything_SetOffset", "Everything_SetMax", "Everything_QueryW",
            "Everything_GetNumResults", "Everything_GetTotResults", "Everything_GetResultFullPathNameW", "Everything_GetResultSize", "Everything_GetResultDateModified",
            "Everything_GetResultDateCreated", "Everything_GetResultAttributes", "Everything_IsFolderResult", "Everything_IsDBLoaded", "Everything_Reset",
        ];

        public static Api? TryBind(nint library)
        {
            nint Find(string name) => NativeLibrary.TryGetExport(library, name, out var address) ? address : 0;
            var addresses = ExportNames.Select(Find).ToArray();
            if (addresses.Contains(0))
            {
                return null;
            }

            return new Api
            {
                GetLastError = (delegate* unmanaged<uint>)addresses[0],
                SetSearch = (delegate* unmanaged<char*, void>)addresses[1],
                SetRequestFlags = (delegate* unmanaged<uint, void>)addresses[2],
                SetOffset = (delegate* unmanaged<uint, void>)addresses[3],
                SetMax = (delegate* unmanaged<uint, void>)addresses[4],
                Query = (delegate* unmanaged<int, int>)addresses[5],
                GetNumResults = (delegate* unmanaged<uint>)addresses[6],
                GetTotResults = (delegate* unmanaged<uint>)addresses[7],
                GetResultFullPath = (delegate* unmanaged<uint, char*, uint, uint>)addresses[8],
                GetResultSize = (delegate* unmanaged<uint, long*, int>)addresses[9],
                GetResultDateModified = (delegate* unmanaged<uint, long*, int>)addresses[10],
                GetResultDateCreated = (delegate* unmanaged<uint, long*, int>)addresses[11],
                GetResultAttributes = (delegate* unmanaged<uint, uint>)addresses[12],
                IsFolderResult = (delegate* unmanaged<uint, int>)addresses[13],
                IsDbLoaded = (delegate* unmanaged<int>)addresses[14],
                Reset = (delegate* unmanaged<void>)addresses[15],
            };
        }
    }
}

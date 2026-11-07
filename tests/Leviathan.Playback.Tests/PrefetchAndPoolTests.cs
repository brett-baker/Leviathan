using System.Buffers.Binary;
using Leviathan.Core.Platform;
using Leviathan.Testing;

namespace Leviathan.Playback.Tests;

public sealed class MediaPrefetcherTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-prefetch-" + Guid.NewGuid().ToString("N"));

    /// <summary>A file of any length (never allocated) with the bytes of a few boxes placed where a test wants them.</summary>
    private sealed class SparseStream(long length, params (long Offset, byte[] Bytes)[] islands) : Stream
    {
        private long _position;

        public int Reads { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            var n = (int)Math.Min(count, Math.Max(0, length - _position));
            Array.Clear(buffer, offset, n);
            foreach (var (at, bytes) in islands)
            {
                for (var i = 0; i < bytes.Length; i++)
                {
                    var index = at + i - _position;
                    if (index >= 0 && index < n)
                    {
                        buffer[offset + (int)index] = bytes[i];
                    }
                }
            }

            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Box(string type, long size, bool large = false)
    {
        var header = new byte[large ? 16 : 8];
        BinaryPrimitives.WriteUInt32BigEndian(header, large ? 1u : (uint)size);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(header, 4);
        if (large)
        {
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), (ulong)size);
        }

        return header;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void Plan_ASmallFile_IsReadWhole()
    {
        var plan = MediaPrefetcher.Plan(new SparseStream(1_000_000), ".mp4", 1_000_000);

        Assert.Equal([new ByteRange(0, 1_000_000)], plan);
    }

    [Fact]
    public void Plan_AnMp4WithTheIndexUpFront_ReadsOnlyTheHead_WhateverItsLength()
    {
        // ftyp (24 bytes) then moov, then a 6 GB mdat: the index is inside the head.
        var stream = new SparseStream(6_000_000_000, (0, Box("ftyp", 24)), (24, Box("moov", 900_000)), (24 + 900_000, Box("mdat", 5_999_000_000 - 900_000, large: true)));

        var plan = MediaPrefetcher.Plan(stream, ".MP4", 6_000_000_000);

        Assert.Equal([new ByteRange(0, MediaPrefetcher.HeadBytes)], plan);
    }

    [Fact]
    public void Plan_AnMp4WithTheIndexAtTheEnd_ReadsTheHead_AndTheStartOfTheIndex()
    {
        // ftyp, a 32-bit-sized mdat of 1 GB, then a 2 MB moov at the end.
        const long Length = 24 + 1_000_000_000 + 2_000_000;
        var stream = new SparseStream(Length, (0, Box("ftyp", 24)), (24, Box("mdat", 1_000_000_000)), (24 + 1_000_000_000, Box("moov", 2_000_000)));

        var plan = MediaPrefetcher.Plan(stream, ".mp4", Length);

        Assert.Equal([new ByteRange(0, MediaPrefetcher.HeadBytes), new ByteRange(24 + 1_000_000_000, 2_000_000)], plan);
        Assert.True(stream.Reads <= 4, $"{stream.Reads} reads to find the index");
    }

    [Fact]
    public void Plan_AHugeIndex_IsCutToTheFirstFewMegabytes()
    {
        const long Length = 24 + 100_000_000 + 30_000_000;
        var stream = new SparseStream(Length, (0, Box("ftyp", 24)), (24, Box("mdat", 100_000_000)), (24 + 100_000_000, Box("moov", 30_000_000)));

        var plan = MediaPrefetcher.Plan(stream, ".mov", Length);

        Assert.Equal(new ByteRange(24 + 100_000_000, MediaPrefetcher.IndexBytes), plan[1]);
    }

    [Fact]
    public void Plan_AnMp4WhoseBoxesCannotBeWalked_ReadsTheHeadOnly()
    {
        // A zero-sized-looking box where a header should be: nothing to follow, nothing invented.
        var stream = new SparseStream(500_000_000, (0, Box("ftyp", 4)));

        Assert.Equal([new ByteRange(0, MediaPrefetcher.HeadBytes)], MediaPrefetcher.Plan(stream, ".mp4", 500_000_000));
    }

    [Theory]
    [InlineData(".mkv")]
    [InlineData(".avi")]
    [InlineData(".webm")]
    public void Plan_OtherContainers_ReadTheHeadAndTheLastMegabyte(string extension)
    {
        var plan = MediaPrefetcher.Plan(new SparseStream(100_000_000), extension, 100_000_000);

        Assert.Equal([new ByteRange(0, MediaPrefetcher.HeadBytes), new ByteRange(100_000_000 - MediaPrefetcher.TailBytes, MediaPrefetcher.TailBytes)], plan);
    }

    [Fact]
    public void Plan_NeverOverlapsTheHeadAndTheTail()
    {
        var length = MediaPrefetcher.HeadBytes + 1000;

        var plan = MediaPrefetcher.Plan(new SparseStream(length), ".mkv", length);

        Assert.Equal([new ByteRange(0, MediaPrefetcher.HeadBytes), new ByteRange(MediaPrefetcher.HeadBytes, 1000)], plan);
    }

    [Fact]
    public async Task Prefetch_OfARealFile_ReadsTheHeadAndTail_ButNotTheMiddle()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "odd ✓ 名前.mkv");
        await using (var file = new FileStream(path, FileMode.Create))
        {
            file.SetLength(20L << 20);
        }

        var result = await MediaPrefetcher.PrefetchAsync(path, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(MediaPrefetcher.HeadBytes + MediaPrefetcher.TailBytes, result.BytesRead);
        Assert.Equal(2, result.Ranges.Count);
    }

    [Fact]
    public async Task Prefetch_OfAMissingFile_IsAnErrorNotAnException()
    {
        var result = await MediaPrefetcher.PrefetchAsync(Path.Combine(_folder, "nope.mp4"), CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal(0, result.BytesRead);
    }

    [Fact]
    public async Task Prefetch_WhenCancelled_Throws_SoTheCallerCanStop()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "a.mkv");
        await File.WriteAllBytesAsync(path, new byte[1000]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaPrefetcher.PrefetchAsync(path, cts.Token));
    }
}

public sealed class EnginePoolTests
{
    private sealed class Factory
    {
        public List<FakeVideoEngine> Made { get; } = [];

        public TaskCompletionSource? Hold { get; set; }

        public async Task<IVideoEngine> CreateAsync(CancellationToken cancellationToken)
        {
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            var engine = new FakeVideoEngine();
            lock (Made)
            {
                Made.Add(engine);
            }

            return engine;
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "Timed out");
    }

    [Fact]
    public async Task Warm_MakesOneSpare_AndRentHandsItOver_ThenWarmsAnother()
    {
        var factory = new Factory();
        await using var pool = new EnginePool(factory.CreateAsync);

        await pool.WarmAsync();
        await pool.WarmAsync();
        Assert.Single(factory.Made);

        var rented = await pool.RentAsync(CancellationToken.None);

        Assert.Same(factory.Made[0], rented);
        await WaitUntil(() => factory.Made.Count == 2 && pool.HasSpare);
    }

    [Fact]
    public async Task Rent_WithoutAWarmUp_MakesOneTheSlowWay()
    {
        var factory = new Factory();
        await using var pool = new EnginePool(factory.CreateAsync);

        var rented = await pool.RentAsync(CancellationToken.None);

        Assert.Same(factory.Made[0], rented);
    }

    [Fact]
    public async Task Rent_WhileTheSpareIsStillBeingMade_WaitsForItInsteadOfMakingTwo()
    {
        var factory = new Factory { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var pool = new EnginePool(factory.CreateAsync);
        var warming = pool.WarmAsync();

        var renting = pool.RentAsync(CancellationToken.None);
        factory.Hold.SetResult();
        var engine = await renting;
        await warming;

        Assert.Same(factory.Made[0], engine);
    }

    [Fact]
    public async Task Return_KeepsTheEngineClosedAsTheSpare_AndDisposesASecond()
    {
        var factory = new Factory();
        await using var pool = new EnginePool(factory.CreateAsync);
        var a = (FakeVideoEngine)await pool.RentAsync(CancellationToken.None);
        await WaitUntil(() => pool.HasSpare);
        var b = (FakeVideoEngine)await pool.RentAsync(CancellationToken.None);
        await WaitUntil(() => pool.HasSpare);
        await a.OpenAsync("x.mp4", TimeSpan.Zero, true, CancellationToken.None);

        // The spare is taken again when a returned one comes back while another spare exists: that one is disposed.
        await pool.ReturnAsync(a);

        Assert.Contains("Close", a.Calls);
        Assert.True(a.Disposed);
        Assert.False(b.Disposed);
    }

    [Fact]
    public async Task Return_WhenThereIsNoSpare_KeepsTheEngine()
    {
        var factory = new Factory { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var pool = new EnginePool(factory.CreateAsync);
        factory.Hold.SetResult();
        var engine = new FakeVideoEngine();

        await pool.ReturnAsync(engine);

        Assert.False(engine.Disposed);
        Assert.Same(engine, await pool.RentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Preload_OpensTheNextFilePausedInAHiddenPlayer_AndTakeHandsItOver()
    {
        var factory = new Factory();
        await using var pool = new EnginePool(factory.CreateAsync);

        await pool.PreloadAsync(@"\\nas\inbox\next.mkv", TimeSpan.FromSeconds(60));
        var taken = await pool.TakePreloadedAsync(@"\\NAS\inbox\NEXT.mkv");

        var engine = Assert.IsType<FakeVideoEngine>(taken);
        Assert.Equal(PlaybackState.Paused, engine.Snapshot.State);
        Assert.Equal(TimeSpan.FromSeconds(60), engine.Snapshot.Position);
        Assert.Null(pool.PreloadedPath);
        Assert.Null(await pool.TakePreloadedAsync(@"\\nas\inbox\next.mkv"));
    }

    [Fact]
    public async Task Preload_OfTheSameFileTwice_OpensItOnce()
    {
        var factory = new Factory();
        await using var pool = new EnginePool(factory.CreateAsync);

        await pool.PreloadAsync("a.mkv", TimeSpan.Zero);
        await pool.PreloadAsync("a.mkv", TimeSpan.Zero);

        var engine = (FakeVideoEngine)(await pool.TakePreloadedAsync("a.mkv"))!;
        Assert.Single(engine.Calls, c => c.StartsWith("Open ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preload_OfAnotherFile_ThrowsTheFirstAway()
    {
        var factory = new Factory();
        await using var pool = new EnginePool(factory.CreateAsync);
        await pool.PreloadAsync("a.mkv", TimeSpan.Zero);
        var first = factory.Made[0];

        await pool.PreloadAsync("b.mkv", TimeSpan.Zero);

        await WaitUntil(() => first.Disposed);
        Assert.Null(await pool.TakePreloadedAsync("a.mkv"));
        Assert.NotNull(await pool.TakePreloadedAsync("b.mkv"));
    }

    [Fact]
    public async Task Preload_OfAFileThatWillNotPlay_GivesNothingToTake_AndDisposesThePlayer()
    {
        var factory = new Factory();
        await using var pool = new EnginePool(async ct =>
        {
            var engine = (FakeVideoEngine)await factory.CreateAsync(ct);
            engine.Refuse["bad.mp4"] = "Unsupported codec";
            return engine;
        });

        await pool.PreloadAsync("bad.mp4", TimeSpan.Zero);

        Assert.Null(await pool.TakePreloadedAsync("bad.mp4"));
        Assert.True(factory.Made[0].Disposed);
    }

    [Fact]
    public async Task Dispose_DisposesTheSpare_AndThePreloadedPlayer_AndStopsWarming()
    {
        var factory = new Factory();
        var pool = new EnginePool(factory.CreateAsync);
        await pool.WarmAsync();
        await pool.PreloadAsync("a.mkv", TimeSpan.Zero);
        await WaitUntil(() => factory.Made.Count == 2);

        await pool.DisposeAsync();

        Assert.All(factory.Made, e => Assert.True(e.Disposed));
        await pool.WarmAsync();
        Assert.Equal(2, factory.Made.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.RentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AWarmUpThatFails_IsNotAnError_AndTheNextRentTriesAgain()
    {
        var attempts = 0;
        await using var pool = new EnginePool(_ => ++attempts == 1 ? throw new InvalidOperationException("no engine") : Task.FromResult<IVideoEngine>(new FakeVideoEngine()));

        await pool.WarmAsync();
        Assert.False(pool.HasSpare);

        Assert.NotNull(await pool.RentAsync(CancellationToken.None));
    }
}

using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Tests;

public sealed class VideoThumbnailSourceTests
{
    private static readonly ThumbnailImage Real = new(2, 2, new byte[16]);
    private static readonly ThumbnailImage Frame = new(4, 4, new byte[64]);
    private static readonly ThumbnailImage Icon = new(1, 1, new byte[4]);

    private sealed class FakeShell(bool hasThumbnail) : IThumbnailSource
    {
        public List<ThumbnailMode> Modes { get; } = [];

        public Task<ThumbnailImage?> CreateAsync(ThumbnailRequest request, CancellationToken cancellationToken)
        {
            Modes.Add(request.Mode);
            return Task.FromResult<ThumbnailImage?>(request.Mode == ThumbnailMode.ThumbnailOnly ? (hasThumbnail ? Real : null) : Icon);
        }
    }

    private sealed class FakeFrames(Func<ThumbnailImage?> answer) : IVideoFrameSource
    {
        public int Calls { get; private set; }

        public Task<ThumbnailImage?> GetFrameAsync(ThumbnailRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer());
        }
    }

    private static ThumbnailRequest Request(string path, ThumbnailMode mode = ThumbnailMode.Thumbnail) =>
        new(LocationPath.Parse(path), DateTimeOffset.UnixEpoch, 1000, 128, mode);

    [Fact]
    public async Task AVideoTheShellHasAThumbnailFor_UsesTheShellsAndNeverStartsFfmpeg()
    {
        var shell = new FakeShell(hasThumbnail: true);
        var frames = new FakeFrames(() => Frame);

        var image = await new VideoThumbnailSource(shell, frames).CreateAsync(Request(@"D:\v\clip.mkv"), CancellationToken.None);

        Assert.Same(Real, image);
        Assert.Equal(0, frames.Calls);
        Assert.Equal([ThumbnailMode.ThumbnailOnly], shell.Modes);
    }

    [Fact]
    public async Task AVideoTheShellHasNoThumbnailFor_GetsAFrameFromFfmpeg()
    {
        var shell = new FakeShell(hasThumbnail: false);
        var frames = new FakeFrames(() => Frame);

        var image = await new VideoThumbnailSource(shell, frames).CreateAsync(Request(@"D:\v\clip.webm"), CancellationToken.None);

        Assert.Same(Frame, image);
        Assert.Equal(1, frames.Calls);
    }

    [Fact]
    public async Task WhenFfmpegHasNoFrameEither_TheShellsIconIsTheAnswer()
    {
        var shell = new FakeShell(hasThumbnail: false);

        var none = await new VideoThumbnailSource(shell, new FakeFrames(() => null)).CreateAsync(Request(@"D:\v\clip.mkv"), CancellationToken.None);
        var failed = await new VideoThumbnailSource(shell, new FakeFrames(() => throw new IOException("pipe closed"))).CreateAsync(Request(@"D:\v\clip.mkv"), CancellationToken.None);

        Assert.Same(Icon, none);
        Assert.Same(Icon, failed);
    }

    [Fact]
    public async Task WhatIsNotAVideoOrNotAThumbnailRequest_GoesStraightToTheShell()
    {
        var shell = new FakeShell(hasThumbnail: false);
        var frames = new FakeFrames(() => Frame);
        var source = new VideoThumbnailSource(shell, frames);

        await source.CreateAsync(Request(@"D:\v\photo.jpg"), CancellationToken.None);
        await source.CreateAsync(Request(@"D:\v\clip.mkv", ThumbnailMode.IconOnly), CancellationToken.None);
        await source.CreateAsync(Request(@"D:\v\clip.mkv", ThumbnailMode.CachedOnly), CancellationToken.None);

        Assert.Equal(0, frames.Calls);
        Assert.Equal([ThumbnailMode.Thumbnail, ThumbnailMode.IconOnly, ThumbnailMode.CachedOnly], shell.Modes);
    }

    [Fact]
    public async Task PreferFrames_SkipsTheShellForVideo()
    {
        var shell = new FakeShell(hasThumbnail: true);

        var image = await new VideoThumbnailSource(shell, new FakeFrames(() => Frame), preferFrames: true).CreateAsync(Request(@"D:\v\clip.mkv"), CancellationToken.None);

        Assert.Same(Frame, image);
        Assert.Empty(shell.Modes);
    }

    [Fact]
    public async Task ACancelledRequest_IsNotAnswered_WithTheIcon()
    {
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VideoThumbnailSource(new FakeShell(false), new CancelingFrames(cts)).CreateAsync(Request(@"D:\v\clip.mkv"), cts.Token));
    }

    private sealed class CancelingFrames(CancellationTokenSource cts) : IVideoFrameSource
    {
        public async Task<ThumbnailImage?> GetFrameAsync(ThumbnailRequest request, CancellationToken cancellationToken)
        {
            await cts.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }
}

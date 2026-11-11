using Leviathan.Interop;
using Leviathan.Providers.Remote;

namespace Leviathan.Interop.Tests;

/// <summary>Passwords kept in Windows Credential Manager (spec §7.1). Each test uses a server name nobody else uses and removes what it saved.</summary>
public sealed class WindowsRemoteSecretsTests : IDisposable
{
    private readonly WindowsRemoteSecrets _secrets = new();
    private readonly RemoteEndpoint _endpoint = new(RemoteProtocol.Sftp, "leviathan-test-" + Guid.NewGuid().ToString("N")[..8] + ".invalid", 22, "tester");

    public void Dispose() => _secrets.Set(_endpoint, null);

    [Fact]
    public void Get_WithNothingSaved_IsNull() => Assert.Null(_secrets.Get(_endpoint));

    [Fact]
    public void SetThenGet_RoundTripsAPassword_WithSpacesAndUnicode()
    {
        _secrets.Set(_endpoint, "pässwörd with spaces ✓");

        Assert.Equal("pässwörd with spaces ✓", _secrets.Get(_endpoint));
    }

    [Fact]
    public void Set_ReplacesTheOldPassword_AndNullForgetsIt()
    {
        _secrets.Set(_endpoint, "first");
        _secrets.Set(_endpoint, "second");
        Assert.Equal("second", _secrets.Get(_endpoint));

        _secrets.Set(_endpoint, null);

        Assert.Null(_secrets.Get(_endpoint));
    }

    [Fact]
    public void ForgettingWhatWasNeverSaved_IsFine() => _secrets.Set(_endpoint, null);

    [Fact]
    public void Secrets_AreKeptPerServerAndAccount()
    {
        var other = _endpoint with { User = "someone-else" };
        _secrets.Set(_endpoint, "mine");
        try
        {
            Assert.Null(_secrets.Get(other));
        }
        finally
        {
            _secrets.Set(other, null);
        }
    }

    [Fact]
    public void TheTargetNamesTheServer_ButNeverHoldsThePassword()
    {
        var target = WindowsRemoteSecrets.TargetOf(_endpoint);

        Assert.StartsWith("Leviathan:sftp://tester@", target, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyPassword_IsKeptAsEmpty_NotAsMissing()
    {
        _secrets.Set(_endpoint, string.Empty);

        Assert.Equal(string.Empty, _secrets.Get(_endpoint));
    }
}

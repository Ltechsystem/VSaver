using ValheimSync.Core.Storage;
using Xunit;

namespace ValheimSync.Tests;

/// <summary>
/// Only the paths reachable without a network / real OAuth flow: credential resolution
/// failures, placeholder detection, and the not-initialized guard. The actual Drive
/// round-trip is deliberately untested here (it needs real credentials).
/// NOTE: never call ClearCachedToken() in tests — it deletes the developer's real
/// cached OAuth token under %APPDATA%.
/// </summary>
public sealed class GoogleDriveProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vstests-gdrive-" + Guid.NewGuid().ToString("N"));

    public GoogleDriveProviderTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Initialize_MissingExplicitCredentialsPath_ThrowsWithThatPath()
    {
        var missing = Path.Combine(_dir, "nope", "credentials.json");
        var provider = new GoogleDriveStorageProvider("folder-id", missing);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => provider.InitializeAsync());
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public async Task Initialize_PlaceholderCredentials_ThrowsHelpfulError()
    {
        var creds = Path.Combine(_dir, "credentials.json");
        await File.WriteAllTextAsync(creds,
            """{"installed":{"client_id":"YOUR_CLIENT_ID","client_secret":"YOUR_CLIENT_SECRET"}}""");
        var provider = new GoogleDriveStorageProvider("folder-id", creds);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.InitializeAsync());
        Assert.Contains("placeholder", ex.Message);
    }

    [Fact]
    public async Task AnyDriveCall_BeforeInitialize_Throws()
    {
        var provider = new GoogleDriveStorageProvider("folder-id");

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ListFilesAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CopyAsync("a", "b"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DeleteAsync("a"));
    }

    [Fact]
    public void TokenStore_LivesUnderAppData()
    {
        Assert.Contains("ValheimSync", GoogleDriveStorageProvider.TokenStorePath);
        Assert.EndsWith("token", GoogleDriveStorageProvider.TokenStorePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}

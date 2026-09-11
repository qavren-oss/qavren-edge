using System.Security.Cryptography;
using Qavren.Edge.Sqlite;

namespace Qavren.Edge.Sample.Pages;

public partial class EncryptionPage : ContentPage
{
    private readonly IEdgeDatabase _database;
    private readonly ISqliteNativeProvider _native;

    public EncryptionPage(IEdgeDatabase database, ISqliteNativeProvider native)
    {
        InitializeComponent();
        _database = database;
        _native = native;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        BuildLabel.Text = MauiProgram.IsCipherBuild
            ? "Build configuration: Cipher — linked against Qavren.Edge.Sqlite.Native.Cipher.\n" +
              "Rebuild with `-c Release` to get the plain package instead."
            : "Build configuration: Release — linked against Qavren.Edge.Sqlite.Native (no encryption).\n" +
              "Rebuild with `-c Cipher` to get the SQLCipher package instead.";

        var native = _native.Describe();
        ProviderLabel.Text =
            $"provider {native.ProviderName}\nlibrary {native.LibraryName}\n" +
            $"resolved {native.ResolvedPath ?? "(default probing)"}\n" +
            $"sqlite {native.SqliteVersion}\nvec {native.VecVersion}\ncipher {native.CipherVersion}\n" +
            $"supportsEncryption {_native.SupportsEncryption}";

        var info = await _database.GetInfoAsync();
        DatabaseLabel.Text = $"{_database.Path}\nencrypted: {info.IsEncrypted}";

        RekeyButton.IsEnabled = MauiProgram.IsCipherBuild;
        RekeyLabel.Text = MauiProgram.IsCipherBuild
            ? "Rotating writes a new 32-byte raw key to SecureStorage and rekeys the file in place."
            : "Rekey is unavailable: this build has no codec.";
    }

    private async void OnRekey(object? sender, EventArgs e)
    {
        try
        {
            var fresh = SqliteKey.FromRawBytes(RandomNumberGenerator.GetBytes(32));
            await _database.RekeyAsync(fresh);
            RekeyLabel.Text = $"rekeyed at {DateTimeOffset.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            RekeyLabel.Text = $"rekey failed: {ex.GetType().Name}: {ex.Message}";
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KQ.Brs.Core;

namespace KQ.Brs.Poc.Services;

public enum AppTheme { System, Light, Dark }

/// <summary>Everything the user can change in Settings, saved as JSON in %LocalAppData%\KQ.Brs.Poc.</summary>
public sealed record AppSettings
{
    public string ReaderHost { get; init; } = "192.168.0.161";
    public int ReaderPort { get; init; } = 23;
    public string ReaderUser { get; init; } = "alien";

    /// <summary>Stored DPAPI-protected (current Windows user); see <see cref="SettingsStore"/>.</summary>
    [JsonIgnore] public string ReaderPassword { get; init; } = "password";
    public string? ProtectedPassword { get; init; }

    public int StreamPort { get; init; } = 4000;
    public bool AutoConnect { get; init; } = true;
    public string SupervisorPin { get; init; } = "1234";
    public AppTheme Theme { get; init; } = AppTheme.System;
    /// <summary>Off (default): the flight starts empty and you add each passenger yourself. On: 700 generated demo bags.</summary>
    public bool GenerateDemoBags { get; init; }

    /// <summary>Pop up a "Tag not linked" alert when a tag nobody is waiting for is held at check-in.</summary>
    public bool ShowUnlinkedTagAlerts { get; init; } = true;

    /// <summary>Show "Flag tag as KQ-412" in bag details, to demonstrate a wrong-flight bag (spec 7.2). Off by default.</summary>
    public bool ShowMisrouteDemo { get; init; }

    /// <summary>Arm the loading antenna (3) as soon as the app starts, instead of on the Loading page.</summary>
    public bool ArmLoadingOnStart { get; init; }

    /// <summary>Highest licence-plate serial issued so far. Kept outside the database so plates never repeat after a clean.</summary>
    public int LastPlateSerial { get; init; }

    public BrsOptions Brs { get; init; } = new();
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KQ.Brs.Poc");

    private static string FilePath => Path.Combine(DataFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
            if (settings.ProtectedPassword is { } p)
            {
                var clear = ProtectedData.Unprotect(Convert.FromBase64String(p), null, DataProtectionScope.CurrentUser);
                settings = settings with { ReaderPassword = Encoding.UTF8.GetString(clear) };
            }
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataFolder);
        var protectedPassword = Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.ReaderPassword), null, DataProtectionScope.CurrentUser));
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings with { ProtectedPassword = protectedPassword }, Json));
    }
}

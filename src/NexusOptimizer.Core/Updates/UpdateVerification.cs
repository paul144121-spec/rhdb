using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexusOptimizer.Core.Updates;

/// <summary>Signiertes Update-Manifest. Die Signatur deckt genau die UTF-8-Bytes von <see cref="SignedManifest.ManifestJson"/> ab.</summary>
public sealed record UpdateManifest
{
    public string Version { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long SizeBytes { get; init; }
    public string? ReleaseNotes { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
}

public sealed record SignedManifest(string ManifestJson, string SignatureBase64);

public sealed record UpdateCheckResult(bool IsValid, bool UpdateAvailable, UpdateManifest? Manifest, string Message);

/// <summary>
/// Prüfung von Updates (Abschnitt 27.3 „Secure update verification“): ECDSA-P-256-Signatur des Manifests gegen einen fest
/// hinterlegten öffentlichen Schlüssel, nur HTTPS-Downloadadressen und SHA-256-Prüfung des Pakets vor jeder Installation.
/// Ohne konfigurierten Schlüssel und Kanal findet keinerlei Updateverkehr statt.
/// </summary>
public sealed class UpdateVerifier
{
    private readonly byte[]? _publicKeySpki;

    /// <param name="publicKeySpkiBase64">Öffentlicher ECDSA-Schlüssel (SubjectPublicKeyInfo, Base64) oder null, wenn kein Updatekanal eingerichtet ist.</param>
    public UpdateVerifier(string? publicKeySpkiBase64)
    {
        if (!string.IsNullOrWhiteSpace(publicKeySpkiBase64)) _publicKeySpki = Convert.FromBase64String(publicKeySpkiBase64);
    }

    public bool IsConfigured => _publicKeySpki is not null;

    public UpdateCheckResult Verify(SignedManifest signed, Version currentVersion)
    {
        if (_publicKeySpki is null) return new UpdateCheckResult(false, false, null, "Es ist kein Updatekanal eingerichtet.");

        byte[] signature;
        try { signature = Convert.FromBase64String(signed.SignatureBase64); }
        catch (FormatException) { return new UpdateCheckResult(false, false, null, "Die Signatur des Update-Manifests ist nicht lesbar."); }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(_publicKeySpki, out _);
            if (!ecdsa.VerifyData(Encoding.UTF8.GetBytes(signed.ManifestJson), signature, HashAlgorithmName.SHA256))
                return new UpdateCheckResult(false, false, null, "Die Signatur des Update-Manifests ist ungültig. Das Update wird verworfen.");
        }
        catch (CryptographicException)
        {
            return new UpdateCheckResult(false, false, null, "Die Signaturprüfung des Update-Manifests ist fehlgeschlagen.");
        }

        UpdateManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<UpdateManifest>(signed.ManifestJson); }
        catch (JsonException) { manifest = null; }
        if (manifest is null || !System.Version.TryParse(manifest.Version, out var offered))
            return new UpdateCheckResult(false, false, null, "Das Update-Manifest ist beschädigt.");

        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new UpdateCheckResult(false, false, manifest, "Die Downloadadresse des Updates ist keine HTTPS-Adresse. Das Update wird verworfen.");
        if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(char.IsAsciiHexDigit) || manifest.SizeBytes <= 0)
            return new UpdateCheckResult(false, false, manifest, "Das Update-Manifest enthält keine gültige Prüfsumme.");

        // Kein Downgrade: Ältere, gültig signierte Manifeste dürfen nicht erneut eingespielt werden.
        return offered > currentVersion
            ? new UpdateCheckResult(true, true, manifest, $"Version {offered} ist verfügbar.")
            : new UpdateCheckResult(true, false, manifest, "Die installierte Version ist aktuell.");
    }

    /// <summary>Vergleicht Größe und SHA-256 der heruntergeladenen Datei mit dem geprüften Manifest.</summary>
    public static async Task<bool> VerifyPackageAsync(string filePath, UpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length != manifest.SizeBytes) return false;
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(manifest.Sha256));
    }
}

using System.Net;
using System.Text.RegularExpressions;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Security;

/// <summary>
/// Zentrale, strikte Validierung von Aktionsparametern. Wird identisch in der Oberfläche,
/// beim Profilimport, für KI-Vorschläge und in der privilegierten Komponente verwendet.
/// </summary>
public static class ParameterValidator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    public static ValidationResult Validate(IReadOnlyList<ParameterSpec> specs, ActionParameters parameters)
    {
        var errors = new List<string>();
        var known = new HashSet<string>(specs.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var name in parameters.Values.Keys)
        {
            if (!known.Contains(name)) errors.Add($"Unbekannter Parameter '{Truncate(name)}'.");
        }

        foreach (var spec in specs)
        {
            var value = parameters.GetString(spec.Name);
            if (value is null)
            {
                if (spec.Required) errors.Add($"Pflichtparameter '{spec.Name}' fehlt.");
                continue;
            }

            var error = ValidateValue(spec, value);
            if (error is not null) errors.Add(error);
        }

        return errors.Count == 0 ? ValidationResult.Ok : new ValidationResult(false, errors);
    }

    public static string? ValidateValue(ParameterSpec spec, string value)
    {
        if (value.Length > spec.MaxLength) return $"Parameter '{spec.Name}' ist zu lang (maximal {spec.MaxLength} Zeichen).";
        if (ContainsControlCharacters(value)) return $"Parameter '{spec.Name}' enthält unzulässige Steuerzeichen.";

        switch (spec.Type)
        {
            case ParameterType.Integer:
                if (!long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number))
                    return $"Parameter '{spec.Name}' muss eine ganze Zahl sein.";
                if (spec.Min is not null && number < spec.Min) return $"Parameter '{spec.Name}' unterschreitet den Mindestwert {spec.Min}.";
                if (spec.Max is not null && number > spec.Max) return $"Parameter '{spec.Name}' überschreitet den Höchstwert {spec.Max}.";
                break;

            case ParameterType.Boolean:
                if (!bool.TryParse(value, out _)) return $"Parameter '{spec.Name}' muss 'true' oder 'false' sein.";
                break;

            case ParameterType.Choice:
                if (spec.Choices is null || !spec.Choices.Contains(value, StringComparer.OrdinalIgnoreCase))
                    return $"Parameter '{spec.Name}' hat keinen zulässigen Wert.";
                break;

            case ParameterType.ChoiceList:
                var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) return $"Parameter '{spec.Name}' darf nicht leer sein.";
                if (spec.Choices is null || parts.Any(p => !spec.Choices.Contains(p, StringComparer.OrdinalIgnoreCase)))
                    return $"Parameter '{spec.Name}' enthält einen unzulässigen Eintrag.";
                if (parts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != parts.Length)
                    return $"Parameter '{spec.Name}' enthält doppelte Einträge.";
                break;

            case ParameterType.Guid:
                if (!Guid.TryParseExact(value, "D", out _)) return $"Parameter '{spec.Name}' muss eine GUID sein.";
                break;

            case ParameterType.FilePath:
                var pathError = ValidateFilePath(value);
                if (pathError is not null) return $"Parameter '{spec.Name}': {pathError}";
                break;

            case ParameterType.IpAddressList:
                var addresses = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (addresses.Length is 0 or > 4) return $"Parameter '{spec.Name}' muss 1 bis 4 IP-Adressen enthalten.";
                foreach (var address in addresses)
                {
                    if (!IsPlainIpAddress(address)) return $"Parameter '{spec.Name}' enthält keine gültige IP-Adresse: '{Truncate(address)}'.";
                }
                break;

            case ParameterType.DriveLetter:
                if (value.Length != 2 || value[1] != ':' || !char.IsAsciiLetter(value[0]))
                    return $"Parameter '{spec.Name}' muss ein Laufwerksbuchstabe wie 'C:' sein.";
                break;

            case ParameterType.Text:
                if (string.IsNullOrWhiteSpace(value) && spec.Required) return $"Parameter '{spec.Name}' darf nicht leer sein.";
                break;
        }

        if (spec.Pattern is not null)
        {
            try
            {
                if (!Regex.IsMatch(value, spec.Pattern, RegexOptions.CultureInvariant, RegexTimeout))
                    return $"Parameter '{spec.Name}' hat ein unzulässiges Format.";
            }
            catch (RegexMatchTimeoutException)
            {
                return $"Parameter '{spec.Name}' konnte nicht geprüft werden.";
            }
        }

        return null;
    }

    /// <summary>Prüft einen vollqualifizierten lokalen Windows-Dateipfad, ohne auf das Dateisystem zuzugreifen.</summary>
    public static string? ValidateFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Pfad ist leer.";
        if (path.Length > 260) return "Pfad ist zu lang.";
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            return "Es sind nur vollqualifizierte lokale Pfade (z. B. C:\\Spiele\\spiel.exe) zulässig.";
        if (path.IndexOfAny(new[] { '"', '<', '>', '|', '*', '?', '/', '\0' }) >= 0 || path.IndexOf(':', 2) >= 0)
            return "Pfad enthält unzulässige Zeichen.";
        foreach (var segment in path[3..].Split('\\'))
        {
            if (segment.Length == 0) return "Pfad enthält leere Segmente.";
            if (segment is "." or "..") return "Relative Pfadsegmente sind nicht zulässig.";
            if (segment.EndsWith(' ') || segment.EndsWith('.')) return "Pfadsegmente dürfen nicht mit Leerzeichen oder Punkt enden.";
        }
        return null;
    }

    public static bool IsPlainIpAddress(string text)
    {
        if (text.Length is 0 or > 45) return false;
        // IPAddress.TryParse akzeptiert auch Kurzformen wie "1" – daher zusätzlich eine Formprüfung.
        if (!IPAddress.TryParse(text, out var address)) return false;
        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => text.Count(c => c == '.') == 3 && text.All(c => char.IsAsciiDigit(c) || c == '.'),
            System.Net.Sockets.AddressFamily.InterNetworkV6 => text.All(c => char.IsAsciiHexDigit(c) || c == ':' || c == '.'),
            _ => false,
        };
    }

    public static bool ContainsControlCharacters(string value) => value.Any(c => char.IsControl(c));

    private static string Truncate(string value) => value.Length <= 40 ? value : value[..40] + "…";
}

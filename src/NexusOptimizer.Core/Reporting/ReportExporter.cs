using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;

namespace NexusOptimizer.Core.Reporting;

public enum ReportFormat
{
    Json,
    Csv,
    Text,
}

/// <summary>Export von Verlauf, Empfehlungen und Messwerten als JSON, CSV oder lesbarer Textbericht.</summary>
public static class ReportExporter
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FileExtension(ReportFormat format) => format switch { ReportFormat.Json => ".json", ReportFormat.Csv => ".csv", _ => ".txt" };

    public static string ExportTransactions(IReadOnlyList<TransactionRecord> records, ReportFormat format)
    {
        switch (format)
        {
            case ReportFormat.Json:
                return JsonSerializer.Serialize(records.Select(r => new
                {
                    r.Id, r.BatchId, Zeitpunkt = r.Timestamp, Aktion = r.ActionId, Name = r.ActionName, Parameter = r.Parameters.Values, Quelle = r.Source, Status = r.Status,
                    Risiko = r.Risk, Berechtigung = r.PrivilegeUsed, Neustart = r.Restart, Umkehrbar = r.IsReversible, Vorher = r.OriginalDisplay, Beabsichtigt = r.IntendedDisplay,
                    Tatsaechlich = r.ActualDisplay, Verifikation = r.VerificationMessage, Fehler = r.ErrorDetails, RueckgaengigAm = r.RolledBackAt,
                }), Json);

            case ReportFormat.Csv:
                var csv = new StringBuilder();
                csv.AppendLine(CsvLine("Zeitpunkt", "Aktion", "Name", "Parameter", "Quelle", "Status", "Risiko", "Berechtigung", "Vorher", "Beabsichtigt", "Tatsächlich", "Verifikation", "Fehler", "Rückgängig am", "Transaktions-ID", "Lauf-ID"));
                foreach (var r in records)
                {
                    csv.AppendLine(CsvLine(r.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), r.ActionId, r.ActionName, r.Parameters.ToString(), r.Source.ToGerman(), r.Status.ToGerman(),
                        r.Risk.ToGerman(), r.PrivilegeUsed.ToGerman(), r.OriginalDisplay, r.IntendedDisplay, r.ActualDisplay, r.VerificationMessage, r.ErrorDetails ?? "",
                        r.RolledBackAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "", r.Id, r.BatchId));
                }
                return csv.ToString();

            default:
                var text = new StringBuilder();
                text.AppendLine("NEXUS Optimizer AI – Optimierungsverlauf");
                text.AppendLine($"Erstellt am {DateTimeOffset.Now.ToString("f", De)}");
                text.AppendLine(new string('=', 72));
                foreach (var batch in records.GroupBy(r => r.BatchId))
                {
                    var first = batch.First();
                    text.AppendLine();
                    text.AppendLine($"Lauf vom {first.Timestamp.ToLocalTime().ToString("g", De)} – {first.Source.ToGerman()}{(string.IsNullOrEmpty(first.SourceLabel) ? "" : $" ({first.SourceLabel})")}");
                    text.AppendLine(new string('-', 72));
                    foreach (var r in batch)
                    {
                        text.AppendLine($"  • {r.ActionName}");
                        text.AppendLine($"      Status:        {r.Status.ToGerman()}");
                        text.AppendLine($"      Risiko:        {r.Risk.ToGerman()} · {r.PrivilegeUsed.ToGerman()}");
                        text.AppendLine($"      Vorher:        {r.OriginalDisplay}");
                        text.AppendLine($"      Beabsichtigt:  {r.IntendedDisplay}");
                        if (!string.IsNullOrEmpty(r.ActualDisplay)) text.AppendLine($"      Tatsächlich:   {r.ActualDisplay}");
                        if (!string.IsNullOrEmpty(r.VerificationMessage)) text.AppendLine($"      Verifikation:  {r.VerificationMessage}");
                        if (r.Restart != RestartRequirement.None) text.AppendLine($"      Hinweis:       {r.Restart.ToGerman()}");
                        if (!string.IsNullOrEmpty(r.ErrorDetails)) text.AppendLine($"      Fehler:        {r.ErrorDetails}");
                        if (r.RolledBackAt is { } at) text.AppendLine($"      Rückgängig:    {at.ToLocalTime().ToString("g", De)}");
                    }
                }
                return text.ToString();
        }
    }

    public static string ExportRecommendations(IReadOnlyList<Recommendation> recommendations, SystemSnapshot? snapshot, ReportFormat format)
    {
        switch (format)
        {
            case ReportFormat.Json:
                return JsonSerializer.Serialize(new
                {
                    ErstelltAm = DateTimeOffset.Now,
                    Analyse = RuleBasedAdvisor.EngineDescription,
                    Empfehlungen = recommendations.Select(r => new { r.Id, Titel = r.Title, Erklaerung = r.Explanation, Dringlichkeit = r.Severity, Bereich = r.Area, Belege = r.Evidence, Aktionen = r.Actions.Select(a => a.ActionId) }),
                }, Json);

            case ReportFormat.Csv:
                var csv = new StringBuilder();
                csv.AppendLine(CsvLine("Dringlichkeit", "Bereich", "Titel", "Erklärung", "Belege", "Vorgeschlagene Aktionen"));
                foreach (var r in recommendations)
                    csv.AppendLine(CsvLine(r.Severity.ToString(), r.Area.ToGerman(), r.Title, r.Explanation, string.Join(" | ", r.Evidence), string.Join(" | ", r.Actions.Select(a => a.ActionId))));
                return csv.ToString();

            default:
                var text = new StringBuilder();
                text.AppendLine("NEXUS Optimizer AI – Systembericht");
                text.AppendLine($"Erstellt am {DateTimeOffset.Now.ToString("f", De)}");
                text.AppendLine(RuleBasedAdvisor.EngineDescription);
                text.AppendLine(new string('=', 72));
                if (snapshot is not null)
                {
                    var info = snapshot.Info;
                    text.AppendLine();
                    text.AppendLine("System");
                    text.AppendLine($"  Windows:        {info.OsName} {info.OsDisplayVersion} (Build {info.OsBuild})");
                    text.AppendLine($"  Prozessor:      {info.CpuName} ({info.CpuCores?.ToString() ?? "?"} Kerne / {info.CpuLogicalProcessors} Threads)");
                    text.AppendLine($"  Arbeitsspeicher: {Storage.ByteFormat.Format(info.MemoryTotalBytes)}");
                    foreach (var gpu in info.Gpus) text.AppendLine($"  Grafik:         {gpu.Name}");
                    foreach (var volume in info.Volumes) text.AppendLine($"  Laufwerk {volume.Letter}     {Storage.ByteFormat.Format(volume.FreeBytes)} frei von {Storage.ByteFormat.Format(volume.TotalBytes)}");
                    text.AppendLine($"  CPU-Auslastung (Mittel): {Format(snapshot.Cpu.Average)} · RAM (Mittel): {Format(snapshot.Memory.Average)}");
                }
                text.AppendLine();
                text.AppendLine(recommendations.Count == 0 ? "Keine Empfehlungen – es wurden keine Auffälligkeiten festgestellt." : $"Empfehlungen ({recommendations.Count})");
                foreach (var r in recommendations)
                {
                    text.AppendLine();
                    text.AppendLine($"[{SeverityText(r.Severity)}] {r.Title}");
                    text.AppendLine("  " + r.Explanation);
                    foreach (var evidence in r.Evidence) text.AppendLine("  – " + evidence);
                }
                return text.ToString();
        }
    }

    public static string ExportMetrics(IReadOnlyDictionary<MetricId, IReadOnlyList<MetricPoint>> series, ReportFormat format)
    {
        if (format == ReportFormat.Json)
        {
            return JsonSerializer.Serialize(series.ToDictionary(
                s => MetricCatalog.Get(s.Key).Name,
                s => s.Value.Select(p => new { Zeitpunkt = p.Timestamp, Wert = Math.Round(p.Value, 3) })), Json);
        }

        var builder = new StringBuilder();
        builder.AppendLine(CsvLine("Zeitpunkt", "Messgröße", "Wert", "Einheit"));
        foreach (var (metric, points) in series)
        {
            var definition = MetricCatalog.Get(metric);
            foreach (var point in points)
                builder.AppendLine(CsvLine(point.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), definition.Name, point.Value.ToString("0.###", CultureInfo.InvariantCulture), definition.Unit.ToString()));
        }
        return builder.ToString();
    }

    private static string Format(double? value) => value is null ? "nicht verfügbar" : value.Value.ToString("0.0", De) + " %";

    private static string SeverityText(RecommendationSeverity severity) => severity switch
    {
        RecommendationSeverity.Critical => "Kritisch",
        RecommendationSeverity.Warning => "Warnung",
        RecommendationSeverity.Suggestion => "Vorschlag",
        _ => "Hinweis",
    };

    /// <summary>CSV-Zeile mit Semikolon (deutsches Excel) und Schutz vor Formelinjektion.</summary>
    internal static string CsvLine(params string[] fields) => string.Join(";", fields.Select(CsvField));

    internal static string CsvField(string? value)
    {
        value ??= "";
        // Zellen, die mit =, +, - oder @ beginnen, würden von Tabellenkalkulationen als Formel interpretiert.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        var needsQuotes = value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0;
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}

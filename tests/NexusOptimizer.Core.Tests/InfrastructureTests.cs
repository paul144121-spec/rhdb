using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Chat;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Notifications;
using NexusOptimizer.Core.Profiles;
using NexusOptimizer.Core.Registry;
using NexusOptimizer.Core.Reporting;
using NexusOptimizer.Core.Security;
using NexusOptimizer.Core.Services;
using NexusOptimizer.Core.Settings;
using NexusOptimizer.Core.Updates;
using NexusOptimizer.Data;

namespace NexusOptimizer.Core.Tests;

public class PipeProtocolTests
{
    private static PipeRequestHandler Handler(out InMemoryRegistryStore registry, out FakeServiceManager services)
    {
        registry = new InMemoryRegistryStore();
        services = new FakeServiceManager().Add("Fax", ServiceStartMode.Automatic).Add("WinDefend", ServiceStartMode.Automatic);
        var store = registry;
        var actions = RegistryTweakCatalog.All.Select(t => (IOptimizationAction)new RegistryTweakAction(t, store)).Append(new ServiceStartupAction(services));
        return new PipeRequestHandler(new ActionCatalog(actions), () => new SystemContext { WindowsBuild = 22631, IsElevated = true });
    }

    [Fact]
    public async Task Messages_roundtrip_through_a_stream()
    {
        using var stream = new MemoryStream();
        var request = new PipeRequest { Operation = PipeOperation.Apply, ActionId = "reg.power.disable-fast-startup", Parameters = new() { ["a"] = "ä" } };
        await PipeProtocol.WriteAsync(stream, request);
        stream.Position = 0;

        var read = await PipeProtocol.ReadAsync<PipeRequest>(stream);

        Assert.Equal(request.RequestId, read!.RequestId);
        Assert.Equal("ä", read.Parameters["a"]);
        Assert.Null(await PipeProtocol.ReadAsync<PipeRequest>(stream)); // sauberes Verbindungsende
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(PipeProtocol.MaxMessageBytes + 1)]
    public async Task Invalid_frame_lengths_are_rejected_without_allocating(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length).Concat(new byte[8]).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeProtocol.ReadAsync<PipeRequest>(stream));
    }

    [Fact]
    public async Task Garbage_payload_is_rejected()
    {
        var payload = Encoding.UTF8.GetBytes("kein json");
        using var stream = new MemoryStream(BitConverter.GetBytes(payload.Length).Concat(payload).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeProtocol.ReadAsync<PipeRequest>(stream));
    }

    [Fact]
    public async Task Privileged_handler_executes_an_allowlisted_admin_action()
    {
        var handler = Handler(out var registry, out _);
        var tweak = RegistryTweakCatalog.Find("power.disable-fast-startup")!;

        var backup = await handler.HandleAsync(new PipeRequest { Operation = PipeOperation.Backup, ActionId = tweak.ActionId }, null);
        var apply = await handler.HandleAsync(new PipeRequest { Operation = PipeOperation.Apply, ActionId = tweak.ActionId }, null);
        var verify = await handler.HandleAsync(new PipeRequest { Operation = PipeOperation.Verify, ActionId = tweak.ActionId }, null);

        Assert.True(backup.Ok && apply.Ok && verify.Ok);
        Assert.True(JsonSerializer.Deserialize<VerificationResult>(verify.PayloadJson!)!.Verified);
        Assert.Equal(0u, registry.ReadValue(tweak.Root, tweak.View, tweak.KeyPath, tweak.ValueName)!.AsDWord());

        var payload = JsonSerializer.Deserialize<BackupData>(backup.PayloadJson!)!;
        var rollback = await handler.HandleAsync(new PipeRequest { Operation = PipeOperation.Rollback, ActionId = tweak.ActionId, BackupJson = payload.PayloadJson }, null);
        Assert.True(JsonSerializer.Deserialize<RollbackResult>(rollback.PayloadJson!)!.Success);
        Assert.Null(registry.ReadValue(tweak.Root, tweak.View, tweak.KeyPath, tweak.ValueName));
    }

    [Theory]
    [InlineData("cmd.exe /c calc")]
    [InlineData("shell.execute")]
    [InlineData("")]
    [InlineData("REG.POWER.DISABLE-FAST-STARTUP")]
    public async Task Unknown_or_malformed_action_ids_are_refused(string actionId)
    {
        var handler = Handler(out var registry, out _);
        var response = await handler.HandleAsync(new PipeRequest { Operation = PipeOperation.Apply, ActionId = actionId }, null);
        Assert.False(response.Ok);
        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public async Task User_level_actions_are_not_executed_by_the_privileged_component()
    {
        var handler = Handler(out var registry, out _);
        var response = await handler.HandleAsync(new PipeRequest { Operation = PipeOperation.Apply, ActionId = "reg.explorer.show-file-extensions" }, null);
        Assert.False(response.Ok);
        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public async Task Handler_revalidates_parameters_and_protected_targets_itself()
    {
        var handler = Handler(out _, out var services);

        var protectedService = await handler.HandleAsync(new PipeRequest { Operation = PipeOperation.Apply, ActionId = ServiceStartupAction.ActionId, Parameters = new() { ["service"] = "WinDefend", ["mode"] = "Disabled" } }, null);
        var extraParameter = await handler.HandleAsync(new PipeRequest { Operation = PipeOperation.Apply, ActionId = ServiceStartupAction.ActionId, Parameters = new() { ["service"] = "Fax", ["mode"] = "Manual", ["command"] = "calc" } }, null);
        var wrongVersion = await handler.HandleAsync(new PipeRequest { ProtocolVersion = 99, Operation = PipeOperation.Apply, ActionId = ServiceStartupAction.ActionId, Parameters = new() { ["service"] = "Fax", ["mode"] = "Manual" } }, null);
        var badOperation = await handler.HandleAsync(new PipeRequest { Operation = (PipeOperation)42, ActionId = ServiceStartupAction.ActionId }, null);

        Assert.False(protectedService.Ok);
        Assert.False(extraParameter.Ok);
        Assert.False(wrongVersion.Ok);
        Assert.False(badOperation.Ok);
        Assert.Equal(ServiceStartMode.Automatic, services.GetService("WinDefend")!.StartMode);
        Assert.Equal(ServiceStartMode.Automatic, services.GetService("Fax")!.StartMode);
    }

    [Fact]
    public async Task Rollback_payload_cannot_redirect_the_write_to_another_location()
    {
        var handler = Handler(out var registry, out _);
        var tweak = RegistryTweakCatalog.Find("power.disable-fast-startup")!;
        registry.WriteValue(tweak.Root, tweak.View, tweak.KeyPath, tweak.ValueName, tweak.OptimizedValue);
        // Manipulierte Sicherung mit zusätzlichen Feldern für einen fremden Pfad – diese werden ignoriert.
        const string forged = "{\"Version\":1,\"KeyExisted\":true,\"ValueExisted\":true,\"OriginalType\":0,\"OriginalData\":\"1\",\"IntendedType\":0,\"IntendedData\":\"0\",\"KeyPath\":\"SOFTWARE\\\\Microsoft\\\\Windows Defender\",\"ValueName\":\"DisableAntiSpyware\"}";

        var response = await handler.HandleAsync(new PipeRequest { Operation = PipeOperation.Rollback, ActionId = tweak.ActionId, BackupJson = forged }, null);

        Assert.True(response.Ok);
        Assert.Equal(1u, registry.ReadValue(tweak.Root, tweak.View, tweak.KeyPath, tweak.ValueName)!.AsDWord());
        Assert.False(registry.KeyExists(RegistryRoot.LocalMachine, RegistryViewKind.Registry64, @"SOFTWARE\Microsoft\Windows Defender"));
    }
}

public class UpdateVerifierTests
{
    private static (UpdateVerifier Verifier, ECDsa Key) Create()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (new UpdateVerifier(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())), key);
    }

    private static SignedManifest Sign(ECDsa key, UpdateManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest);
        return new SignedManifest(json, Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256)));
    }

    private static UpdateManifest Manifest(string version = "2.0.0", string url = "https://example.invalid/setup.exe") => new()
    {
        Version = version, DownloadUrl = url, Sha256 = new string('a', 64), SizeBytes = 10,
    };

    [Fact]
    public void Valid_signed_manifest_with_newer_version_is_accepted()
    {
        var (verifier, key) = Create();
        var result = verifier.Verify(Sign(key, Manifest()), new Version(1, 0, 0));
        Assert.True(result.IsValid);
        Assert.True(result.UpdateAvailable);
    }

    [Fact]
    public void Tampered_manifest_or_foreign_signature_is_rejected()
    {
        var (verifier, key) = Create();
        var signed = Sign(key, Manifest());
        Assert.False(verifier.Verify(signed with { ManifestJson = signed.ManifestJson.Replace("example.invalid", "evil.invalid") }, new Version(1, 0)).IsValid);

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(verifier.Verify(Sign(other, Manifest()), new Version(1, 0)).IsValid);
        Assert.False(verifier.Verify(signed with { SignatureBase64 = "###" }, new Version(1, 0)).IsValid);
    }

    [Fact]
    public void Downgrades_and_non_https_downloads_are_refused()
    {
        var (verifier, key) = Create();
        Assert.False(verifier.Verify(Sign(key, Manifest("0.9.0")), new Version(1, 0)).UpdateAvailable);
        Assert.False(verifier.Verify(Sign(key, Manifest(url: "http://example.invalid/setup.exe")), new Version(1, 0)).IsValid);
    }

    [Fact]
    public void Without_configured_key_no_update_is_ever_accepted()
    {
        var (_, key) = Create();
        var result = new UpdateVerifier(null).Verify(Sign(key, Manifest()), new Version(1, 0));
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Package_hash_and_size_must_match()
    {
        using var temp = new TempDirectory();
        var file = temp.File("setup.exe", 10, fill: 1);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

        Assert.True(await UpdateVerifier.VerifyPackageAsync(file, Manifest() with { Sha256 = hash, SizeBytes = 10 }));
        Assert.False(await UpdateVerifier.VerifyPackageAsync(file, Manifest() with { Sha256 = new string('0', 64), SizeBytes = 10 }));
        Assert.False(await UpdateVerifier.VerifyPackageAsync(file, Manifest() with { Sha256 = hash, SizeBytes = 11 }));
    }
}

public class RedactionAndReportingTests
{
    [Theory]
    [InlineData("Schlüssel sk-ant-api03-ABCDEFGHIJKLMNOP1234 verwendet", "sk-ant-api03")]
    [InlineData("Authorization: Bearer abcdef1234567890abcdef", "abcdef1234567890abcdef")]
    [InlineData("api_key=supergeheim123", "supergeheim123")]
    [InlineData("{\"password\":\"hunter2hunter2\"}", "hunter2hunter2")]
    [InlineData(@"Datei C:\Users\maria.mustermann\AppData\Local\x.log", "maria.mustermann")]
    public void Secrets_and_personal_paths_are_redacted(string input, string mustNotAppear)
        => Assert.DoesNotContain(mustNotAppear, LogRedactor.Redact(input));

    [Fact]
    public void Ordinary_text_is_left_intact()
        => Assert.Equal("CPU-Auslastung 42 % auf Laufwerk C:", LogRedactor.Redact("CPU-Auslastung 42 % auf Laufwerk C:"));

    private static TransactionRecord Record(string name = "Test", string original = "alt") => new()
    {
        Id = "t1", BatchId = "b1", Timestamp = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero), ActionId = "reg.test", ActionName = name,
        Status = TransactionStatus.Verified, Risk = RiskLevel.Low, OriginalDisplay = original, IntendedDisplay = "neu", IsReversible = true,
    };

    [Fact]
    public void Transaction_export_supports_all_three_formats()
    {
        var records = new[] { Record() };
        var json = ReportExporter.ExportTransactions(records, ReportFormat.Json);
        Assert.Equal("reg.test", JsonDocument.Parse(json).RootElement[0].GetProperty("Aktion").GetString());

        var csv = ReportExporter.ExportTransactions(records, ReportFormat.Csv);
        Assert.Equal(2, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("Angewendet und verifiziert", csv);

        var text = ReportExporter.ExportTransactions(records, ReportFormat.Text);
        Assert.Contains("Vorher:", text);
        Assert.Contains("alt", text);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")")]
    [InlineData("+cmd|' /C calc'!A0")]
    [InlineData("-2+3")]
    [InlineData("@SUM(1)")]
    public void Csv_export_neutralizes_formula_injection(string value)
    {
        var field = ReportExporter.CsvField(value);
        Assert.StartsWith("'", field.TrimStart('"'));
    }

    [Fact]
    public void Csv_fields_with_separators_and_quotes_are_escaped()
    {
        Assert.Equal("\"a;b\"", ReportExporter.CsvField("a;b"));
        Assert.Equal("\"sagt \"\"hallo\"\"\"", ReportExporter.CsvField("sagt \"hallo\""));
        Assert.Equal("normal", ReportExporter.CsvField("normal"));
    }

    [Fact]
    public void Recommendation_report_states_that_analysis_is_rule_based()
    {
        var recommendations = new[] { new Recommendation { Id = "r", Title = "Titel", Explanation = "Erklärung", Evidence = new[] { "Beleg 1" } } };
        var text = ReportExporter.ExportRecommendations(recommendations, AdvisorTests.Snapshot(), ReportFormat.Text);
        Assert.Contains("regelbasiert", text);
        Assert.Contains("Beleg 1", text);
        Assert.Contains("Test-CPU", text);
    }

    [Fact]
    public void Metric_export_writes_one_row_per_point()
    {
        var series = new Dictionary<MetricId, IReadOnlyList<MetricPoint>>
        {
            [MetricId.CpuTotal] = new[] { new MetricPoint(DateTimeOffset.UtcNow, 12.5), new MetricPoint(DateTimeOffset.UtcNow, 20) },
        };
        Assert.Equal(3, ReportExporter.ExportMetrics(series, ReportFormat.Csv).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("CPU-Auslastung", ReportExporter.ExportMetrics(series, ReportFormat.Json));
    }
}

public class DatabaseTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private string DbPath => Path.Combine(_temp.Path, "nexus.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private static TransactionRecord Record(string id, TransactionStatus status = TransactionStatus.Verified, string action = "a.one", string parameters = "{}") => new()
    {
        Id = id, BatchId = "batch", Timestamp = DateTimeOffset.UtcNow, ActionId = action, ActionName = "Name " + id, ParametersJson = parameters, Status = status,
        Risk = RiskLevel.Low, IsReversible = true, BackupJson = "sicherung", OriginalDisplay = "alt", IntendedDisplay = "neu", Source = ExecutionSource.OneClick, SourceLabel = "Etikett",
    };

    [Fact]
    public void Optimization_history_persists_across_application_restarts()
    {
        var journal = new SqliteTransactionJournal(new NexusDatabase(DbPath));
        journal.Insert(Record("1"));
        journal.Insert(Record("2", TransactionStatus.BackedUp));
        journal.Update(Record("1") with { Status = TransactionStatus.RolledBack, RolledBackAt = DateTimeOffset.UtcNow, ErrorDetails = "Notiz" });

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var reopened = new SqliteTransactionJournal(new NexusDatabase(DbPath)); // „Neustart“

        var recent = reopened.GetRecent();
        Assert.Equal(new[] { "2", "1" }, recent.Select(r => r.Id));
        var first = reopened.Get("1")!;
        Assert.Equal(TransactionStatus.RolledBack, first.Status);
        Assert.NotNull(first.RolledBackAt);
        Assert.Equal("sicherung", first.BackupJson);
        Assert.Equal(ExecutionSource.OneClick, first.Source);
        Assert.Equal("Etikett", first.SourceLabel);
        Assert.Equal("2", Assert.Single(reopened.GetIncomplete()).Id);
        Assert.Equal(2, reopened.GetBatch("batch").Count);
    }

    [Fact]
    public void Later_active_changes_are_found_for_the_same_action_and_parameters_only()
    {
        var journal = new SqliteTransactionJournal(new NexusDatabase(DbPath));
        var first = Record("1", parameters: "{\"plan\":\"x\"}");
        journal.Insert(first);
        journal.Insert(Record("2", parameters: "{\"plan\":\"x\"}"));
        journal.Insert(Record("3", parameters: "{\"plan\":\"y\"}"));
        journal.Insert(Record("4", TransactionStatus.RolledBack, parameters: "{\"plan\":\"x\"}"));
        journal.Insert(Record("5", action: "a.other", parameters: "{\"plan\":\"x\"}"));

        Assert.Equal("2", Assert.Single(journal.GetLaterActive(first)).Id);
        Assert.Throws<KeyNotFoundException>(() => journal.Update(Record("fehlt")));
    }

    [Fact]
    public async Task Engine_with_sqlite_journal_can_roll_back_after_restart()
    {
        var action = new FakeAction("a.one");
        var catalog = new ActionCatalog(new[] { action });
        var planner = new OptimizationPlanner(catalog);

        var journal = new SqliteTransactionJournal(new NexusDatabase(DbPath));
        var executor = new OptimizationExecutor(new LocalActionRunner(catalog), journal, new ExecutionGate());
        var plan = await planner.BuildAsync(new[] { new PlanRequest("a.one") }, EngineHarness.Permissive, new SystemContext(), ExecutionSource.User);
        Assert.True((await executor.ExecuteAsync(plan)).AllSucceeded);
        Assert.Equal("neu", action.State);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var journal2 = new SqliteTransactionJournal(new NexusDatabase(DbPath));
        var rollback = new RollbackService(new OptimizationExecutor(new LocalActionRunner(catalog), journal2, new ExecutionGate()), journal2);

        var outcome = await rollback.RollbackLastAsync();

        Assert.True(outcome!.Success);
        Assert.Equal("alt", action.State);
        Assert.Equal(TransactionStatus.RolledBack, journal2.GetRecent()[0].Status);
    }

    [Fact]
    public void Corrupted_database_file_is_set_aside_and_replaced()
    {
        File.WriteAllText(DbPath, "Dies ist keine SQLite-Datenbank, sondern Datenmüll. " + new string('x', 4096));

        var database = new NexusDatabase(DbPath);

        Assert.NotNull(database.CorruptBackupPath);
        Assert.True(File.Exists(database.CorruptBackupPath));
        var journal = new SqliteTransactionJournal(database);
        journal.Insert(Record("1"));
        Assert.Single(journal.GetRecent());
    }

    [Fact]
    public void Settings_roundtrip_and_corrupted_settings_fall_back_to_defaults()
    {
        var database = new NexusDatabase(DbPath);
        var store = new SqliteSettingsStore(database);
        Assert.Equal("Dark", store.Load().Theme);

        store.Save(new AppSettings { Theme = "Light", MaxInteractiveRisk = RiskLevel.Moderate, LatencyEndpoints = new List<string> { "1.1.1.1" }, AiMode = AiMode.Troubleshooting });
        var loaded = new SqliteSettingsStore(database).Load();
        Assert.Equal("Light", loaded.Theme);
        Assert.Equal(RiskLevel.Moderate, loaded.MaxInteractiveRisk);
        Assert.Equal(AiMode.Troubleshooting, loaded.AiMode);
        Assert.Equal("1.1.1.1", Assert.Single(loaded.LatencyEndpoints));

        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE documents SET json = '{ kaputt' WHERE collection = 'settings';";
            command.ExecuteNonQuery();
        }
        var service = new SettingsService(store);
        Assert.Equal("Dark", service.Current.Theme);
    }

    [Fact]
    public void Metric_history_supports_range_queries_downsampling_and_retention()
    {
        var store = new SqliteMetricHistoryStore(new NexusDatabase(DbPath));
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 100; i++)
            store.Append(start.AddSeconds(i * 15), new Dictionary<MetricId, double> { [MetricId.CpuTotal] = i, [MetricId.GpuTemperature] = double.NaN });

        Assert.Equal(100, store.Query(MetricId.CpuTotal, start, start.AddHours(1)).Count);
        Assert.Empty(store.Query(MetricId.GpuTemperature, start, start.AddHours(1))); // NaN wird nie gespeichert

        var reduced = store.Query(MetricId.CpuTotal, start, start.AddHours(1), maxPoints: 10);
        Assert.Equal(10, reduced.Count);
        Assert.Equal(4.5, reduced[0].Value, 3);
        Assert.Equal(49.5, reduced.Average(p => p.Value), 3);

        Assert.Equal(40, store.Prune(start.AddSeconds(40 * 15)));
        Assert.Equal(60, store.Query(MetricId.CpuTotal, start, start.AddHours(1)).Count);
    }

    [Fact]
    public void Document_stores_roundtrip_profiles_feedback_notifications_and_chat()
    {
        var database = new NexusDatabase(DbPath);

        var profiles = new SqliteProfileStore(database);
        var profile = new OptimizationProfile { Name = "Meins", PowerPlan = "balanced", Items = { new ProfileItem { ActionId = "reg.x", Parameters = { ["a"] = "1" } } } };
        profiles.Save(profile);
        profiles.SetActive(profile.Id, "batch");
        Assert.Equal("1", Assert.Single(new SqliteProfileStore(database).GetCustomProfiles()).Items[0].Parameters["a"]);
        Assert.Equal((profile.Id, "batch"), new SqliteProfileStore(database).GetActive());

        var feedback = new SqliteFeedbackStore(database);
        feedback.Record("a.one", FeedbackKind.Declined);
        feedback.Record("a.two", FeedbackKind.Declined);
        Thread.Sleep(5);
        feedback.Record("a.two", FeedbackKind.Accepted); // spätere Zustimmung hebt die Ablehnung auf
        Assert.Equal(new[] { "a.one" }, feedback.GetDeclined());

        var notifications = new NotificationCenter(new SqliteNotificationStore(database));
        notifications.Publish(NotificationKind.OptimizationCompleted, "Fertig", "Text", NotificationSeverity.Success);
        Assert.Equal(1, notifications.UnreadCount);
        notifications.MarkAllRead();
        Assert.Equal(0, notifications.UnreadCount);

        var chat = new SqliteChatStore(database);
        chat.Append(new ChatMessageRecord { Role = ChatRole.User, Text = "Hallo", Timestamp = DateTimeOffset.UtcNow.AddSeconds(-1) });
        chat.Append(new ChatMessageRecord { Role = ChatRole.Assistant, Text = "Guten Tag", Kind = ChatMessageKind.Explanation });
        Assert.Equal(new[] { "Hallo", "Guten Tag" }, chat.GetAll().Select(m => m.Text));
        chat.DeleteAll();
        Assert.Empty(chat.GetAll());

        database.DeleteAllData();
        Assert.Empty(new SqliteProfileStore(database).GetCustomProfiles());
    }
}

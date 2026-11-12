using System.Text.Json;
using System.Text.Json.Serialization;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Core.Registry;

namespace NexusOptimizer.Core.Profiles;

public sealed record ProfileItem
{
    public string ActionId { get; init; } = "";
    public Dictionary<string, string> Parameters { get; init; } = new();

    public PlanRequest ToRequest() => new(ActionId, new ActionParameters(Parameters));
    public string Key => ToRequest().Key;
}

public sealed record OptimizationProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsBuiltIn { get; init; }
    public OptimizationGoal Goal { get; init; } = OptimizationGoal.BalancedPerformance;
    /// <summary>„balanced“, „high-performance“, „ultimate“, „power-saver“ oder null (Energieplan nicht ändern).</summary>
    public string? PowerPlan { get; init; }
    public List<ProfileItem> Items { get; init; } = new();
    /// <summary>Dateinamen ausführbarer Dateien (z. B. „spiel.exe“), bei deren Start das Profil vorgeschlagen bzw. automatisch aktiviert wird.</summary>
    public List<string> AssignedApplications { get; init; } = new();
    /// <summary>Tägliche Aktivierungszeit im Format HH:mm oder null.</summary>
    public string? ScheduleTime { get; init; }
}

public static class BuiltInProfiles
{
    public const string Standard = "builtin-standard";
    public const string Balanced = "builtin-balanced";
    public const string Gaming = "builtin-gaming";
    public const string Competitive = "builtin-competitive";
    public const string MaximumPerformance = "builtin-max-performance";
    public const string Quiet = "builtin-quiet";
    public const string BatterySaver = "builtin-battery";
    public const string Productivity = "builtin-productivity";
    public const string Streaming = "builtin-streaming";
    public const string ContentCreation = "builtin-content";
    public const string Custom = "builtin-custom";
    public const string Experimental = "builtin-experimental";

    private static ProfileItem Tweak(string id) => new() { ActionId = "reg." + id };
    private static ProfileItem Power(string setting, int? ac, int? dc)
    {
        var parameters = new Dictionary<string, string> { ["setting"] = setting };
        if (ac is not null) parameters["ac"] = ac.Value.ToString();
        if (dc is not null) parameters["dc"] = dc.Value.ToString();
        return new ProfileItem { ActionId = PowerSettingAction.ActionId, Parameters = parameters };
    }

    private static List<ProfileItem> Tagged(params string[] tags)
        => RegistryTweakCatalog.All.Where(t => !t.Experimental && t.Risk <= RiskLevel.Low && t.Tags.Any(tag => tags.Contains(tag))).Select(t => new ProfileItem { ActionId = t.ActionId }).ToList();

    public static IReadOnlyList<OptimizationProfile> All { get; } = Build();

    public static OptimizationProfile? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    private static IReadOnlyList<OptimizationProfile> Build() => new[]
    {
        new OptimizationProfile { Id = Standard, Name = "Standard", IsBuiltIn = true, Goal = OptimizationGoal.Stability,
            Description = "Keine Änderungen. Beim Aktivieren werden die Änderungen des zuvor aktiven Profils zurückgenommen." },
        new OptimizationProfile { Id = Balanced, Name = "Ausbalanciert", IsBuiltIn = true, Goal = OptimizationGoal.BalancedPerformance, PowerPlan = "balanced",
            Description = "Risikoarme Einstellungen für den Alltag: weniger Vorschläge und Hintergrundaktivität, ausgewogener Energieplan.",
            Items = Tagged(RegistryTweakCatalog.TagSafe, RegistryTweakCatalog.TagBalanced) },
        new OptimizationProfile { Id = Gaming, Name = "Gaming", IsBuiltIn = true, Goal = OptimizationGoal.MaximumFps, PowerPlan = "high-performance",
            Description = "Spielmodus, reduzierte Aufzeichnungslast und leistungsorientierter Energieplan. Erhöht Verbrauch und Lautstärke.",
            Items = Tagged(RegistryTweakCatalog.TagGaming) },
        new OptimizationProfile { Id = Competitive, Name = "Wettkampf-Gaming", IsBuiltIn = true, Goal = OptimizationGoal.StableFrametimes, PowerPlan = "high-performance",
            Description = "Wie „Gaming“, zusätzlich ohne Transparenz und Animationen sowie mit kürzerer Menüverzögerung für eine möglichst direkte Oberfläche.",
            Items = Tagged(RegistryTweakCatalog.TagGaming, RegistryTweakCatalog.TagLowEnd).Append(Tweak("responsiveness.menu-show-delay")).DistinctBy(i => i.ActionId).ToList() },
        new OptimizationProfile { Id = MaximumPerformance, Name = "Maximale Leistung", IsBuiltIn = true, Goal = OptimizationGoal.MaximumFps, PowerPlan = "ultimate",
            Description = "Stärkster verfügbarer Energieplan und reduzierte optische Effekte. Deutlich höherer Stromverbrauch und mehr Abwärme.",
            Items = Tagged(RegistryTweakCatalog.TagPerformance, RegistryTweakCatalog.TagLowEnd) },
        new OptimizationProfile { Id = Quiet, Name = "Leise", IsBuiltIn = true, Goal = OptimizationGoal.LowNoise, PowerPlan = "balanced",
            Description = "Begrenzt den maximalen Prozessorzustand auf 99 %, wodurch der Turbo-Takt entfällt. Senkt Temperatur und Lüfterdrehzahl auf Kosten der Spitzenleistung.",
            Items = new List<ProfileItem> { Power(PowerSettingCatalog.ProcessorMax, 99, 99) } },
        new OptimizationProfile { Id = BatterySaver, Name = "Akkusparen", IsBuiltIn = true, Goal = OptimizationGoal.BatteryLife, PowerPlan = "power-saver",
            Description = "Sparsamer Energieplan, kürzere Bildschirm-Abschaltzeit im Akkubetrieb und weniger optische Effekte.",
            Items = new List<ProfileItem> { Power(PowerSettingCatalog.DisplayTimeout, null, 120), Power(PowerSettingCatalog.ProcessorMax, null, 80), Tweak("visual.disable-transparency"), Tweak("background.disable-background-apps") } },
        new OptimizationProfile { Id = Productivity, Name = "Produktivität", IsBuiltIn = true, Goal = OptimizationGoal.Productivity, PowerPlan = "balanced",
            Description = "Aufgeräumte Arbeitsumgebung: Dateiendungen sichtbar, weniger Vorschläge, Tipps und Unterbrechungen.",
            Items = new List<ProfileItem>
            {
                Tweak("explorer.show-file-extensions"), Tweak("explorer.disable-sync-provider-ads"), Tweak("privacy.disable-start-suggestions"), Tweak("privacy.disable-windows-tips"),
                Tweak("privacy.disable-settings-suggestions"), Tweak("privacy.disable-welcome-experience"), Tweak("notifications.disable-setup-suggestions"), Tweak("search.disable-search-highlights"),
            } },
        new OptimizationProfile { Id = Streaming, Name = "Streaming", IsBuiltIn = true, Goal = OptimizationGoal.StableFrametimes, PowerPlan = "high-performance",
            Description = "Spielmodus und Leistungsplan – die Aufnahmefunktionen von Windows bleiben bewusst aktiv.",
            Items = new List<ProfileItem> { Tweak("gaming.enable-game-mode"), Tweak("gaming.disable-game-bar-tips") } },
        new OptimizationProfile { Id = ContentCreation, Name = "Inhaltserstellung", IsBuiltIn = true, Goal = OptimizationGoal.Productivity, PowerPlan = "high-performance",
            Description = "Leistungsplan und kein automatischer Energiesparmodus im Netzbetrieb, damit lange Render- und Exportvorgänge nicht unterbrochen werden.",
            Items = new List<ProfileItem> { Power(PowerSettingCatalog.SleepTimeout, 0, null), Tweak("explorer.show-file-extensions") } },
        new OptimizationProfile { Id = Custom, Name = "Benutzerdefiniert", IsBuiltIn = true, Goal = OptimizationGoal.BalancedPerformance,
            Description = "Leere Vorlage. Klonen Sie dieses Profil und stellen Sie Ihre eigene Auswahl zusammen." },
        new OptimizationProfile { Id = Experimental, Name = "Experimentell", IsBuiltIn = true, Goal = OptimizationGoal.MaximumFps,
            Description = "Übersicht der experimentellen Änderungen. Diese können nicht als Profil aktiviert werden, sondern nur einzeln und nach Freischaltung im Expertenlabor.",
            Items = RegistryTweakCatalog.All.Where(t => t.Experimental).Select(t => new ProfileItem { ActionId = t.ActionId }).ToList() },
    };
}

public sealed record ProfileImportResult(OptimizationProfile? Profile, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool Success => Profile is not null && Errors.Count == 0;
}

/// <summary>Export und streng validierter Import von Profilen. Ein Import führt nie etwas aus.</summary>
public static class ProfileSerializer
{
    public const int MaxImportBytes = 256 * 1024;
    private static readonly string[] AllowedPowerPlans = { "balanced", "high-performance", "ultimate", "power-saver" };

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    public static string Export(OptimizationProfile profile) => JsonSerializer.Serialize(profile with { IsBuiltIn = false }, Options);

    public static ProfileImportResult Import(string json, ActionCatalog catalog)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return new ProfileImportResult(null, new[] { "Die Datei ist leer." }, warnings);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxImportBytes) return new ProfileImportResult(null, new[] { "Die Profildatei ist zu groß." }, warnings);

        OptimizationProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<OptimizationProfile>(json, Options);
        }
        catch (JsonException ex)
        {
            return new ProfileImportResult(null, new[] { "Die Datei ist kein gültiges Profil: " + ex.Message }, warnings);
        }
        if (profile is null) return new ProfileImportResult(null, new[] { "Die Datei enthält kein Profil." }, warnings);

        if (profile.SchemaVersion != OptimizationProfile.CurrentSchemaVersion) errors.Add($"Nicht unterstützte Profilversion {profile.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80 || profile.Name.Any(char.IsControl)) errors.Add("Der Profilname fehlt oder ist ungültig.");
        if (profile.Description is { Length: > 1000 } || (profile.Description?.Any(c => char.IsControl(c) && c is not ('\n' or '\r')) ?? false)) errors.Add("Die Profilbeschreibung ist ungültig.");
        if (profile.PowerPlan is not null && !AllowedPowerPlans.Contains(profile.PowerPlan)) errors.Add("Unbekannter Energieplan im Profil.");
        if (profile.Items is null || profile.Items.Count > 200) errors.Add("Das Profil enthält zu viele oder keine gültigen Einträge.");
        if (profile.AssignedApplications is { Count: > 50 } || (profile.AssignedApplications?.Any(a => a.Length > 100 || a.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || a.Contains('\\') || a.Contains('/')) ?? false))
            errors.Add("Die zugewiesenen Anwendungen sind ungültig.");
        if (profile.ScheduleTime is not null && !TimeOnly.TryParseExact(profile.ScheduleTime, "HH:mm", out _)) errors.Add("Die geplante Uhrzeit ist ungültig.");
        if (errors.Count > 0) return new ProfileImportResult(null, errors, warnings);

        var kept = new List<ProfileItem>();
        foreach (var item in profile.Items!)
        {
            if (item is null || !ActionCatalog.IsValidActionId(item.ActionId) || catalog.Find(item.ActionId) is not { } action)
            {
                errors.Add($"Unbekannte Aktion „{Shorten(item?.ActionId)}“ – das Profil wurde nicht importiert.");
                continue;
            }
            var validation = action.ValidateParameters(new ActionParameters(item.Parameters ?? new()));
            if (!validation.IsValid)
            {
                errors.Add($"Ungültige Parameter für „{action.Metadata.Name}“: {validation.ErrorText}");
                continue;
            }
            if (!action.Metadata.IsReversible)
            {
                errors.Add($"„{action.Metadata.Name}“ ist nicht umkehrbar und darf nicht Teil eines Profils sein.");
                continue;
            }
            if (action.Metadata.IsExperimental) warnings.Add($"„{action.Metadata.Name}“ ist experimentell und wird bei der Profilaktivierung nicht ausgeführt.");
            else if (action.Metadata.Risk >= RiskLevel.Moderate) warnings.Add($"„{action.Metadata.Name}“ hat die Risikostufe „{action.Metadata.Risk.ToGerman()}“ und wird nur ausgeführt, wenn Ihre Risikofreigabe das zulässt.");
            kept.Add(item with { Parameters = item.Parameters ?? new() });
        }
        if (errors.Count > 0) return new ProfileImportResult(null, errors, warnings);

        // Importierte Profile erhalten immer eine neue ID und gelten nie als eingebaut.
        var imported = profile with { Id = Guid.NewGuid().ToString("N"), IsBuiltIn = false, Items = kept, Name = profile.Name.Trim() };
        return new ProfileImportResult(imported, errors, warnings);
    }

    private static string Shorten(string? value) => string.IsNullOrEmpty(value) ? "(leer)" : value.Length <= 60 ? value : value[..60] + "…";
}

public interface IProfileStore
{
    IReadOnlyList<OptimizationProfile> GetCustomProfiles();
    void Save(OptimizationProfile profile);
    void Delete(string profileId);
    (string? ProfileId, string? BatchId) GetActive();
    void SetActive(string? profileId, string? batchId);
}

public sealed class InMemoryProfileStore : IProfileStore
{
    private readonly Dictionary<string, OptimizationProfile> _profiles = new();
    private (string?, string?) _active;
    public IReadOnlyList<OptimizationProfile> GetCustomProfiles() => _profiles.Values.ToList();
    public void Save(OptimizationProfile profile) => _profiles[profile.Id] = profile;
    public void Delete(string profileId) => _profiles.Remove(profileId);
    public (string? ProfileId, string? BatchId) GetActive() => _active;
    public void SetActive(string? profileId, string? batchId) => _active = (profileId, batchId);
}

public sealed record ProfileDifference(string Title, string? Left, string? Right);

public sealed record ProfileActivationResult(bool Success, string Message, OptimizationPlan? Plan = null, BatchResult? Batch = null, IReadOnlyList<RollbackOutcome>? Restored = null);

/// <summary>Verwaltung, Vergleich, Aktivierung und Zurücksetzen von Optimierungsprofilen.</summary>
public sealed class ProfileManager
{
    private readonly IProfileStore _store;
    private readonly ActionCatalog _catalog;
    private readonly OptimizationPlanner _planner;
    private readonly OptimizationExecutor _executor;
    private readonly RollbackService _rollback;
    private readonly IPowerPolicy _power;

    public ProfileManager(IProfileStore store, ActionCatalog catalog, OptimizationPlanner planner, OptimizationExecutor executor, RollbackService rollback, IPowerPolicy power)
    {
        _store = store;
        _catalog = catalog;
        _planner = planner;
        _executor = executor;
        _rollback = rollback;
        _power = power;
    }

    public event EventHandler? ActiveProfileChanged;

    public IReadOnlyList<OptimizationProfile> GetAll() => BuiltInProfiles.All.Concat(_store.GetCustomProfiles().OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)).ToList();
    public OptimizationProfile? Find(string id) => GetAll().FirstOrDefault(p => p.Id == id);
    public OptimizationProfile? ActiveProfile => _store.GetActive().ProfileId is { } id ? Find(id) : null;

    public OptimizationProfile Create(string name, string description = "")
    {
        var profile = new OptimizationProfile { Name = UniqueName(name), Description = description };
        _store.Save(profile);
        return profile;
    }

    public OptimizationProfile Clone(OptimizationProfile source, string? name = null)
    {
        var items = source.Items.Where(i => _catalog.Find(i.ActionId) is { Metadata.IsExperimental: false }).Select(i => i with { Parameters = new Dictionary<string, string>(i.Parameters) }).ToList();
        var clone = source with { Id = Guid.NewGuid().ToString("N"), IsBuiltIn = false, Name = UniqueName(name ?? source.Name + " (Kopie)"), Items = items, AssignedApplications = new List<string>(source.AssignedApplications) };
        _store.Save(clone);
        return clone;
    }

    public void Update(OptimizationProfile profile)
    {
        if (profile.IsBuiltIn) throw new InvalidOperationException("Eingebaute Profile können nicht geändert werden. Klonen Sie das Profil.");
        _store.Save(profile);
    }

    public void Delete(OptimizationProfile profile)
    {
        if (profile.IsBuiltIn) throw new InvalidOperationException("Eingebaute Profile können nicht gelöscht werden.");
        _store.Delete(profile.Id);
    }

    private string UniqueName(string name)
    {
        var existing = GetAll().Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!existing.Contains(name)) return name;
        for (var i = 2; ; i++) if (!existing.Contains($"{name} {i}")) return $"{name} {i}";
    }

    /// <summary>Übersetzt die Energieplan-Präferenz in einen auf diesem System vorhandenen Plan.</summary>
    public PowerPlanInfo? ResolvePowerPlan(string? preference, out string? note)
    {
        note = null;
        if (preference is null) return null;
        var plans = _power.GetPlans();
        PowerPlanInfo? Find(Guid id) => plans.FirstOrDefault(p => p.Id == id);
        var chain = preference switch
        {
            "ultimate" => new[] { WellKnownPowerPlans.UltimatePerformance, WellKnownPowerPlans.HighPerformance },
            "high-performance" => new[] { WellKnownPowerPlans.HighPerformance, WellKnownPowerPlans.UltimatePerformance },
            "power-saver" => new[] { WellKnownPowerPlans.PowerSaver },
            _ => new[] { WellKnownPowerPlans.Balanced },
        };
        var plan = chain.Select(Find).FirstOrDefault(p => p is not null);
        if (plan is null) note = "Der gewünschte Energieplan ist auf diesem System nicht vorhanden; der Energieplan bleibt unverändert.";
        else if (plan.Id != chain[0]) note = $"Der bevorzugte Energieplan ist nicht vorhanden; stattdessen wird „{plan.Name}“ verwendet.";
        return plan;
    }

    public IReadOnlyList<PlanRequest> BuildRequests(OptimizationProfile profile, out string? note)
    {
        var requests = new List<PlanRequest>();
        if (ResolvePowerPlan(profile.PowerPlan, out note) is { } plan)
            requests.Add(new PlanRequest(PowerPlanActivateAction.ActionId, ActionParameters.Of(("plan", plan.Id.ToString())), $"Energieplan des Profils „{profile.Name}“"));
        requests.AddRange(profile.Items.Select(i => i.ToRequest() with { Reason = $"Profil „{profile.Name}“" }));
        return requests;
    }

    public async Task<(OptimizationPlan Plan, string? Note)> PreviewAsync(OptimizationProfile profile, RiskPolicy policy, SystemContext context, ExecutionSource source = ExecutionSource.Profile, CancellationToken cancellationToken = default)
    {
        var requests = BuildRequests(profile, out var note);
        var plan = await _planner.BuildAsync(requests, policy, context, source, cancellationToken).ConfigureAwait(false);
        return (plan, note);
    }

    /// <summary>
    /// Aktiviert ein Profil: Zuerst werden die Änderungen des bisher aktiven Profils zurückgenommen, dann wird das neue Profil
    /// über Planer und Ausführungs-Engine angewendet. Risikoprüfungen gelten unverändert.
    /// </summary>
    public async Task<ProfileActivationResult> ActivateAsync(OptimizationProfile profile, RiskPolicy policy, SystemContext context, ExecutionSource source = ExecutionSource.Profile,
        IProgress<ExecutionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var restored = await ResetActiveAsync().ConfigureAwait(false);
        if (restored.Any(r => !r.Success && !r.Conflict))
            return new ProfileActivationResult(false, "Das bisherige Profil konnte nicht vollständig zurückgesetzt werden. Das neue Profil wurde nicht aktiviert.", Restored: restored);

        var (plan, note) = await PreviewAsync(profile, policy, context, source, cancellationToken).ConfigureAwait(false);
        if (plan.IsEmpty)
        {
            _store.SetActive(profile.Id, null);
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
            var notPermitted = plan.Rejected.Count(r => r.Kind == RejectionKind.NotPermitted);
            var message = notPermitted > 0
                ? $"Profil „{profile.Name}“ aktiv. {notPermitted} Einträge wurden wegen Ihrer Risikofreigabe nicht ausgeführt; alle übrigen Einstellungen lagen bereits vor."
                : $"Profil „{profile.Name}“ aktiv. Alle Einstellungen lagen bereits vor – es wurde nichts geändert.";
            return new ProfileActivationResult(true, Append(message, note), plan, null, restored);
        }

        var batch = await _executor.ExecuteAsync(plan, new ExecutionOptions { Progress = progress, SourceLabel = profile.Name }, cancellationToken).ConfigureAwait(false);
        if (batch.Busy) return new ProfileActivationResult(false, "Es läuft bereits ein Optimierungsvorgang.", plan, batch, restored);

        _store.SetActive(profile.Id, batch.BatchId);
        ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
        var summary = batch.FailedCount == 0
            ? $"Profil „{profile.Name}“ aktiviert: {batch.SucceededCount} Änderungen angewendet und verifiziert."
            : $"Profil „{profile.Name}“ teilweise aktiviert: {batch.SucceededCount} erfolgreich, {batch.FailedCount} fehlgeschlagen.";
        return new ProfileActivationResult(batch.FailedCount == 0, Append(summary, note), plan, batch, restored);
    }

    /// <summary>„Profil zurücksetzen“: nimmt alle Änderungen des aktiven Profils zurück.</summary>
    public async Task<IReadOnlyList<RollbackOutcome>> ResetActiveAsync()
    {
        var (profileId, batchId) = _store.GetActive();
        IReadOnlyList<RollbackOutcome> outcomes = Array.Empty<RollbackOutcome>();
        if (batchId is not null) outcomes = await _rollback.RollbackBatchAsync(batchId).ConfigureAwait(false);
        if (profileId is not null && outcomes.All(o => o.Success || o.Conflict))
        {
            _store.SetActive(null, null);
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
        }
        return outcomes;
    }

    public IReadOnlyList<ProfileDifference> Compare(OptimizationProfile left, OptimizationProfile right)
    {
        var differences = new List<ProfileDifference>();
        static string PlanName(string? p) => p switch { "balanced" => "Ausbalanciert", "high-performance" => "Höchstleistung", "ultimate" => "Ultimative Leistung", "power-saver" => "Energiesparmodus", _ => "unverändert" };
        if (left.PowerPlan != right.PowerPlan) differences.Add(new ProfileDifference("Energieplan", PlanName(left.PowerPlan), PlanName(right.PowerPlan)));
        if (left.Goal != right.Goal) differences.Add(new ProfileDifference("Ziel", left.Goal.ToGerman(), right.Goal.ToGerman()));

        static string Describe(ProfileItem item)
        {
            return item.Parameters.Count == 0 ? "Enthalten" : string.Join(", ", item.Parameters.Select(p => $"{p.Key}={p.Value}"));
        }
        string Title(ProfileItem item)
        {
            var name = _catalog.Find(item.ActionId)?.Metadata.Name ?? item.ActionId;
            return item.Parameters.TryGetValue("setting", out var setting) ? $"{name}: {PowerSettingCatalog.Find(setting)?.Name ?? setting}" : name;
        }
        static string Target(ProfileItem item) => item.ActionId + "|" + (item.Parameters.TryGetValue("setting", out var s) ? s : "");

        var leftItems = left.Items.GroupBy(Target).ToDictionary(g => g.Key, g => g.Last());
        var rightItems = right.Items.GroupBy(Target).ToDictionary(g => g.Key, g => g.Last());
        foreach (var key in leftItems.Keys.Union(rightItems.Keys))
        {
            leftItems.TryGetValue(key, out var l);
            rightItems.TryGetValue(key, out var r);
            if (l is not null && r is not null && l.Key == r.Key) continue;
            differences.Add(new ProfileDifference(Title(l ?? r!), l is null ? null : Describe(l), r is null ? null : Describe(r)));
        }
        return differences;
    }

    private static string Append(string message, string? note) => note is null ? message : message + " " + note;
}

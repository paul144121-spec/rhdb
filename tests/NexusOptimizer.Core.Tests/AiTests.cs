using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using NexusOptimizer.AI;
using NexusOptimizer.AI.Providers;
using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Chat;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Hardware;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Core.Registry;
using NexusOptimizer.Core.Settings;
using NexusOptimizer.Core.Startup;

namespace NexusOptimizer.Core.Tests;

public class GermanIntentTests
{
    [Theory]
    [InlineData("Optimiere meinen PC für Gaming.", ChatIntent.OptimizeGaming)]
    [InlineData("Warum ist meine CPU-Auslastung so hoch?", ChatIntent.ExplainCpu)]
    [InlineData("Welche Programme verlangsamen meinen PC?", ChatIntent.SlowPrograms)]
    [InlineData("Analysiere meine RAM-Auslastung.", ChatIntent.AnalyzeMemory)]
    [InlineData("Erstelle ein Gaming-Profil für dieses Spiel.", ChatIntent.CreateGameProfile)]
    [InlineData("Welche Einstellungen sollte ich ändern?", ChatIntent.RecommendSettings)]
    [InlineData("Zeige mir alle riskanten Tweaks.", ChatIntent.ListRiskyTweaks)]
    [InlineData("Mache die letzte Optimierung rückgängig.", ChatIntent.UndoLast)]
    [InlineData("Warum ist mein Internet so langsam?", ChatIntent.SlowInternet)]
    [InlineData("Überwache meine Temperaturen.", ChatIntent.Temperatures)]
    [InlineData("Erstelle einen automatischen Optimierungsplan.", ChatIntent.AutomationPlan)]
    [InlineData("Mein Laufwerk ist fast voll, kannst du aufräumen?", ChatIntent.Storage)]
    [InlineData("Mein PC fährt so langsam hoch – was ist im Autostart?", ChatIntent.Startup)]
    [InlineData("Wie kann ich den Akku schonen?", ChatIntent.Power)]
    [InlineData("Was ist mit Datenschutz und Telemetrie?", ChatIntent.Privacy)]
    [InlineData("Hallo!", ChatIntent.Greeting)]
    [InlineData("Was kannst du alles?", ChatIntent.Help)]
    [InlineData("Wie wird das Wetter morgen in Köln?", ChatIntent.Unknown)]
    [InlineData("", ChatIntent.Unknown)]
    public void Recognizes_the_documented_german_requests(string text, ChatIntent expected)
        => Assert.Equal(expected, GermanIntentParser.Parse(text));

    [Fact]
    public void Normalization_folds_umlauts_and_does_not_match_short_words_inside_longer_ones()
    {
        Assert.Equal(" uberhitzt mein gerat heiss ", GermanIntentParser.Normalize("Überhitzt mein Gerät? Heiß!"));
        Assert.NotEqual(ChatIntent.Greeting, GermanIntentParser.Parse("Hilfe"));       // „hi“ steckt in „Hilfe“
        Assert.NotEqual(ChatIntent.AnalyzeMemory, GermanIntentParser.Parse("Programme")); // „ram“ steckt in „Programme“
    }
}

public class ProposalParserTests
{
    private static ActionCatalog Catalog()
    {
        var registry = new InMemoryRegistryStore();
        var actions = RegistryTweakCatalog.All.Select(t => (IOptimizationAction)new RegistryTweakAction(t, registry)).ToList();
        actions.Add(new PowerSettingAction(new FakePowerPolicy()));
        return new ActionCatalog(actions);
    }

    private static string Block(string json) => $"Hier ist meine Einschätzung.\n```nexus-aktionen\n{json}\n```";

    [Fact]
    public void Valid_proposals_are_accepted_and_the_block_is_removed_from_the_visible_text()
    {
        var result = AiProposalParser.Parse(Block("[{\"aktion\":\"reg.gaming.enable-game-mode\",\"parameter\":{},\"begruendung\":\"Spielmodus ist aus\"},{\"typ\":\"rueckgaengig\"},{\"typ\":\"seite\",\"seite\":\"cleanup\"}]"), Catalog());

        Assert.Equal("Hier ist meine Einschätzung.", result.VisibleText);
        Assert.Equal(new[] { ProposalKind.Action, ProposalKind.RollbackLast, ProposalKind.OpenPage }, result.Proposals.Select(p => p.Kind));
        Assert.Equal("reg.gaming.enable-game-mode", result.Proposals[0].Requests.Single().ActionId);
        Assert.Empty(result.Rejected);
    }

    [Theory]
    [InlineData("[{\"aktion\":\"cmd.exe /c format c:\"}]")]
    [InlineData("[{\"aktion\":\"shell.run\",\"parameter\":{\"command\":\"powershell -enc AAAA\"}}]")]
    [InlineData("[{\"aktion\":\"reg.gaming.enable-game-mode\",\"parameter\":{\"pfad\":\"HKLM\\\\SOFTWARE\\\\Microsoft\\\\Windows Defender\"}}]")]
    [InlineData("[{\"aktion\":\"power.setting.set\",\"parameter\":{\"setting\":\"processor-max\",\"ac\":999}}]")]
    [InlineData("[{\"aktion\":\"power.setting.set\",\"parameter\":{\"setting\":{\"verschachtelt\":true}}}]")]
    [InlineData("[{\"aktion\":\"reg.expert.mmcss-network-throttling\"}]")]
    [InlineData("[{\"typ\":\"seite\",\"seite\":\"../../windows/system32\"}]")]
    [InlineData("[{\"typ\":\"ausfuehren\",\"befehl\":\"calc\"}]")]
    [InlineData("{\"aktion\":\"reg.gaming.enable-game-mode\"}")]
    [InlineData("das ist kein json")]
    [InlineData("[42, \"text\"]")]
    public void Untrusted_model_output_cannot_smuggle_in_anything_executable(string json)
    {
        var result = AiProposalParser.Parse(Block(json), Catalog());
        Assert.Empty(result.Proposals);
        Assert.NotEmpty(result.Rejected);
    }

    [Fact]
    public void Number_of_proposals_and_block_size_are_limited()
    {
        var many = "[" + string.Join(",", Enumerable.Repeat("{\"typ\":\"rueckgaengig\"}", 30)) + "]";
        Assert.Equal(AiProposalParser.MaxProposals, AiProposalParser.Parse(Block(many), Catalog()).Proposals.Count);

        var huge = "[{\"typ\":\"rueckgaengig\",\"begruendung\":\"" + new string('x', AiProposalParser.MaxBlockLength) + "\"}]";
        Assert.Empty(AiProposalParser.Parse(Block(huge), Catalog()).Proposals);
    }

    [Fact]
    public void Reason_text_is_sanitized_and_text_without_block_passes_through()
    {
        var result = AiProposalParser.Parse(Block("[{\"typ\":\"rueckgaengig\",\"begruendung\":\"Zeile1\\nZeile2\\u0007" + new string('a', 600) + "\"}]"), Catalog());
        var description = result.Proposals.Single().Description;
        Assert.DoesNotContain('\n', description);
        Assert.DoesNotContain('\u0007', description);
        Assert.True(description.Length <= 401);

        var plain = AiProposalParser.Parse("Nur Text mit ```code``` darin.", Catalog());
        Assert.Equal("Nur Text mit ```code``` darin.", plain.VisibleText);
        Assert.Empty(plain.Proposals);
    }

    [Fact]
    public void Unterminated_block_is_still_hidden_from_the_user()
    {
        var result = AiProposalParser.Parse("Antwort.\n```nexus-aktionen\n[{\"typ\":\"rueckgaengig\"}]", Catalog());
        Assert.Equal("Antwort.", result.VisibleText);
        Assert.Single(result.Proposals);
    }
}

public class AiInfrastructureTests
{
    [Fact]
    public void Rate_limiter_enforces_a_sliding_window()
    {
        var time = new ManualTimeProvider();
        var limiter = new AiRateLimiter(maxRequests: 2, window: TimeSpan.FromMinutes(1), time);
        Assert.True(limiter.TryAcquire(out _));
        Assert.True(limiter.TryAcquire(out _));
        Assert.False(limiter.TryAcquire(out var retry));
        Assert.True(retry > TimeSpan.Zero);
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(limiter.TryAcquire(out _));
    }

    [Fact]
    public void Response_cache_expires_and_keys_depend_on_model_and_content()
    {
        var time = new ManualTimeProvider();
        var cache = new AiResponseCache(TimeSpan.FromMinutes(5), 4, time);
        var request = new AiRequest("system", new[] { new AiChatMessage(ChatRole.User, "Frage") });
        cache.Set(request.CacheKey("a"), "Antwort");

        Assert.True(cache.TryGet(request.CacheKey("a"), out var text));
        Assert.Equal("Antwort", text);
        Assert.False(cache.TryGet(request.CacheKey("b"), out _));
        Assert.NotEqual(request.CacheKey("a"), (request with { SystemPrompt = "anders" }).CacheKey("a"));
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.False(cache.TryGet(request.CacheKey("a"), out _));
    }

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://127.0.0.1:11434/", true)]
    [InlineData("https://api.example.com/v1", true)]
    [InlineData("http://api.example.com/v1", false)]       // unverschlüsselt außerhalb des eigenen Rechners
    [InlineData("https://user:pass@api.example.com", false)] // Zugangsdaten in der Adresse
    [InlineData("ftp://example.com", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("nicht gültig", false)]
    [InlineData("", false)]
    public void Endpoint_rules(string endpoint, bool valid)
    {
        if (valid) Assert.NotNull(AiEndpointRules.Validate(endpoint));
        else Assert.Throws<AiProviderException>(() => AiEndpointRules.Validate(endpoint));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            // Der Anfragetext wird sofort gelesen, weil der Anbieter die Anfrage nach dem Senden freigibt.
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return _respond(request);
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    private static async Task<string> Collect(IAsyncEnumerable<string> stream)
    {
        var builder = new StringBuilder();
        await foreach (var chunk in stream) builder.Append(chunk);
        return builder.ToString();
    }

    private static readonly AiRequest Request = new("System", new[] { new AiChatMessage(ChatRole.User, "Hallo") });

    [Fact]
    public async Task Ollama_stream_is_parsed_and_stays_local()
    {
        var handler = new StubHandler(_ => Ok("{\"message\":{\"content\":\"Guten \"},\"done\":false}\nkaputte zeile\n{\"message\":{\"content\":\"Tag\"},\"done\":false}\n{\"done\":true}\n{\"message\":{\"content\":\"ignoriert\"}}"));
        var provider = new OllamaProvider(new HttpClient(handler), "http://localhost:11434", "llama3.2");

        Assert.True(provider.IsLocal);
        Assert.Equal("Guten Tag", await Collect(provider.StreamAsync(Request)));
        Assert.Equal("http://localhost:11434/api/chat", handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task Ollama_validation_reports_missing_model_and_unreachable_service()
    {
        var provider = new OllamaProvider(new HttpClient(new StubHandler(_ => Ok("{\"models\":[{\"name\":\"mistral\"}]}"))), "http://localhost:11434", "llama3.2");
        var result = await provider.ValidateAsync();
        Assert.False(result.Success);
        Assert.Equal(new[] { "mistral" }, result.Models);

        var down = new OllamaProvider(new HttpClient(new StubHandler(_ => throw new HttpRequestException("verweigert"))), "http://localhost:11434", "llama3.2");
        Assert.False((await down.ValidateAsync()).Success);
        var error = await Assert.ThrowsAsync<AiProviderException>(() => Collect(down.StreamAsync(Request)));
        Assert.Equal(AiErrorKind.Network, error.Kind);
    }

    [Fact]
    public async Task OpenAi_compatible_stream_is_parsed_and_key_is_sent_only_as_header()
    {
        var handler = new StubHandler(_ => Ok("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"Hallo\"}}]}\n\n: kommentar\ndata: {\"choices\":[{\"delta\":{\"content\":\" Welt\"}}]}\n\ndata: [DONE]\ndata: {\"choices\":[{\"delta\":{\"content\":\"zu spät\"}}]}\n"));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), "https://api.example.com/v1", "modell", "geheimer-schluessel");

        Assert.False(provider.IsLocal);
        Assert.Equal("Hallo Welt", await Collect(provider.StreamAsync(Request)));
        var sent = handler.Requests.Single();
        Assert.Equal("https://api.example.com/v1/chat/completions", sent.RequestUri!.ToString());
        Assert.Equal("geheimer-schluessel", sent.Headers.Authorization!.Parameter);
        Assert.DoesNotContain("geheimer-schluessel", handler.Bodies.Single());
        Assert.Contains("\"stream\":true", handler.Bodies.Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AiErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, AiErrorKind.ModelNotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, AiErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, AiErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AiErrorKind.ServerError)]
    public async Task Http_errors_are_mapped_to_german_messages_without_leaking_the_key(HttpStatusCode status, AiErrorKind expected)
    {
        var provider = new OpenAiCompatibleProvider(new HttpClient(new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"key geheimer-schluessel invalid\"}") })),
            "https://api.example.com/v1", "modell", "geheimer-schluessel");

        var error = await Assert.ThrowsAsync<AiProviderException>(() => Collect(provider.StreamAsync(Request)));

        Assert.Equal(expected, error.Kind);
        Assert.DoesNotContain("geheimer-schluessel", error.Message);
    }

    [Fact]
    public void Anthropic_provider_requires_a_key_and_uses_the_default_model()
    {
        Assert.Throws<AiProviderException>(() => new AnthropicProvider("", null));
        var provider = new AnthropicProvider("sk-ant-test", null);
        Assert.Equal("claude-opus-5-5", provider.Model);
        Assert.False(provider.IsLocal);
    }
}

public class ChatEngineTests
{
    private sealed class FakeProvider : IAiProvider
    {
        public List<string> Chunks { get; set; } = new();
        public AiProviderException? Failure { get; set; }
        public bool FailAfterChunks { get; set; }
        public AiRequest? LastRequest { get; private set; }
        public AiProviderKind Kind => AiProviderKind.Anthropic;
        public string DisplayName => "Testmodell";
        public string Model => "test-1";
        public bool IsLocal { get; set; }
        public Task<AiConnectionResult> ValidateAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AiConnectionResult(true, "ok", Array.Empty<string>()));

        public async IAsyncEnumerable<string> StreamAsync(AiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (Failure is not null && !FailAfterChunks) throw Failure;
            foreach (var chunk in Chunks)
            {
                await Task.Yield();
                yield return chunk;
            }
            if (Failure is not null) throw Failure;
        }
    }

    private sealed class StubFactory : IAiProviderFactory
    {
        public IAiProvider? Provider { get; set; }
        public string? Note { get; set; }
        public IAiProvider? Create(AppSettings settings, out string? note) { note = Note; return Provider; }
    }

    private sealed class Harness
    {
        public InMemoryRegistryStore Registry { get; } = new();
        public FakeHardwareInfoProvider Hardware { get; } = new();
        public FakeProcessMonitor Processes { get; } = new();
        public FakeStartupInventory Startup { get; } = new();
        public InMemoryChatStore Store { get; } = new();
        public StubFactory Factory { get; } = new();
        public SettingsService Settings { get; } = new(new InMemorySettingsStore());
        public TransactionRecord? LastChange { get; set; }
        public ActionCatalog Catalog { get; }
        public ChatEngine Engine { get; }
        public AiRateLimiter Limiter { get; } = new(maxRequests: 2);

        public Harness()
        {
            var power = new FakePowerPolicy();
            var actions = RegistryTweakCatalog.All.Select(t => (IOptimizationAction)new RegistryTweakAction(t, Registry)).ToList();
            actions.Add(new PowerPlanActivateAction(power));
            Catalog = new ActionCatalog(actions);
            var monitoring = new MonitoringService(new FakeMetricsCollector { Default = new SystemMetrics { CpuTotalPercent = 37, MemoryTotalBytes = 16UL << 30, MemoryAvailableBytes = 6UL << 30, CommitUsedBytes = 10UL << 30, CommitLimitBytes = 20UL << 30 } });
            monitoring.SampleOnce();
            Processes.Processes.Add(new ProcessSnapshot { ProcessId = 100, Name = "geheimes-programm", CpuPercent = 25, WorkingSetBytes = 2L << 30 });
            Startup.Entries.Add(new StartupEntry { Source = StartupSource.RegistryRunUser, Name = "PrivaterUpdater", Command = @"C:\Users\maria\AppData\updater.exe" });
            var snapshots = new SystemSnapshotService(Hardware, monitoring, Startup, Processes, power, Catalog, new InMemoryFeedbackStore());
            Engine = new ChatEngine(Settings, snapshots, new RuleBasedAdvisor(), Catalog, Store, Factory, () => LastChange, Limiter);
        }

        public async Task<(string Text, List<ChatProposal> Proposals, List<ChatNotice> Notices, ChatCompleted Completed)> Ask(string text)
        {
            var builder = new StringBuilder();
            var proposals = new List<ChatProposal>();
            var notices = new List<ChatNotice>();
            ChatCompleted? completed = null;
            await foreach (var item in Engine.SendAsync(text))
            {
                switch (item)
                {
                    case ChatTextChunk chunk: builder.Append(chunk.Text); break;
                    case ChatProposalItem proposal: proposals.Add(proposal.Proposal); break;
                    case ChatNotice notice: notices.Add(notice); break;
                    case ChatCompleted done: completed = done; break;
                }
            }
            return (builder.ToString(), proposals, notices, completed!);
        }
    }

    [Fact]
    public async Task Without_a_model_the_answer_is_rule_based_uses_real_data_and_says_so()
    {
        var harness = new Harness();
        var (text, proposals, _, completed) = await harness.Ask("Warum ist meine CPU-Auslastung so hoch?");

        Assert.False(completed.UsedLanguageModel);
        Assert.Equal(ChatEngine.RuleBasedLabel, completed.ProviderLabel);
        Assert.Contains("37", text);                          // echter Messwert
        Assert.Contains("geheimes-programm", text);           // echter Prozess (lokal, keine Übermittlung)
        Assert.Contains("kein KI-Sprachmodell", text);
        Assert.Contains("nicht aus maschinellem Lernen", text);
        Assert.Contains(proposals, p => p.Kind == ProposalKind.OpenPage && p.PageKey == "processes");

        var (second, _, _, _) = await harness.Ask("Analysiere meine RAM-Auslastung.");
        Assert.DoesNotContain("kein KI-Sprachmodell", second); // Hinweis nur einmal je Sitzung
        Assert.Contains("RAM-Bereiniger", second);
    }

    [Fact]
    public async Task Undo_request_never_claims_completion_and_only_offers_a_confirmable_card()
    {
        var harness = new Harness();
        var (none, noProposals, _, _) = await harness.Ask("Mache die letzte Optimierung rückgängig.");
        Assert.Contains("nichts rückgängig zu machen", none);
        Assert.DoesNotContain(noProposals, p => p.Kind == ProposalKind.RollbackLast);

        harness.LastChange = new TransactionRecord { Id = "t", BatchId = "b", Timestamp = DateTimeOffset.UtcNow, ActionId = "reg.x", ActionName = "Transparenz aus", OriginalDisplay = "an", IntendedDisplay = "aus" };
        var (text, proposals, _, completed) = await harness.Ask("Mache die letzte Optimierung rückgängig.");

        Assert.Contains("Transparenz aus", text);
        Assert.Contains("Bis dahin wurde nichts verändert", text);
        Assert.Equal(ChatMessageKind.PlannedAction, completed.Kind);
        Assert.Single(proposals, p => p.Kind == ProposalKind.RollbackLast);
    }

    [Fact]
    public async Task Temperature_question_reports_unavailable_sensors_instead_of_inventing_values()
    {
        var (text, _, _, _) = await new Harness().Ask("Überwache meine Temperaturen.");
        Assert.Contains("nicht verfügbar", text);
        Assert.DoesNotContain("°C", text.Replace("nicht verfügbar", ""));
    }

    [Fact]
    public async Task Gaming_request_offers_confirmable_actions_and_makes_no_fps_promise()
    {
        var harness = new Harness();
        var recording = RegistryTweakCatalog.Find("gaming.disable-background-recording")!;
        harness.Registry.WriteValue(recording.Root, recording.View, recording.KeyPath, recording.ValueName, RegistryValueData.DWord(1));

        var (text, proposals, _, _) = await harness.Ask("Optimiere meinen PC für Gaming.");

        var card = Assert.Single(proposals, p => p.Kind == ProposalKind.Action);
        Assert.Contains(card.Requests, r => r.ActionId == recording.ActionId);
        Assert.Contains("kann nicht zugesagt werden", text);
        Assert.Equal(0, harness.Registry.WriteCount - 1); // nur der Testaufbau hat geschrieben – der Chat ändert nichts
    }

    [Fact]
    public async Task Model_text_is_streamed_while_the_proposal_block_stays_hidden_even_when_split_across_chunks()
    {
        var harness = new Harness();
        harness.Factory.Provider = new FakeProvider
        {
            IsLocal = true,
            Chunks = { "Der Spielmodus ", "ist aus. ``", "`nexus-akt", "ionen\n[{\"aktion\":\"reg.gaming.enable-game-mode\",\"begruendung\":\"aus\"},", "{\"aktion\":\"format.c\"}]\n```" },
        };

        var (text, proposals, notices, completed) = await harness.Ask("Was empfiehlst du?");

        Assert.Equal("Der Spielmodus ist aus.", text.Trim());
        Assert.DoesNotContain("nexus", text);
        Assert.Equal("reg.gaming.enable-game-mode", Assert.Single(proposals).Requests.Single().ActionId);
        Assert.Contains(notices, n => n.Text.Contains("verworfen"));
        Assert.True(completed.UsedLanguageModel);
        Assert.Equal(0, harness.Registry.WriteCount); // ein Vorschlag führt nichts aus
    }

    [Fact]
    public async Task Text_that_merely_looks_like_the_start_of_a_block_is_not_swallowed()
    {
        var harness = new Harness();
        harness.Factory.Provider = new FakeProvider { IsLocal = true, Chunks = { "Nutzen Sie ``", "`powershell", "``` nicht.", " Ende ``" } };

        var (text, _, _, _) = await harness.Ask("Frage");

        Assert.Equal("Nutzen Sie ```powershell``` nicht. Ende ``", text);
    }

    [Fact]
    public async Task Provider_failure_is_reported_and_the_rule_engine_answers_instead()
    {
        var harness = new Harness();
        harness.Factory.Provider = new FakeProvider { IsLocal = true, Failure = new AiProviderException(AiErrorKind.Network, "Dienst nicht erreichbar.") };

        var (text, _, notices, completed) = await harness.Ask("Warum ist meine CPU-Auslastung so hoch?");

        Assert.Contains(notices, n => n.IsError && n.Text.Contains("Dienst nicht erreichbar"));
        Assert.False(completed.UsedLanguageModel);
        Assert.Contains("CPU-Auslastung", text);
    }

    [Fact]
    public async Task Failure_in_the_middle_of_a_stream_does_not_lose_the_fallback_answer()
    {
        var harness = new Harness();
        harness.Factory.Provider = new FakeProvider { IsLocal = true, Chunks = { "Teilantwort" }, Failure = new AiProviderException(AiErrorKind.Timeout, "Zeitüberschreitung."), FailAfterChunks = true };

        var (text, _, notices, completed) = await harness.Ask("Hallo");

        Assert.StartsWith("Teilantwort", text);
        Assert.Contains("Guten Tag", text);
        Assert.Contains(notices, n => n.IsError);
        Assert.False(completed.UsedLanguageModel);
    }

    [Fact]
    public async Task Missing_consent_note_is_surfaced_and_no_provider_is_called()
    {
        var harness = new Harness();
        harness.Factory.Note = "Für den Cloud-Dienst liegt keine Zustimmung zur Datenübermittlung vor.";
        var (_, _, notices, completed) = await harness.Ask("Hallo");
        Assert.Contains(notices, n => n.Text.Contains("Zustimmung"));
        Assert.False(completed.UsedLanguageModel);
    }

    [Fact]
    public async Task Cloud_requests_are_rate_limited_but_local_models_are_not()
    {
        var harness = new Harness();
        var cloud = new FakeProvider { IsLocal = false, Chunks = { "ok" } };
        harness.Factory.Provider = cloud;
        Assert.True((await harness.Ask("eins")).Completed.UsedLanguageModel);
        Assert.True((await harness.Ask("zwei")).Completed.UsedLanguageModel);
        var third = await harness.Ask("drei");
        Assert.False(third.Completed.UsedLanguageModel);
        Assert.Contains(third.Notices, n => n.Text.Contains("begrenzt"));

        harness.Factory.Provider = new FakeProvider { IsLocal = true, Chunks = { "ok" } };
        Assert.True((await harness.Ask("vier")).Completed.UsedLanguageModel);
    }

    [Fact]
    public async Task Strict_privacy_context_contains_no_names_paths_or_network_identifiers()
    {
        var harness = new Harness();
        var strict = await harness.Engine.PreviewContextAsync(PrivacyMode.Strict);
        foreach (var secret in new[] { "GEHEIMER-PC", "geheimes-programm", "PrivaterUpdater", "maria", "192.168.178.23", "AA-BB-CC-DD-EE-FF", "Test-CPU 9000", @"C:\Users" })
            Assert.DoesNotContain(secret, strict);
        Assert.Contains("16 Threads", strict);

        var balanced = await harness.Engine.PreviewContextAsync(PrivacyMode.Balanced);
        Assert.Contains("Test-CPU 9000", balanced);
        Assert.Contains("geheimes-programm", balanced);
        foreach (var secret in new[] { "GEHEIMER-PC", "maria", "192.168.178.23", "AA-BB-CC-DD-EE-FF", @"C:\Users" })
            Assert.DoesNotContain(secret, balanced);
    }

    [Fact]
    public async Task What_is_sent_to_the_model_equals_the_preview_and_includes_safety_rules()
    {
        var harness = new Harness();
        var provider = new FakeProvider { IsLocal = true, Chunks = { "ok" } };
        harness.Factory.Provider = provider;
        var preview = await harness.Engine.PreviewContextAsync();

        await harness.Ask("Frage");

        var prompt = provider.LastRequest!.SystemPrompt;
        Assert.Contains(preview.Trim(), prompt);
        Assert.Contains("Behaupte deshalb nie", prompt);
        Assert.Contains("reg.gaming.enable-game-mode", prompt);
        Assert.DoesNotContain("reg.expert.", prompt);            // experimentelle Aktionen werden dem Modell gar nicht angeboten
        Assert.DoesNotContain("reg.gpu.enable-hags", prompt);    // über der freigegebenen Risikostufe
        Assert.Equal("Frage", provider.LastRequest.Messages[^1].Text);
    }

    [Fact]
    public async Task History_is_stored_can_be_cleared_and_is_not_stored_when_disabled()
    {
        var harness = new Harness();
        await harness.Ask("Hallo");
        Assert.Equal(2, harness.Engine.LoadHistory().Count);
        harness.Engine.RecordSystemMessage("Änderung angewendet und verifiziert.", ChatMessageKind.VerifiedResult);
        Assert.Equal(ChatRole.System, harness.Engine.LoadHistory()[^1].Role);

        harness.Engine.ClearHistory();
        Assert.Empty(harness.Engine.LoadHistory());

        harness.Settings.Update(s => s with { SaveChatHistory = false });
        await harness.Ask("Hallo");
        Assert.Empty(harness.Store.GetAll());
    }

    [Fact]
    public async Task Conversation_history_sent_to_the_model_alternates_and_excludes_application_status_messages()
    {
        var harness = new Harness();
        var provider = new FakeProvider { IsLocal = true, Chunks = { "Antwort" } };
        harness.Factory.Provider = provider;
        await harness.Ask("Erste Frage");
        harness.Engine.RecordSystemMessage("Status der Anwendung", ChatMessageKind.ExecutedAction);
        await harness.Ask("Zweite Frage");

        var messages = provider.LastRequest!.Messages;
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User }, messages.Select(m => m.Role));
        Assert.DoesNotContain(messages, m => m.Text.Contains("Status der Anwendung"));
    }
}

public class ProviderFactoryTests
{
    private static AiProviderFactory Factory(out FakeSecretStore secrets)
    {
        secrets = new FakeSecretStore();
        return new AiProviderFactory(new SettingsService(new InMemorySettingsStore()), secrets);
    }

    [Fact]
    public void Default_configuration_uses_no_language_model_and_no_network()
    {
        var factory = Factory(out _);
        Assert.Null(factory.Create(new AppSettings(), out var note));
        Assert.Null(note);
    }

    [Fact]
    public void Cloud_provider_requires_explicit_consent_and_a_stored_key()
    {
        var factory = Factory(out var secrets);
        var settings = new AppSettings { AiProvider = AiProviderKind.Anthropic };

        Assert.Null(factory.Create(settings, out var note));
        Assert.Contains("Zustimmung", note);

        Assert.Null(factory.Create(settings with { CloudAiConsent = true }, out note));
        Assert.Contains("API-Schlüssel", note);

        secrets.Set(AiProviderFactory.AnthropicKeyName, "sk-ant-test");
        var provider = factory.Create(settings with { CloudAiConsent = true }, out note);
        Assert.IsType<AnthropicProvider>(provider);
        Assert.Null(note);
    }

    [Fact]
    public void Local_ollama_needs_no_consent_but_a_remote_ollama_host_does()
    {
        var factory = Factory(out _);
        var local = new AppSettings { AiProvider = AiProviderKind.Ollama, OllamaModel = "llama3.2" };
        Assert.IsType<OllamaProvider>(factory.Create(local, out _));

        Assert.Null(factory.Create(local with { OllamaEndpoint = "https://ollama.example.com" }, out var note));
        Assert.Contains("Zustimmung", note);

        Assert.Null(factory.Create(local with { OllamaModel = "" }, out note));
        Assert.NotNull(note);

        Assert.Null(factory.Create(local with { OllamaEndpoint = "http://ollama.example.com" }, out note)); // unverschlüsselt + entfernt
        Assert.NotNull(note);
    }
}

using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Registry;

namespace NexusOptimizer.Core.Tests;

public class RegistryTransactionTests
{
    private static readonly RegistryTarget Target = new(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, @"Software\NexusTest\Unit", "Wert");

    [Fact]
    public void Rollback_restores_previous_value()
    {
        var store = new InMemoryRegistryStore();
        store.WriteValue(Target.Root, Target.View, Target.KeyPath, Target.ValueName, RegistryValueData.DWord(7));
        var intended = RegistryValueData.DWord(1);

        var payload = RegistryValueTransaction.Capture(store, Target, intended);
        store.WriteValue(Target.Root, Target.View, Target.KeyPath, Target.ValueName, intended);
        Assert.True(RegistryValueTransaction.Verify(store, Target, intended).Verified);

        var result = RegistryValueTransaction.Rollback(store, Target, payload, force: false);

        Assert.True(result.Success);
        Assert.Equal(7u, store.ReadValue(Target.Root, Target.View, Target.KeyPath, Target.ValueName)!.AsDWord());
    }

    [Fact]
    public void Rollback_deletes_value_that_did_not_exist_and_removes_created_key()
    {
        var store = new InMemoryRegistryStore();
        var intended = RegistryValueData.Text("100");

        var payload = RegistryValueTransaction.Capture(store, Target, intended);
        Assert.False(payload.KeyExisted);
        Assert.False(payload.ValueExisted);
        store.WriteValue(Target.Root, Target.View, Target.KeyPath, Target.ValueName, intended);

        var result = RegistryValueTransaction.Rollback(store, Target, payload, force: false);

        Assert.True(result.Success);
        Assert.Null(store.ReadValue(Target.Root, Target.View, Target.KeyPath, Target.ValueName));
        Assert.False(store.KeyExists(Target.Root, Target.View, Target.KeyPath));
    }

    [Fact]
    public void Rollback_keeps_preexisting_key_when_value_was_absent()
    {
        var store = new InMemoryRegistryStore();
        store.WriteValue(Target.Root, Target.View, Target.KeyPath, "Anderer", RegistryValueData.DWord(5));
        var intended = RegistryValueData.DWord(1);

        var payload = RegistryValueTransaction.Capture(store, Target, intended);
        store.WriteValue(Target.Root, Target.View, Target.KeyPath, Target.ValueName, intended);
        RegistryValueTransaction.Rollback(store, Target, payload, force: false);

        Assert.True(store.KeyExists(Target.Root, Target.View, Target.KeyPath));
        Assert.Equal(5u, store.ReadValue(Target.Root, Target.View, Target.KeyPath, "Anderer")!.AsDWord());
    }

    [Fact]
    public void Rollback_detects_conflicting_later_change_unless_forced()
    {
        var store = new InMemoryRegistryStore();
        store.WriteValue(Target.Root, Target.View, Target.KeyPath, Target.ValueName, RegistryValueData.DWord(7));
        var intended = RegistryValueData.DWord(1);
        var payload = RegistryValueTransaction.Capture(store, Target, intended);
        store.WriteValue(Target.Root, Target.View, Target.KeyPath, Target.ValueName, intended);

        // Jemand anderes ändert den Wert danach.
        store.WriteValue(Target.Root, Target.View, Target.KeyPath, Target.ValueName, RegistryValueData.DWord(99));

        var cautious = RegistryValueTransaction.Rollback(store, Target, payload, force: false);
        Assert.False(cautious.Success);
        Assert.True(cautious.ConflictDetected);
        Assert.Equal(99u, store.ReadValue(Target.Root, Target.View, Target.KeyPath, Target.ValueName)!.AsDWord());

        var forced = RegistryValueTransaction.Rollback(store, Target, payload, force: true);
        Assert.True(forced.Success);
        Assert.Equal(7u, store.ReadValue(Target.Root, Target.View, Target.KeyPath, Target.ValueName)!.AsDWord());
    }

    [Fact]
    public void Rollback_preserves_original_value_type()
    {
        var store = new InMemoryRegistryStore();
        store.WriteValue(Target.Root, Target.View, Target.KeyPath, Target.ValueName, new RegistryValueData(RegistryValueType.ExpandString, "%TEMP%"));
        var intended = RegistryValueData.DWord(1);
        var payload = RegistryValueTransaction.Parse(RegistryValueTransaction.Serialize(RegistryValueTransaction.Capture(store, Target, intended)));
        store.WriteValue(Target.Root, Target.View, Target.KeyPath, Target.ValueName, intended);

        Assert.True(RegistryValueTransaction.Rollback(store, Target, payload, force: false).Success);

        var restored = store.ReadValue(Target.Root, Target.View, Target.KeyPath, Target.ValueName)!;
        Assert.Equal(RegistryValueType.ExpandString, restored.Type);
        Assert.Equal("%TEMP%", restored.Data);
    }

    [Fact]
    public void Views_are_kept_separate()
    {
        var store = new InMemoryRegistryStore();
        store.WriteValue(RegistryRoot.LocalMachine, RegistryViewKind.Registry64, @"SOFTWARE\Test", "V", RegistryValueData.DWord(64));
        store.WriteValue(RegistryRoot.LocalMachine, RegistryViewKind.Registry32, @"SOFTWARE\Test", "V", RegistryValueData.DWord(32));

        Assert.Equal(64u, store.ReadValue(RegistryRoot.LocalMachine, RegistryViewKind.Registry64, @"SOFTWARE\Test", "V")!.AsDWord());
        Assert.Equal(32u, store.ReadValue(RegistryRoot.LocalMachine, RegistryViewKind.Registry32, @"SOFTWARE\Test", "V")!.AsDWord());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Version\":2,\"IntendedType\":0,\"IntendedData\":\"1\"}")]
    [InlineData("{\"Version\":1,\"IntendedType\":0,\"IntendedData\":\"keine-zahl\"}")]
    [InlineData("{\"Version\":1,\"ValueExisted\":true,\"OriginalType\":5,\"OriginalData\":\"ZZ\",\"IntendedType\":0,\"IntendedData\":\"1\"}")]
    public void Parse_rejects_corrupted_backup(string json)
        => Assert.Throws<FormatException>(() => RegistryValueTransaction.Parse(json));
}

public class RegistryPathGuardTests
{
    [Theory]
    [InlineData(RegistryRoot.LocalMachine, @"SOFTWARE\Microsoft\Windows Defender", "X")]
    [InlineData(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring")]
    [InlineData(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL")]
    [InlineData(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios", "Enabled")]
    [InlineData(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Services\mpssvc", "Start")]
    [InlineData(RegistryRoot.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA")]
    [InlineData(RegistryRoot.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Autostart")]
    [InlineData(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Autostart")]
    [InlineData(RegistryRoot.LocalMachine, @"SOFTWARE\Harmlos", "EnableSmartScreen")]
    [InlineData(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverride")]
    public void Security_critical_targets_are_rejected(RegistryRoot root, string path, string value)
    {
        var result = RegistryPathGuard.Validate(new RegistryTarget(root, RegistryViewKind.Registry64, path, value));
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(@"\Software\Test")]
    [InlineData(@"Software\Test\")]
    [InlineData(@"Software\\Test")]
    [InlineData(@"HKEY_CURRENT_USER\Software\Test")]
    [InlineData(@"Software/Test")]
    [InlineData(@"Software\Te*st")]
    [InlineData("")]
    public void Malformed_paths_are_rejected(string path)
        => Assert.False(RegistryPathGuard.Validate(new RegistryTarget(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, path, "V")).IsValid);

    [Fact]
    public void Similar_prefix_is_not_confused_with_protected_key()
    {
        // "...\RunMRU" ist kein Unterschlüssel von "...\Run".
        var result = RegistryPathGuard.Validate(new RegistryTarget(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, @"Software\Microsoft\Windows\CurrentVersion\RunMRUTest", "V"));
        Assert.True(result.IsValid);
    }
}

public class RegistryTweakCatalogTests
{
    [Fact]
    public void Every_catalog_entry_is_valid_and_constructible()
    {
        var store = new InMemoryRegistryStore();
        foreach (var definition in RegistryTweakCatalog.All)
        {
            var action = new RegistryTweakAction(definition, store); // wirft bei gesperrten Pfaden oder ungültigen Werten
            Assert.True(NexusOptimizer.Core.Actions.ActionCatalog.IsValidActionId(action.Metadata.Id), action.Metadata.Id);
            Assert.False(string.IsNullOrWhiteSpace(definition.Name));
            Assert.False(string.IsNullOrWhiteSpace(definition.Description));
            Assert.False(string.IsNullOrWhiteSpace(definition.ExpectedBehavior));
            Assert.False(string.IsNullOrWhiteSpace(definition.OptimizedLabel));
            Assert.True(action.Metadata.IsReversible);
            Assert.Equal(definition.Root == RegistryRoot.LocalMachine ? PrivilegeLevel.Administrator : PrivilegeLevel.User, action.Metadata.RequiredPrivilege);
        }
    }

    [Fact]
    public void Catalog_ids_are_unique()
        => Assert.Equal(RegistryTweakCatalog.All.Count, RegistryTweakCatalog.All.Select(t => t.Id).Distinct().Count());

    [Fact]
    public void Experimental_or_risky_tweaks_are_never_tagged_as_safe()
    {
        foreach (var definition in RegistryTweakCatalog.All.Where(t => t.Experimental || t.Risk >= RiskLevel.Moderate))
            Assert.DoesNotContain(RegistryTweakCatalog.TagSafe, definition.Tags);
    }

    [Fact]
    public void No_tweak_promises_guaranteed_gains()
    {
        foreach (var definition in RegistryTweakCatalog.All)
        {
            var text = (definition.Description + " " + definition.ExpectedBehavior).ToLowerInvariant();
            Assert.DoesNotContain("garantiert mehr", text);
            Assert.DoesNotContain("mehr fps", text);
        }
    }

    [Fact]
    public async Task Tweak_action_full_cycle_apply_verify_rollback()
    {
        var store = new InMemoryRegistryStore();
        var definition = RegistryTweakCatalog.Find("explorer.show-file-extensions")!;
        var action = new RegistryTweakAction(definition, store);
        store.WriteValue(definition.Root, definition.View, definition.KeyPath, definition.ValueName, RegistryValueData.DWord(1));

        var before = await action.DetectCurrentStateAsync(ActionParameters.Empty);
        Assert.False(before.IsApplied);

        var backup = await action.CreateBackupAsync(ActionParameters.Empty);
        Assert.True((await action.ApplyAsync(ActionParameters.Empty)).Success);
        Assert.True((await action.VerifyAsync(ActionParameters.Empty)).Verified);
        Assert.True((await action.DetectCurrentStateAsync(ActionParameters.Empty)).IsApplied);

        var rollback = await action.RollbackAsync(ActionParameters.Empty, backup, force: false);
        Assert.True(rollback.Success);
        Assert.Equal(1u, store.ReadValue(definition.Root, definition.View, definition.KeyPath, definition.ValueName)!.AsDWord());
    }

    [Fact]
    public async Task Missing_value_counts_as_applied_when_windows_default_equals_target()
    {
        var store = new InMemoryRegistryStore();
        var action = new RegistryTweakAction(RegistryTweakCatalog.Find("gaming.enable-game-mode")!, store);

        var state = await action.DetectCurrentStateAsync(ActionParameters.Empty);
        var preview = await action.PreviewAsync(ActionParameters.Empty);

        Assert.True(state.IsApplied);
        Assert.False(preview.WillChange);
    }

    [Fact]
    public async Task Access_denied_is_reported_as_failure_not_success()
    {
        var store = new InMemoryRegistryStore();
        var definition = RegistryTweakCatalog.Find("explorer.show-file-extensions")!;
        store.DenyWrites(definition.Root, definition.View, definition.KeyPath);
        var action = new RegistryTweakAction(definition, store);

        var result = await action.ApplyAsync(ActionParameters.Empty);

        Assert.False(result.Success);
        Assert.False((await action.VerifyAsync(ActionParameters.Empty)).Verified);
    }

    [Fact]
    public async Task Tweak_requiring_existing_value_is_unsupported_when_driver_did_not_create_it()
    {
        var store = new InMemoryRegistryStore();
        var action = new RegistryTweakAction(RegistryTweakCatalog.Find("gpu.enable-hags")!, store);

        var support = await action.IsSupportedAsync(new SystemContext { WindowsBuild = 22631 }, ActionParameters.Empty);

        Assert.False(support.IsSupported);
    }

    [Fact]
    public async Task Version_range_is_enforced()
    {
        var action = new RegistryTweakAction(RegistryTweakCatalog.Find("taskbar.align-left")!, new InMemoryRegistryStore());
        Assert.False((await action.IsSupportedAsync(new SystemContext { WindowsBuild = 19045 }, ActionParameters.Empty)).IsSupported);
        Assert.True((await action.IsSupportedAsync(new SystemContext { WindowsBuild = 22631 }, ActionParameters.Empty)).IsSupported);
    }
}

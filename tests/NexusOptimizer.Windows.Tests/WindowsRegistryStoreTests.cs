using Microsoft.Win32;
using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Registry;
using NexusOptimizer.Windows.RegistryAccess;

namespace NexusOptimizer.Windows.Tests;

/// <summary>
/// Integrationstests gegen die echte Registrierung. Es wird ausschließlich ein Wegwerf-Schlüssel unter
/// HKCU\Software\NexusOptimizerAI.Tests verwendet, der nach jedem Test entfernt wird.
/// </summary>
public sealed class WindowsRegistryStoreTests : IDisposable
{
    private static readonly string Root = @"Software\NexusOptimizerAI.Tests." + Guid.NewGuid().ToString("N");
    private readonly string _key = Root + @"\" + Guid.NewGuid().ToString("N");
    private readonly WindowsRegistryStore _store = new();

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);

    [Fact]
    public void Round_trips_all_supported_value_types()
    {
        var values = new[]
        {
            RegistryValueData.DWord(0xFFFFFFFF),
            RegistryValueData.QWord(ulong.MaxValue),
            RegistryValueData.Text("Äpfel & Birnen"),
            new RegistryValueData(RegistryValueType.ExpandString, "%TEMP%\\x"),
            RegistryValueData.Multi(new[] { "eins", "zwei" }),
            RegistryValueData.Binary(new byte[] { 2, 0, 0, 0, 0xAB }),
        };

        for (var i = 0; i < values.Length; i++)
        {
            _store.WriteValue(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, _key, "v" + i, values[i]);
            var read = _store.ReadValue(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, _key, "v" + i);
            Assert.NotNull(read);
            Assert.Equal(values[i].Type, read!.Type);
            Assert.Equal(values[i].Data, read.Data);
        }
    }

    [Fact]
    public void Missing_key_and_value_read_as_null()
    {
        Assert.False(_store.KeyExists(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, _key));
        Assert.Null(_store.ReadValue(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, _key, "fehlt"));
        Assert.False(_store.DeleteValue(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, _key, "fehlt"));
    }

    [Fact]
    public void Transaction_restores_previous_value_on_real_registry()
    {
        var target = new RegistryTarget(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, _key, "Wert");
        _store.WriteValue(target.Root, target.View, target.KeyPath, target.ValueName, RegistryValueData.Text("400"));
        var intended = RegistryValueData.Text("100");

        var payload = RegistryValueTransaction.Parse(RegistryValueTransaction.Serialize(RegistryValueTransaction.Capture(_store, target, intended)));
        _store.WriteValue(target.Root, target.View, target.KeyPath, target.ValueName, intended);
        Assert.True(RegistryValueTransaction.Verify(_store, target, intended).Verified);

        Assert.True(RegistryValueTransaction.Rollback(_store, target, payload, force: false).Success);
        Assert.Equal("400", _store.ReadValue(target.Root, target.View, target.KeyPath, target.ValueName)!.Data);
    }

    [Fact]
    public void Transaction_removes_value_and_created_key_that_did_not_exist()
    {
        var target = new RegistryTarget(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, _key + @"\Neu", "Wert");
        var intended = RegistryValueData.DWord(1);

        var payload = RegistryValueTransaction.Capture(_store, target, intended);
        _store.WriteValue(target.Root, target.View, target.KeyPath, target.ValueName, intended);
        Assert.True(_store.KeyExists(target.Root, target.View, target.KeyPath));

        Assert.True(RegistryValueTransaction.Rollback(_store, target, payload, force: false).Success);
        Assert.Null(_store.ReadValue(target.Root, target.View, target.KeyPath, target.ValueName));
        Assert.False(_store.KeyExists(target.Root, target.View, target.KeyPath));
    }

    [Fact]
    public void Delete_key_if_empty_keeps_keys_with_content()
    {
        _store.WriteValue(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, _key, "v", RegistryValueData.DWord(1));
        Assert.False(_store.DeleteKeyIfEmpty(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, _key));
        _store.DeleteValue(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, _key, "v");
        Assert.True(_store.DeleteKeyIfEmpty(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, _key));
    }

    [Fact]
    public async Task Every_catalog_tweak_state_can_be_detected_without_error()
    {
        // Rein lesend: prüft, dass kein Katalogeintrag beim Lesen auf einem echten Windows eine Ausnahme auslöst.
        foreach (var definition in RegistryTweakCatalog.All)
        {
            var action = new RegistryTweakAction(definition, _store);
            var state = await action.DetectCurrentStateAsync(ActionParameters.Empty);
            Assert.False(string.IsNullOrWhiteSpace(state.DisplayValue));
        }
    }
}

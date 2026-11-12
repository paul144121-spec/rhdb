using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Core.Services;
using NexusOptimizer.Windows.Native;
using ServiceStartMode = NexusOptimizer.Core.Services.ServiceStartMode;

namespace NexusOptimizer.Windows.SystemAccess;

/// <summary>Windows-Energierichtlinien über die dokumentierte Power-Management-API (powrprof.dll).</summary>
public sealed class WindowsPowerPolicy : IPowerPolicy
{
    private static void Check(uint status, string operation)
    {
        if (status == NativeMethods.ErrorSuccess) return;
        if (status == NativeMethods.ErrorAccessDenied) throw new UnauthorizedAccessException($"{operation}: Zugriff verweigert.");
        throw new Win32Exception((int)status, $"{operation} ist fehlgeschlagen (Fehler {status}).");
    }

    public Guid GetActivePlan()
    {
        Check(NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out var pointer), "Aktiven Energiesparplan lesen");
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { NativeMethods.LocalFree(pointer); }
    }

    public void SetActivePlan(Guid plan) => Check(NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref plan), "Energiesparplan aktivieren");

    public IReadOnlyList<PowerPlanInfo> GetPlans()
    {
        var active = GetActivePlan();
        var plans = new List<PowerPlanInfo>();
        for (uint index = 0; index < 64; index++)
        {
            var guid = Guid.Empty;
            var size = (uint)Marshal.SizeOf<Guid>();
            var status = NativeMethods.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NativeMethods.AccessScheme, index, ref guid, ref size);
            if (status == NativeMethods.ErrorNoMoreItems) break;
            if (status != NativeMethods.ErrorSuccess) break;
            plans.Add(new PowerPlanInfo(guid, ReadName(guid), guid == active));
        }
        return plans;
    }

    private static string ReadName(Guid plan)
    {
        uint size = 0;
        NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (size is 0 or > 4096) return plan.ToString();
        var buffer = new byte[size];
        if (NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != NativeMethods.ErrorSuccess) return plan.ToString();
        var name = Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(name) ? plan.ToString() : name;
    }

    public uint? ReadAcValue(Guid plan, Guid subgroup, Guid setting)
        => NativeMethods.PowerReadACValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref setting, out var value) == NativeMethods.ErrorSuccess ? value : null;

    public uint? ReadDcValue(Guid plan, Guid subgroup, Guid setting)
        => NativeMethods.PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref setting, out var value) == NativeMethods.ErrorSuccess ? value : null;

    public void WriteAcValue(Guid plan, Guid subgroup, Guid setting, uint value)
        => Check(NativeMethods.PowerWriteACValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref setting, value), "Energieeinstellung (Netzbetrieb) schreiben");

    public void WriteDcValue(Guid plan, Guid subgroup, Guid setting, uint value)
        => Check(NativeMethods.PowerWriteDCValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref setting, value), "Energieeinstellung (Akkubetrieb) schreiben");

    public Guid DuplicatePlan(Guid source, string name)
    {
        var destination = IntPtr.Zero;
        Check(NativeMethods.PowerDuplicateScheme(IntPtr.Zero, ref source, ref destination), "Energiesparplan duplizieren");
        Guid created;
        try { created = Marshal.PtrToStructure<Guid>(destination); }
        finally { NativeMethods.LocalFree(destination); }
        RenamePlan(created, name);
        return created;
    }

    public void RenamePlan(Guid plan, string name)
    {
        var bytes = Encoding.Unicode.GetBytes(name + "\0");
        Check(NativeMethods.PowerWriteFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, bytes, (uint)bytes.Length), "Energiesparplan umbenennen");
    }

    public void DeletePlan(Guid plan)
    {
        if (WellKnownPowerPlans.All.Contains(plan)) throw new InvalidOperationException("Die in Windows eingebauten Energiesparpläne werden nicht gelöscht.");
        if (GetActivePlan() == plan) throw new InvalidOperationException("Der aktive Energiesparplan kann nicht gelöscht werden.");
        Check(NativeMethods.PowerDeleteScheme(IntPtr.Zero, ref plan), "Energiesparplan löschen");
    }
}

/// <summary>Dienstinventar über WMI (Win32_Service) und Änderungen des Starttyps über den Dienststeuerungs-Manager.</summary>
public sealed class WindowsServiceManager : IServiceManager
{
    public IReadOnlyList<ServiceInfo> GetServices() => Read(null);

    public ServiceInfo? GetService(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => c is '\'' or '\\' or '"' || char.IsControl(c))) return null;
        return Read(name).FirstOrDefault();
    }

    private static string StateText(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Running => "Wird ausgeführt",
        ServiceControllerStatus.Stopped => "Beendet",
        ServiceControllerStatus.Paused => "Angehalten",
        ServiceControllerStatus.StartPending => "Wird gestartet",
        ServiceControllerStatus.StopPending => "Wird beendet",
        _ => status.ToString(),
    };

    private static List<ServiceInfo> Read(string? name)
    {
        var services = new Dictionary<string, ServiceInfo>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Grundlage: Dienststeuerungs-Manager über die .NET-Dienststeuerung (funktioniert ohne Administratorrechte und ohne WMI).
        try
        {
            foreach (var controller in ServiceController.GetServices())
            {
                using (controller)
                {
                    try
                    {
                        var depends = controller.ServicesDependedOn.Select(s => s.ServiceName).ToArray();
                        foreach (var dependency in depends)
                        {
                            if (!dependents.TryGetValue(dependency, out var list)) dependents[dependency] = list = new List<string>();
                            list.Add(controller.ServiceName);
                        }
                        if (name is not null && !controller.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;

                        var status = controller.Status;
                        services[controller.ServiceName] = new ServiceInfo
                        {
                            Name = controller.ServiceName,
                            DisplayName = string.IsNullOrWhiteSpace(controller.DisplayName) ? controller.ServiceName : controller.DisplayName,
                            State = StateText(status),
                            IsRunning = status == ServiceControllerStatus.Running,
                            StartMode = controller.StartType switch
                            {
                                System.ServiceProcess.ServiceStartMode.Boot => ServiceStartMode.Boot,
                                System.ServiceProcess.ServiceStartMode.System => ServiceStartMode.System,
                                System.ServiceProcess.ServiceStartMode.Automatic => ServiceStartMode.Automatic,
                                System.ServiceProcess.ServiceStartMode.Manual => ServiceStartMode.Manual,
                                System.ServiceProcess.ServiceStartMode.Disabled => ServiceStartMode.Disabled,
                                _ => ServiceStartMode.Unknown,
                            },
                            IsDriver = (controller.ServiceType & (ServiceType.KernelDriver | ServiceType.FileSystemDriver)) != 0,
                            DependsOn = depends,
                        };
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or PlatformNotSupportedException) { }

        // Anreicherung: Beschreibung, Pfad, Prozess-ID, Konto und „verzögerter Start“ liefert WMI.
        try
        {
            var wql = "SELECT Name, DisplayName, Description, State, StartMode, DelayedAutoStart, PathName, ProcessId, StartName, ServiceType FROM Win32_Service"
                      + (name is null ? "" : $" WHERE Name = '{name}'");
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", wql);
            using var items = searcher.Get();
            foreach (var item in items)
            {
                using (item)
                {
                    var serviceName = item["Name"]?.ToString() ?? "";
                    if (serviceName.Length == 0) continue;
                    var existing = services.GetValueOrDefault(serviceName) ?? new ServiceInfo { Name = serviceName };
                    var startMode = item["StartMode"]?.ToString();
                    var delayed = item["DelayedAutoStart"] is true;
                    var state = item["State"]?.ToString() ?? "";
                    var type = item["ServiceType"]?.ToString() ?? "";
                    services[serviceName] = existing with
                    {
                        DisplayName = item["DisplayName"]?.ToString() ?? (existing.DisplayName.Length > 0 ? existing.DisplayName : serviceName),
                        Description = item["Description"]?.ToString() ?? "",
                        State = state switch { "Running" => "Wird ausgeführt", "Stopped" => "Beendet", "Paused" => "Angehalten", "Start Pending" => "Wird gestartet", "Stop Pending" => "Wird beendet", "" => existing.State, _ => state },
                        IsRunning = state.Length == 0 ? existing.IsRunning : state == "Running",
                        StartMode = startMode switch
                        {
                            "Boot" => ServiceStartMode.Boot,
                            "System" => ServiceStartMode.System,
                            "Auto" => delayed ? ServiceStartMode.AutomaticDelayed : ServiceStartMode.Automatic,
                            "Manual" => ServiceStartMode.Manual,
                            "Disabled" => ServiceStartMode.Disabled,
                            _ => existing.StartMode,
                        },
                        BinaryPath = item["PathName"]?.ToString(),
                        ProcessId = item["ProcessId"] is null ? 0 : Convert.ToInt32(item["ProcessId"]),
                        Account = item["StartName"]?.ToString(),
                        IsDriver = existing.IsDriver || type.Contains("Driver", StringComparison.OrdinalIgnoreCase),
                    };
                }
            }
        }
        catch (Exception ex) when (WmiSupport.IsRecoverable(ex)) { }

        return services.Values
            .Select(s => s with { Dependents = dependents.TryGetValue(s.Name, out var list) ? list : Array.Empty<string>() })
            .OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void SetStartMode(string name, ServiceStartMode mode)
    {
        // Zweite Verteidigungslinie: geschützte Dienste werden auch hier abgelehnt.
        if (ServiceProtection.IsProtected(name, out var reason)) throw new InvalidOperationException(reason);
        var startType = mode switch
        {
            ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed => 2u,
            ServiceStartMode.Manual => 3u,
            ServiceStartMode.Disabled => 4u,
            _ => throw new InvalidOperationException("Dieser Starttyp kann nicht gesetzt werden."),
        };

        var manager = NativeMethods.OpenSCManagerW(null, null, NativeMethods.ScManagerConnect);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Der Dienststeuerungs-Manager konnte nicht geöffnet werden.");
        try
        {
            var service = NativeMethods.OpenServiceW(manager, name, NativeMethods.ServiceChangeConfig | NativeMethods.ServiceQueryConfig);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == NativeMethods.ErrorAccessDenied) throw new UnauthorizedAccessException("Zugriff auf den Dienst verweigert.");
                throw new Win32Exception(error, "Der Dienst konnte nicht geöffnet werden.");
            }
            try
            {
                if (!NativeMethods.ChangeServiceConfigW(service, NativeMethods.ServiceNoChange, startType, NativeMethods.ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Der Starttyp konnte nicht geändert werden.");

                if (startType == 2)
                {
                    var info = new NativeMethods.SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = mode == ServiceStartMode.AutomaticDelayed };
                    // Manche Dienste lassen den verzögerten Start nicht zu; der Starttyp „Automatisch“ bleibt dann bestehen.
                    NativeMethods.ChangeServiceConfig2W(service, NativeMethods.ServiceConfigDelayedAutoStartInfo, ref info);
                }
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(manager);
        }
    }
}

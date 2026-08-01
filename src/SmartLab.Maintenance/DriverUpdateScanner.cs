using System.Globalization;
using System.Management;

namespace SmartLab.Maintenance;

/// <param name="UpdateId">Windows Update's identifier for this driver, a GUID.</param>
/// <param name="InstalledDate">The bound driver's date, or empty when it cannot be matched.</param>
/// <param name="InstalledVersion">The bound driver's version, on the same terms.</param>
/// <param name="Available">The offered driver's date.</param>
/// <remarks>
/// Dates rather than versions on both sides of the arrow, because that is the one
/// figure both sources actually publish. Windows Update gives a driver's date but not
/// its version - the version appears only inside a vendor-written title - and putting
/// an installed version opposite an offered date compares two different things while
/// looking like it compares one.
/// </remarks>
public sealed record DriverUpdate(
    string UpdateId,
    string Title,
    string Device,
    string Provider,
    string InstalledVersion,
    string InstalledDate,
    string Available,
    long SizeBytes);

/// <summary>A device Windows is not driving, and why.</summary>
public sealed record ProblemDevice(string Name, string InstanceId, int ProblemCode)
{
    public string Problem => DriverProblem.Describe(ProblemCode);
}

/// <summary>
/// Which Device Manager error codes mean a driver is missing or broken.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from the WMI query so the rule can be tested against a list of numbers,
/// which is the half that decides what an operator is shown. The query itself returns
/// every device with any non-zero code, and most of those are not driver faults at
/// all: a phone unplugged three weeks ago still has a record, with code 45.
/// </para>
/// <para>
/// The codes are listed rather than excluded, so a code nobody here has seen is
/// reported as nothing rather than as a broken driver. Being quiet about an unknown
/// state is recoverable; telling someone their working sound card needs a driver is
/// how a maintenance tool talks them into breaking it.
/// </para>
/// </remarks>
public static class DriverProblem
{
    /// <summary>Codes that mean the driver, not the device or the user, is the problem.</summary>
    private static readonly Dictionary<int, string> DriverFaults = new()
    {
        [1] = "no driver is configured for this device",
        [3] = "the driver may be corrupted",
        [10] = "the device cannot start",
        [18] = "the driver needs reinstalling",
        [19] = "the driver's registry entry is damaged",
        [28] = "no driver is installed",
        [31] = "the driver failed to load",
        [37] = "the driver would not initialise",
        [38] = "a previous instance of the driver is still in memory",
        [39] = "the driver is missing or corrupted",
        [40] = "the driver's registry entry cannot be read",
        [41] = "the driver loaded but found no device",
    };

    public static bool IsDriverFault(int code) => DriverFaults.ContainsKey(code);

    public static string Describe(int code) =>
        DriverFaults.TryGetValue(code, out var text) ? text : $"problem code {code}";
}

/// <summary>
/// Asks Windows Update what drivers it has for this machine, and Windows what it is
/// failing to drive.
/// </summary>
/// <remarks>
/// <para>
/// Windows Update is the source for the same reason winget is the source next door:
/// the thing that can vouch for a driver is the thing that signs and publishes it.
/// A driver is kernel code. Fetching one from a vendor page this app scraped, with no
/// signature it can check, would install into the kernel exactly the way this app's
/// other half was written to clean up after.
/// </para>
/// <para>
/// Searching writes nothing and needs no elevation. Installing does, and lives in
/// <see cref="ElevatedDriverInstall"/> behind the same prompt the repair tools use.
/// </para>
/// <para>
/// The Update Agent is reached through late binding rather than an interop assembly.
/// The type library is a Windows component, not a package this project can carry, and
/// a machine with the service disabled must produce a stated reason rather than a
/// missing-assembly crash in a section someone opened to read a list.
/// </para>
/// </remarks>
public static class DriverUpdateScanner
{
    /// <summary>
    /// Drivers Windows Update would install, excluding ones already hidden.
    /// </summary>
    /// <remarks>
    /// A hidden update is one somebody declined on purpose. Offering it again under a
    /// different heading is the behaviour that teaches people to stop declining.
    /// </remarks>
    private const string Criteria = "IsInstalled=0 and Type='Driver' and IsHidden=0";

    /// <summary>
    /// What Windows Update has, or a reason it could not say.
    /// </summary>
    /// <remarks>
    /// An empty list and a failed search must never read the same. "No driver updates"
    /// from a service that never answered is the one verdict this is not allowed to
    /// give, for the same reason a missing winget must not read as "everything current".
    /// </remarks>
    public static (IReadOnlyList<DriverUpdate> Drivers, string? Error) Search()
    {
        if (Type.GetTypeFromProgID("Microsoft.Update.Session") is not { } sessionType)
        {
            return ([], "The Windows Update Agent is not available on this machine, " +
                        "so drivers cannot be looked up.");
        }

        try
        {
            dynamic session = Activator.CreateInstance(sessionType)!;
            session.ClientApplicationID = "SmartLab";

            dynamic searcher = session.CreateUpdateSearcher();
            searcher.Online = true;

            dynamic result = searcher.Search(Criteria);
            dynamic updates = result.Updates;

            var installed = InstalledDrivers();
            var drivers = new List<DriverUpdate>();

            int count = updates.Count;

            for (var i = 0; i < count; i++)
                if (Describe(updates.Item(i), installed) is { } driver) drivers.Add(driver);

            return (drivers, null);
        }
        catch (Exception ex)
        {
            return ([], $"Windows Update could not be asked about drivers: {ex.Message}");
        }
    }

    /// <summary>Devices Windows is failing to drive, whether or not an update exists.</summary>
    /// <remarks>
    /// Read separately from the search because the two answer different questions. A
    /// device with no driver that Windows Update also has nothing for is the case worth
    /// naming out loud: nothing in this app can fix it, and saying so beats a list that
    /// quietly omits it.
    /// </remarks>
    public static IReadOnlyList<ProblemDevice> ProblemDevices()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity " +
                "WHERE ConfigManagerErrorCode <> 0");

            var devices = new List<ProblemDevice>();

            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                using (item)
                {
                    var code = ToInt(item["ConfigManagerErrorCode"]);
                    if (!DriverProblem.IsDriverFault(code)) continue;

                    var name = item["Name"] as string;
                    var id = item["DeviceID"] as string;

                    if (string.IsNullOrWhiteSpace(id)) continue;

                    devices.Add(new ProblemDevice(
                        string.IsNullOrWhiteSpace(name) ? "Unnamed device" : name, id, code));
                }
            }

            return devices;
        }
        catch
        {
            // WMI is refused or broken. The driver list is still worth showing, and a
            // section that throws here would take that with it.
            return [];
        }
    }

    /// <summary>One update read into plain values, or null when it is not a driver.</summary>
    /// <remarks>
    /// Every field is read from the update itself. The per-driver entry collection that
    /// would carry a version is reachable but opaque: its items arrive as bare COM
    /// objects with no type information, so no property on them resolves. Reading the
    /// version out of a vendor-written title instead would be guesswork dressed as data.
    /// </remarks>
    private static DriverUpdate? Describe(dynamic update, IReadOnlyList<InstalledDriver> installed)
    {
        var id = Text(() => update.Identity.UpdateID);

        // The identifier is what crosses into the elevated half later. Anything that is
        // not a GUID is not something this app will carry across that boundary.
        if (!Guid.TryParse(id, out var guid)) return null;

        var title = Text(() => update.Title);
        var device = First(Text(() => update.DriverModel), title);
        var current = Match(installed, Text(() => update.DriverHardwareID));

        return new DriverUpdate(
            guid.ToString(),
            title.Length == 0 ? device : title,
            device.Length == 0 ? "Unnamed device" : device,
            Text(() => update.DriverProvider),
            current?.Version ?? string.Empty,
            current?.Date ?? string.Empty,
            First(DateText(() => update.DriverVerDate), "newer"),
            Size(() => update.MaxDownloadSize));
    }

    /// <param name="HardwareId">As Windows records it, in full.</param>
    private sealed record InstalledDriver(string HardwareId, string Version, string Date);

    /// <summary>
    /// The driver currently bound to a hardware id, matched on a prefix.
    /// </summary>
    /// <remarks>
    /// Windows Update names hardware by the part that identifies the model -
    /// <c>pci\ven_8086&amp;dev_a780</c> - while Windows records what the device
    /// actually reported, subsystem and revision included. An exact comparison of the
    /// two never matches, which is how every row came to claim the device had no driver
    /// at all.
    /// </remarks>
    private static InstalledDriver? Match(IReadOnlyList<InstalledDriver> installed, string hardwareId) =>
        hardwareId.Length == 0
            ? null
            : installed.FirstOrDefault(d =>
                d.HardwareId.StartsWith(hardwareId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every signed driver Windows has bound to something.</summary>
    /// <remarks>
    /// Only so a row can say what is being replaced. A device this cannot match still
    /// lists: the update is real whether or not its predecessor can be named.
    /// </remarks>
    private static List<InstalledDriver> InstalledDrivers()
    {
        var drivers = new List<InstalledDriver>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT HardWareID, DriverVersion, DriverDate FROM Win32_PnPSignedDriver " +
                "WHERE HardWareID IS NOT NULL");

            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                using (item)
                {
                    if (item["HardWareID"] is not string hardware || hardware.Length == 0) continue;

                    drivers.Add(new InstalledDriver(
                        hardware,
                        item["DriverVersion"] as string ?? string.Empty,
                        CimDate(item["DriverDate"] as string)));
                }
            }
        }
        catch
        {
            // Without this the rows simply lose the half of the arrow they came from.
        }

        return drivers;
    }

    /// <summary>A WMI <c>yyyyMMddHHmmss.ffffff±UUU</c> stamp as a plain date.</summary>
    private static string CimDate(string? value) =>
        value is { Length: >= 8 } && DateTime.TryParseExact(
            value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;

    private static string First(params string[] candidates) =>
        candidates.FirstOrDefault(c => c.Length > 0) ?? string.Empty;

    /// <remarks>
    /// Every read of a COM property is allowed to fail. These objects are late-bound,
    /// the interfaces differ by Windows build, and one absent property must cost a
    /// column rather than the whole list.
    /// </remarks>
    private static string Text(Func<object?> read)
    {
        try
        {
            return read() as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DateText(Func<object?> read)
    {
        try
        {
            return read() is DateTime date
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static long Size(Func<object?> read)
    {
        try
        {
            return Convert.ToInt64(read() ?? 0L, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static int ToInt(object? value)
    {
        try
        {
            return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }
}

using System.Runtime.InteropServices;
using AlertaIBT.Core.Audio;

namespace AlertaIBT.App.Platform;

/// <summary>
/// The Windows per-app output (the "Output device" of a program in Settings, System, Sound, Volume mixer) through the undocumented
/// <c>IAudioPolicyConfigFactory</c> of the activation class <c>Windows.Media.Internal.AudioPolicyConfig</c>, the same one EarTrumpet uses
/// (<c>SetPersistedDefaultAudioEndpoint</c> / <c>GetPersistedDefaultAudioEndpoint</c>). The interface has two known IIDs: Windows 10 before
/// 21H2, and Windows 10 21H2 and later (Windows 11). Both are tried, newest first, and the one Windows accepts is kept. The device id goes in
/// the form <c>\\?\SWD#MMDEVAPI#{endpoint id}#{e6327cad-dcec-4949-ae8a-991e976a79d2}</c>.
/// </summary>
/// <remarks>
/// The interface is an <c>IInspectable</c> (WinRT) one, which .NET no longer marshals, so the calls go through the v-table by slot: 3
/// <c>IUnknown</c> methods, 3 <c>IInspectable</c> methods, 19 methods this class never calls, then Set (slot 25), Get (slot 26) and Clear
/// (slot 27). Nothing is created before the first call, so building the app's object graph (tests included) never touches Windows. Tests
/// never use this class: they use a fake <see cref="IAppOutputPolicy"/>. It never throws.
/// </remarks>
public sealed class WindowsAppOutputPolicy : IAppOutputPolicy
{
    private const string ClassId = "Windows.Media.Internal.AudioPolicyConfig";
    private const int SetSlot = 25;
    private const int GetSlot = 26;
    private const int FlowRender = 0; // EDataFlow.eRender
    private const int RoleConsole = 0; // ERole.eConsole
    private const int RoleMultimedia = 1; // ERole.eMultimedia

    private static readonly Guid Iid21H2 = new("ab3d4648-e242-459f-b02f-541c70306324");
    private static readonly Guid IidDownlevel = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");

    private readonly object _gate = new();
    private IntPtr _factory;
    private bool _unavailable;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFunction(IntPtr self, uint processId, int flow, int role, IntPtr deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFunction(IntPtr self, uint processId, int flow, int role, out IntPtr deviceId);

    /// <summary>Which of the two interface ids Windows accepted (<c>21H2</c> or <c>downlevel</c>), or <c>none</c>; for the log and the smoke test.</summary>
    public string Variant { get; private set; } = "none";

    /// <inheritdoc />
    public string? GetPersistedEndpoint(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            lock (_gate)
            {
                if (Factory() is not { } factory)
                {
                    return null;
                }

                var get = Marshal.GetDelegateForFunctionPointer<GetFunction>(Slot(factory, GetSlot));
                if (get(factory, (uint)processId, FlowRender, RoleMultimedia, out var hstring) < 0)
                {
                    return null;
                }

                try
                {
                    return MmdevapiDevicePath.ToEndpointId(ReadHString(hstring));
                }
                finally
                {
                    if (hstring != IntPtr.Zero)
                    {
                        _ = WindowsDeleteString(hstring);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or MarshalDirectiveException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool SetPersistedEndpoint(int processId, string? endpointId)
    {
        if (processId <= 0)
        {
            return false;
        }

        var hstring = IntPtr.Zero;
        try
        {
            lock (_gate)
            {
                if (Factory() is not { } factory)
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(endpointId))
                {
                    var path = MmdevapiDevicePath.ForRender(endpointId);
                    if (WindowsCreateString(path, (uint)path.Length, out hstring) < 0)
                    {
                        return false;
                    }
                }

                // A null string clears the choice: the program follows the Windows default again.
                var set = Marshal.GetDelegateForFunctionPointer<SetFunction>(Slot(factory, SetSlot));
                var ok = set(factory, (uint)processId, FlowRender, RoleConsole, hstring) >= 0;
                ok &= set(factory, (uint)processId, FlowRender, RoleMultimedia, hstring) >= 0;
                return ok;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or MarshalDirectiveException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (hstring != IntPtr.Zero)
            {
                _ = WindowsDeleteString(hstring);
            }
        }
    }

    // The factory, made on first use; null when this Windows does not have the interface (then it is not tried again).
    private IntPtr? Factory()
    {
        if (_factory != IntPtr.Zero)
        {
            return _factory;
        }

        if (_unavailable)
        {
            return null;
        }

        if (WindowsCreateString(ClassId, (uint)ClassId.Length, out var classId) < 0)
        {
            return null;
        }

        try
        {
            foreach (var (iid, name) in new[] { (Iid21H2, "21H2"), (IidDownlevel, "downlevel") })
            {
                var id = iid;
                if (RoGetActivationFactory(classId, ref id, out var factory) >= 0 && factory != IntPtr.Zero)
                {
                    _factory = factory;
                    Variant = name;
                    return _factory;
                }
            }

            _unavailable = true;
            return null;
        }
        finally
        {
            _ = WindowsDeleteString(classId);
        }
    }

    private static IntPtr Slot(IntPtr instance, int index) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), index * IntPtr.Size);

    private static string ReadHString(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = WindowsGetStringRawBuffer(hstring, out var length);
        return buffer == IntPtr.Zero || length == 0 ? string.Empty : Marshal.PtrToStringUni(buffer, (int)length) ?? string.Empty;
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
}

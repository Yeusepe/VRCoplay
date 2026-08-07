using System.Runtime.InteropServices;
using Valve.VR;

namespace VRCoplay;

internal sealed class VrXInputBridge : IDisposable
{
    private static readonly uint SetSize = (uint)Marshal.SizeOf<VRActiveActionSet_t>(),
        AnalogSize = (uint)Marshal.SizeOf<InputAnalogActionData_t>(), DigitalSize = (uint)Marshal.SizeOf<InputDigitalActionData_t>();
    private static readonly string[] AnalogActions = ["left_stick", "right_stick", "left_trigger", "right_trigger"];
    private static readonly (string Name, uint Button)[] DigitalActions = [
        ("left_thumb", 0x0040), ("right_thumb", 0x0080), ("left_shoulder", 0x0100), ("right_shoulder", 0x0200),
        ("a", 0x1000), ("b", 0x2000), ("x", 0x4000), ("y", 0x8000)
    ];

    private readonly CVRInput _input;
    private readonly ulong[] _analog, _digital;
    private readonly VRActiveActionSet_t[] _sets;
    private readonly Thread _poll;
    private nuint _server, _pad;
    private int _disposed;

    public VrXInputBridge()
    {
        CVRSystem? vr = null;
        try
        {
            var initError = EVRInitError.None;
            vr = OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Background);
            if (vr is null) throw new InvalidOperationException(OpenVR.GetStringForHmdError(initError));

            _input = OpenVR.Input;
            Check(_input.SetActionManifestPath(Path.Combine(AppContext.BaseDirectory, "Input", "actions.json")));
            ulong set = 0; Check(_input.GetActionSetHandle("/actions/vrcoplay", ref set));
            ulong Handle(string name) { ulong value = 0; Check(_input.GetActionHandle($"/actions/vrcoplay/in/{name}", ref value)); return value; }
            _sets = [new() { ulActionSet = set }];
            _analog = AnalogActions.Select(Handle).ToArray(); _digital = DigitalActions.Select(x => Handle(x.Name)).ToArray();

            var config = new USBServerConfig { Address = "127.0.0.1:32341" };
            Require(Native.NewUSBServer(ref config, out _server, 0), "start libVIIPER");
            uint bus = 0; Require(Native.CreateUSBBus(_server, ref bus), "create the controller bus");
            Require(Native.CreateXbox360Device(_server, out _pad, bus, 1, 0, 0, 0), "create the Xbox controller");
            _poll = new(() => { try { Poll(); } catch { Native.SetXbox360DeviceState(_pad, default); } }) { IsBackground = true }; _poll.Start();
        }
        catch (Exception error)
        {
            Close();
            if (vr is not null) OpenVR.Shutdown();
            throw new InvalidOperationException($"Controller bridge could not start. Start SteamVR and install usbip-win2 0.9.7.7. {error.Message}");
        }
    }

    private void Poll()
    {
        // https://github.com/ValveSoftware/openvr/wiki/SteamVR-Input
        while (Volatile.Read(ref _disposed) == 0)
        {
            var state = default(Xbox360DeviceState);
            if (_input.UpdateActionState(_sets, SetSize) == EVRInputError.None)
            {
                var left = Analog(0); var right = Analog(1);
                for (var i = 0; i < _digital.Length; i++)
                {
                    var data = default(InputDigitalActionData_t);
                    _input.GetDigitalActionData(_digital[i], ref data, DigitalSize, OpenVR.k_ulInvalidInputValueHandle);
                    if (data.bActive && data.bState) state.Buttons |= DigitalActions[i].Button;
                }
                state.LX = Stick(left.x); state.LY = Stick(left.y); state.RX = Stick(right.x); state.RY = Stick(right.y);
                state.LT = Trigger(Analog(2).x); state.RT = Trigger(Analog(3).x);
            }
            Require(Native.SetXbox360DeviceState(_pad, state), "update the Xbox controller"); Thread.Sleep(8);
        }
    }

    private InputAnalogActionData_t Analog(int index)
    {
        var data = default(InputAnalogActionData_t);
        _input.GetAnalogActionData(_analog[index], ref data, AnalogSize, OpenVR.k_ulInvalidInputValueHandle);
        return data.bActive ? data : default;
    }

    private static void Check(EVRInputError error)
    { if (error != EVRInputError.None) throw new InvalidOperationException($"OpenVR input error: {error}."); }
    private static void Require(bool success, string action)
    { if (!success) throw new InvalidOperationException($"Could not {action}."); }
    private static short Stick(float value) => (short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue);
    private static byte Trigger(float value) => (byte)Math.Clamp(value * byte.MaxValue, byte.MinValue, byte.MaxValue);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _poll.Join(); Close(); OpenVR.Shutdown();
    }

    private void Close()
    {
        if (_server == 0) return;
        if (_pad != 0) Native.RemoveXbox360Device(_pad);
        Native.CloseUSBServer(_server); _server = _pad = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct USBServerConfig
    {
        [MarshalAs(UnmanagedType.LPStr)] public string Address;
        public ulong ConnectionTimeout, DeviceHandlerConnectTimeout;
        public uint WriteBatchFlushInterval;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Xbox360DeviceState
    {
        public uint Buttons;
        public byte LT, RT;
        public short LX, LY, RX, RY;
        public byte Reserved0, Reserved1, Reserved2, Reserved3, Reserved4, Reserved5;
    }

    // Taken from https://github.com/Alia5/VIIPER/tree/v0.7.0/examples/libVIIPER/csharp
    private static class Native
    {
        private const string Library = "libVIIPER";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool NewUSBServer(ref USBServerConfig config, out nuint handle, nint logCallback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CloseUSBServer(nuint handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CreateUSBBus(nuint handle, ref uint bus);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CreateXbox360Device(nuint server, out nuint device, uint bus, byte autoAttach, ushort vendor, ushort product, byte subtype);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool SetXbox360DeviceState(nuint device, Xbox360DeviceState state);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool RemoveXbox360Device(nuint device);
    }
}

using System.Runtime.InteropServices;

namespace EpsilonGCS.Services;

/// <summary>
/// Minimal XInput (Xbox-compatible controller) reader used by the J.STICK button.
/// Left stick: pan/tilt.  Triggers: zoom out/in.  Shoulder buttons: focus -/+.
/// A: track at cross (VHCL).  B: RATE mode.  X: snapshot.  Y: switch EO/IR.
/// </summary>
public static class XInputJoystick
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint PacketNumber;
        public Gamepad Pad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState14(int userIndex, out State state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState910(int userIndex, out State state);

    public const ushort ButtonLeftShoulder = 0x0100;
    public const ushort ButtonRightShoulder = 0x0200;
    public const ushort ButtonA = 0x1000;
    public const ushort ButtonB = 0x2000;
    public const ushort ButtonX = 0x4000;
    public const ushort ButtonY = 0x8000;

    private static bool _use910;

    /// <summary>Returns the first connected controller (index 0..3), or null.</summary>
    public static Gamepad? Read()
    {
        for (int i = 0; i < 4; i++)
        {
            int result;
            State s;
            try
            {
                result = _use910 ? XInputGetState910(i, out s) : XInputGetState14(i, out s);
            }
            catch (DllNotFoundException)
            {
                if (_use910) return null;
                _use910 = true;
                return Read();
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            if (result == 0) return s.Pad;
        }
        return null;
    }

    /// <summary>Maps a stick axis to -100..100 % with a dead zone.</summary>
    public static int Axis(short raw, double deadZone = 0.15)
    {
        double v = raw / 32767.0;
        if (Math.Abs(v) < deadZone) return 0;
        double scaled = (Math.Abs(v) - deadZone) / (1 - deadZone) * Math.Sign(v);
        return (int)Math.Round(Math.Clamp(scaled, -1, 1) * 100);
    }
}

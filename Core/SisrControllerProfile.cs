using System;

namespace SBridge.Core;

internal enum SisrControllerType { Xbox360, DualShock4, DualSense, DualSenseEdge, Switch2Pro }

internal sealed record SisrControllerProfile(SisrControllerType ControllerType = SisrControllerType.Xbox360,
    bool GyroPassthrough = true, bool TouchpadPassthrough = true, bool BackButtonPassthrough = false)
{
    public void Validate()
    { if (!Enum.IsDefined(ControllerType)) throw new ArgumentException("Unsupported SISR controller type."); }
    public string ApiType => ControllerType switch
    {
        SisrControllerType.Xbox360 => "xbox360", SisrControllerType.DualShock4 => "dualshock4", SisrControllerType.DualSense => "dualsense",
        SisrControllerType.DualSenseEdge => "dualsenseedge", SisrControllerType.Switch2Pro => "ns2pro",
        _ => throw new ArgumentException("Unsupported SISR controller type.")
    };
}

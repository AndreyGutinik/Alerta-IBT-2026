namespace AlertaIBT.Core.Screens;

/// <summary>A monitor in physical pixels. <paramref name="DevicePath"/> stays stable between reboots.</summary>
/// <param name="DevicePath">The monitor's device interface path; empty when Windows gave none.</param>
/// <param name="DeviceName">The display name, such as <c>\\.\DISPLAY2</c>.</param>
/// <param name="X">The left edge in virtual-desktop pixels.</param>
/// <param name="Y">The top edge in virtual-desktop pixels.</param>
/// <param name="Width">The physical width in pixels.</param>
/// <param name="Height">The physical height in pixels.</param>
/// <param name="IsPrimary">Whether it is the primary monitor.</param>
public sealed record ScreenInfo(string DevicePath, string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary);

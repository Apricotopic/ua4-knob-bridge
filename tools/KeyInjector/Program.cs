using System.Runtime.InteropServices;

const byte VkVolumeDown = 0xAE;
const uint KeyUp = 0x0002;
keybd_event(VkVolumeDown, 0, 0, UIntPtr.Zero);
keybd_event(VkVolumeDown, 0, KeyUp, UIntPtr.Zero);
Console.WriteLine("Injected VK_VOLUME_DOWN press/release");

[DllImport("user32.dll")]
static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

using System;
using System.Runtime.InteropServices;

namespace Particulas
{
    // Interop com a API de console do Win32 (mouse + teclado via ReadConsoleInput)
    static class Native
    {
        public const int STD_INPUT_HANDLE = -10, STD_OUTPUT_HANDLE = -11;
        public const uint ENABLE_PROCESSED_INPUT = 0x1, ENABLE_WINDOW_INPUT = 0x8, ENABLE_MOUSE_INPUT = 0x10,
                          ENABLE_QUICK_EDIT_MODE = 0x40, ENABLE_EXTENDED_FLAGS = 0x80,
                          ENABLE_VIRTUAL_TERMINAL_INPUT = 0x200;
        public const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x4;
        public const ushort KEY_EVENT = 1, MOUSE_EVENT = 2;
        public const uint MOUSE_WHEELED = 0x4;

        [StructLayout(LayoutKind.Explicit)]
        public struct KEY_EVENT_RECORD
        {
            [FieldOffset(0)] public int bKeyDown;
            [FieldOffset(4)] public ushort wRepeatCount;
            [FieldOffset(6)] public ushort wVirtualKeyCode;
            [FieldOffset(8)] public ushort wVirtualScanCode;
            [FieldOffset(10)] public ushort UnicodeChar;
            [FieldOffset(12)] public uint dwControlKeyState;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct MOUSE_EVENT_RECORD
        {
            [FieldOffset(0)] public short X;
            [FieldOffset(2)] public short Y;
            [FieldOffset(4)] public uint dwButtonState;
            [FieldOffset(8)] public uint dwControlKeyState;
            [FieldOffset(12)] public uint dwEventFlags;
        }

        [StructLayout(LayoutKind.Explicit, Size = 20)]
        public struct INPUT_RECORD
        {
            [FieldOffset(0)] public ushort EventType;
            [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
            [FieldOffset(4)] public MOUSE_EVENT_RECORD MouseEvent;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetConsoleMode(IntPtr h, out uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetConsoleMode(IntPtr h, uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetNumberOfConsoleInputEvents(IntPtr h, out uint count);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "ReadConsoleInputW")]
        public static extern bool ReadConsoleInput(IntPtr h, [Out] INPUT_RECORD[] buffer, uint length, out uint read);

        // Resolução do timer do Windows: sem isto, Thread.Sleep arredonda para ~15,6 ms
        // e o laço a 40 fps cai para ~32 fps com engasgos.
        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint ms);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint ms);
    }

    // xorshift128+: bem mais rápido que System.Random no laço quente das partículas
    sealed class FastRandom
    {
        ulong s0, s1;

        public FastRandom(ulong seed)
        {
            s0 = seed ^ 0x9E3779B97F4A7C15UL;
            s1 = (seed * 0xBF58476D1CE4E5B9UL) ^ 0x94D049BB133111EBUL;
            if (s0 == 0 && s1 == 0) s1 = 1;
        }

        public double NextDouble()
        {
            ulong x = s0, y = s1;
            s0 = y;
            x ^= x << 23;
            s1 = x ^ y ^ (x >> 17) ^ (y >> 26);
            return ((s1 + y) >> 11) * (1.0 / 9007199254740992.0);
        }

        public int Next(int max) { return (int)(NextDouble() * max); }

        public double Gauss()
        {
            double u1 = 1.0 - NextDouble(), u2 = NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}

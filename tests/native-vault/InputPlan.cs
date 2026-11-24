using System;
namespace NativeUiDriver {
    public struct Stroke { public ushort Vk, Scan; public uint Flags; }
    public static class KeyboardPlanner {
        public static Stroke[] Plan(short mapping, uint scan) {
            // Ctrl/Alt and dead-key compositions are not silently approximated.
            if (mapping == -1 || ((mapping >> 8) & 0xff) > 1 || scan == 0 || scan > 0xff)
                throw new InvalidOperationException("KeyboardCharacterNotMappable");
            Stroke down = new Stroke { Scan = (ushort)scan, Flags = 8 };
            Stroke up = new Stroke { Scan = (ushort)scan, Flags = 10 };
            if (((mapping >> 8) & 1) == 0) return new Stroke[] {down, up};
            return new Stroke[] {
                new Stroke {Vk = 0x10}, down, up, new Stroke {Vk = 0x10, Flags = 2}
            };
        }
    }
}

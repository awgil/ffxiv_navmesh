using System.Collections.Generic;
using System.Numerics;

namespace Navmesh;

public struct BitMask
{
    public ulong Raw;

    public BitMask(ulong raw = 0) { Raw = raw; }

    public bool this[int index]
    {
        get => (Raw & MaskForBit(index)) != 0;
        set
        {
            if (value)
                Set(index);
            else
                Clear(index);
        }
    }

    public void Reset() => Raw = 0;
    public bool Any() => Raw != 0;
    public bool None() => Raw == 0;
    public void Set(int index) => Raw |= MaskForBit(index);
    public void Clear(int index) => Raw &= ~MaskForBit(index);
    public void Toggle(int index) => Raw ^= MaskForBit(index);
    public int NumSetBits() => BitOperations.PopCount(Raw);
    public int LowestSetBit() => BitOperations.TrailingZeroCount(Raw); // returns out-of-range value (64) if no bits are set
    public int HighestSetBit() => 63 - BitOperations.LeadingZeroCount(Raw); // returns out-of-range value (-1) if no bits are set

    public static BitMask operator ~(BitMask a) => new(~a.Raw);
    public static BitMask operator &(BitMask a, BitMask b) => new(a.Raw & b.Raw);
    public static BitMask operator |(BitMask a, BitMask b) => new(a.Raw | b.Raw);
    public static BitMask operator ^(BitMask a, BitMask b) => new(a.Raw ^ b.Raw);
    public static bool operator ==(BitMask a, BitMask b) => a.Raw == b.Raw;
    public static bool operator !=(BitMask a, BitMask b) => a.Raw != b.Raw;

    public IEnumerable<int> SetBits()
    {
        ulong v = Raw;
        while (v != 0)
        {
            int index = BitOperations.TrailingZeroCount(v);
            yield return index;
            v &= ~(1ul << index);
        }
    }

    private ulong MaskForBit(int index) => (uint)index < 64 ? (1ul << index) : 0;

    public override bool Equals(object? obj) => obj is BitMask other && this == other;
    public override int GetHashCode() => Raw.GetHashCode();
    public override string ToString() => $"{Raw:X}";
}

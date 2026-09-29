// Minimal LZMA decoder (algorithm from the public-domain LZMA SDK by Igor Pavlov), used only to read
// LZMA-compressed Unity bundles when scanning for dependency references.
// Unity stores LZMA blocks as: 5 property bytes (lc/lp/pb + dictionary size) followed by the raw stream;
// the uncompressed size comes from the bundle's block table.

using System;

namespace EFTAutoPrefab
{
    public static class EFTLzma
    {
        const int NumBitModelTotalBits = 11;
        const uint BitModelTotal = 1u << NumBitModelTotalBits;
        const int NumMoveBits = 5;
        const uint TopValue = 1u << 24;

        sealed class RangeDecoder
        {
            readonly byte[] _buf; int _pos;
            public uint Range, Code;
            public RangeDecoder(byte[] buf, int pos)
            {
                _buf = buf; _pos = pos;
                Code = 0; Range = 0xFFFFFFFF;
                for (int i = 0; i < 5; i++) Code = (Code << 8) | Next();
            }
            byte Next() => _pos < _buf.Length ? _buf[_pos++] : (byte)0;

            public uint DecodeBit(ushort[] probs, int i)
            {
                uint bound = (Range >> NumBitModelTotalBits) * probs[i];
                uint bit;
                if (Code < bound)
                {
                    Range = bound;
                    probs[i] += (ushort)((BitModelTotal - probs[i]) >> NumMoveBits);
                    bit = 0;
                }
                else
                {
                    Range -= bound; Code -= bound;
                    probs[i] -= (ushort)(probs[i] >> NumMoveBits);
                    bit = 1;
                }
                if (Range < TopValue) { Range <<= 8; Code = (Code << 8) | Next(); }
                return bit;
            }

            public uint DecodeDirect(int numBits)
            {
                uint res = 0;
                for (int i = 0; i < numBits; i++)
                {
                    Range >>= 1;
                    uint t = (Code - Range) >> 31; // 1 if Code < Range
                    Code -= Range & (t - 1);
                    res = (res << 1) | (1 - t);
                    if (Range < TopValue) { Range <<= 8; Code = (Code << 8) | Next(); }
                }
                return res;
            }
        }

        static ushort[] Probs(int n) { var p = new ushort[n]; for (int i = 0; i < n; i++) p[i] = (ushort)(BitModelTotal >> 1); return p; }

        static uint BitTree(RangeDecoder rc, ushort[] probs, int offset, int numBits)
        {
            uint m = 1;
            for (int i = 0; i < numBits; i++) m = (m << 1) + rc.DecodeBit(probs, offset + (int)m);
            return m - (1u << numBits);
        }

        static uint BitTreeReverse(RangeDecoder rc, ushort[] probs, int offset, int numBits)
        {
            uint m = 1, sym = 0;
            for (int i = 0; i < numBits; i++)
            {
                uint bit = rc.DecodeBit(probs, offset + (int)m);
                m = (m << 1) + bit;
                sym |= bit << i;
            }
            return sym;
        }

        sealed class LenDecoder
        {
            readonly ushort[] _choice = Probs(2);
            readonly ushort[] _low, _mid, _high = Probs(256);
            public LenDecoder(int posStates) { _low = Probs(posStates << 3); _mid = Probs(posStates << 3); }
            public uint Decode(RangeDecoder rc, int posState)
            {
                if (rc.DecodeBit(_choice, 0) == 0) return BitTree(rc, _low, posState << 3, 3);
                if (rc.DecodeBit(_choice, 1) == 0) return 8 + BitTree(rc, _mid, posState << 3, 3);
                return 16 + BitTree(rc, _high, 0, 8);
            }
        }

        /// <summary>Decodes Unity's LZMA block: props(5 bytes) + stream, to exactly outSize bytes.</summary>
        public static byte[] Decode(byte[] src, int outSize)
        {
            if (src.Length < 5) throw new ArgumentException("LZMA block too short");
            int d = src[0];
            if (d >= 9 * 5 * 5) throw new ArgumentException("bad LZMA properties");
            int lc = d % 9; d /= 9; int lp = d % 5; int pb = d / 5;
            // dictionary size (bytes 1..4) only bounds back-references; we decode into the full output buffer

            var outB = new byte[outSize];
            var rc = new RangeDecoder(src, 5);
            int posStates = 1 << pb;

            var isMatch = Probs(12 << 4);
            var isRep = Probs(12);
            var isRepG0 = Probs(12);
            var isRepG1 = Probs(12);
            var isRepG2 = Probs(12);
            var isRep0Long = Probs(12 << 4);
            var posSlot = Probs(4 << 6);
            var posDecoders = Probs(115); // kNumFullDistances(128) - kEndPosModelIndex(14) + 1
            var align = Probs(16);
            var lenDec = new LenDecoder(posStates);
            var repLenDec = new LenDecoder(posStates);
            var literal = Probs(0x300 << (lc + lp));

            uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
            int state = 0;
            int pos = 0;
            byte prev = 0;

            while (pos < outSize)
            {
                int posState = pos & (posStates - 1);
                if (rc.DecodeBit(isMatch, (state << 4) + posState) == 0)
                {
                    int litState = (((pos & ((1 << lp) - 1)) << lc) + (prev >> (8 - lc)));
                    int baseI = 0x300 * litState;
                    uint sym = 1;
                    if (state >= 7)
                    {
                        uint matchByte = outB[pos - (int)rep0 - 1];
                        do
                        {
                            uint matchBit = (matchByte >> 7) & 1;
                            matchByte <<= 1;
                            uint bit = rc.DecodeBit(literal, baseI + (int)(((1 + matchBit) << 8) + sym));
                            sym = (sym << 1) | bit;
                            if (matchBit != bit) break;
                        } while (sym < 0x100);
                    }
                    while (sym < 0x100) sym = (sym << 1) | rc.DecodeBit(literal, baseI + (int)sym);
                    prev = (byte)sym;
                    outB[pos++] = prev;
                    state = state < 4 ? 0 : (state < 10 ? state - 3 : state - 6);
                    continue;
                }

                uint len;
                if (rc.DecodeBit(isRep, state) != 0)
                {
                    if (pos == 0) throw new InvalidOperationException("LZMA data error");
                    if (rc.DecodeBit(isRepG0, state) == 0)
                    {
                        if (rc.DecodeBit(isRep0Long, (state << 4) + posState) == 0)
                        {
                            state = state < 7 ? 9 : 11;
                            prev = outB[pos - (int)rep0 - 1];
                            outB[pos++] = prev;
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (rc.DecodeBit(isRepG1, state) == 0) dist = rep1;
                        else
                        {
                            if (rc.DecodeBit(isRepG2, state) == 0) dist = rep2;
                            else { dist = rep3; rep3 = rep2; }
                            rep2 = rep1;
                        }
                        rep1 = rep0; rep0 = dist;
                    }
                    len = repLenDec.Decode(rc, posState);
                    state = state < 7 ? 8 : 11;
                }
                else
                {
                    rep3 = rep2; rep2 = rep1; rep1 = rep0;
                    len = lenDec.Decode(rc, posState);
                    state = state < 7 ? 7 : 10;

                    int lenToPosState = (int)(len < 4 ? len : 3);
                    uint slot = BitTree(rc, posSlot, lenToPosState << 6, 6);
                    if (slot >= 4)
                    {
                        int numDirect = (int)((slot >> 1) - 1);
                        rep0 = (2 | (slot & 1)) << numDirect;
                        if (slot < 14)
                            rep0 += BitTreeReverse(rc, posDecoders, (int)(rep0 - slot) - 1, numDirect);
                        else
                        {
                            rep0 += rc.DecodeDirect(numDirect - 4) << 4;
                            rep0 += BitTreeReverse(rc, align, 0, 4);
                        }
                        if (rep0 == 0xFFFFFFFF) break; // end marker
                    }
                    else rep0 = slot;
                    if (rep0 >= (uint)pos) throw new InvalidOperationException("LZMA distance out of range");
                }

                len += 2;
                int src0 = pos - (int)rep0 - 1;
                for (uint i = 0; i < len && pos < outSize; i++) outB[pos++] = outB[src0++];
                prev = outB[pos - 1];
            }
            return outB;
        }
    }
}

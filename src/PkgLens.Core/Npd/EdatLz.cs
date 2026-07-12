namespace PkgLens.Core.Npd;

/// <summary>
/// The range-coder LZ decompressor used by NPDRM EDAT compression. Ported faithfully from RPCS3's
/// <c>lz.cpp</c> (originally Hykem, GPL v2.0+); the pointer arithmetic is reproduced with array
/// indices. One call decompresses one EDAT block. Returns the number of bytes written to
/// <paramref name="output"/>, or a negative value on error (malformed stream / buffer over- or
/// under-run) — it fails loudly rather than emitting corrupt data.
///
/// NOTE: this core is a faithful port of the proven reference, but — unlike the rest of the EDAT
/// pipeline — it has not yet been checked byte-for-byte against a real compressed EDAT in this repo
/// (no compressed sample was available). Treat its output as unverified until such a check is run.
/// </summary>
internal static class EdatLz
{
    private sealed class St
    {
        public byte[] In = Array.Empty<byte>();
        public int Src;      // "src" pointer offset; the range coder reads at In[Src + 5]
        public uint Range;
        public uint Code;
        public byte[] Tmp = new byte[0xCC8];
    }

    public static int Decompress(byte[] output, byte[] input, int size)
    {
        try { return Run(output, input, size); }
        catch (IndexOutOfRangeException) { return -1; }
        catch (ArgumentOutOfRangeException) { return -1; }
    }

    private static void DecodeRange(St s)
    {
        if ((s.Range >> 24) == 0)
        {
            s.Range <<= 8;
            s.Code = (s.Code << 8) + s.In[s.Src + 5];
            s.Src++;
        }
    }

    private static int DecodeBit(St s, ref int index, bool hasIndex, int cIdx)
    {
        DecodeRange(s);

        byte c = s.Tmp[cIdx];
        uint val = (s.Range >> 8) * c;
        c = (byte)(c - (c >> 3));
        if (hasIndex) index <<= 1;

        if (s.Code < val)
        {
            s.Range = val;
            s.Tmp[cIdx] = (byte)(c + 31);
            if (hasIndex) index++;
            return 1;
        }

        s.Tmp[cIdx] = c;
        s.Code -= val;
        s.Range -= val;
        return 0;
    }

    private static int DecodeNumber(St s, int ptr, int index, out int bitFlag)
    {
        int i = 1;
        if (index >= 3)
        {
            DecodeBit(s, ref i, true, ptr + 0x18);
            if (index >= 4)
            {
                DecodeBit(s, ref i, true, ptr + 0x18);
                if (index >= 5)
                {
                    DecodeRange(s);
                    for (; index >= 5; index--)
                    {
                        i <<= 1;
                        s.Range >>= 1;
                        if (s.Code < s.Range) i++;
                        else s.Code -= s.Range;
                    }
                }
            }
        }

        bitFlag = DecodeBit(s, ref i, true, ptr);
        if (index >= 1)
        {
            DecodeBit(s, ref i, true, ptr + 0x8);
            if (index >= 2)
                DecodeBit(s, ref i, true, ptr + 0x10);
        }
        return i;
    }

    private static int DecodeWord(St s, int ptr, int index, out int bitFlag)
    {
        int i = 1;
        index /= 8;

        if (index >= 3)
        {
            DecodeBit(s, ref i, true, ptr + 4);
            if (index >= 4)
            {
                DecodeBit(s, ref i, true, ptr + 4);
                if (index >= 5)
                {
                    DecodeRange(s);
                    for (; index >= 5; index--)
                    {
                        i <<= 1;
                        s.Range >>= 1;
                        if (s.Code < s.Range) i++;
                        else s.Code -= s.Range;
                    }
                }
            }
        }

        bitFlag = DecodeBit(s, ref i, true, ptr);
        if (index >= 1)
        {
            DecodeBit(s, ref i, true, ptr + 1);
            if (index >= 2)
                DecodeBit(s, ref i, true, ptr + 2);
        }
        return i;
    }

    private static int Run(byte[] output, byte[] input, int size)
    {
        int offset = 0, bitFlag = 0, dataLength = 0, dataOffset = 0;
        byte prev = 0;

        int start = 0;          // 'start' index into output (bytes written = start)
        int end = size;         // 'end' = out + size
        byte head = input[0];

        var s = new St
        {
            In = input,
            Src = 0,
            Range = 0xFFFFFFFF,
            Code = ((uint)input[1] << 24) | ((uint)input[2] << 16) | ((uint)input[3] << 8) | input[4],
        };

        if (head > 0x80)
        {
            // Dictionary header says "not compressed": the block is 'code' raw bytes at input[5..].
            // (RPCS3's port returns 0 here, which truncates output; we return the copied length.)
            if (s.Code <= (uint)size)
            {
                Array.Copy(input, 5, output, 0, (int)s.Code);
                return (int)s.Code;
            }
            return -1;
        }

        // Sliding-window / probability table.
        for (int k = 0; k < 0xCA8; k++) s.Tmp[k] = 0x80;

        int dummy = 0;
        while (true)
        {
            int sect1 = offset + 0xB68;
            if (DecodeBit(s, ref dummy, false, sect1) == 0) // raw char
            {
                if (offset > 0) offset--;
                if (start == end) return start;

                int sect = (((((start & 7) << 8) + prev) >> head) & 7) * 0xFF - 1;
                sect1 = sect;
                int index = 1;
                do
                {
                    DecodeBit(s, ref index, true, sect1 + index);
                } while ((index >> 8) == 0);

                output[start++] = (byte)index;
            }
            else // compressed char stream
            {
                int index = -1;
                do
                {
                    sect1 += 8;
                    bitFlag = DecodeBit(s, ref dummy, false, sect1);
                    index += bitFlag;
                } while (bitFlag != 0 && index < 6);

                int bSize = 0x160;
                int sect2 = index + 0x7F1;

                if (index >= 0 || bitFlag != 0)
                {
                    int sect = (index << 5) | (((start << index) & 3) << 3) | (offset & 7);
                    sect1 = 0xBA8 + sect;
                    dataLength = DecodeNumber(s, sect1, index, out bitFlag);
                    if (dataLength == 0xFF) return start; // end of stream
                }
                else
                {
                    dataLength = 1;
                }

                if (dataLength <= 2)
                {
                    sect2 += 0xF8;
                    bSize = 0x40;
                }

                int diff = 0, shift = 1;
                do
                {
                    diff = (shift << 4) - bSize;
                    bitFlag = DecodeBit(s, ref shift, true, sect2 + (shift << 3));
                } while (diff < 0);

                if (diff > 0 || bitFlag != 0)
                {
                    if (bitFlag == 0) diff -= 8;
                    int sect3 = 0x928 + diff;
                    dataOffset = DecodeWord(s, sect3, diff, out bitFlag);
                }
                else
                {
                    dataOffset = 1;
                }

                int bufStart = start - dataOffset;
                int bufEnd = start + dataLength + 1;
                if (bufStart < 0) return -1;   // underflow
                if (bufEnd > end) return -1;    // overflow

                offset = ((bufEnd + 1) & 1) + 6;

                // Byte-by-byte copy (may overlap — LZ back-reference).
                do
                {
                    output[start++] = output[bufStart++];
                } while (start < bufEnd);
            }

            prev = output[start - 1];
        }
    }
}

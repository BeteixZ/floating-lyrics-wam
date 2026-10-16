// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from MxIris-LyricsX-Project/LyricsKit, revision 6f071990d0c9c6f48d29284db805990bffb912ed.
// Source: Sources/LyricsService/Parser/QQMusicQrcDecrypter.swift

using System.IO.Compression;
using System.Text;

namespace AppleMusicLyrics.Infrastructure.Windows.External;

/// <summary>The QQ QRC variant has custom DES tables and byte ordering; framework TripleDES is incompatible.</summary>
internal static class QrcLyricsDecoder
{
    private static readonly byte[] Key1 = Encoding.ASCII.GetBytes("!@#)(NHLiuy*$%^&");
    private static readonly byte[] Key2 = Encoding.ASCII.GetBytes("123ZXC!@#)(*$%^&");
    private static readonly byte[] Key3 = Encoding.ASCII.GetBytes("!@#)(*$%^&abcDEF");
    private static readonly int[][] SBoxes =
    [
        [14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7, 0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8, 4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0, 15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13],
        [15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10, 3, 13, 4, 7, 15, 2, 8, 15, 12, 0, 1, 10, 6, 9, 11, 5, 0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15, 13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9],
        [10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8, 13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1, 13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7, 1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12],
        [7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15, 13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9, 10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4, 3, 15, 0, 6, 10, 10, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14],
        [2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9, 14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6, 4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14, 11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3],
        [12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11, 10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8, 9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6, 4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13],
        [4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1, 13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6, 1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2, 6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12],
        [13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7, 1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2, 7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8, 2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11],
    ];
    private static readonly int[] KeyC = [56, 48, 40, 32, 24, 16, 8, 0, 57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18, 10, 2, 59, 51, 43, 35];
    private static readonly int[] KeyD = [62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37, 29, 21, 13, 5, 60, 52, 44, 36, 28, 20, 12, 4, 27, 19, 11, 3];
    private static readonly int[] KeyCompression = [13, 16, 10, 23, 0, 4, 2, 27, 14, 5, 20, 9, 22, 18, 11, 3, 25, 7, 15, 6, 26, 19, 12, 1, 40, 51, 30, 36, 46, 54, 29, 39, 50, 44, 32, 47, 43, 48, 38, 55, 33, 52, 45, 41, 49, 35, 28, 31];
    private static readonly int[] KeyShifts = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1];
    private static readonly int[] Permutation = [15, 6, 19, 20, 28, 11, 27, 16, 0, 14, 22, 25, 4, 17, 30, 9, 1, 7, 23, 13, 31, 26, 2, 8, 18, 12, 29, 5, 21, 10, 3, 24];
    private const int MaxDecodedBytes = 4 * 1024 * 1024;

    internal static string Decode(string hex)
    {
        if (hex.Length == 0 || hex.Length > MaxDecodedBytes * 2 || hex.Length % 16 != 0)
        {
            throw new FormatException("QQ QRC payload must contain complete eight-byte blocks.");
        }

        var data = Convert.FromHexString(hex);
        Transform(data, Key1, decrypt: true);
        Transform(data, Key2, decrypt: false);
        Transform(data, Key3, decrypt: true);

        // Upstream drops the two-byte zlib header and inflates the raw DEFLATE stream.
        using var input = new MemoryStream(data, 2, data.Length - 2, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = deflate.Read(buffer)) > 0)
        {
            if (output.Length + count > MaxDecodedBytes)
            {
                throw new InvalidDataException("QQ QRC decoded payload exceeds the size limit.");
            }
            output.Write(buffer, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(output.ToArray());
    }

    private static void Transform(byte[] data, byte[] key, bool decrypt)
    {
        var schedule = BuildSchedule(key, decrypt);
        for (var offset = 0; offset < data.Length; offset += 8)
        {
            var block = data.AsSpan(offset, 8);
            uint left = 0, right = 0;
            for (var index = 0; index < 32; index++)
            {
                var group = index / 8;
                var position = 57 + group * 2 - index % 8 * 8;
                left |= ByteBit(block, position, 31 - index);
                right |= ByteBit(block, position - 1, 31 - index);
            }
            for (var round = 0; round < 15; round++)
            {
                var previous = right;
                right = Round(right, schedule[round]) ^ left;
                left = previous;
            }
            left ^= Round(right, schedule[15]);

            for (var index = 0; index < 8; index++)
            {
                uint value = 0;
                for (var bit = 0; bit < 8; bit++)
                {
                    var state = bit % 2 == 0 ? right : left;
                    value |= WordBit(state, 7 - index + bit / 2 * 8, 7 - bit);
                }
                block[index < 4 ? 3 - index : 11 - index] = (byte)value;
            }
        }
    }

    private static byte[][] BuildSchedule(ReadOnlySpan<byte> key, bool decrypt)
    {
        uint c = 0, d = 0;
        for (var index = 0; index < 28; index++)
        {
            c |= ByteBit(key, KeyC[index], 31 - index);
            d |= ByteBit(key, KeyD[index], 31 - index);
        }
        var schedule = new byte[16][];
        for (var round = 0; round < 16; round++)
        {
            var shift = KeyShifts[round];
            c = ((c << shift) | (c >> (28 - shift))) & 0xfffffff0;
            d = ((d << shift) | (d >> (28 - shift))) & 0xfffffff0;
            var subkey = new byte[6];
            for (var bit = 0; bit < 48; bit++)
            {
                // The upstream QRC variant uses -27 here, rather than standard DES's -28.
                var value = bit < 24 ? WordBit(c, KeyCompression[bit], 7 - bit % 8)
                    : WordBit(d, KeyCompression[bit] - 27, 7 - bit % 8);
                subkey[bit / 8] |= (byte)value;
            }
            schedule[decrypt ? 15 - round : round] = subkey;
        }
        return schedule;
    }

    private static uint Round(uint state, ReadOnlySpan<byte> key)
    {
        // DES expansion in the upstream order, packed as eight groups of six bits.
        Span<int> groups = stackalloc int[8];
        for (var group = 0; group < 8; group++)
        {
            var six = 0;
            for (var bit = 0; bit < 6; bit++)
            {
                var source = (group * 4 + bit + 31) % 32;
                six = (six << 1) | (int)WordBit(state, source, 0);
            }
            var keyGroup = 0;
            for (var bit = 0; bit < 6; bit++)
            {
                var source = group * 6 + bit;
                keyGroup = (keyGroup << 1) | ((key[source / 8] >> (7 - source % 8)) & 1);
            }
            groups[group] = six ^ keyGroup;
        }
        uint substituted = 0;
        for (var group = 0; group < 8; group++)
        {
            var six = groups[group];
            var index = (six & 0x20) | ((six & 0x1f) >> 1) | ((six & 1) << 4);
            substituted |= (uint)SBoxes[group][index] << (28 - group * 4);
        }
        uint result = 0;
        for (var bit = 0; bit < 32; bit++)
        {
            result |= WordBit(substituted, Permutation[bit], 31 - bit);
        }
        return result;
    }

    private static uint ByteBit(ReadOnlySpan<byte> data, int source, int destination)
        => (uint)((data[source / 32 * 4 + 3 - source % 32 / 8] >> (7 - source % 8)) & 1) << destination;
    private static uint WordBit(uint data, int source, int destination)
        => ((data >> (31 - source)) & 1) << destination;
}

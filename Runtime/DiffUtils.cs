using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace BundleDiff
{
    public static class DiffUtils
    {
        #region Interface

        /// <summary>
        /// compute the adler32 result by given byte array
        /// 通过给定的比特数据计算出adler校验和
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        public static uint ComputeAdler32(byte[] buffer)
        {
            uint a = 1, b = 0;
            var count = buffer.Length;
            for (int i = 0; i < count; i++)
            {
                a = (a + buffer[i]) % MOD_ADLER;
                b = (b + a) % MOD_ADLER;
            }

            return (b << 16) | a;
        }
        
        /// <summary>
        /// adler 32 rolling check
        /// adler32 算法的滚动实现
        /// </summary>
        /// <param name="adler"></param>
        /// <param name="outByte"></param>
        /// <param name="inByte"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public static uint ComputeAdler32Roll(uint adler, byte outByte, byte inByte, int length)
        {
            uint a = adler & 0xFFFF;
            uint b = (adler >> 16) & 0xFFFF;
            a = (a - outByte + inByte) % MOD_ADLER;
            b = (b - (uint)(length * outByte) + a + (MOD_ADLER - 1)) % MOD_ADLER;

            return (b << 16) | a;
        }

        /// <summary>
        /// use xxhash to compute strong hash, support read from middle->end, then start->middle-1
        /// 使用xxhash来计算强校验码，支持从数据中间向右读取，再从头向中间读取
        /// </summary>
        /// <returns></returns>
        public static ulong ComputeStrongSum(byte[] data, int offset, int length)
        {
            ulong seed = 0;
            ulong hash = seed + PRIME64_1 + PRIME64_5;

            int remaining = length;
            int currentIndex = offset;
            int end = length;
            // read from offset to end
            // endIndex should in range
            while (currentIndex + 7 < end)
            {
                ulong value = BitConverter.ToUInt64(data, currentIndex);
                hash ^= ProcessBlock(value);
                hash = RotateLeft(hash, 27) * PRIME64_1 + PRIME64_4;
                currentIndex += 8;
                remaining -= 8;
            }

            // try read left bytes
            if (remaining > 0)
            {
                var endLeft = length - currentIndex;
                var startOffset = 8 - endLeft;
                ulong value = GetSpecialBlockValue(data, currentIndex, end, startOffset);
                hash ^= ProcessBlock(value);
                hash = RotateLeft(hash, 27) * PRIME64_1 + PRIME64_4;
                remaining -= 8;
                currentIndex = startOffset;
                while (remaining > 0)
                {
                    value = BitConverter.ToUInt64(data, currentIndex);
                    hash ^= ProcessBlock(value);
                    hash = RotateLeft(hash, 27) * PRIME64_1 + PRIME64_4;
                    currentIndex += 8;
                    remaining -= 8;
                }
            }
            
            hash ^= hash >> 33;
            hash *= PRIME64_2;
            hash ^= hash >> 29;
            hash *= PRIME64_3;
            hash ^= hash >> 32;
            return hash;
        }

        /// <summary>
        /// md5 compute file hash
        /// 使用Md5计算文件hash
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        public static byte[] ComputeFileHash(Stream stream)
        {
            var md5 = MD5.Create();
            return md5.ComputeHash(stream);
        }

        public static int ComputeBlockSizeByFileSize(int fileSize)
        {
            if (fileSize < 100 * 1024 * 1024)
                return SMALL_BLOCK_SIZE;

            return LARGE_BLOCK_SIZE;
        }
        
        public static byte[] Pad(byte[] array, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                array[i] = 0;
            }
            return array;
        }
        
        #endregion

        #region method

        private static ulong ProcessBlock(ulong block)
        {
            return RotateLeft(block * PRIME64_2, 31) * PRIME64_1;
        }

        private static ulong RotateLeft(ulong value, int bits)
        {
            return (value << bits) | (value >> (64 - bits));
        }

        private static ulong GetSpecialBlockValue(byte[] data, int currentIndex, int endLeft, int startLeft)
        {
            var buffer = new byte[8];
            var bufferIndex = 0;
            var dataIndex = currentIndex;
            // read end last byte
            for (int i = 0; i < endLeft; i++)
            {
                buffer[bufferIndex++] = data[dataIndex++];
            }
            // read start byte
            for (int i = 0; i < endLeft; i++)
            {
                buffer[bufferIndex++] = data[i];
            }

            return BitConverter.ToUInt64(buffer);
        }

        #endregion


        #region Field

        private const uint MOD_ADLER = 65521;
        
        private const ulong PRIME64_1 = 11400714785074694791UL;
        private const ulong PRIME64_2 = 14029467366897019727UL;
        private const ulong PRIME64_3 = 1609587929392839161UL;
        private const ulong PRIME64_4 = 9650029242287828579UL;
        private const ulong PRIME64_5 = 2870177450012600261UL;

        private const int SMALL_BLOCK_SIZE = 2048;
        private const int LARGE_BLOCK_SIZE = 4096;

        #endregion
    }
}
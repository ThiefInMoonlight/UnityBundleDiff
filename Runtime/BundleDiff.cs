using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace BundleDiff
{
    public static class BundleDiff
    {
        public static void MergeZSyncFile(Stream oldFileStream, Stream zsyncFileStream, string newFileUrl, string outputPath)
        {
            try
            {
                var tmpBuffer = new byte[28];
                var read = zsyncFileStream.Read(tmpBuffer, 0, 28);
                if (read != 28)
                {
                    throw new Exception($"Cannot read zsync Info head, {read} < 28");
                }

                var fileHash = new byte[16];
                Array.Copy(tmpBuffer, 0, fileHash, 0, 16);
                var fileLength = BitConverter.ToInt32(tmpBuffer, 16);
                var blockSize = BitConverter.ToInt32(tmpBuffer, 20);
                var blockCount = BitConverter.ToInt32(tmpBuffer, 24);
                var blockSums = GetZSyncBlockSums(tmpBuffer, zsyncFileStream, blockCount);
                CompareRemoteBlocksWithOldFile(oldFileStream, blockSums, blockSize);
                ZSyncMerge(oldFileStream, blockSums, newFileUrl, outputPath);
                using (var newFile = File.OpenRead(outputPath))
                {
                    var hash = DiffUtils.ComputeFileHash(newFile);
                    if (!hash.SequenceEqual(fileHash))
                    {
                        Debug.LogError($"failed to zsync merge file: {outputPath}, hash not correct");
                    }
                }
            }
            catch(Exception e)
            {
                Debug.LogError($"zsync merge failed, outputFile:{outputPath}, message:{e.Message}");
            }
        }

        public static void MergeBSDiffPatch(Stream oldFileStream, Stream PatchStream, string outputPath)
        {
            try
            {
                var tmpBuffer = new byte[28];
                var read = PatchStream.Read(tmpBuffer, 0, 28);
                if (read != 28)
                {
                    throw new Exception($"Cannot read zsync Info head, {read} < 28");
                }

                var fileHash = new byte[16];
                Array.Copy(tmpBuffer, 0, fileHash, 0, 16);
                var fileLength = BitConverter.ToInt32(tmpBuffer, 16);
                var blockSize = BitConverter.ToInt32(tmpBuffer, 20);
                var blockCount = BitConverter.ToInt32(tmpBuffer, 24);

                using (var output = File.Create(outputPath))
                {
                    for (int i = 0; i < blockCount; i++)
                    {
                        PatchStream.Read(tmpBuffer, 0, 9);
                        var blockStart = BitConverter.ToInt32(tmpBuffer, 0);
                        var blockLength = BitConverter.ToInt32(tmpBuffer, 4);
                        var remoteBlock = BitConverter.ToBoolean(tmpBuffer, 8);
                        if (remoteBlock)
                        {
                            PatchStream.CopyTo(output, blockLength);
                        }
                        else
                        {
                            oldFileStream.Seek(blockStart, SeekOrigin.Begin);
                            oldFileStream.CopyTo(output, blockLength);
                        }
                    }
                }

                using (var newFile = File.OpenRead(outputPath))
                {
                    var hash = DiffUtils.ComputeFileHash(newFile);
                    if (!hash.SequenceEqual(fileHash))
                    {
                        Debug.LogError($"failed to bsdiff merge file: {outputPath}, hash not correct");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"bsdiff merge failed, outputFile:{outputPath}, message:{e.Message}");
            }
        }



        #region Method

        private static List<BlockSum> GetZSyncBlockSums(byte[] buffer, Stream stream, int blockCount)
        {
            var blockSums = new List<BlockSum>();
            for (int i = 0; i < blockCount; i++)
            {
                var block = new BlockSum();
                stream.Read(buffer, 0, 20);
                block.BlockStart = BitConverter.ToInt32(buffer, 0);
                block.BlockSize = BitConverter.ToInt32(buffer, 4);
                block.WeakSum = BitConverter.ToUInt32(buffer, 8);
                block.StrongSum = BitConverter.ToUInt64(buffer, 16);
                block.RemoteBlock = true;
                blockSums.Add(block);
            }

            return blockSums;
        }

        public static void CompareRemoteBlocksWithOldFile(Stream oldFile, List<BlockSum> blockSums, int blockSize)
        {
            GetBlockInfo(blockSums, out var rbDict, out var msTable);
            var fileLen = oldFile.Length;
            var buffer = new byte[blockSize];
            oldFile.Seek(0, SeekOrigin.Begin);
            var searching = true;
            var jumpSearch = true;
            var searchIndex = 0;
            var rollIndex = -1;
            var targetBlockId = -1;
            uint weakSum = 0;
            ulong strongSum = 0;
            var read = 0;
            while (searching)
            {
                targetBlockId = -1;
                if (jumpSearch)
                {
                    read = oldFile.Read(buffer, 0, blockSize);
                    if (read < blockSize)
                        buffer = DiffUtils.Pad(buffer, read, blockSize);
                    weakSum = DiffUtils.ComputeAdler32(buffer);
                }
                else
                {
                    // when rolling, regard the buffer as stack, control the HeadIndex we know where should to be replaced
                    // 滚动校验的时候，把buffer当作一个栈，这样就可知道最老的byte，方便进出
                    byte outByte = buffer[rollIndex];
                    byte inByte = 0;
                    if (searchIndex + blockSize <= fileLen)
                    {
                        oldFile.Read(buffer, rollIndex, 1);
                        inByte = buffer[rollIndex];
                    }
                    else
                    {
                        buffer[rollIndex] = 0;
                        read--;
                    }

                    weakSum = DiffUtils.ComputeAdler32Roll(weakSum, outByte, inByte, blockSize);
                }

                var tag = GetWeakSumTag(weakSum);
                // if tag hit, then search the dict, more efficient
                // 如果缺失表命中，才进行查询
                if (msTable[tag] > 0)
                {
                    if (rbDict.TryGetValue(weakSum, out var list))
                    {
                        var bufferHead = rollIndex + 1;
                        if (bufferHead >= blockSize)
                            bufferHead = 0;
                        strongSum = DiffUtils.ComputeStrongSum(buffer, bufferHead, blockSize);
                        foreach (var index in list)
                        {
                            var block = blockSums[index];
                            if (block.RemoteBlock && read == block.BlockSize && strongSum == block.StrongSum)
                            {
                                targetBlockId = index;
                                break;
                            }
                        }
                    }
                }

                if (targetBlockId >= 0)
                {
                    var block = blockSums[targetBlockId];
                    block.RemoteBlock = false;
                    block.BlockStart = searchIndex;
                    blockSums[targetBlockId] = block;

                    jumpSearch = true;
                    searchIndex += blockSize;
                }
                else
                {
                    searchIndex ++;
                    jumpSearch = false;
                }

                if (searchIndex >= fileLen)
                {
                    searching = false;
                }
            }
            MergeBlocks(blockSums);
        }

        /// <summary>
        /// get dict of blocks and missing table
        /// 根据区块信息获取区块字典和缺失表
        /// </summary>
        /// <param name="blockSums"></param>
        /// <param name="rbDict"></param>
        /// <param name="msTable"></param>
        private static void GetBlockInfo(List<BlockSum> blockSums, out Dictionary<uint, List<int>> rbDict, out byte[] msTable)
        {
            rbDict = new Dictionary<uint, List<int>>();
            msTable = new byte[MissingTableCount];
            var count = blockSums.Count;
            for (int i = 0; i < count; i++)
            {
                var block = blockSums[i];
                if (!rbDict.TryGetValue(block.WeakSum, out var list))
                {
                    rbDict[block.WeakSum] = new List<int>();
                }

                rbDict[block.WeakSum].Add(i);
                var tag = GetWeakSumTag(block.WeakSum);
                msTable[tag] = 1;
            }
        }

        private static void MergeBlocks(List<BlockSum> blockSums)
        {
            var count = blockSums.Count;
            for (int i = count - 1; i > 0; i--)
            {
                var rb = blockSums[i];
                var lb = blockSums[i - 1];
                if (rb.RemoteBlock == lb.RemoteBlock)
                {
                    if ((lb.BlockStart + lb.BlockSize) == rb.BlockStart)
                    {
                        lb.BlockSize += rb.BlockSize;
                        blockSums.RemoveAt(i);
                        blockSums[i - 1] = lb;
                    }
                }
            }
        }

        /// <summary>
        /// get middle 7 of the weaksum
        /// 获取若校验码的7位
        /// </summary>
        /// <param name="weakSum"></param>
        /// <returns></returns>
        private static int GetWeakSumTag(uint weakSum)
        {
            var middleBit = (weakSum >> 12) & 0x7F;
            return (int)middleBit;
        }


        private static void ZSyncMerge(Stream oldFile, List<BlockSum> blockSums, string newFileUrl, string outputPath)
        {
            using (var stream = File.Create(outputPath))
            {
                foreach (var block in blockSums)
                {
                    if (block.RemoteBlock)
                    {
                        DownloadBlockFromRemote(stream, newFileUrl, block);
                    }
                    else
                    {
                        CopyBlockFromOldFile(block, stream, oldFile);
                    }
                }
            }
        }

        private static void DownloadBlockFromRemote(Stream newFile ,string newFileUrl, BlockSum block)
        {
            var request = UnityWebRequest.Get(newFileUrl);
            request.SetRequestHeader("Range",
                $"bytes={block.BlockStart}-{block.BlockStart + block.BlockSize - 1}");
            request.SendWebRequest();
            while (!request.isDone)
            {
                continue;
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new Exception(
                    $"failed to download file:{newFileUrl}, range bytes={block.BlockStart}-{block.BlockStart + block.BlockSize - 1} ");
            }
            
            newFile.Write(request.downloadHandler.data);
        }

        private static void CopyBlockFromOldFile(BlockSum block, Stream newFile, Stream oldFile)
        {
            oldFile.Seek(block.BlockStart, SeekOrigin.Begin);
            oldFile.CopyTo(newFile, block.BlockSize);
        }

        #endregion

        #region Field

        private const int MissingTableCount = 256;

        #endregion
    }
}
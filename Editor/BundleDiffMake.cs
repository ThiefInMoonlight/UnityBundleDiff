using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BundleDiff.Editor
{
    public class BundleDiffMake
    {
        public static void ZSyncMake(string newFilePath, string zsyncInfoPath)
        {
            try
            {
                using (var newFile = File.OpenRead(newFilePath))
                {
                    var fileHash = DiffUtils.ComputeFileHash(newFile);
                    var fileLength = (int)newFile.Length;
                    var blockSize = DiffUtils.ComputeBlockSizeByFileSize(fileLength);
                    newFile.Seek(0, SeekOrigin.Begin);
                    var buffer = new byte[blockSize];
                    var blockSums = GetNewFileBlockSums(newFile, buffer, blockSize);

                    using (var zsyncStream = File.OpenWrite(zsyncInfoPath))
                    {
                        var blockCount = blockSums.Count;
                        zsyncStream.Write(fileHash);//16
                        zsyncStream.Write(BitConverter.GetBytes(fileLength));//4
                        zsyncStream.Write(BitConverter.GetBytes(blockSize));//4
                        zsyncStream.Write(BitConverter.GetBytes(blockCount));//4
                        // has write 28 byte
                        for (int i = 0; i < blockCount; i++)
                        {
                            var block = blockSums[i];
                            zsyncStream.Write(BitConverter.GetBytes(block.BlockStart));//4
                            zsyncStream.Write(BitConverter.GetBytes(block.BlockSize));//4
                            zsyncStream.Write(BitConverter.GetBytes(block.WeakSum));//4
                            zsyncStream.Write(BitConverter.GetBytes(block.StrongSum));//8
                            // one block write 20 byte
                        }

                        zsyncStream.Flush();
                    }
                }
            }
            catch(Exception e)
            {
                if (File.Exists(zsyncInfoPath))
                {
                    File.Delete(zsyncInfoPath);
                }

                Debug.LogError($"ZSyncMake failed, newFile:{newFilePath}, outputFile:{zsyncInfoPath}, error:{e}");
            }
        }

        public static void BSDiffMake(string newFilePath, string oldFilePath, string outputPath)
        {
            try
            {
                using (var newFile = File.OpenRead(newFilePath))
                {
                    var fileHash = DiffUtils.ComputeFileHash(newFile);
                    var fileLength = (int)newFile.Length;
                    var blockSize = DiffUtils.ComputeBlockSizeByFileSize(fileLength);
                    newFile.Seek(0, SeekOrigin.Begin);
                    var buffer = new byte[blockSize];
                    var blockSums = GetNewFileBlockSums(newFile, buffer, blockSize);
                    
                    using (var oldStream = File.OpenRead(oldFilePath))
                    {
                        BundleDiff.CompareRemoteBlocksWithOldFile(oldStream, blockSums, blockSize);

                        using (var output = File.Create(outputPath))
                        {
                            var blockCount = blockSums.Count;
                            output.Write(fileHash); //16
                            output.Write(BitConverter.GetBytes(fileLength));//4
                            output.Write(BitConverter.GetBytes(blockSize));//4
                            output.Write(BitConverter.GetBytes(blockCount));//4
                            // has write 28 byte
                            for (int i = 0; i < blockCount; i++)
                            {
                                var block = blockSums[i];
                                output.Write(BitConverter.GetBytes(block.BlockStart));//4
                                output.Write(BitConverter.GetBytes(block.BlockSize));//4
                                output.Write(BitConverter.GetBytes(block.RemoteBlock));//1
                                // one block write 9 byte

                                if (block.RemoteBlock)
                                {
                                    // copy new file block to patch 
                                    newFile.Seek(block.BlockStart, SeekOrigin.Begin);
                                    newFile.CopyTo(output, block.BlockSize);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }


        #region Method

        private static List<BlockSum> GetNewFileBlockSums(Stream stream, byte[] buffer, int blockSize)
        {
            var blockSums = new List<BlockSum>();
            stream.Seek(0, SeekOrigin.Begin);
            var totalRead = stream.Length;
            var index = 0;
            while (totalRead > 0)
            {
                var read = stream.Read(buffer, 0, blockSize);
                if (read == 0)
                {
                    throw new Exception($"read failed, read 0 byte from file");
                }

                if (read < blockSize)
                {
                    buffer = DiffUtils.Pad(buffer, read, blockSize);
                }

                var weakSum = DiffUtils.ComputeAdler32(buffer);
                var strongSum = DiffUtils.ComputeStrongSum(buffer, 0, blockSize);
                var block = new BlockSum();
                block.BlockStart = index;
                block.BlockSize = read;
                block.WeakSum = weakSum;
                block.StrongSum = strongSum;
                block.RemoteBlock = true;
                blockSums.Add(block);
                index += read;
            }

            return blockSums;
        }

        #endregion
    }
}
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace WorldBuilder.Shared.Lib {
    /// <summary>
    /// Fixes two incompatibilities between Chorizite.DatReaderWriter and ACE's DatLoader:
    ///
    /// 1. Free block allocation: Chorizite assumes free blocks are contiguous
    ///    (FirstFreeBlock += BlockSize), but the retail DAT format uses a linked
    ///    list. If the copied DAT's free blocks aren't contiguous, Chorizite will
    ///    allocate in-use blocks, corrupting B-tree nodes and file data.
    ///    Fix: before opening the DAT, reset FreeCount=0 so all new allocations
    ///    come from file expansion at the end.
    ///
    /// 2. Leaf node branch sentinels: Chorizite writes 0xCDCDCDCD for unused
    ///    branch slots in non-empty leaf nodes. ACE checks Branches[0]!=0 to
    ///    detect internal nodes, so it misidentifies leaves as internal nodes
    ///    and tries to traverse phantom children.
    ///    Fix: after writing, walk the B-tree and replace 0xCDCDCDCD with 0.
    /// </summary>
    public static class DatExportFixer {
        private const int HEADER_OFFSET = 320;

        private const int OFF_MAGIC      = HEADER_OFFSET + 0;
        private const int OFF_BLOCKSIZE  = HEADER_OFFSET + 4;
        private const int OFF_FILESIZE   = HEADER_OFFSET + 8;
        private const int OFF_FREE_HEAD  = HEADER_OFFSET + 20;
        private const int OFF_FREE_TAIL  = HEADER_OFFSET + 24;
        private const int OFF_FREE_COUNT = HEADER_OFFSET + 28;
        private const int OFF_ROOT_BLOCK = HEADER_OFFSET + 32;

        private const int EXPECTED_MAGIC = 0x00005442;

        private const int MAX_BRANCHES = 62;
        private const int MAX_FILES = 61;
        private const int FILE_ENTRY_SIZE = 24;
        private const int NODE_HEADER_SIZE = MAX_BRANCHES * 4 + 4;

        private const uint SENTINEL = 0xCDCDCDCD;

        /// <summary>
        /// Patches the DAT header so that FreeCount=0, forcing Chorizite to
        /// allocate all new blocks via file expansion (safe, contiguous space).
        /// Must be called BEFORE opening the file with DatReaderWriter.
        /// </summary>
        public static void PatchFreeBlocksBeforeExport(string datPath) {
            if (!File.Exists(datPath)) return;

            using var fs = new FileStream(datPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (fs.Length < HEADER_OFFSET + 80) return;

            var buf = new byte[4];

            fs.Position = OFF_MAGIC;
            fs.ReadExactly(buf, 0, 4);
            int magic = BinaryPrimitives.ReadInt32LittleEndian(buf);
            if (magic != EXPECTED_MAGIC) return;

            fs.Position = OFF_FILESIZE;
            fs.ReadExactly(buf, 0, 4);
            int fileSize = BinaryPrimitives.ReadInt32LittleEndian(buf);

            BinaryPrimitives.WriteInt32LittleEndian(buf, fileSize);
            fs.Position = OFF_FREE_HEAD;
            fs.Write(buf, 0, 4);

            fs.Position = OFF_FREE_TAIL;
            fs.Write(buf, 0, 4);

            BinaryPrimitives.WriteInt32LittleEndian(buf, 0);
            fs.Position = OFF_FREE_COUNT;
            fs.Write(buf, 0, 4);

            fs.Flush();

            Console.WriteLine($"[DatExportFixer] Patched free blocks in {Path.GetFileName(datPath)}: FreeHead={fileSize:X8}, FreeTail={fileSize:X8}, FreeCount=0");
        }

        /// <summary>
        /// Walks the entire B-tree after export and replaces 0xCDCDCDCD sentinel
        /// values in leaf node branch slots with 0x00000000 so ACE's DatLoader
        /// correctly identifies them as leaf nodes.
        /// Must be called AFTER disposing the DatReaderWriter.
        /// </summary>
        public static void FixLeafBranchSentinels(string datPath) {
            if (!File.Exists(datPath)) return;

            using var fs = new FileStream(datPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (fs.Length < HEADER_OFFSET + 80) return;

            var headerBuf = new byte[80];
            fs.Position = HEADER_OFFSET;
            fs.ReadExactly(headerBuf, 0, 80);

            int magic = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(0, 4));
            if (magic != EXPECTED_MAGIC) return;

            int blockSize = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(4, 4));
            int fileSize = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(8, 4));
            int rootBlock = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(32, 4));

            if (blockSize <= 4 || rootBlock <= 0) return;

            int nodesFixed = 0;
            var visited = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(rootBlock);

            int nodeDataSize = NODE_HEADER_SIZE + MAX_FILES * FILE_ENTRY_SIZE;
            var nodeBuf = new byte[nodeDataSize];

            while (stack.Count > 0) {
                int nodeOffset = stack.Pop();
                if (nodeOffset <= 0 || nodeOffset >= fs.Length || !visited.Add(nodeOffset)) continue;

                try {
                    Array.Clear(nodeBuf);
                    ReadBlockChain(fs, blockSize, nodeOffset, nodeBuf, nodeDataSize);
                }
                catch {
                    continue;
                }

                int fileCount = BinaryPrimitives.ReadInt32LittleEndian(
                    nodeBuf.AsSpan(MAX_BRANCHES * 4, 4));

                if (fileCount < 0 || fileCount > MAX_FILES) continue;

                int branchCount = 0;
                bool hasRealBranch = false;
                for (int i = 0; i < MAX_BRANCHES; i++) {
                    uint b = BinaryPrimitives.ReadUInt32LittleEndian(
                        nodeBuf.AsSpan(i * 4, 4));
                    if (b != 0 && b != SENTINEL) {
                        branchCount = i + 1;
                        hasRealBranch = true;
                    }
                }

                if (hasRealBranch && fileCount > 0) {
                    branchCount = Math.Min(fileCount + 1, MAX_BRANCHES);
                }

                if (branchCount > 0) {
                    for (int i = 0; i < branchCount; i++) {
                        int childOffset = BinaryPrimitives.ReadInt32LittleEndian(
                            nodeBuf.AsSpan(i * 4, 4));
                        if (childOffset > 0 && childOffset < fs.Length
                            && childOffset != unchecked((int)SENTINEL)) {
                            stack.Push(childOffset);
                        }
                    }
                }

                bool needsFixup = false;
                for (int i = 0; i < MAX_BRANCHES; i++) {
                    uint b = BinaryPrimitives.ReadUInt32LittleEndian(
                        nodeBuf.AsSpan(i * 4, 4));
                    if (b == SENTINEL) {
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            nodeBuf.AsSpan(i * 4, 4), 0);
                        needsFixup = true;
                    }
                }

                if (needsFixup) {
                    try {
                        WriteBlockChain(fs, blockSize, nodeOffset, nodeBuf, nodeDataSize);
                        nodesFixed++;
                    }
                    catch {
                        Console.WriteLine($"[DatExportFixer] Warning: failed to write fixup for node at 0x{nodeOffset:X8}");
                    }
                }
            }

            fs.Flush();

            if (nodesFixed > 0) {
                Console.WriteLine($"[DatExportFixer] Fixed {nodesFixed} leaf node(s) in {Path.GetFileName(datPath)}");
            }
        }

        private static void ReadBlockChain(FileStream fs, int blockSize, int startBlock,
            byte[] buffer, int bytesToRead) {
            int currentBlock = startBlock;
            int bufferOffset = 0;
            var ptrBuf = new byte[4];
            int dataPerBlock = blockSize - 4;

            while (bufferOffset < bytesToRead && currentBlock > 0) {
                int toRead = Math.Min(dataPerBlock, bytesToRead - bufferOffset);

                fs.Position = currentBlock + 4;
                int read = fs.Read(buffer, bufferOffset, toRead);
                bufferOffset += read;
                if (read < toRead) break;

                if (bufferOffset < bytesToRead) {
                    fs.Position = currentBlock;
                    fs.ReadExactly(ptrBuf, 0, 4);
                    currentBlock = BinaryPrimitives.ReadInt32LittleEndian(ptrBuf);
                }
                else {
                    break;
                }
            }
        }

        private static void WriteBlockChain(FileStream fs, int blockSize, int startBlock,
            byte[] buffer, int bytesToWrite) {
            int currentBlock = startBlock;
            int bufferOffset = 0;
            var ptrBuf = new byte[4];
            int dataPerBlock = blockSize - 4;

            while (bufferOffset < bytesToWrite && currentBlock > 0) {
                int toWrite = Math.Min(dataPerBlock, bytesToWrite - bufferOffset);

                fs.Position = currentBlock + 4;
                fs.Write(buffer, bufferOffset, toWrite);
                bufferOffset += toWrite;

                if (bufferOffset < bytesToWrite) {
                    fs.Position = currentBlock;
                    fs.ReadExactly(ptrBuf, 0, 4);
                    currentBlock = BinaryPrimitives.ReadInt32LittleEndian(ptrBuf);
                }
                else {
                    break;
                }
            }
        }
    }
}

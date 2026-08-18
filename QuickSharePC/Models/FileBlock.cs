using System;

namespace QuickShare.PC.Models
{
    public class FileBlock : IComparable<FileBlock>
    {
        public const int BLOCK_SIZE = 1024 * 1024; // 1MB

        public bool IsFile { get; }
        public int FileIndex { get; }
        public string Path { get; }
        public long LastModified { get; }
        public long TotalSize { get; }
        public int Index { get; }
        public byte[]? Data { get; }
        public int DataLength { get; }

        public FileBlock(bool isFile, int fileIndex, string path, long lastModified, long totalSize, int index, byte[]? data, int dataLength = 0)
        {
            IsFile = isFile;
            FileIndex = fileIndex;
            Path = path;
            LastModified = lastModified;
            TotalSize = totalSize;
            Index = index;
            Data = data;
            DataLength = dataLength;
        }

        public long GetStartPosition()
        {
            return (long)BLOCK_SIZE * Index;
        }

        public long CalcBlockCount()
        {
            if (TotalSize == 0) return 1;
            return (TotalSize + BLOCK_SIZE - 1) / BLOCK_SIZE;
        }

        public int CompareTo(FileBlock? other)
        {
            if (other == null) return 1;
            if (this.FileIndex != other.FileIndex)
            {
                return this.FileIndex.CompareTo(other.FileIndex);
            }
            return this.Index.CompareTo(other.Index);
        }
    }
}

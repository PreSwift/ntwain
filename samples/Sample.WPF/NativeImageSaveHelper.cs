using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Sample.WPF
{
    /// <summary>
    /// 将 TWAIN Native 传输的 DIB 直接流式写入 BMP 文件，避免整图加载到内存。
    /// </summary>
    static class NativeImageSaveHelper
    {
        const int BitmapInfoHeaderSize = 40;
        const int FileHeaderSize = 14;
        const int ChunkSize = 1024 * 1024;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BitmapFileHeader
        {
            public ushort BfType;
            public uint BfSize;
            public ushort BfReserved1;
            public ushort BfReserved2;
            public uint BfOffBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BitmapInfoHeader
        {
            public uint BiSize;
            public int BiWidth;
            public int BiHeight;
            public ushort BiPlanes;
            public ushort BiBitCount;
            public uint BiCompression;
            public uint BiSizeImage;
            public int BiXPelsPerMeter;
            public int BiYPelsPerMeter;
            public uint BiClrUsed;
            public uint BiClrImportant;
        }

        public static bool IsDib(IntPtr data)
        {
            return data != IntPtr.Zero && Marshal.ReadInt16(data) == BitmapInfoHeaderSize;
        }

        public static string SaveDibToBmpFile(IntPtr dibData, string filePath)
        {
            if (!IsDib(dibData) || string.IsNullOrWhiteSpace(filePath))
                return null;

            var header = (BitmapInfoHeader)Marshal.PtrToStructure(dibData, typeof(BitmapInfoHeader));
            if (!ValidateHeader(ref header))
                return null;

            uint paletteBytes = header.BiClrUsed * 4;
            if (header.BiClrUsed == 0 && header.BiBitCount <= 8)
            {
                paletteBytes = (uint)((1 << header.BiBitCount) * 4);
            }

            var bfOffBits = (uint)(FileHeaderSize + header.BiSize + paletteBytes);
            var bfSize = bfOffBits + header.BiSizeImage;
            var fileHeader = new BitmapFileHeader
            {
                BfType = 0x4D42,
                BfSize = bfSize,
                BfOffBits = bfOffBits
            };

            var folder = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                WriteStructure(fs, fileHeader);
                CopyNativeMemory(fs, dibData, (int)(bfSize - FileHeaderSize));
            }

            return filePath;
        }

        static bool ValidateHeader(ref BitmapInfoHeader header)
        {
            if (header.BiHeight == 0 || header.BiWidth == 0 || header.BiBitCount == 0)
                return false;

            if (header.BiSize == 0)
                header.BiSize = BitmapInfoHeaderSize;

            if (header.BiClrUsed == 0)
            {
                switch (header.BiBitCount)
                {
                    case 1: header.BiClrUsed = 2; break;
                    case 4: header.BiClrUsed = 16; break;
                    case 8: header.BiClrUsed = 256; break;
                }
            }

            if (header.BiSizeImage == 0)
            {
                header.BiSizeImage = (uint)((((header.BiWidth * header.BiBitCount) + 31) & ~31) >> 3)
                    * (uint)Math.Abs(header.BiHeight);
            }

            return true;
        }

        static void WriteStructure(Stream stream, BitmapFileHeader header)
        {
            var size = Marshal.SizeOf(typeof(BitmapFileHeader));
            var buffer = new byte[size];
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(header, ptr, false);
                Marshal.Copy(ptr, buffer, 0, size);
                stream.Write(buffer, 0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        static void CopyNativeMemory(Stream stream, IntPtr source, int totalBytes)
        {
            if (totalBytes <= 0)
                return;

            var buffer = new byte[Math.Min(ChunkSize, totalBytes)];
            var offset = 0;
            while (offset < totalBytes)
            {
                var count = Math.Min(buffer.Length, totalBytes - offset);
                Marshal.Copy(new IntPtr(source.ToInt64() + offset), buffer, 0, count);
                stream.Write(buffer, 0, count);
                offset += count;
            }
        }

    }
}

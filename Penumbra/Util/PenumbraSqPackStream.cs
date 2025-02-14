using Lumina.Data.Structs;
using Lumina.Extensions;

namespace Penumbra.Util;

public class PenumbraSqPackStream : IDisposable
{
    public Stream BaseStream { get; protected set; }

    protected BinaryReader Reader { get; set; }

    public PenumbraSqPackStream(FileInfo file)
        : this(file.OpenRead())
    { }

    public PenumbraSqPackStream(Stream stream)
    {
        BaseStream = stream;
        Reader = new BinaryReader(BaseStream);
    }

    public SqPackHeader GetSqPackHeader()
    {
        BaseStream.Position = 0;

        return Reader.ReadStructure<SqPackHeader>();
    }

    public SqPackFileInfo GetFileMetadata(long offset)
    {
        BaseStream.Position = offset;

        return Reader.ReadStructure<SqPackFileInfo>();
    }

    public T ReadFile<T>(long offset) where T : PenumbraFileResource
    {
        using var ms = new MemoryStream();

        BaseStream.Position = offset;

        var fileInfo = Reader.ReadStructure<SqPackFileInfo>();
        var file     = Activator.CreateInstance<T>();

        // check if we need to read the extended model header or just default to the standard file header
        if (fileInfo.Type == FileType.Model)
        {
            BaseStream.Position = offset;

            var modelFileInfo = Reader.ReadStructure<ModelBlock>();

            file.FileInfo = new PenumbraFileInfo
            {
                HeaderSize  = modelFileInfo.Size,
                Type        = modelFileInfo.Type,
                BlockCount  = modelFileInfo.UsedNumberOfBlocks,
                RawFileSize = modelFileInfo.RawFileSize,
                Offset      = offset,

                // todo: is this useful?
                ModelBlock = modelFileInfo,
            };
        }
        else
        {
            file.FileInfo = new PenumbraFileInfo
            {
                HeaderSize  = fileInfo.Size,
                Type        = fileInfo.Type,
                BlockCount  = fileInfo.NumberOfBlocks,
                RawFileSize = fileInfo.RawFileSize,
                Offset      = offset,
            };
        }

        switch (fileInfo.Type)
        {
            case FileType.Empty: throw new FileNotFoundException($"The file located at 0x{offset:x} is empty.");

            case FileType.Standard:
                ReadStandardFile(file, ms);
                break;

            case FileType.Model:
                ReadModelFile(file, ms);
                break;

            case FileType.Texture:
                ReadTextureFile(file, ms);
                break;

            default: throw new NotImplementedException($"File Type {(uint)fileInfo.Type} is not implemented.");
        }

        file.Data = ms.ToArray();
        if (file.Data.Length != file.FileInfo.RawFileSize)
            Debug.WriteLine("Read data size does not match file size.");

        file.FileStream          = new MemoryStream(file.Data, false);
        file.Reader              = new BinaryReader(file.FileStream);
        file.FileStream.Position = 0;

        file.LoadFile();

        return file;
    }

    private void ReadStandardFile(PenumbraFileResource resource, MemoryStream ms)
    {
        var blocks = Reader.ReadStructures<DatStdFileBlockInfos>((int)resource.FileInfo!.BlockCount);

        foreach (var block in blocks)
            ReadFileBlock(resource.FileInfo.Offset + resource.FileInfo.HeaderSize + block.Offset, ms);

        // reset position ready for reading
        ms.Position = 0;
    }

    private unsafe void ReadModelFile(PenumbraFileResource resource, MemoryStream ms)
    {
        var mdlBlock = resource.FileInfo!.ModelBlock;
        var baseOffset = resource.FileInfo.Offset + resource.FileInfo.HeaderSize;

        // Calculate total blocks upfront to avoid redundant recalculations
        int totalBlocks = mdlBlock.StackBlockNum + mdlBlock.RuntimeBlockNum;
        for (var i = 0; i < 3; i++)
        {
            totalBlocks += mdlBlock.VertexBufferBlockNum[i];
            totalBlocks += mdlBlock.EdgeGeometryVertexBufferBlockNum[i];
            totalBlocks += mdlBlock.IndexBufferBlockNum[i];
        }

        // Read all compressed block sizes in one go
        var compressedBlockSizes = Reader.ReadStructures<ushort>(totalBlocks);
        var currentBlockIndex = 0;

        // Preallocate arrays for vertex/index data offsets and sizes
        var vertexDataOffsets = new int[3];
        var indexDataOffsets = new int[3];
        var vertexBufferSizes = new int[3];
        var indexBufferSizes = new int[3];

        // Helper: Reusable buffer for decompression
        var decompressionBuffer = new byte[32000]; // Maximum block size

        // Process stack blocks
        Reader.Seek(baseOffset + mdlBlock.StackOffset);
        var stackStart = ms.Position;
        for (var i = 0; i < mdlBlock.StackBlockNum; i++, currentBlockIndex++)
            ProcessCompressedBlock(ms, compressedBlockSizes[currentBlockIndex], decompressionBuffer);
        var stackSize = (int)(ms.Position - stackStart);

        // Process runtime blocks
        Reader.Seek(baseOffset + mdlBlock.RuntimeOffset);
        var runtimeStart = ms.Position;
        for (var i = 0; i < mdlBlock.RuntimeBlockNum; i++, currentBlockIndex++)
            ProcessCompressedBlock(ms, compressedBlockSizes[currentBlockIndex], decompressionBuffer);
        var runtimeSize = (int)(ms.Position - runtimeStart);

        // Process vertex, edge, and index buffers
        for (var i = 0; i < 3; i++)
        {
            // Vertex Buffers
            if (mdlBlock.VertexBufferBlockNum[i] > 0)
            {
                Reader.Seek(baseOffset + mdlBlock.VertexBufferOffset[i]);
                vertexDataOffsets[i] = (int)ms.Position;
                for (var j = 0; j < mdlBlock.VertexBufferBlockNum[i]; j++, currentBlockIndex++)
                    vertexBufferSizes[i] += (int)ProcessCompressedBlock(ms, compressedBlockSizes[currentBlockIndex], decompressionBuffer);
            }

            // Edge Geometry Buffers
            if (mdlBlock.EdgeGeometryVertexBufferBlockNum[i] > 0)
            {
                for (var j = 0; j < mdlBlock.EdgeGeometryVertexBufferBlockNum[i]; j++, currentBlockIndex++)
                    ProcessCompressedBlock(ms, compressedBlockSizes[currentBlockIndex], decompressionBuffer);
            }

            // Index Buffers
            if (mdlBlock.IndexBufferBlockNum[i] > 0)
            {
                indexDataOffsets[i] = (int)ms.Position;
                for (var j = 0; j < mdlBlock.IndexBufferBlockNum[i]; j++, currentBlockIndex++)
                    indexBufferSizes[i] += (int)ProcessCompressedBlock(ms, compressedBlockSizes[currentBlockIndex], decompressionBuffer);
            }
        }

        // Write metadata to memory stream
        ms.Seek(0, SeekOrigin.Begin);
        ms.Write(BitConverter.GetBytes(mdlBlock.Version));
        ms.Write(BitConverter.GetBytes(stackSize));
        ms.Write(BitConverter.GetBytes(runtimeSize));
        ms.Write(BitConverter.GetBytes(mdlBlock.VertexDeclarationNum));
        ms.Write(BitConverter.GetBytes(mdlBlock.MaterialNum));
        foreach (var offset in vertexDataOffsets)
            ms.Write(BitConverter.GetBytes(offset));
        foreach (var offset in indexDataOffsets)
            ms.Write(BitConverter.GetBytes(offset));
        foreach (var size in vertexBufferSizes)
            ms.Write(BitConverter.GetBytes(size));
        foreach (var size in indexBufferSizes)
            ms.Write(BitConverter.GetBytes(size));
        ms.Write(new[] { mdlBlock.NumLods });
        ms.Write(BitConverter.GetBytes(mdlBlock.IndexBufferStreamingEnabled));
        ms.Write(BitConverter.GetBytes(mdlBlock.EdgeGeometryEnabled));
        ms.Write(new byte[] { 0 });
    }

    // New helper to process compressed blocks
    private uint ProcessCompressedBlock(MemoryStream ms, ushort compressedSize, byte[] decompressionBuffer)
    {
        var blockHeader = Reader.ReadStructure<DatBlockHeader>();

        if (blockHeader.CompressedSize == 32000) // Uncompressed
        {
            ms.Write(Reader.ReadBytes((int)blockHeader.UncompressedSize));
            return blockHeader.UncompressedSize;
        }

        var compressedData = Reader.ReadBytes(compressedSize);

        using var compressedStream = new MemoryStream(compressedData);
        using var zlibStream = new DeflateStream(compressedStream, CompressionMode.Decompress);
        int bytesRead;
        while ((bytesRead = zlibStream.Read(decompressionBuffer, 0, decompressionBuffer.Length)) > 0)
            ms.Write(decompressionBuffer, 0, bytesRead);

        return blockHeader.UncompressedSize;
    }

    private void ReadTextureFile(PenumbraFileResource resource, MemoryStream ms)
    {
        if (resource.FileInfo!.BlockCount == 0)
            return;

        // Read all LOD blocks at once
        var blocks = Reader.ReadStructures<LodBlock>((int)resource.FileInfo!.BlockCount);

        // Handle mipmap header
        var mipMapSize = blocks[0].CompressedOffset;
        if (mipMapSize > 0)
        {
            var mipMapData = ReadBytesAtPosition(
                resource.FileInfo.Offset + resource.FileInfo.HeaderSize,
                (int)mipMapSize
            );
            ms.Write(mipMapData);
        }

        // Process texture blocks
        foreach (var block in blocks)
        {
            if (block.CompressedSize == 0)
                continue;

            var runningBlockTotal = block.CompressedOffset + resource.FileInfo.Offset + resource.FileInfo.HeaderSize;

            // Read primary block
            ReadFileBlock(runningBlockTotal, ms, true);

            // Process additional blocks
            for (var j = 1; j < block.BlockCount; j++)
            {
                runningBlockTotal += Reader.BaseStream.Seek(0, SeekOrigin.Current); // Read offset increment correctly
                ReadFileBlock(runningBlockTotal, ms, true);
            }

            // Skip the unknown trailing Int16
            Reader.BaseStream.Seek(2, SeekOrigin.Current);
        }
    }

    // Utility: Reads bytes from a specific position
    private byte[] ReadBytesAtPosition(long position, int count)
    {
        var originalPos = BaseStream.Position;
        BaseStream.Position = position;

        var data = Reader.ReadBytes(count);

        BaseStream.Position = originalPos;
        return data;
    }

    protected uint ReadFileBlock(MemoryStream dest, bool resetPosition = false)
        => ReadFileBlock(Reader.BaseStream.Position, dest, resetPosition);

    protected uint ReadFileBlock(long offset, MemoryStream dest, bool resetPosition = false)
    {
        var originalPosition = BaseStream.Position;
        BaseStream.Position = offset;

        // Read block header
        var blockHeader = Reader.ReadStructure<DatBlockHeader>();

        if (blockHeader.CompressedSize == 32000) // Uncompressed block
        {
            // Directly copy uncompressed data
            var buffer = Reader.ReadBytes((int)blockHeader.UncompressedSize);
            dest.Write(buffer);
        }
        else
        {
            // Reuse buffer for compressed data
            Span<byte> compressedData = stackalloc byte[(int)blockHeader.CompressedSize];
            Reader.Read(compressedData);

            // Stream decompression to destination
            using var compressedStream = new MemoryStream(compressedData.ToArray());
            using var zlibStream = new DeflateStream(compressedStream, CompressionMode.Decompress);
            zlibStream.CopyTo(dest);
        }

        if (resetPosition)
            BaseStream.Position = originalPosition;

        return blockHeader.UncompressedSize;
    }

    public void Dispose()
    {
        Reader.Dispose();
        Dispose(true);
    }

    protected virtual void Dispose(bool _)
    { }

    public class PenumbraFileInfo
    {
        public uint     HeaderSize;
        public FileType Type;
        public uint     RawFileSize;
        public uint     BlockCount;

        public long Offset { get; internal set; }

        public ModelBlock ModelBlock { get; internal set; }
    }

    public class PenumbraFileResource
    {
        public PenumbraFileResource()
        { }

        public PenumbraFileInfo? FileInfo { get; internal set; }

        public byte[] Data { get; internal set; } = new byte[0];

        public MemoryStream? FileStream { get; internal set; }

        public BinaryReader? Reader { get; internal set; }

        /// <summary>
        /// Called once the files are read out from the dats. Used to further parse the file into usable data structures.
        /// </summary>
        public virtual void LoadFile()
        {
            // this function is intentionally left blank
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DatBlockHeader
    {
        public uint Size;
        public uint unknown1;
        public uint CompressedSize;
        public uint UncompressedSize;
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct LodBlock
    {
        public uint CompressedOffset;
        public uint CompressedSize;
        public uint DecompressedSize;
        public uint BlockOffset;
        public uint BlockCount;
    }
}

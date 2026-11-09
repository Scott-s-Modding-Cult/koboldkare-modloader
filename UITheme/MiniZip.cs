using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace UITheme {
    /// <summary>
    /// Minimal read-only .zip reader (stored + deflate entries). KoboldKare doesn't ship
    /// System.IO.Compression.dll (ZipArchive), but DeflateStream lives in System.dll, so this is
    /// all a theme importer needs. No Zip64, no encryption (refused).
    /// </summary>
    internal sealed class MiniZip : IDisposable {
        public sealed class Entry {
            public string Name;            // forward slashes, as stored
            public long CompressedSize;
            public long UncompressedSize;
            internal ushort Method;
            internal long LocalHeaderOffset;
            public bool IsDirectory => Name.EndsWith("/");
        }

        private readonly Stream stream;
        private readonly BinaryReader reader;
        public readonly List<Entry> Entries = new List<Entry>();

        public MiniZip(string path) {
            stream = File.OpenRead(path);
            reader = new BinaryReader(stream);
            ReadCentralDirectory();
        }

        private void ReadCentralDirectory() {
            // End of central directory record: last 22 bytes + up to 64KB comment.
            long searchStart = Math.Max(0, stream.Length - 22 - 65535);
            long eocd = -1;
            for (long pos = stream.Length - 22; pos >= searchStart; pos--) {
                stream.Position = pos;
                if (reader.ReadUInt32() == 0x06054b50) {
                    eocd = pos;
                    break;
                }
            }
            if (eocd < 0) {
                throw new InvalidDataException("not a zip file");
            }
            stream.Position = eocd + 10;
            int count = reader.ReadUInt16();
            uint size = reader.ReadUInt32();
            uint offset = reader.ReadUInt32();
            if (offset == 0xFFFFFFFF || size == 0xFFFFFFFF || count == 0xFFFF) {
                throw new InvalidDataException("Zip64 archives aren't supported");
            }

            stream.Position = offset;
            for (int i = 0; i < count; i++) {
                if (reader.ReadUInt32() != 0x02014b50) {
                    throw new InvalidDataException("corrupt central directory");
                }
                stream.Position += 4;                  // version made by / needed
                ushort flags = reader.ReadUInt16();
                ushort method = reader.ReadUInt16();
                stream.Position += 8;                  // time, date, crc
                uint compressed = reader.ReadUInt32();
                uint uncompressed = reader.ReadUInt32();
                int nameLength = reader.ReadUInt16();
                int extraLength = reader.ReadUInt16();
                int commentLength = reader.ReadUInt16();
                stream.Position += 8;                  // disk, internal attr, external attr
                uint localOffset = reader.ReadUInt32();
                string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength)).Replace('\\', '/');
                stream.Position += extraLength + commentLength;
                if ((flags & 1) != 0) {
                    throw new InvalidDataException("encrypted zips aren't supported");
                }
                Entries.Add(new Entry {
                    Name = name, Method = method, CompressedSize = compressed, UncompressedSize = uncompressed,
                    LocalHeaderOffset = localOffset,
                });
            }
        }

        public byte[] Read(Entry entry, long maxBytes) {
            if (entry.UncompressedSize > maxBytes) {
                throw new InvalidDataException($"{entry.Name} is too large");
            }
            stream.Position = entry.LocalHeaderOffset;
            if (reader.ReadUInt32() != 0x04034b50) {
                throw new InvalidDataException($"corrupt entry {entry.Name}");
            }
            stream.Position += 22;
            int nameLength = reader.ReadUInt16();
            int extraLength = reader.ReadUInt16();
            stream.Position += nameLength + extraLength;

            var raw = reader.ReadBytes((int)entry.CompressedSize);
            switch (entry.Method) {
                case 0:
                    return raw;
                case 8:
                    using (var input = new MemoryStream(raw))
                    using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                    using (var output = new MemoryStream()) {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0) {
                            output.Write(buffer, 0, read);
                            if (output.Length > maxBytes) {
                                throw new InvalidDataException($"{entry.Name} is too large");
                            }
                        }
                        return output.ToArray();
                    }
                default:
                    throw new InvalidDataException($"{entry.Name} uses an unsupported compression method ({entry.Method})");
            }
        }

        public void Dispose() {
            reader?.Dispose();
            stream?.Dispose();
        }
    }
}

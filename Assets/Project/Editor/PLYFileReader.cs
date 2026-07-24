// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// Reader for INRIA 3DGS ".ply" files (binary_little_endian). Editor-only:
// PLY parsing happens once at conversion time, never on the Quest player.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace GaussianSplatVR.Editor
{
    public static class PLYFileReader
    {
        public enum ElementType { None, Float, Double, UChar }

        public static int TypeToSize(ElementType t) => t switch
        { ElementType.Float => 4, ElementType.Double => 8, ElementType.UChar => 1, ElementType.None => 0,
          _ => throw new ArgumentOutOfRangeException(nameof(t)) };

        /// <summary>Header only: splat count, per-splat stride, ordered (name, type) list.</summary>
        public static void ReadFileHeader(string path, out int count, out int stride, out List<(string name, ElementType type)> attrs)
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"PLY file not found: {path}");
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            ReadHeader(fs, path, out count, out stride, out attrs);
        }

        /// <summary>
        /// Header + vertex body. The body is returned in file layout (stride*count).
        /// Caller owns <paramref name="vertices"/> and must Dispose() it.
        /// </summary>
        public static unsafe void ReadFile(string path, out int count, out int stride,
            out List<(string name, ElementType type)> attrs, out NativeArray<byte> vertices)
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"PLY file not found: {path}");
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            ReadHeader(fs, path, out count, out stride, out attrs);   // leaves the stream at the body

            vertices = new NativeArray<byte>(count * stride, Allocator.Persistent);
            var span = new Span<byte>(vertices.GetUnsafePtr(), vertices.Length);
            int read = fs.Read(span);
            if (read != vertices.Length)
            {
                vertices.Dispose();
                throw new IOException($"PLY {path}: expected {vertices.Length} body bytes, got {read}.");
            }
        }

        static void ReadHeader(FileStream fs, string path, out int count, out int stride, out List<(string, ElementType)> attrs)
        {
            count = 0; stride = 0; attrs = new List<(string, ElementType)>();
            if (fs.Length >= 2L * 1024 * 1024 * 1024) throw new IOException($"PLY {path}: files >= 2GB are not supported.");

            bool binaryLE = false;
            for (int i = 0; i < 9000; ++i)
            {
                string line = ReadLine(fs);
                if (line == "end_header" || line.Length == 0) break;
                var t = line.Split(' ');

                if (t.Length == 3 && t[0] == "format" && t[1] == "binary_little_endian" && t[2] == "1.0") binaryLE = true;
                else if (t.Length == 3 && t[0] == "element" && t[1] == "vertex") count = int.Parse(t[2]);
                else if (t.Length == 3 && t[0] == "property")
                {
                    ElementType type = t[1] switch
                    { "float" or "float32" => ElementType.Float, "double" => ElementType.Double,
                      "uchar" or "uint8" => ElementType.UChar, _ => ElementType.None };
                    stride += TypeToSize(type);
                    attrs.Add((t[2], type));
                }
            }
            if (!binaryLE) throw new IOException($"PLY {path}: only 'binary_little_endian 1.0' is supported.");
        }

        // One header line; strips a trailing '\r' so CRLF parses like LF.
        static string ReadLine(FileStream fs)
        {
            var bytes = new List<byte>(64);
            int b;
            while ((b = fs.ReadByte()) != -1 && b != '\n') bytes.Add((byte)b);
            if (bytes.Count > 0 && bytes[^1] == (byte)'\r') bytes.RemoveAt(bytes.Count - 1);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
    }
}

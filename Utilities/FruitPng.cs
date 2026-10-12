using System.IO;
using System.IO.Compression;
using System.Text;

namespace FruitLib
{
    /// <summary>
    /// A small PNG encoder in plain C#: RGB, 8 bits, no filtering. The game's
    /// <c>ImageConversion.EncodeToPNG</c> hands its bytes back through an out-array wrapper, the kind
    /// that crashed <c>GetParticles</c>; this touches nothing of Unity's, so it also runs on a worker
    /// thread. About 3.5 ms for 384 × 256.
    /// </summary>
    /// <remarks>Since 5.18.0.</remarks>
    public static class FruitPng
    {
        private static readonly uint[] Crc = MakeCrc();

        private static uint[] MakeCrc()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        /// <summary>A PNG from RGBA texels in Unity's order (bottom row first), alpha dropped.</summary>
        public static byte[] FromRgba(byte[] rgba, int w, int h)
        {
            var raw = new byte[(w * 3 + 1) * h];
            int o = 0;
            for (int y = h - 1; y >= 0; y--)
            {
                raw[o++] = 0;   // filter: none
                int s = y * w * 4;
                for (int x = 0; x < w; x++, s += 4) { raw[o++] = rgba[s]; raw[o++] = rgba[s + 1]; raw[o++] = rgba[s + 2]; }
            }

            byte[] z;
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0x01);
                using (var d = new DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) d.Write(raw, 0, raw.Length);
                uint a = 1, b = 0;
                foreach (byte v in raw) { a = (a + v) % 65521; b = (b + a) % 65521; }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                z = ms.ToArray();
            }

            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var ihdr = new byte[13];
                BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h);
                ihdr[8] = 8; ihdr[9] = 2;   // 8-bit RGB
                Chunk(png, "IHDR", ihdr);
                Chunk(png, "IDAT", z);
                Chunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; BE(len, 0, (uint)data.Length); s.Write(len, 0, 4);
            var t = Encoding.ASCII.GetBytes(type);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint c = 0xFFFFFFFFu;
            foreach (byte v in t) c = Crc[(c ^ v) & 0xFF] ^ (c >> 8);
            foreach (byte v in data) c = Crc[(c ^ v) & 0xFF] ^ (c >> 8);
            var crc = new byte[4]; BE(crc, 0, c ^ 0xFFFFFFFFu); s.Write(crc, 0, 4);
        }

        private static void BE(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }
    }
}

using System.Runtime.InteropServices;
using UnityEngine;

namespace Dennokoworks.DenLattice
{
    /// <summary>
    /// <see cref="Vector3"/> の密な配列を <c>byte[]</c> 1 本として持つためのヘルパー。
    ///
    /// テキスト YAML では <c>Vector3[]</c> が 1 要素あたり 4 行に展開され、
    /// Prefab インスタンス上では <c>Array.data[i]</c> が要素数ぶん
    /// PropertyModification として積まれる。制御点は最大 1000 点になりうるので、
    /// <see cref="MeshEdit"/> と同じ理由で byte[] へ畳んでおく。
    /// </summary>
    internal static class VectorBlob
    {
        internal const int Stride = 12;

        /// <summary>float とそのビット表現を相互変換する。BitConverter と違い確保が起きない。</summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float Value;
            [FieldOffset(0)] public int Bits;
        }

        internal static Vector3 Read(byte[] buffer, int index)
        {
            var offset = index * Stride;
            if (buffer == null || offset + Stride > buffer.Length) return Vector3.zero;

            return new Vector3(
                ReadFloat(buffer, offset),
                ReadFloat(buffer, offset + 4),
                ReadFloat(buffer, offset + 8));
        }

        internal static void Write(byte[] buffer, int index, Vector3 value)
        {
            var offset = index * Stride;
            if (buffer == null || offset + Stride > buffer.Length) return;

            WriteFloat(buffer, offset, value.x);
            WriteFloat(buffer, offset + 4, value.y);
            WriteFloat(buffer, offset + 8, value.z);
        }

        /// <summary>要素数ぶんの領域を確保する。既に足りていればそのまま返す。</summary>
        internal static byte[] Resize(byte[] buffer, int count)
        {
            var required = count * Stride;
            if (buffer != null && buffer.Length == required) return buffer;

            return new byte[required];
        }

        internal static bool AnyNonZero(byte[] buffer)
        {
            if (buffer == null) return false;

            foreach (var b in buffer)
            {
                if (b != 0) return true;
            }

            return false;
        }

        private static int ReadInt(byte[] buffer, int offset)
        {
            return buffer[offset]
                   | (buffer[offset + 1] << 8)
                   | (buffer[offset + 2] << 16)
                   | (buffer[offset + 3] << 24);
        }

        private static float ReadFloat(byte[] buffer, int offset)
        {
            return new FloatBits { Bits = ReadInt(buffer, offset) }.Value;
        }

        private static void WriteInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteFloat(byte[] buffer, int offset, float value)
        {
            WriteInt(buffer, offset, new FloatBits { Value = value }.Bits);
        }
    }
}

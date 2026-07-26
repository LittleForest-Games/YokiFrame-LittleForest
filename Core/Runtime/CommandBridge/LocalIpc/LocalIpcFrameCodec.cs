using System;
using System.Text;

namespace YokiFrame
{
    /// <summary>
    /// Stateless length-prefixed UTF-8 JSON frame codec.
    /// </summary>
    public static class LocalIpcFrameCodec
    {
        private static readonly UTF8Encoding sStrictUtf8 =
            new UTF8Encoding(false, true);

        public static byte[] Encode(string json, int maxPayloadBytes)
        {
            if (json == null)
                throw new ArgumentNullException(nameof(json));

            var payload = sStrictUtf8.GetBytes(json);
            if (payload.Length == 0 || payload.Length > maxPayloadBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(json),
                    "Frame payload is outside the configured byte limit.");
            }

            var frame = new byte[payload.Length + sizeof(uint)];
            WriteUInt32LittleEndian(
                frame,
                0,
                checked((uint)payload.Length));
            Buffer.BlockCopy(
                payload,
                0,
                frame,
                sizeof(uint),
                payload.Length);
            return frame;
        }

        /// <summary>
        /// Decodes one complete frame from a byte buffer.
        /// Incomplete input returns false with an empty error code.
        /// </summary>
        public static bool TryDecode(
            byte[] buffer,
            int offset,
            int count,
            int maxPayloadBytes,
            out string json,
            out int consumedBytes,
            out string errorCode)
        {
            json = string.Empty;
            consumedBytes = 0;
            errorCode = string.Empty;
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 ||
                count < 0 ||
                offset > buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException();
            }

            if (count < sizeof(uint))
                return false;

            var payloadLength =
                ReadUInt32LittleEndian(buffer, offset);
            if (payloadLength == 0 ||
                payloadLength > maxPayloadBytes)
            {
                errorCode = "frame-too-large";
                return false;
            }

            var frameLength =
                checked(sizeof(uint) + (int)payloadLength);
            if (count < frameLength)
                return false;

            try
            {
                json = sStrictUtf8.GetString(
                    buffer,
                    offset + sizeof(uint),
                    checked((int)payloadLength));
            }
            catch (DecoderFallbackException)
            {
                errorCode = "invalid-utf8";
                return false;
            }

            consumedBytes = frameLength;
            return true;
        }

        public static uint ReadUInt32LittleEndian(
            byte[] buffer,
            int offset)
        {
            return (uint)(
                buffer[offset] |
                buffer[offset + 1] << 8 |
                buffer[offset + 2] << 16 |
                buffer[offset + 3] << 24);
        }

        public static void WriteUInt32LittleEndian(
            byte[] buffer,
            int offset,
            uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }
    }
}

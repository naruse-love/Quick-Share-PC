using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace QuickShare.PC.Services
{
    public static class QuickShareConstants
    {
        public const string CLIENT_HEADER = "HFXC";
        public const int VERSION_CODE = 300;

        // Controller identifiers
        public const short SHUTDOWN = 0;
        public const short LIST_FILES = 1;
        public const short DELETE_FILE = 2;
        public const short MKDIR = 3;
        public const short REQUEST_RECEIVE = 10;
        public const short REQUEST_SEND = 11;

        // Transfer identifiers
        public const short END_POINT = -1;
        public const short FILE = 0;
        public const short FOLDER = 1;
        public const short FILE_SLICE = 2;
        public const short EOF = 3;
        public const short END_OF_INTERRUPTED = 4;
        public const short END_OF_READ_ERROR = 5;
        public const short END_OF_WRITE_ERROR = 6;
    }

    public class QuickShareStream
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[8];

        public QuickShareStream(Stream stream)
        {
            _stream = stream;
        }

        public Stream BaseStream => _stream;

        public void Close()
        {
            _stream.Close();
        }

        public void ReadFully(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = _stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0)
                    throw new EndOfStreamException();
                totalRead += read;
            }
        }

        public async Task ReadFullyAsync(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await _stream.ReadAsync(buffer, offset + totalRead, count - totalRead);
                if (read <= 0)
                    throw new EndOfStreamException();
                totalRead += read;
            }
        }

        public short ReadShort()
        {
            ReadFully(_buffer, 0, 2);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(_buffer, 0, 2);
            return BitConverter.ToInt16(_buffer, 0);
        }

        public async Task<short> ReadShortAsync()
        {
            await ReadFullyAsync(_buffer, 0, 2);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(_buffer, 0, 2);
            return BitConverter.ToInt16(_buffer, 0);
        }

        public void WriteShort(short value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            _stream.Write(bytes, 0, 2);
        }

        public int ReadInt()
        {
            ReadFully(_buffer, 0, 4);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(_buffer, 0, 4);
            return BitConverter.ToInt32(_buffer, 0);
        }

        public async Task<int> ReadIntAsync()
        {
            await ReadFullyAsync(_buffer, 0, 4);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(_buffer, 0, 4);
            return BitConverter.ToInt32(_buffer, 0);
        }

        public void WriteInt(int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            _stream.Write(bytes, 0, 4);
        }

        public long ReadLong()
        {
            ReadFully(_buffer, 0, 8);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(_buffer, 0, 8);
            return BitConverter.ToInt64(_buffer, 0);
        }

        public async Task<long> ReadLongAsync()
        {
            await ReadFullyAsync(_buffer, 0, 8);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(_buffer, 0, 8);
            return BitConverter.ToInt64(_buffer, 0);
        }

        public void WriteLong(long value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            _stream.Write(bytes, 0, 8);
        }

        public bool ReadBoolean()
        {
            int val = _stream.ReadByte();
            if (val < 0)
                throw new EndOfStreamException();
            return val != 0;
        }

        public async Task<bool> ReadBooleanAsync()
        {
            byte[] buf = new byte[1];
            int read = await _stream.ReadAsync(buf, 0, 1);
            if (read <= 0)
                throw new EndOfStreamException();
            return buf[0] != 0;
        }

        public void WriteBoolean(bool value)
        {
            _stream.WriteByte((byte)(value ? 1 : 0));
        }

        public byte ReadByte()
        {
            int val = _stream.ReadByte();
            if (val < 0)
                throw new EndOfStreamException();
            return (byte)val;
        }

        public async Task<byte> ReadByteAsync()
        {
            byte[] buf = new byte[1];
            int read = await _stream.ReadAsync(buf, 0, 1);
            if (read <= 0)
                throw new EndOfStreamException();
            return buf[0];
        }

        public void WriteByte(byte value)
        {
            _stream.WriteByte(value);
        }

        public string ReadUTF()
        {
            ushort utflen = (ushort)ReadShort();
            byte[] bytearr = new byte[utflen];
            ReadFully(bytearr, 0, utflen);
            return Encoding.UTF8.GetString(bytearr, 0, utflen);
        }

        public async Task<string> ReadUTFAsync()
        {
            int utflen = (ushort)(await ReadShortAsync());
            byte[] bytearr = new byte[utflen];
            await ReadFullyAsync(bytearr, 0, utflen);
            return Encoding.UTF8.GetString(bytearr, 0, utflen);
        }

        public void WriteUTF(string str)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(str);
            if (bytes.Length > 65535)
                throw new ArgumentException("String too long for UTF-8 write");
            WriteShort((short)bytes.Length);
            _stream.Write(bytes, 0, bytes.Length);
        }
    }
}

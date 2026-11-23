using System;
using System.IO;
using LibHac;
using LibHac.Fs;
using LibHac.Fs.Fsa;

namespace Extractor.Core
{
    // Read-only Stream adapter over a LibHac IFile. The caller owns the IFile.
    public sealed class IFileAsStream : Stream
    {
        private readonly IFile _file;
        private readonly long _length;
        private long _position;

        public IFileAsStream(IFile file)
        {
            _file = file;
            file.GetSize(out long size).ThrowIfFailure();
            _length = size;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _length) return 0;
            int toRead = (int)Math.Min(buffer.Length, _length - _position);
            _file.Read(out long read, _position, buffer.Slice(0, toRead)).ThrowIfFailure();
            _position += read;
            return (int)read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0) throw new IOException("Cannot seek before the beginning of the stream.");
            _position = target;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

using System;
using System.Security.Cryptography;

namespace Extractor.Core
{
    // AES-CTR built on AES-ECB. Counter block = nonce[0:8] || BE64(byteOffset >> 4).
    public sealed class AesCtr : IDisposable
    {
        private readonly Aes _aes;
        private readonly ICryptoTransform _enc;
        private readonly byte[] _counter = new byte[16];
        private readonly byte[] _keystream = new byte[16];
        private int _ksPos;
        private ulong _blockCounter;

        public AesCtr(byte[] key, byte[] nonce, long byteOffset)
        {
            _aes = Aes.Create();
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _aes.Key = key;
            _enc = _aes.CreateEncryptor();
            Array.Copy(nonce, 0, _counter, 0, 8);
            _blockCounter = (ulong)(byteOffset >> 4);
            int partial = (int)(byteOffset & 15);
            if (partial != 0)
            {
                FillKeystream();
                _ksPos = partial;
            }
            else
            {
                _ksPos = 16;
            }
        }

        private void FillKeystream()
        {
            ulong v = _blockCounter;
            for (int i = 15; i >= 8; i--) { _counter[i] = (byte)v; v >>= 8; }
            _enc.TransformBlock(_counter, 0, 16, _keystream, 0);
            _blockCounter++;
            _ksPos = 0;
        }

        public void CryptInPlace(byte[] buf, int offset, int count)
        {
            int end = offset + count;
            int i = offset;
            while (i < end && _ksPos < 16)
                buf[i++] ^= _keystream[_ksPos++];
            while (end - i >= 16)
            {
                FillKeystream();
                for (int j = 0; j < 16; j++) buf[i + j] ^= _keystream[j];
                i += 16;
                _ksPos = 16;
            }
            while (i < end)
            {
                if (_ksPos >= 16) FillKeystream();
                buf[i++] ^= _keystream[_ksPos++];
            }
        }

        public void Dispose()
        {
            _enc.Dispose();
            _aes.Dispose();
        }
    }
}
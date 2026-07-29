using System.Buffers;
using System.Buffers.Binary;
using System.Threading;
using Lidgren.Network;
using SpaceWizards.Sodium;

namespace Port.Net;

/// <summary>
/// AEAD packet crypto matching Robust.Shared.Network.NetEncryption.
/// </summary>
public sealed class NetPacketCrypto
{
    public const int Overhead = sizeof(ulong) + CryptoAeadXChaCha20Poly1305Ietf.AddBytes;

    ulong _nonce;
    readonly byte[] _key;

    public NetPacketCrypto(byte[] key, bool isServer)
    {
        if (key.Length != CryptoAeadXChaCha20Poly1305Ietf.KeyBytes)
            throw new ArgumentException("bad key size");
        _key = key;
        _nonce = isServer ? 0ul : 1ul;
    }

    public void Encrypt(NetOutgoingMessage message)
    {
        var nonce = Interlocked.Add(ref _nonce, 2);
        var lengthBytes = message.LengthBytes;
        var encryptedSize = lengthBytes + Overhead;
        var data = message.Data.AsSpan(0, lengthBytes);

        Span<byte> plaintext;
        Span<byte> ciphertext;
        byte[]? returnPool = null;

        if (message.Data.Length >= encryptedSize)
        {
            returnPool = ArrayPool<byte>.Shared.Rent(lengthBytes);
            plaintext = returnPool.AsSpan(0, lengthBytes);
            data.CopyTo(plaintext);
            ciphertext = message.Data.AsSpan(0, encryptedSize);
        }
        else
        {
            plaintext = data;
            ciphertext = message.Data = new byte[encryptedSize];
        }

        Span<byte> nonceData = stackalloc byte[CryptoAeadXChaCha20Poly1305Ietf.NoncePublicBytes];
        nonceData.Fill(0);
        BinaryPrimitives.WriteUInt64LittleEndian(nonceData, nonce);
        BinaryPrimitives.WriteUInt64LittleEndian(ciphertext, nonce);

        CryptoAeadXChaCha20Poly1305Ietf.Encrypt(
            ciphertext[sizeof(ulong)..],
            out _,
            plaintext,
            ReadOnlySpan<byte>.Empty,
            nonceData,
            _key);

        message.LengthBytes = encryptedSize;
        if (returnPool != null)
            ArrayPool<byte>.Shared.Return(returnPool);
    }

    public bool TryDecrypt(NetIncomingMessage message)
    {
        if (message.LengthBytes < Overhead)
            return false;

        var nonce = message.ReadUInt64();
        var cipherText = message.Data.AsSpan(sizeof(ulong), message.LengthBytes - sizeof(ulong));
        var buffer = cipherText.ToArray();

        Span<byte> nonceData = stackalloc byte[CryptoAeadXChaCha20Poly1305Ietf.NoncePublicBytes];
        nonceData.Fill(0);
        BinaryPrimitives.WriteUInt64LittleEndian(nonceData, nonce);

        var ok = CryptoAeadXChaCha20Poly1305Ietf.Decrypt(
            message.Data,
            out var messageLength,
            buffer,
            ReadOnlySpan<byte>.Empty,
            nonceData,
            _key);

        message.Position = 0;
        message.LengthBytes = messageLength;
        return ok;
    }
}

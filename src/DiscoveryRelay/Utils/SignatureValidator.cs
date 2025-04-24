using System.Text;
using System.Security.Cryptography;
using NBitcoin.Secp256k1;
using DiscoveryRelay.Models;

namespace DiscoveryRelay.Utils;

public static class SignatureValidator // : IEventValidator
{
    /// <summary>
    /// Validates a Nostr event signature using SecpSchnorr.
    /// </summary>
    /// <param name="event">The Nostr event to validate.</param>
    /// <param name="pubKeyHex">The public key in hexadecimal format.</param>
    /// <param name="signatureHex">The signature in hexadecimal format.</param>
    /// <param name="eventHashHex">The event hash in hexadecimal format.</param>
    /// <returns>True if the signature is valid, otherwise false.</returns>
    public static string? Validate(NostrEvent e)
    {
        try
        {
            var pubKeyBytes = Convert.FromHexString(e.PubKey);
            var signatureBytes = Convert.FromHexString(e.Signature);
            var idBytes = Convert.FromHexString(e.Id);

            if (!SecpSchnorrSignature.TryCreate(signatureBytes, out var signature))
            {
                return Messages.InvalidSignature;
            }

            return Context.Instance.CreateXOnlyPubKey(pubKeyBytes).SigVerifyBIP340(signature, idBytes)
                ? null
                : Messages.InvalidSignature;
        }
        catch
        {
            return Messages.InvalidSignature;
        }
    }
}

using System.Text;
using System.Security.Cryptography;
using NBitcoin.Secp256k1;
using DiscoveryRelay.Models;

namespace DiscoveryRelay.Utils;

public static class SignatureValidator : IEventValidator
{
    /// <summary>
    /// Validates a Nostr event signature using SecpSchnorr.
    /// </summary>
    /// <param name="event">The Nostr event to validate.</param>
    /// <param name="pubKeyHex">The public key in hexadecimal format.</param>
    /// <param name="signatureHex">The signature in hexadecimal format.</param>
    /// <param name="eventHashHex">The event hash in hexadecimal format.</param>
    /// <returns>True if the signature is valid, otherwise false.</returns>
    public static bool ValidateSignature(NostrEvent @event, string pubKeyHex, string signatureHex, string eventHashHex)
    {
        try
        {
            // Convert hex strings to byte arrays
            var pubKeyBytes = Convert.FromHexString(pubKeyHex);
            var signatureBytes = Convert.FromHexString(signatureHex);
            var eventHashBytes = Convert.FromHexString(eventHashHex);

            // Parse the public key
            if (!Context.Instance.TryCreatePubKey(pubKeyBytes, out var pubKey))
            {
                return false;
            }

            // Parse the signature
            if (!SchnorrSignature.TryCreate(signatureBytes, out var signature))
            {
                return false;
            }

            // Verify the signature
            return pubKey.SigVerifyBIP340(signature, eventHashBytes);
        }
        catch
        {
            // Return false if any exception occurs
            return false;
        }
    }
}

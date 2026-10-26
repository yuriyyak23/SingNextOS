using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    // Immutable observations of an already-decided cleanup invocation. No authority reads them.
    private static void ObserveSecureExecutionClosure(ISemanticTraceSinkV1 sink, SecureExecutionRequest request,
        SecureExecutionReceipt? admittedReceipt, ProcessHandle owner, ulong invocation,
        PlatformBackendEpoch admissionEpoch, PlatformBackendEpoch completionEpoch,
        SecureExecutionClosure? rawClosure, bool completed)
    {
        try
        {
            string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            // Entire nested contract snapshot, including strings/nulls and collection order.
            // Provider collections are observed here outside locks; serialization failure loses
            // observation only. This does not attest immutability at provider admission.
            string requestSnapshot = JsonSerializer.Serialize(request);
            string receiptSnapshot = JsonSerializer.Serialize(admittedReceipt);
            string ownerSnapshot = JsonSerializer.Serialize(owner);
            string closureSnapshot = JsonSerializer.Serialize(rawClosure);
            string GenerationDigest(PlatformBackendEpoch epoch) => Hash(FormattableString.Invariant(
                $"composition-close/3:{Encoding.UTF8.GetByteCount(requestSnapshot)}:{requestSnapshot}:{Encoding.UTF8.GetByteCount(receiptSnapshot)}:{receiptSnapshot}:{Encoding.UTF8.GetByteCount(ownerSnapshot)}:{ownerSnapshot}:{epoch.Value}"));
            string correlation = FormattableString.Invariant($"composition-close:{request.Binding.Id}:{request.Binding.Generation}:{invocation}");
            ulong sequence = 0;
            bool Emit(SemanticTraceEventKindV1 kind, PlatformBackendEpoch epoch, bool known, bool decision)
            {
                // Prefix observations cannot claim a receipt before the cleanup observation.
                string observedClosure = known ? closureSnapshot : "null";
                string digest = Hash(FormattableString.Invariant(
                    $"closure-observation/2:{(byte)kind}:{invocation}:{known}:{decision}:{Encoding.UTF8.GetByteCount(observedClosure)}:{observedClosure}"));
                return sink.TryRecord(new(1, correlation, ++sequence, kind, "runtime.composition.close",
                    GenerationDigest(epoch), digest));
            }
            if (!Emit(SemanticTraceEventKindV1.Submit, admissionEpoch, false, false)) return;
            if (!Emit(SemanticTraceEventKindV1.EffectPossible, admissionEpoch, false, false)) return;
            if (completionEpoch != admissionEpoch &&
                !Emit(SemanticTraceEventKindV1.GenerationChanged, completionEpoch, rawClosure.HasValue, false)) return;
            _ = Emit(completed ? SemanticTraceEventKindV1.RetireOrComplete : SemanticTraceEventKindV1.Quarantined,
                completionEpoch, rawClosure.HasValue, completed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Rejected/broken observation stops delivery; it cannot alter cleanup or invent a suffix.
        }
    }
}

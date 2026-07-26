namespace Mersal.CallCentre.Infrastructure;

/// <summary>The delegation seam to patient-service for contact edits (phase 15.4). callcentre-service validates the
/// value server-side then forwards the change under the caller's bearer; patient-service owns the one-primary rule
/// and the contact history (corrections are updates with history, never silent overwrites). The HTTP implementation
/// lives in the Api layer; tests inject a fake. Results are passed through faithfully.</summary>
public interface IContactGateway
{
    Task<GatewayResult> UpdateContactAsync(Guid beneficiaryId, Guid contactId, object body, string? bearer, CancellationToken ct = default);
    Task<GatewayResult> AddContactAsync(Guid beneficiaryId, object body, string? bearer, CancellationToken ct = default);
}

using Hl7.Fhir.Model;

namespace Abm.Requesting.Claim.Operation;

public interface IClaimOperation
{
    Task<ClaimOperationOutcome> Process(
        Parameters claimParameters,
        CancellationToken cancellationToken);
}
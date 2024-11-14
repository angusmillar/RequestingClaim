using Hl7.Fhir.Model;

namespace Abm.Requesting.Claim.Operation;

public record ClaimRequest(Identifier requisition, ResourceReference organization);
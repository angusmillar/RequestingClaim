using System.Net;
using Hl7.Fhir.Model;

namespace Abm.Requesting.Claim.Operation;

public record ClaimOperationOutcome(Resource Resource, HttpStatusCode HttpStatusCode);
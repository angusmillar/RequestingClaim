﻿using Hl7.Fhir.Model;

namespace Abm.Requesting.Claim.FhirSupport;

public interface IOperationOutcomeSupport
{
  OperationOutcome GetFatal(string[] messageList);
  OperationOutcome GetFatal(string[]? messageList, OperationOutcome? operationOutcome);
  OperationOutcome GetError(string[] messageList);
  OperationOutcome GetError(string[]? messageList, OperationOutcome? operationOutcome);
  OperationOutcome GetWarning(string[] messageList);
  OperationOutcome GetWarning(string[]? messageList, OperationOutcome? operationOutcome);
  OperationOutcome GetInformation(string[] messageList);
  OperationOutcome GetInformation(string[]? messageList, OperationOutcome? operationOutcome);
  OperationOutcome MergeOperationOutcomeList(IEnumerable<OperationOutcome> operationOutcomeList);
  string[] ExtractErrorMessages(OperationOutcome operationOutcome);
}

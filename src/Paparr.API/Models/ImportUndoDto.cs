namespace Paparr.API.Models;

public sealed record UndoPreviewDto(Guid Revision, bool CanUndo, string? Reason, string? SourceRelativePath,
    string? LibraryRelativePath, Guid? OperationId, string? OperationStatus, string? Error, bool CanRetry);

public sealed record StartUndoRequest(Guid Revision, Guid OperationId);
public sealed record RetryUndoRequest(Guid OperationId);

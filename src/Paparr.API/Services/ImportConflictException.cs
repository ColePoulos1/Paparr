namespace Paparr.API.Services;

public sealed class ImportConflictException(string message) : Exception(message);

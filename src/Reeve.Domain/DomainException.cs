namespace Reeve.Domain;

/// <summary>A domain rule was violated. The message is safe to return to API clients.</summary>
public class DomainException(string message) : Exception(message);

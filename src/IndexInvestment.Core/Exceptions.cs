namespace IndexInvestment.Core;

/// <summary>Base class for errors raised by the portfolio domain layer.</summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
    public DomainException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// A business rule was violated (nothing to undo, position missing,
/// another run in progress, unknown user, ...). The web layer maps
/// these to HTTP 409.
/// </summary>
public sealed class ConflictException : DomainException
{
    public ConflictException(string message) : base(message) { }
}

/// <summary>
/// Input was malformed (bad CSV rows, invalid settings, ...). The web
/// layer maps these to HTTP 400.
/// </summary>
public sealed class ValidationError : DomainException
{
    public ValidationError(string message) : base(message) { }
}

/// <summary>
/// Market data could not be fetched or is incomplete (MOEX request
/// failed, ticker not on the board, stale quotes, ...). The web layer
/// maps these to HTTP 502.
/// </summary>
public sealed class MarketDataException : DomainException
{
    public MarketDataException(string message) : base(message) { }
    public MarketDataException(string message, Exception inner) : base(message, inner) { }
}

using System.Net;
using IndexInvestment.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IndexInvestment.Web;

/// <summary>
/// Maps domain errors to the same HTTP codes the Python webapp used:
/// market-data failures → 502, bad input → 400, business conflicts
/// (nothing to undo, position missing, run in progress, ...) → 409.
/// </summary>
public sealed class DomainExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var (code, handled) = context.Exception switch
        {
            MarketDataException => ((int)HttpStatusCode.BadGateway, true),
            ValidationError => ((int)HttpStatusCode.BadRequest, true),
            DomainException => ((int)HttpStatusCode.Conflict, true),
            _ => (0, false),
        };
        if (!handled) return;

        context.ExceptionHandled = true;
        context.HttpContext.Response.StatusCode = code;
        context.Result = new JsonResult(new { detail = context.Exception.Message });
    }
}

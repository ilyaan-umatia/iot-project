using Application.Api.Models;
using Application.Api.Services;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace Application.Api.Controllers;

[ApiController]
[Route("events")]
public sealed class EventsController(GpioMessageService service) : ControllerBase
{
    [HttpGet]
    public HistoryEntry[] GetRecent([FromQuery, Range(1, 500)] int limit = 100) => service.GetRecentEvents(limit);

    [HttpGet("summary")]
    public DeviceSummary[] GetSummary() => service.GetSummaries();
}

using ClinicApi.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace ClinicApi.Controllers;

[ApiController]
[Route("api/appointments")]
public class AppointmentsController : ControllerBase
{
    private readonly AppointmentService _service;

    public AppointmentsController(AppointmentService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<List<AppointmentListDto>>> GetAppointments([FromQuery] string? status, [FromQuery] string? patientLastName)
    {
        var appointments = await _service.GetAppointmentsAsync(status, patientLastName);
        return Ok(appointments);
    }

    [HttpGet("{idAppointment:int}")]
    public async Task<ActionResult<AppointmentDetailsDto>> GetAppointment(int idAppointment)
    {
        var appointment = await _service.GetAppointmentByIdAsync(idAppointment);
        if (appointment == null) return NotFound(new ErrorResponseDto { Message = "Wizyta nie istnieje." });
        return Ok(appointment);
    }

    [HttpPost]
    public async Task<ActionResult<AppointmentDetailsDto>> CreateAppointment(CreateAppointmentRequestDto dto)
    {
        var result = await _service.CreateAppointmentAsync(dto);
        if (!result.Success) return StatusCode(result.StatusCode, new ErrorResponseDto { Message = result.Error! });

        var created = await _service.GetAppointmentByIdAsync(result.Id!.Value);
        return CreatedAtAction(nameof(GetAppointment), new { idAppointment = result.Id.Value }, created);
    }

    [HttpPut("{idAppointment:int}")]
    public async Task<IActionResult> UpdateAppointment(int idAppointment, UpdateAppointmentRequestDto dto)
    {
        var result = await _service.UpdateAppointmentAsync(idAppointment, dto);
        if (!result.Success) return StatusCode(result.StatusCode, new ErrorResponseDto { Message = result.Error! });
        return Ok();
    }

    [HttpDelete("{idAppointment:int}")]
    public async Task<IActionResult> DeleteAppointment(int idAppointment)
    {
        var result = await _service.DeleteAppointmentAsync(idAppointment);
        if (!result.Success) return StatusCode(result.StatusCode, new ErrorResponseDto { Message = result.Error! });
        return NoContent();
    }
}
